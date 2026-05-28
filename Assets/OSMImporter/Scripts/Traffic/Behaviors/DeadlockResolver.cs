using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ⑤ DeadlockResolver — Ưu tiên TRUNG BÌNH.
    /// Escalation khi xe bị kẹt: Nudge → Reroute → Teleport.
    /// </summary>
    public class DeadlockResolver
    {
        private readonly VehicleContext _ctx;
        private float _stuckCheckSpeed = 0.5f;

        public DeadlockResolver(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            HandleDeadlock(dt);
        }

        /// <summary>
        /// Xe dừng vì đèn đỏ → được quyền chờ lâu hơn patience.
        /// Nhường đường không tính là cố tình dừng.
        /// </summary>
        public bool IsIntentionallyStopped(int depth = 0)
        {
            if (_ctx.IsWaitingAtRedLight) return true;
            if (depth > 15) return false;

            if (_ctx.AheadVehicle != null && _ctx.AheadVehicle.Ctx != null
                && _ctx.AheadVehicle.Ctx.CurrentSpeed < _stuckCheckSpeed)
                return _ctx.AheadVehicle.DeadlockResolver.IsIntentionallyStopped(depth + 1);

            return false;
        }

        private void HandleDeadlock(float dt)
        {
            if (IsIntentionallyStopped())
            {
                _ctx.StuckTimer = 0f;
                _ctx.DeadlockLevel = 0;
                _ctx.IsStuck = false;
                return;
            }

            if (Mathf.Abs(_ctx.CurrentSpeed) < _stuckCheckSpeed && !_ctx.Rerouting)
            {
                _ctx.StuckTimer += dt;
                _ctx.IsStuck = _ctx.StuckTimer > 2f;

                float effectivePatience = Mathf.Max(1.5f, _ctx.Patience * 0.6f);

                if (_ctx.StuckTimer > effectivePatience)
                {
                    _ctx.DeadlockLevel++;
                    _ctx.StuckTimer = 0f;

                    switch (_ctx.DeadlockLevel)
                    {
                        case 1:
                            // Đánh lái né + cài số lùi
                            float curMax = _ctx.CurrentMaxOffset;
                            _ctx.TargetOvertakeOffset = Mathf.Clamp(
                                _ctx.LaneOffset + Random.Range(-2f, 2f),
                                -curMax, curMax + 1f);
                            _ctx.ReversingTimer = 1.2f;
                            _ctx.Braking = false;
                            _ctx.DesiredSpeed = -_ctx.BaseSpeed * 0.4f;
                            break;

                        case 2:
                            // Reroute — tìm đường khác
                            _ctx.Path.Clear();
                            _ctx.DeadlockLevel = 0;
                            _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(0.2f, true));
                            break;

                        default:
                            // Teleport — fallback cuối cùng
                            TeleportToSafeWaypoint();
                            break;
                    }
                }
            }
            else
            {
                _ctx.StuckTimer = 0f;
                _ctx.DeadlockLevel = 0;
                _ctx.IsStuck = false;
            }
        }

        public void TeleportToSafeWaypoint()
        {
            var graph = _ctx.Graph;
            var vals = new List<Waypoint>(graph.Waypoints.Values);
            Waypoint target = null;

            // Tìm node an toàn (không ngã tư, không đèn đỏ, không tắc)
            for (int i = 0; i < 30; i++)
            {
                Waypoint wp = vals[Random.Range(0, vals.Count)];
                if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                if (graph.CongestionCosts != null && graph.CongestionCosts.ContainsKey(wp.OSMNodeId)) continue;

                bool tooClose = false;
                foreach (var col in Physics.OverlapSphere(wp.Position, 8f, _ctx.VehicleLayer))
                {
                    if (col.GetComponentInParent<VehicleAgent>() != _ctx.Agent)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    target = wp;
                    break;
                }
            }

            // Fallback ưu tiên tránh ngã tư
            if (target == null)
            {
                for (int i = 0; i < 20; i++)
                {
                    Waypoint wp = vals[Random.Range(0, vals.Count)];
                    if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                    if (graph.CongestionCosts != null && graph.CongestionCosts.ContainsKey(wp.OSMNodeId)) continue;
                    target = wp;
                    break;
                }
            }

            if (target == null) target = vals[Random.Range(0, vals.Count)];

            _ctx.StartNodeId = target.OSMNodeId;
            _ctx.Transform.position = new Vector3(target.Position.x, 0f, target.Position.z);
            _ctx.DeadlockLevel = 0;
            _ctx.Path.Clear();
            _ctx.ExactPath.Clear();
            _ctx.IsWaitingAtRedLight = false;
            _ctx.IsOvertaking = false;

            _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(0.5f, false));
        }
    }
}
