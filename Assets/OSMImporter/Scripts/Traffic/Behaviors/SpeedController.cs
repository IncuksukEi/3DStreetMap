using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ③ SpeedController — Ưu tiên CAO
    /// IDM (Intelligent Driver Model) + CurveSlowdown + ProximityBrake.
    /// Quyết định _desiredSpeed dựa trên tình huống xung quanh.
    /// </summary>
    public class SpeedController
    {
        private const float CURVE_SLOWDOWN_ANGLE = 25f;
        private const float SCAN_RANGE = 25f;

        private readonly VehicleContext _ctx;

        public SpeedController(VehicleContext ctx) => _ctx = ctx;

        /// <summary>
        /// Gọi cả hai bước: AdaptSpeed + ProximityBrake.
        /// Thường KHÔNG dùng trực tiếp — VehicleAgent gọi từng bước riêng để giữ đúng thứ tự pipeline.
        /// </summary>
        public void ExecuteAll()
        {
            AdaptSpeed();
            ProximityBrake();
        }

        // ══════════════════════════════════════════════════════════════════
        // ADAPT SPEED — IDM + curve slowdown
        // ══════════════════════════════════════════════════════════════════

        public void AdaptSpeed()
        {
            // Tốc độ tối đa giới hạn theo loại đường
            float speedLimit = _ctx.BaseSpeed * _ctx.RuntimeSpeedScale
                             * RoadUtility.GetRoadTypeSpeedMultiplier(_ctx.CurrentRoadType);
            _ctx.DesiredSpeed = Mathf.Min(speedLimit, _ctx.MaxSpeedLimit * _ctx.RuntimeSpeedScale);

            // Giảm tốc khi cua gấp
            ApplyCurveSlowdown();

            // IDM: giữ khoảng cách an toàn
            ApplyIDM();
        }

        private void ApplyCurveSlowdown()
        {
            var path = _ctx.Path;
            int idx = _ctx.PathIdx;
            if (idx <= 0 || idx >= path.Count - 1) return;

            Vector3 currentDir = path[idx].Position - path[idx - 1].Position;
            currentDir.y = 0;
            Vector3 nextDir = path[idx + 1].Position - path[idx].Position;
            nextDir.y = 0;

            if (currentDir.sqrMagnitude > 0.01f && nextDir.sqrMagnitude > 0.01f)
            {
                float angle = Vector3.Angle(currentDir, nextDir);
                if (angle > CURVE_SLOWDOWN_ANGLE)
                {
                    float factor = Mathf.Lerp(1f, 0.2f, Mathf.InverseLerp(CURVE_SLOWDOWN_ANGLE, 120f, angle));
                    _ctx.DesiredSpeed *= factor;
                }
            }
        }

        private void ApplyIDM()
        {
            if (_ctx.AheadVehicle == null || _ctx.AheadDistance >= SCAN_RANGE) return;

            var ahead = _ctx.AheadVehicle;
            float aheadDist = _ctx.AheadDistance;

            // Khoảng cách an toàn tối thiểu
            float emergencyBorder = _ctx.MinFollowDistance + (_ctx.CurrentSpeed * _ctx.SafeReactionTime * 0.35f);
            float safeFollowDist  = _ctx.MinFollowDistance + (_ctx.CurrentSpeed * _ctx.SafeReactionTime);
            float sizeBonus = (_ctx.VehicleWidth + ahead.Ctx.VehicleWidth) * 0.3f;
            emergencyBorder += sizeBonus;
            safeFollowDist  += sizeBonus;

            if (aheadDist < emergencyBorder)
            {
                _ctx.EmergencyBraking = true;
                _ctx.DesiredSpeed = 0f;
            }
            else if (aheadDist < safeFollowDist)
            {
                float ratio = Mathf.Clamp01((aheadDist - emergencyBorder) / (safeFollowDist - emergencyBorder));
                float targetSpeed = Mathf.Max(ahead.Ctx.CurrentSpeed * ratio, ahead.Ctx.CurrentSpeed * 0.4f);

                // Ghost Jam Cushioning: chờ giãn khoảng cách mới ga đi
                if (ahead.Ctx.CurrentSpeed < 1.0f && _ctx.CurrentSpeed < 1.0f)
                {
                    if (aheadDist < emergencyBorder + 1.5f)
                        targetSpeed = Mathf.Min(targetSpeed, 0.4f);
                }

                _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, targetSpeed);
            }
            else
            {
                // Xa → giảm tốc sớm nếu xe trước chậm đáng kể
                if (ahead.Ctx.CurrentSpeed < _ctx.CurrentSpeed * 0.6f)
                {
                    float gapRatio = Mathf.Clamp01((aheadDist - safeFollowDist) / (SCAN_RANGE - safeFollowDist));
                    float sprintSpeed = Mathf.Lerp(ahead.Ctx.CurrentSpeed + 2f, _ctx.DesiredSpeed, gapRatio * gapRatio);
                    _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, sprintSpeed);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // PROXIMITY BRAKE — phanh khi xe bên cạnh sáp quá gần
        // ══════════════════════════════════════════════════════════════════

        public void ProximityBrake()
        {
            Transform t = _ctx.Transform;
            float checkRadius = Mathf.Max(3.0f, _ctx.VehicleWidth * 3.0f);
            Collider[] nearby = Physics.OverlapSphere(t.position, checkRadius, _ctx.VehicleLayer);

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;

                Vector3 diff = t.position - other.transform.position;
                diff.y = 0f;
                float dist = diff.magnitude;
                if (dist < 0.01f) continue;

                // Xe ngược chiều khác làn → bỏ qua (không cần phanh gần)
                float dotOncoming = Vector3.Dot(t.forward, other.transform.forward);
                if (dotOncoming < -0.2f)
                {
                    float lateralSep = Mathf.Abs(Vector3.Dot(diff, t.right));
                    float combinedW = (_ctx.VehicleWidth + other.Ctx.VehicleWidth);
                    if (lateralSep > combinedW * 0.7f) continue;
                }

                float speedBonus = _ctx.CurrentSpeed * 0.05f;
                float minSafeDist = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.85f + 0.4f + speedBonus;

                float confidence = _ctx.Agent.Sensor.CalcConfidence(other, dist);

                if (dist < minSafeDist)
                {
                    float dotFwd = Vector3.Dot(t.forward, -diff.normalized);

                    if (dotFwd > 0.2f)
                    {
                        float proxRatio = 1f - Mathf.Clamp01(dist / minSafeDist);

                        if (proxRatio > 0.85f || confidence > 0.6f)
                        {
                            _ctx.EmergencyBraking = true;
                            _ctx.DesiredSpeed = 0f;
                        }
                        else if (proxRatio > 0.4f || confidence > 0.3f)
                        {
                            float brakeFactor = Mathf.Max(
                                Mathf.InverseLerp(0.4f, 0.85f, proxRatio),
                                Mathf.InverseLerp(0.3f, 0.6f, confidence)
                            );
                            _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, _ctx.CurrentSpeed * (1f - brakeFactor * 0.6f));
                        }
                    }
                    else if (dotFwd > -0.2f && dist < minSafeDist * 0.5f)
                    {
                        _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, _ctx.CurrentSpeed * 0.85f);
                    }
                }
            }
        }
    }
}
