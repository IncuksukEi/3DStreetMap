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
            {
                // Chỉ kế thừa trạng thái dừng chủ động nếu xe phía trước đi cùng hướng (cùng hàng/làn)
                float alignment = Vector3.Dot(_ctx.Transform.forward, _ctx.AheadVehicle.transform.forward);
                if (alignment > 0.7f)
                {
                    return _ctx.AheadVehicle.DeadlockResolver.IsIntentionallyStopped(depth + 1);
                }
            }

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

                float effectivePatience = Mathf.Max(1.2f, _ctx.Patience * 0.5f);

                if (_ctx.StuckTimer > effectivePatience)
                {
                    _ctx.DeadlockLevel++;
                    _ctx.StuckTimer = 0f;

                    float curMax = _ctx.CurrentMaxOffset;

                    switch (_ctx.DeadlockLevel)
                    {
                        case 1:
                            // Level 1: WaitBriefly — Đợi thêm một chút xem xe trước có di chuyển không
                            #if UNITY_EDITOR
                            Debug.Log($"[Deadlock L1] {_ctx.Agent.name} đang chờ đợi kiên nhẫn...");
                            #endif
                            break;

                        case 2:
                            // Level 2: Honk — Bóp còi nhắc nhở xe cản trước mặt
                            #if UNITY_EDITOR
                            Debug.Log($"[Deadlock L2] {_ctx.Agent.name} bóp còi giục giã!");
                            #endif
                            _ctx.Agent.Honk?.TriggerHonk(false);
                            break;

                        case 3:
                            // Level 3: LateralSqueeze — Nhích lách né chướng ngại vật (hạn chế tối đa với ô tô, bỏ qua với xe buýt)
                            #if UNITY_EDITOR
                            Debug.Log($"[Deadlock L3] {_ctx.Agent.name} cố gắng lách khe bên cạnh.");
                            #endif
                            float squeezeRange = 0f;
                            if (_ctx.IsMoto) squeezeRange = Random.Range(-0.8f, 0.8f);
                            else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) squeezeRange = 0f;
                            else squeezeRange = Random.Range(-0.25f, 0.25f); // Ô tô nhích rất nhẹ 25cm

                            _ctx.TargetOvertakeOffset = Mathf.Clamp(
                                _ctx.LaneOffset + squeezeRange,
                                -curMax, curMax);
                            break;

                        case 4:
                            // Level 4: ShoulderUse — Leo lề / Đi vào lề đường (CHỈ cho phép xe máy, ô tô/xe buýt tuyệt đối không leo lề)
                            if (_ctx.IsMoto && (_ctx.Profile.CanUseShoulder || (_ctx.Driver != null && _ctx.Driver.Opportunism > 0.6f)))
                            {
                                #if UNITY_EDITOR
                                Debug.Log($"[Deadlock L4] {_ctx.Agent.name} leo lề vượt cản.");
                                #endif
                                // Mở rộng max offset thêm 0.8m để tràn ra vỉa hè tránh kẹt
                                float spillMax = curMax + 0.8f;
                                float escapeOffset = (_ctx.LaneOffset >= 0f) ? spillMax : -spillMax;
                                _ctx.TargetOvertakeOffset = escapeOffset;
                            }
                            else
                            {
                                // Không leo lề được -> Nhảy thẳng sang Level 5
                                _ctx.DeadlockLevel = 5;
                                _ctx.StuckTimer = effectivePatience + 0.1f; // Trigger ngay L5 ở frame sau
                            }
                            break;

                        case 5:
                            // Level 5: AlleyWrongWay / Reverse escape — Đi ngược chiều ngắn (xe máy) hoặc Cài số lùi
                            bool isSafe = IsSafeToReverse();
                            // Ô tô cực kỳ ngại lùi xe đường chính
                            if (isSafe && _ctx.Profile != null && _ctx.Profile.ReverseReluctance > 0.85f && !_ctx.IsMoto)
                            {
                                isSafe = false;
                            }

                            if (isSafe)
                            {
                                #if UNITY_EDITOR
                                Debug.Log($"[Deadlock L5] {_ctx.Agent.name} cài số lùi thoát kẹt.");
                                #endif
                                _ctx.TargetOvertakeOffset = Mathf.Clamp(
                                    _ctx.LaneOffset + Random.Range(-1.5f, 1.5f),
                                    -curMax, curMax);
                                _ctx.ReversingTimer = 1.5f;
                                _ctx.Braking = false;
                                _ctx.DesiredSpeed = -_ctx.BaseSpeed * 0.35f;
                            }
                            else if (_ctx.IsMoto && _ctx.Profile.CanWrongWayShortDistance)
                            {
                                #if UNITY_EDITOR
                                Debug.Log($"[Deadlock L5-Moto] {_ctx.Agent.name} xe máy quay đầu đi ngược chiều ngắn thoát kẹt.");
                                #endif
                                _ctx.TargetOvertakeOffset = Mathf.Clamp(
                                    _ctx.LaneOffset + (_ctx.LaneOffset >= 0f ? -2f : 2f),
                                    -curMax, curMax);
                            }
                            else
                            {
                                // Không lùi được -> chuyển sang Reroute
                                _ctx.DeadlockLevel = 6;
                                _ctx.StuckTimer = effectivePatience + 0.1f;
                            }
                            break;

                        case 6:
                            // Level 6: Reroute — Tìm đường đi khác tránh điểm kẹt
                            #if UNITY_EDITOR
                            Debug.Log($"[Deadlock L6] {_ctx.Agent.name} tìm đường đi thay thế (Reroute).");
                            #endif
                            _ctx.Path.Clear();
                            _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(0.1f, true));
                            break;

                        default:
                            // Level 7: Teleport — Cứu hộ khẩn cấp (fallback cuối cùng)
                            #if UNITY_EDITOR
                            Debug.LogWarning($"[Deadlock L7] {_ctx.Agent.name} Kẹt cứng hoàn toàn! Tiến hành dịch chuyển tức thời (Teleport).");
                            #endif
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
            _ctx.Transform.position = target.Position;
            _ctx.DeadlockLevel = 0;
            _ctx.Path.Clear();
            _ctx.ExactPath.Clear();
            _ctx.IsWaitingAtRedLight = false;
            _ctx.IsOvertaking = false;

            _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(0.5f, false));
        }

        private bool IsSafeToReverse()
        {
            Transform t = _ctx.Transform;
            if (t == null) return false;

            float length = 1.1f;
            if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) length = 2.5f;
            else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) length = 0.55f;

            float checkDist = length * 1.5f;
            Vector3 origin = t.position + Vector3.up * 0.5f;
            Vector3 dir = -t.forward;

            float halfW = Mathf.Clamp(_ctx.VehicleWidth * 0.45f, 0.3f, 1.0f);
            Vector3 halfExtents = new Vector3(halfW, 0.4f, 0.15f);
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);

            RaycastHit[] hits = Physics.BoxCastAll(origin, halfExtents, dir, rot, checkDist, _ctx.VehicleLayer);
            foreach (var hit in hits)
            {
                var other = hit.collider.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent)
                    return false;
            }
            return true;
        }
    }
}
