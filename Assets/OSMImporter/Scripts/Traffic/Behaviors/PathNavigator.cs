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

            Vector3 myPos = _ctx.Transform.position;
            var currentWp = graph.FindNearest(myPos);
            if (currentWp != null)
            {
                _ctx.StartNodeId = currentWp.OSMNodeId;
            }

            var keys = new List<long>(graph.Waypoints.Keys);
            float minDistSq = 50f * 50f;

            bool pickedTarget = false;
            int pickAttempts = 0;

            while (!pickedTarget && pickAttempts++ < 8)
            {
                long candidateId = keys[Random.Range(0, keys.Count)];

                if (candidateId == 0 || candidateId == _ctx.StartNodeId) continue;

                if (graph.Waypoints.TryGetValue(candidateId, out var destWp))
                {
                    float distSq = (destWp.Position - myPos).sqrMagnitude;
                    if (distSq < minDistSq)
                    {
                        minDistSq *= 0.5f;
                        continue;
                    }

                    // Tỷ lệ chấp nhận đích đến theo tầm quan trọng của đường để xe tập trung đi trên đường lớn
                    float destWeight = 1.0f;
                    switch (destWp.RoadType.ToLower())
                    {
                        case "motorway":    destWeight = 10f; break;
                        case "trunk":       destWeight = 8f;  break;
                        case "primary":     destWeight = 6f;  break;
                        case "secondary":   destWeight = 4f;  break;
                        case "tertiary":    destWeight = 1.5f;break;
                        case "residential": destWeight = 0.2f;break;
                        case "service":     destWeight = 0.05f;break;
                        default:            destWeight = 1.0f;break;
                    }
                    if (Random.value * 10f >= destWeight)
                    {
                        continue; // Bỏ qua đích đến này để tìm điểm khác ưu tiên đường lớn hơn
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

            // ── Bước 1: Subdivide đoạn dài bằng nội suy tuyến tính (đơn giản, không Catmull-Rom) ──
            var points = new List<(Vector3 pos, int rawIdx)>();
            points.Add((rawPath[0].Position, 0));

            for (int i = 0; i < rawPath.Count - 1; i++)
            {
                Vector3 p0 = rawPath[i].Position;
                Vector3 p1 = rawPath[i + 1].Position;
                float segLen = Vector3.Distance(p0, p1);

                // Chiều dài segment tối đa = 4m (cố định, đơn giản, ổn định)
                int subs = Mathf.Max(1, Mathf.CeilToInt(segLen / 4.0f));
                for (int s = 1; s < subs; s++)
                {
                    float t = (float)s / subs;
                    points.Add((Vector3.Lerp(p0, p1, t), i));
                }
                points.Add((p1, i + 1));
            }

            // ── Bước 2: Tính hướng đường tại mỗi điểm (Tránh chia 0 và lạng lách do nhiễu khoảng cách) ──
            Vector3[] directions = new Vector3[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 fwd;
                if (i == 0 && points.Count > 1)
                    fwd = points[1].pos - points[0].pos;
                else if (i == points.Count - 1)
                    fwd = points[i].pos - points[i - 1].pos;
                else
                    fwd = points[i + 1].pos - points[i - 1].pos;

                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f && i > 0)
                {
                    directions[i] = directions[i - 1]; // Tránh lỗi góc vuông quay ngược khi bị trùng Node
                }
                else
                {
                    if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                    directions[i] = fwd.normalized;
                }
            }

            // ── Bước 3: Áp dụng lane offset tỉ lệ với chiều rộng của từng loại đường để tránh đè vỉa hè ──
            float laneOffset = _ctx.LaneOffset;

            if (Mathf.Abs(laneOffset) < 0.01f)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var (pos, rawIdx) = points[i];
                    Waypoint wpRef = rawPath[Mathf.Clamp(rawIdx, 0, rawPath.Count - 1)];
                    _ctx.ExactPath.Add(new VehicleAgent.PathPoint
                    {
                        Position = pos,
                        WaypointRef = wpRef
                    });
                }
            }
            else
            {
                float startRoadMaxOffset = RoadUtility.GetMaxOffset(rawPath[0].RoadType);
                float offsetRatio = Mathf.Clamp01(laneOffset / Mathf.Max(0.5f, startRoadMaxOffset));

                for (int i = 0; i < points.Count; i++)
                {
                    var (pos, rawIdx) = points[i];
                    Waypoint wpRef = rawPath[Mathf.Clamp(rawIdx, 0, rawPath.Count - 1)];

                    float maxOff = RoadUtility.GetMaxOffset(wpRef.RoadType);
                    float curOffset = offsetRatio * maxOff;

                    // Giới hạn an toàn tuyệt đối theo kích thước xe
                    float safeMax = Mathf.Max(0.1f, maxOff - (_ctx.VehicleWidth * 0.5f + 0.15f));
                    curOffset = Mathf.Min(curOffset, safeMax);

                    Vector3 right = Vector3.Cross(Vector3.up, directions[i]).normalized;
                    Vector3 offsetPos = pos + right * curOffset;
                    offsetPos.y = pos.y;

                    _ctx.ExactPath.Add(new VehicleAgent.PathPoint
                    {
                        Position = offsetPos,
                        WaypointRef = wpRef
                    });
                }
            }

            // Không làm mịn (smoothing) ở đây vì nó sẽ làm đường chạy bị cắt góc và lệch khỏi tim làn đường thực tế.
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

            var currentWp = graph.FindNearest(_ctx.Transform.position);
            if (currentWp != null)
            {
                _ctx.StartNodeId = currentWp.OSMNodeId;
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
