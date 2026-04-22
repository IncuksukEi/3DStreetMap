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
        // BUILD EXACT PATH — tạo exact path với lane offset + curve subdivision
        // ══════════════════════════════════════════════════════════════════

        // Khoảng cách tối đa giữa 2 exact point — đoạn dài hơn sẽ được nội suy
        private const float MAX_SEGMENT_LENGTH = 8f;
        // Góc tối thiểu (độ) để kích hoạt subdivision cong
        private const float CURVE_SUBDIVIDE_ANGLE = 15f;

        public void BuildExactPath(List<Waypoint> rawPath)
        {
            _ctx.ExactPath.Clear();
            _ctx.ExactPathIdx = 0;
            _ctx.PathIdx = 0;
            if (rawPath == null || rawPath.Count < 2) return;

            Transform t = _ctx.Transform;

            // Bước 1: Tạo center-line points (không offset) cho toàn bộ path
            var centerPoints = new List<Vector3>(rawPath.Count);
            for (int i = 0; i < rawPath.Count; i++)
                centerPoints.Add(rawPath[i].Position);

            // Bước 2: Subdivide các đoạn dài / cong bằng Catmull-Rom
            var subdividedPoints = new List<(Vector3 pos, int rawIdx)>();
            subdividedPoints.Add((centerPoints[0], 0));

            for (int i = 0; i < centerPoints.Count - 1; i++)
            {
                Vector3 p0 = centerPoints[i];
                Vector3 p1 = centerPoints[i + 1];
                float segLen = Vector3.Distance(p0, p1);

                // Tính góc cua tại điểm tiếp theo
                float angle = 0f;
                if (i + 2 < centerPoints.Count)
                {
                    Vector3 d1 = (p1 - p0); d1.y = 0;
                    Vector3 d2 = (centerPoints[i + 2] - p1); d2.y = 0;
                    if (d1.sqrMagnitude > 0.01f && d2.sqrMagnitude > 0.01f)
                        angle = Vector3.Angle(d1, d2);
                }

                // Số điểm nội suy tùy theo độ dài + góc cua
                int subdivisions = Mathf.Max(1, Mathf.CeilToInt(segLen / MAX_SEGMENT_LENGTH));
                if (angle > CURVE_SUBDIVIDE_ANGLE)
                    subdivisions = Mathf.Max(subdivisions, Mathf.CeilToInt(angle / 15f));

                if (subdivisions > 1)
                {
                    // Catmull-Rom control points
                    Vector3 cp0 = (i > 0) ? centerPoints[i - 1] : p0 - (p1 - p0);
                    Vector3 cp3 = (i + 2 < centerPoints.Count) ? centerPoints[i + 2] : p1 + (p1 - p0);

                    for (int s = 1; s < subdivisions; s++)
                    {
                        float tParam = (float)s / subdivisions;
                        Vector3 interp = CatmullRom(cp0, p0, p1, cp3, tParam);
                        interp.y = Mathf.Lerp(p0.y, p1.y, tParam);
                        subdividedPoints.Add((interp, i)); // rawIdx = segment start
                    }
                }

                subdividedPoints.Add((p1, i + 1));
            }

            // Bước 3: Áp dụng lane offset cho subdivided points
            for (int i = 0; i < subdividedPoints.Count; i++)
            {
                var (pos, rawIdx) = subdividedPoints[i];
                Waypoint wpRef = rawPath[Mathf.Clamp(rawIdx, 0, rawPath.Count - 1)];
                float curMax = RoadUtility.GetMaxOffset(wpRef.RoadType);
                float curOffset = Mathf.Min(_ctx.LaneOffset, curMax);

                // Tính hướng đường tại điểm này
                Vector3 fwd;
                if (i == 0 && subdividedPoints.Count > 1)
                    fwd = (subdividedPoints[1].pos - pos);
                else if (i == subdividedPoints.Count - 1 && i > 0)
                    fwd = (pos - subdividedPoints[i - 1].pos);
                else if (i > 0 && i < subdividedPoints.Count - 1)
                    fwd = (subdividedPoints[i + 1].pos - subdividedPoints[i - 1].pos);
                else
                    fwd = t.forward;

                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f) fwd = t.forward;
                fwd.Normalize();

                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 offsetPos = pos + right * curOffset;
                offsetPos.y = pos.y;

                _ctx.ExactPath.Add(new VehicleAgent.PathPoint
                {
                    Position = offsetPos,
                    WaypointRef = wpRef
                });
            }
        }

        /// <summary>Catmull-Rom spline interpolation giữa p1 và p2.</summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
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
