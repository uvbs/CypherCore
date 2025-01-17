﻿/*
 * Copyright (C) 2012-2019 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.Constants;
using Framework.GameMath;
using Game.DataStorage;
using Game.Entities;
using Game.Maps;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Movement
{
    public class WaypointMovementGenerator : MovementGeneratorMedium<Creature>
    {
        const int FLIGHT_TRAVEL_UPDATE = 100;
        const int TIMEDIFF_NEXT_WP = 250;

        public WaypointMovementGenerator(uint pathid = 0, bool _repeating = true)
        {
            nextMoveTime = new TimeTrackerSmall(0);
            isArrivalDone = false;
            pathId = pathid;
            repeating = _repeating;
        }

        public override void DoReset(Creature creature)
        {
            creature.AddUnitState(UnitState.Roaming | UnitState.RoamingMove);
            StartMoveNow(creature);
        }

        public override void DoFinalize(Creature creature)
        {
            creature.ClearUnitState(UnitState.Roaming | UnitState.RoamingMove);
            creature.SetWalk(false);
        }

        public override void DoInitialize(Creature creature)
        {
            LoadPath(creature);
            creature.AddUnitState(UnitState.Roaming | UnitState.RoamingMove);
        }

        public override bool DoUpdate(Creature creature, uint time_diff)
        {
            // Waypoint movement can be switched on/off
            // This is quite handy for escort quests and other stuff
            if (creature.HasUnitState(UnitState.NotMove))
            {
                creature.ClearUnitState(UnitState.RoamingMove);
                return true;
            }

            // prevent a crash at empty waypoint path.
            if (path == null || path.Empty())
                return false;

            if (Stopped())
            {
                if (CanMove((int)time_diff))
                    return StartMove(creature);
            }
            else
            {
                // Set home position at place on waypoint movement.
                if (creature.GetTransGUID().IsEmpty())
                    creature.SetHomePosition(creature.GetPosition());

                if (creature.IsStopped())
                    Stop(WorldConfig.GetIntValue(WorldCfg.CreatureStopForPlayer));
                else if (creature.MoveSpline.Finalized())
                {
                    OnArrived(creature);
                    return StartMove(creature);
                }
            }

            return true;
        }

        void MovementInform(Creature creature)
        {
            if (creature.IsAIEnabled)
                creature.GetAI().MovementInform(MovementGeneratorType.Waypoint, currentNode);
        }

        public override bool GetResetPosition(Unit u, out float x, out float y, out float z)
        {
            x = y = z = 0;
            // prevent a crash at empty waypoint path.
            if (path == null || path.Empty())
                return false;

            var node = path.LookupByIndex((int)currentNode);
            x = node.x;
            y = node.y;
            z = node.z;
            return true;
        }

        void Stop(int time)
        {
            nextMoveTime.Reset(time);
        }

        bool Stopped()
        {
            return !nextMoveTime.Passed();
        }

        bool CanMove(int diff)
        {
            nextMoveTime.Update(diff);
            return nextMoveTime.Passed();
        }

        void StartMoveNow(Creature creature)
        {
            nextMoveTime.Reset(0);
            StartMove(creature);
        }

        bool StartMove(Creature creature)
        {
            if (path == null || path.Empty())
                return false;

            if (Stopped())
                return true;

            bool transportPath = creature.GetTransport() != null;

            if (isArrivalDone)
            {
                if ((currentNode == path.Count - 1) && !repeating) // If that's our last waypoint
                {
                    WaypointData waypoint = path.LookupByIndex((int)currentNode);

                    float x = waypoint.x;
                    float y = waypoint.y;
                    float z = waypoint.z;
                    float o = creature.GetOrientation();

                    if (!transportPath)
                        creature.SetHomePosition(x, y, z, o);
                    else
                    {
                        Transport trans = creature.GetTransport();
                        if (trans)
                        {
                            o -= trans.GetOrientation();
                            creature.SetTransportHomePosition(x, y, z, o);
                            trans.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
                            creature.SetHomePosition(x, y, z, o);
                        }
                        else
                            transportPath = false;
                        // else if (vehicle) - this should never happen, vehicle offsets are const
                    }

                    creature.GetMotionMaster().Initialize();
                    return false;
                }

                currentNode = (uint)((currentNode + 1) % path.Count);
            }

            var node = path.LookupByIndex((int)currentNode);

            isArrivalDone = false;

            creature.AddUnitState(UnitState.RoamingMove);

            Position formationDest = new Position(node.x, node.y, node.z, 0.0f);
            MoveSplineInit init = new MoveSplineInit(creature);

            //! If creature is on transport, we assume waypoints set in DB are already transport offsets
            if (transportPath)
            {
                init.DisableTransportPathTransformations();
                ITransport trans = creature.GetDirectTransport();
                if (trans != null)
                    trans.CalculatePassengerPosition(ref formationDest.posX, ref formationDest.posY, ref formationDest.posZ, ref formationDest.Orientation);
            }

            //! Do not use formationDest here, MoveTo requires transport offsets due to DisableTransportPathTransformations() call
            //! but formationDest contains global coordinates
            init.MoveTo(node.x, node.y, node.z);

            //! Accepts angles such as 0.00001 and -0.00001, 0 must be ignored, default value in waypoint table
            if (node.orientation != 0 && node.delay != 0)
                init.SetFacing(node.orientation);

            switch (node.moveType)
            {
                case WaypointMoveType.Land:
                    init.SetAnimation(AnimType.ToGround);
                    break;
                case WaypointMoveType.Takeoff:
                    init.SetAnimation(AnimType.ToFly);
                    break;
                case WaypointMoveType.Run:
                    init.SetWalk(false);
                    break;
                case WaypointMoveType.Walk:
                    init.SetWalk(true);
                    break;
            }

            init.Launch();

            //Call for creature group update
            if (creature.GetFormation() != null && creature.GetFormation().GetLeader() == creature)
            {
                creature.SetWalk(node.moveType != WaypointMoveType.Run);
                creature.GetFormation().LeaderMoveTo(formationDest.posX, formationDest.posY, formationDest.posZ);
            }

            return true;
        }

        void LoadPath(Creature creature)
        {
            if (pathId == 0)
                pathId = creature.GetWaypointPath();

            path = Global.WaypointMgr.GetPath(pathId);

            if (path == null)
            {
                // No movement found for entry
                Log.outError(LogFilter.ScriptsAi, "WaypointMovementGenerator.LoadPath: creature {0} (Entry: {1} GUID: {2}) doesn't have waypoint path id: {3}", creature.GetName(), creature.GetEntry(), creature.GetGUID().ToString(), pathId);
                return;
            }

            StartMoveNow(creature);
        }

        void OnArrived(Creature creature)
        {
            if (path == null || path.Empty())
                return;

            if (isArrivalDone)
                return;

            isArrivalDone = true;

            var current = path.LookupByIndex((int)currentNode);

            if (current.eventId != 0 && RandomHelper.URand(0, 99) < current.eventChance)
            {
                Log.outDebug(LogFilter.Unit, "Creature movement start script {0} at point {1} for {2}.", current.eventId, currentNode, creature.GetGUID());
                creature.ClearUnitState(UnitState.RoamingMove);
                creature.GetMap().ScriptsStart(ScriptsType.Waypoint, current.eventId, creature, null);
            }

            // Inform script
            MovementInform(creature);
            creature.UpdateWaypointID(currentNode);

            if (current.delay != 0)
            {
                creature.ClearUnitState(UnitState.RoamingMove);
                Stop((int)current.delay);
            }
        }

        public override MovementGeneratorType GetMovementGeneratorType()
        {
            return MovementGeneratorType.Waypoint;
        }

        public uint GetCurrentNode() { return currentNode; }

        TimeTrackerSmall nextMoveTime;

        bool isArrivalDone;
        uint pathId;
        bool repeating;
        List<WaypointData> path;
        uint currentNode;
    }

    public class FlightPathMovementGenerator : MovementGeneratorMedium<Player>
    {
        public void LoadPath(Player player, uint startNode = 0)
        {
            i_path.Clear();
            i_currentNode = (int)startNode;
            _pointsForPathSwitch.Clear();
            var taxi = player.m_taxi.GetPath();
            float discount = player.GetReputationPriceDiscount(player.m_taxi.GetFlightMasterFactionTemplate());

            for (int src = 0, dst = 1; dst < taxi.Count; src = dst++)
            {
                uint path, cost;
                Global.ObjectMgr.GetTaxiPath(taxi[src], taxi[dst], out path, out cost);
                if (path > CliDB.TaxiPathNodesByPath.Keys.Max())
                    return;

                var nodes = CliDB.TaxiPathNodesByPath[path];
                if (!nodes.Empty())
                {
                    TaxiPathNodeRecord start = nodes[0];
                    TaxiPathNodeRecord end = nodes[nodes.Length - 1];
                    bool passedPreviousSegmentProximityCheck = false;
                    for (uint i = 0; i < nodes.Length; ++i)
                    {
                        if (passedPreviousSegmentProximityCheck || src == 0 || i_path.Empty() || IsNodeIncludedInShortenedPath(i_path.Last(), nodes[i]))
                        {
                            if ((src == 0 || (IsNodeIncludedInShortenedPath(start, nodes[i]) && i >= 2)) &&
                                (dst == taxi.Count - 1 || (IsNodeIncludedInShortenedPath(end, nodes[i]) && i < nodes.Length - 1)))
                            {
                                passedPreviousSegmentProximityCheck = true;
                                i_path.Add(nodes[i]);
                            }
                        }
                        else
                        {
                            i_path.RemoveAt(i_path.Count - 1);
                            _pointsForPathSwitch[_pointsForPathSwitch.Count - 1].PathIndex -= 1;
                        }
                    }
                }

                _pointsForPathSwitch.Add(new TaxiNodeChangeInfo((uint)(i_path.Count - 1), (long)Math.Ceiling(cost * discount)));
            }
        }

        public override void DoInitialize(Player owner)
        {
            Reset(owner);
            InitEndGridInfo();
        }

        public override void DoFinalize(Player owner)
        {
            // remove flag to prevent send object build movement packets for flight state and crash (movement generator already not at top of stack)
            owner.ClearUnitState(UnitState.InFlight);

            owner.Dismount();
            owner.RemoveUnitFlag(UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight);

            if (owner.m_taxi.Empty())
            {
                owner.GetHostileRefManager().SetOnlineOfflineState(true);
                // update z position to ground and orientation for landing point
                // this prevent cheating with landing  point at lags
                // when client side flight end early in comparison server side
                owner.StopMoving();
                owner.SetFallInformation(0, owner.GetPositionZ());
            }

            owner.RemovePlayerFlag(PlayerFlags.TaxiBenchmark);
            owner.RestoreDisplayId();
        }

        public override void DoReset(Player owner)
        {
            owner.GetHostileRefManager().SetOnlineOfflineState(false);
            owner.AddUnitState(UnitState.InFlight);
            owner.AddUnitFlag(UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight);

            MoveSplineInit init = new MoveSplineInit(owner);
            uint end = GetPathAtMapEnd();
            init.args.path = new Vector3[end];
            for (int i = (int)GetCurrentNode(); i != end; ++i)
            {
                Vector3 vertice = new Vector3(i_path[i].Loc.X, i_path[i].Loc.Y, i_path[i].Loc.Z);
                init.args.path[i] = vertice;
            }
            init.SetFirstPointId((int)GetCurrentNode());
            init.SetFly();
            init.SetSmooth();
            init.SetUncompressed();
            init.SetWalk(true);
            init.SetVelocity(30.0f);
            init.Launch();
        }

        public override bool DoUpdate(Player player, uint time_diff)
        {
            uint pointId = (uint)player.MoveSpline.CurrentPathIdx();
            if (pointId > i_currentNode)
            {
                bool departureEvent = true;
                do
                {
                    DoEventIfAny(player, i_path[i_currentNode], departureEvent);
                    while (!_pointsForPathSwitch.Empty() && _pointsForPathSwitch[0].PathIndex <= i_currentNode)
                    {
                        _pointsForPathSwitch.RemoveAt(0);
                        player.m_taxi.NextTaxiDestination();
                        if (!_pointsForPathSwitch.Empty())
                        {
                            player.UpdateCriteria(CriteriaTypes.GoldSpentForTravelling, (uint)_pointsForPathSwitch[0].Cost);
                            player.ModifyMoney(-_pointsForPathSwitch[0].Cost);
                        }
                    }

                    if (pointId == i_currentNode)
                        break;

                    if (i_currentNode == _preloadTargetNode)
                        PreloadEndGrid();
                    i_currentNode += (departureEvent ? 1 : 0);
                    departureEvent = !departureEvent;
                }
                while (true);
            }

            return i_currentNode < (i_path.Count - 1);
        }

        public void SetCurrentNodeAfterTeleport()
        {
            if (i_path.Empty() || i_currentNode >= i_path.Count)
                return;

            uint map0 = i_path[i_currentNode].ContinentID;
            for (int i = i_currentNode + 1; i < i_path.Count; ++i)
            {
                if (i_path[i].ContinentID != map0)
                {
                    i_currentNode = i;
                    return;
                }
            }

        }

        void DoEventIfAny(Player player, TaxiPathNodeRecord node, bool departure)
        {
            uint eventid = departure ? node.DepartureEventID : node.ArrivalEventID;
            if (eventid != 0)
            {
                Log.outDebug(LogFilter.Scripts, "Taxi {0} event {1} of node {2} of path {3} for player {4}", departure ? "departure" : "arrival", eventid, node.NodeIndex, node.PathID, player.GetName());
                player.GetMap().ScriptsStart(ScriptsType.Event, eventid, player, player);
            }
        }

        bool GetResetPos(Player player, out float x, out float y, out float z)
        {
            TaxiPathNodeRecord node = i_path[i_currentNode];
            x = node.Loc.X;
            y = node.Loc.Y;
            z = node.Loc.Z;
            return true;
        }

        void InitEndGridInfo()
        {
            int nodeCount = i_path.Count;        //! Number of nodes in path.
            _endMapId = i_path[nodeCount - 1].ContinentID; //! MapId of last node
            _preloadTargetNode = (uint)nodeCount - 3;
            _endGridX = i_path[nodeCount - 1].Loc.X;
            _endGridY = i_path[nodeCount - 1].Loc.Y;
        }

        void PreloadEndGrid()
        {
            // used to preload the final grid where the flightmaster is
            Map endMap = Global.MapMgr.FindBaseNonInstanceMap(_endMapId);

            // Load the grid
            if (endMap != null)
            {
                Log.outInfo(LogFilter.Server, "Preloading grid ({0}, {1}) for map {2} at node index {3}/{4}", _endGridX, _endGridY, _endMapId, _preloadTargetNode, i_path.Count - 1);
                endMap.LoadGrid(_endGridX, _endGridY);
            }
            else
                Log.outInfo(LogFilter.Server, "Unable to determine map to preload flightmaster grid");
        }

        uint GetPathAtMapEnd()
        {
            if (i_currentNode >= i_path.Count)
                return (uint)i_path.Count;

            uint curMapId = i_path[i_currentNode].ContinentID;
            for (int i = i_currentNode; i < i_path.Count; ++i)
            {
                if (i_path[i].ContinentID != curMapId)
                    return (uint)i;
            }

            return (uint)i_path.Count;
        }

        bool IsNodeIncludedInShortenedPath(TaxiPathNodeRecord p1, TaxiPathNodeRecord p2)
        {
            return p1.ContinentID != p2.ContinentID || Math.Pow(p1.Loc.X - p2.Loc.X, 2) + Math.Pow(p1.Loc.Y - p2.Loc.Y, 2) > (40.0f * 40.0f);
        }

        public override MovementGeneratorType GetMovementGeneratorType() { return MovementGeneratorType.Flight; }

        public List<TaxiPathNodeRecord> GetPath() { return i_path; }

        bool HasArrived() { return (i_currentNode >= i_path.Count); }

        public void SkipCurrentNode() { ++i_currentNode; }

        public uint GetCurrentNode() { return (uint)i_currentNode; }


        float _endGridX;                //! X coord of last node location
        float _endGridY;                //! Y coord of last node location
        uint _endMapId;               //! map Id of last node location
        uint _preloadTargetNode;      //! node index where preloading starts

        int i_currentNode;
        List<TaxiPathNodeRecord> i_path = new List<TaxiPathNodeRecord>();
        List<TaxiNodeChangeInfo> _pointsForPathSwitch = new List<TaxiNodeChangeInfo>();    //! node indexes and costs where TaxiPath changes

        class TaxiNodeChangeInfo
        {
            public TaxiNodeChangeInfo(uint pathIndex, long cost)
            {
                PathIndex = pathIndex;
                Cost = cost;
            }

            public uint PathIndex;
            public long Cost;
        }
    }
}
