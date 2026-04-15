using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// PathNavigator — Xử lý tìm đường:
    /// PickNewDestination + BuildExactPath + RerouteKeepDestination.
    /// </summary>
    public class PathNavigator
    {
        private readonly VehicleContext _ctx;

        public PathNavigator(VehicleContext ctx) => _ctx = ctx;

        // ══════════════════════════════════════════════════════════════════
        // PICK NEW DESTINATION
        // ══════════════════════════════════════════════════════════════════

        public void PickNewDestination()
        {
            // Reset trạng thái lái
            _ctx.TargetOvertakeOffset = _ctx.LaneOffset;
            _ctx.OvertakeOffset = _ctx.LaneOffset;
            _ctx.IsOvertaking = false;

            var graph = _ctx.Graph;
            if (graph == null || graph.Waypoints.Count < 2) return;

            var keys = new List<long>(graph.Waypoints.Keys);
            Vector3 myPos = _ctx.Transform.position;
            float minDistSq = 50f * 50f;

            bool pickedTarget = false;
            int pickAttempts = 0;

            while (!pickedTarget && pickAttempts++ < 8)
            {
                long candidateId = 0;
                float r = Random.value;

                if (TrafficSpawner.Instance != null)
                {
                    if (TrafficSpawner.Instance.EdgeNodes != null
                        && TrafficSpawner.Instance.EdgeNodes.Count > 0 && r < 0.25f)
                    {
                        Waypoint wp = TrafficSpawner.Instance.EdgeNodes[
                            Random.Range(0, TrafficSpawner.Instance.EdgeNodes.Count)];
                        if (wp != null && wp.OSMNodeId != _ctx.StartNodeId)
                            candidateId = wp.OSMNodeId;
                    }
                    else if (TrafficSpawner.Instance.Buildings != null
                             && TrafficSpawner.Instance.Buildings.Count > 0 && r < 0.50f)
                    {
                        Transform targetBldg = TrafficSpawner.Instance.Buildings[
                            Random.Range(0, TrafficSpawner.Instance.Buildings.Count)];
                        Waypoint wp = graph.FindNearest(targetBldg.position);
                        if (wp != null && wp.OSMNodeId != _ctx.StartNodeId)
                            candidateId = wp.OSMNodeId;
                    }
                    else
                    {
                        candidateId = keys[Random.Range(0, keys.Count)];
                    }
                }
                else
                {
                    candidateId = keys[Random.Range(0, keys.Count)];
                }

                if (candidateId == 0 || candidateId == _ctx.StartNodeId) continue;

                if (graph.Waypoints.TryGetValue(candidateId, out var destWp))
                {
                    float distSq = (destWp.Position - myPos).sqrMagnitude;
                    if (distSq < minDistSq)
                    {
                        minDistSq *= 0.5f;
                        continue;
                    }
                }

                _ctx.DestNodeId = candidateId;
                pickedTarget = true;
            }

            if (!pickedTarget)
            {
                _ctx.DestNodeId = keys[Random.Range(0, keys.Count)];
                int tries = 0;
                while (_ctx.DestNodeId == _ctx.StartNodeId && tries++ < 20)
                    _ctx.DestNodeId = keys[Random.Range(0, keys.Count)];
            }

            var candidatePath = graph.FindPath(_ctx.StartNodeId, _ctx.DestNodeId, true);

            int retries = 0;
            while ((candidatePath == null || candidatePath.Count == 0) && retries++ < 8)
            {
                _ctx.DestNodeId = keys[Random.Range(0, keys.Count)];
                candidatePath = graph.FindPath(_ctx.StartNodeId, _ctx.DestNodeId, true);
            }

            if (candidatePath == null || candidatePath.Count == 0)
            {
                _ctx.Agent.DeadlockResolver.TeleportToSafeWaypoint();
                return;
            }

            _ctx.Path = candidatePath;
            BuildExactPath(candidatePath);
        }

        // ══════════════════════════════════════════════════════════════════
        // BUILD EXACT PATH — tạo exact path với lane offset + miter joint
        // ══════════════════════════════════════════════════════════════════

        public void BuildExactPath(List<Waypoint> rawPath)
        {
            _ctx.ExactPath.Clear();
            _ctx.ExactPathIdx = 0;
            _ctx.PathIdx = 0;
            if (rawPath == null || rawPath.Count < 2) return;

            Transform t = _ctx.Transform;

            for (int i = 0; i < rawPath.Count; i++)
            {
                Waypoint current = rawPath[i];
                Vector3 wCurr = current.Position;

                float curMax = RoadUtility.GetMaxOffset(current.RoadType);
                float curOffset = Mathf.Min(_ctx.LaneOffset, curMax);

                if (i == 0)
                {
                    Vector3 d = (rawPath[1].Position - rawPath[0].Position).normalized;
                    if (d.sqrMagnitude < 0.01f) d = t.forward;
                    Vector3 r = Vector3.Cross(Vector3.up, d).normalized;
                    _ctx.ExactPath.Add(new VehicleAgent.PathPoint { Position = wCurr + r * curOffset, WaypointRef = current });
                }
                else if (i == rawPath.Count - 1)
                {
                    Vector3 d = (rawPath[i].Position - rawPath[i - 1].Position).normalized;
                    if (d.sqrMagnitude < 0.01f) d = t.forward;
                    Vector3 r = Vector3.Cross(Vector3.up, d).normalized;
                    _ctx.ExactPath.Add(new VehicleAgent.PathPoint { Position = wCurr + r * curOffset, WaypointRef = current });
                }
                else
                {
                    Vector3 wPrev = rawPath[i - 1].Position;
                    Vector3 wNext = rawPath[i + 1].Position;

                    Vector3 d1 = (wCurr - wPrev).normalized;
                    if (d1.sqrMagnitude < 0.01f) d1 = t.forward;
                    Vector3 r1 = Vector3.Cross(Vector3.up, d1).normalized;

                    Vector3 d2 = (wNext - wCurr).normalized;
                    if (d2.sqrMagnitude < 0.01f) d2 = d1;
                    Vector3 r2 = Vector3.Cross(Vector3.up, d2).normalized;

                    // Bo tròn miter joint
                    Vector3 r_avg = (r1 + r2).normalized;
                    if (r_avg.sqrMagnitude < 0.01f) r_avg = r1;

                    float angleDot = Vector3.Dot(d1, d2);
                    float cosHalf = Mathf.Sqrt(Mathf.Max(0.001f, (1f + angleDot) / 2f));
                    float miterDist = curOffset / cosHalf;
                    miterDist = Mathf.Min(miterDist, curOffset * 1.5f);

                    Vector3 cornerPos = wCurr + r_avg * miterDist;
                    cornerPos.y = wCurr.y;

                    _ctx.ExactPath.Add(new VehicleAgent.PathPoint { Position = cornerPos, WaypointRef = current });
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // REROUTE KEEP DESTINATION — tìm đường mới tới cùng đích
        // ══════════════════════════════════════════════════════════════════

        public void RerouteKeepDestination()
        {
            _ctx.TargetOvertakeOffset = _ctx.LaneOffset;
            _ctx.OvertakeOffset = _ctx.LaneOffset;
            _ctx.IsOvertaking = false;

            var graph = _ctx.Graph;
            if (graph == null || graph.Waypoints.Count < 2)
            {
                PickNewDestination();
                return;
            }

            // Đánh dấu path cũ là congested để A* tránh
            if (_ctx.Path != null)
            {
                for (int i = _ctx.PathIdx; i < Mathf.Min(_ctx.Path.Count, _ctx.PathIdx + 8); i++)
                {
                    long wpId = _ctx.Path[i].OSMNodeId;
                    if (graph.CongestionCosts.TryGetValue(wpId, out float existing))
                        graph.CongestionCosts[wpId] = Mathf.Min(existing + 5f, 30f);
                    else
                        graph.CongestionCosts[wpId] = 8f;
                }
            }

            // Thử congestion-aware
            var newPath = graph.FindPath(_ctx.StartNodeId, _ctx.DestNodeId, true);
            if (newPath != null && newPath.Count > 0)
            {
                _ctx.Path = newPath;
                BuildExactPath(newPath);
                return;
            }

            // Thử không congestion-aware
            newPath = graph.FindPath(_ctx.StartNodeId, _ctx.DestNodeId, false);
            if (newPath != null && newPath.Count > 0)
            {
                _ctx.Path = newPath;
                BuildExactPath(newPath);
                return;
            }

            // Fallback đổi đích
            PickNewDestination();
        }
    }
}
