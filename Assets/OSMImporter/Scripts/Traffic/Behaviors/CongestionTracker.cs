using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ⑦ CongestionTracker — Ưu tiên THẤP.
    /// Phát hiện khu vực tắc phía trước + Báo cáo congestion cho waypoint graph.
    /// </summary>
    public class CongestionTracker
    {
        private const float CONGESTION_SCAN_RADIUS = 25f;
        private const int   CONGESTION_VEHICLE_THRESHOLD = 5;
        private const float CONGESTION_CHECK_INTERVAL = 2f;

        private readonly VehicleContext _ctx;

        public CongestionTracker(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            DetectCongestionAhead(dt);
            ReportCongestion(dt);
        }

        // ── Quét phía trước trên path. Nếu nhiều xe chậm/dừng → reroute sớm ──
        private void DetectCongestionAhead(float dt)
        {
            _ctx.CongestionCheckTimer -= dt;
            if (_ctx.CongestionCheckTimer > 0f) return;
            _ctx.CongestionCheckTimer = CONGESTION_CHECK_INTERVAL + Random.Range(-0.5f, 0.5f);

            if (_ctx.Rerouting || _ctx.Path == null || _ctx.Path.Count == 0) return;
            if (_ctx.PathIdx >= _ctx.Path.Count) return;

            Transform t = _ctx.Transform;
            Vector3 lookAheadPos = t.position + t.forward * 20f;

            int lookCount = Mathf.Min(5, _ctx.Path.Count - _ctx.PathIdx);
            if (lookCount > 1)
            {
                Vector3 sum = Vector3.zero;
                for (int i = _ctx.PathIdx; i < _ctx.PathIdx + lookCount; i++)
                    sum += _ctx.Path[i].Position;
                lookAheadPos = sum / lookCount;
            }

            int slowCount = 0;
            Collider[] nearby = Physics.OverlapSphere(lookAheadPos, CONGESTION_SCAN_RADIUS, _ctx.VehicleLayer);
            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;
                if (other.Ctx.CurrentSpeed < 1.0f) slowCount++;
            }

            if (slowCount >= CONGESTION_VEHICLE_THRESHOLD)
            {
                var graph = _ctx.Graph;
                for (int i = _ctx.PathIdx; i < Mathf.Min(_ctx.Path.Count, _ctx.PathIdx + lookCount); i++)
                {
                    long wpId = _ctx.Path[i].OSMNodeId;
                    if (graph.CongestionCosts.TryGetValue(wpId, out float existing))
                        graph.CongestionCosts[wpId] = Mathf.Min(existing + 2f, 20f);
                    else
                        graph.CongestionCosts[wpId] = 5f;
                }

                _ctx.Path.Clear();
                _ctx.DeadlockLevel = 0;
                _ctx.StuckTimer = 0f;
                _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(Random.Range(0.1f, 0.5f), true));
            }
        }

        // ── Báo cáo congestion cho waypoint đang đứng nếu bị kẹt ──
        private void ReportCongestion(float dt)
        {
            _ctx.CongestionReportTimer -= dt;
            if (_ctx.CongestionReportTimer > 0f) return;
            _ctx.CongestionReportTimer = 1f;

            var graph = _ctx.Graph;

            if (!_ctx.IsStuck && !_ctx.IsColliding)
            {
                // Đường trống → decay congestion
                if (_ctx.PathIdx < _ctx.Path.Count)
                {
                    long wpId = _ctx.Path[Mathf.Min(_ctx.PathIdx, _ctx.Path.Count - 1)].OSMNodeId;
                    if (graph.CongestionCosts.TryGetValue(wpId, out float val))
                    {
                        val -= 2.0f;
                        if (val <= 1f) graph.CongestionCosts.Remove(wpId);
                        else graph.CongestionCosts[wpId] = val;
                    }
                }
                return;
            }

            // Xe bị kẹt → đánh dấu congested
            Waypoint nearest = graph.FindNearest(_ctx.Transform.position);
            if (nearest != null)
            {
                long wpId = nearest.OSMNodeId;
                if (graph.CongestionCosts.TryGetValue(wpId, out float existing))
                    graph.CongestionCosts[wpId] = Mathf.Min(existing + 1f, 20f);
                else
                    graph.CongestionCosts[wpId] = 3f;

                foreach (long connId in nearest.ConnectedWaypointIds)
                {
                    if (graph.CongestionCosts.TryGetValue(connId, out float connVal))
                        graph.CongestionCosts[connId] = Mathf.Min(connVal + 0.2f, 8f);
                    else
                        graph.CongestionCosts[connId] = 1.5f;
                }
            }
        }
    }
}
