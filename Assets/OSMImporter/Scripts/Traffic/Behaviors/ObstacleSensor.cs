using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ① ObstacleSensor — Ưu tiên CAO NHẤT
    /// Phát hiện xe phía trước (ScanAhead) + Dự đoán va chạm (PredictiveCollision).
    /// Kết quả ghi vào ctx.AheadVehicle / AheadDistance / AheadConfidence.
    /// </summary>
    public class ObstacleSensor
    {
        private const float SCAN_RANGE = 25f;
        private const float PREDICTIVE_TIME_HORIZON = 0.8f;

        private readonly VehicleContext _ctx;

        public ObstacleSensor(VehicleContext ctx) => _ctx = ctx;

        public void Execute()
        {
            _ctx.AheadVehicle = null;
            _ctx.AheadDistance = float.MaxValue;
            _ctx.AheadConfidence = 0f;

            ScanAhead();
            PredictiveCollisionCheck();
        }

        // ── Confidence Score ──
        private float CalculateObstacleConfidence(VehicleAgent other, float distance)
        {
            if (other == null || other.Ctx == null || distance <= 0f) return 0f;

            Transform t = _ctx.Transform;

            // Khoảng cách (weight 0.35)
            float distFactor = 1f - Mathf.Clamp01(distance / SCAN_RANGE);
            distFactor *= distFactor;

            // Tốc độ tương đối (weight 0.25)
            Vector3 relVel = t.forward * _ctx.CurrentSpeed - other.transform.forward * other.Ctx.CurrentSpeed;
            Vector3 toOther = (other.transform.position - t.position).normalized;
            float closingSpeed = Vector3.Dot(relVel, toOther);
            float speedFactor = Mathf.Clamp01(closingSpeed / (_ctx.BaseSpeed * 2f));

            // Góc tiếp cận (weight 0.20)
            float dotFwd = Vector3.Dot(t.forward, toOther);
            float angleFactor = Mathf.Clamp01(dotFwd);

            // Trajectory overlap (weight 0.20)
            float lateralDist = Vector3.Cross(t.forward, toOther).magnitude * distance;
            float safeLateralDist = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.9f;

            // Xe đã khác làn hoàn toàn → loại bỏ
            if (lateralDist > safeLateralDist + 0.2f) return 0f;

            float trajectoryFactor = 1f - Mathf.Clamp01(lateralDist / Mathf.Max(2f, safeLateralDist));

            return distFactor * 0.35f + speedFactor * 0.25f + angleFactor * 0.20f + trajectoryFactor * 0.20f;
        }

        // Dùng bởi SpeedController.ProximityBrake
        public float CalcConfidence(VehicleAgent other, float distance)
            => CalculateObstacleConfidence(other, distance);

        // ── ScanAhead — BoxCast thẳng phát hiện xe cùng dải ──
        private void ScanAhead()
        {
            Transform t = _ctx.Transform;
            Vector3 origin = t.position + Vector3.up * 0.5f;
            Vector3 fwd = t.forward;
            float halfW = Mathf.Clamp(_ctx.VehicleWidth * 0.55f, 0.5f, 1.5f);
            Vector3 halfExtents = new Vector3(halfW, 0.4f, 0.15f);

            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);
            RaycastHit[] hits = Physics.BoxCastAll(origin, halfExtents, fwd, rot, SCAN_RANGE, _ctx.VehicleLayer);

            foreach (var hit in hits)
            {
                var other = hit.collider.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;
                if (_ctx.IsOvertaking && other == _ctx.OvertakingTarget) continue;

                float confidence = CalculateObstacleConfidence(other, hit.distance);
                if (confidence < 0.10f) continue;

                // Xe đi ngược chiều
                bool isOncoming = Vector3.Dot(t.forward, other.transform.forward) < -0.2f;
                if (isOncoming)
                {
                    HandleOncoming(other, hit.distance, t);
                    if (!_ctx.IsOvertaking) continue;
                }

                // Chọn xe confidence cao nhất
                if (confidence > _ctx.AheadConfidence)
                {
                    _ctx.AheadDistance = hit.distance;
                    _ctx.AheadVehicle = other;
                    _ctx.AheadConfidence = confidence;
                }
            }
        }

        // ── Xử lý xe ngược chiều (dạt phải nhường) ──
        private void HandleOncoming(VehicleAgent other, float hitDistance, Transform t)
        {
            // Khoảng cách ngang chính xác (dùng right vector thay vì cross product)
            Vector3 toOther = other.transform.position - t.position;
            toOther.y = 0f;
            float latDist = Mathf.Abs(Vector3.Dot(toOther, t.right));
            float combinedWidth = (_ctx.VehicleWidth + other.Ctx.VehicleWidth);

            if (_ctx.OvertakeOffset < 1.5f && hitDistance < 20f
                && latDist < combinedWidth * 0.7f)
            {
                float curMax = _ctx.CurrentMaxOffset;

                if (other.Ctx.VehicleWidth > _ctx.VehicleWidth + 0.3f)
                {
                    _ctx.TargetOvertakeOffset = curMax + 0.8f;
                    if (hitDistance < 10f) _ctx.ReversingTimer = 1.0f;
                    else _ctx.Braking = true;
                }
                else if (hitDistance < 8f)
                {
                    if (_ctx.Agent.GetInstanceID() < other.GetInstanceID())
                    {
                        _ctx.TargetOvertakeOffset = curMax + 0.5f;
                        _ctx.ReversingTimer = 1.0f;
                    }
                    else
                    {
                        _ctx.TargetOvertakeOffset = Mathf.Min(1.2f, curMax);
                        _ctx.Braking = true;
                    }
                }
                else
                {
                    _ctx.TargetOvertakeOffset = Mathf.Min(1.2f, curMax);
                }

                _ctx.IsOvertaking = false;
                _ctx.OvertakeCooldown = 1.5f;
            }
        }

        // ── PredictiveCollisionCheck — CPA (Closest Point of Approach) ──
        private void PredictiveCollisionCheck()
        {
            if (_ctx.CurrentSpeed < 0.5f) return;

            Transform t = _ctx.Transform;
            float checkRadius = Mathf.Max(SCAN_RANGE, _ctx.CurrentSpeed * PREDICTIVE_TIME_HORIZON * 1.2f);
            Collider[] nearby = Physics.OverlapSphere(t.position, checkRadius, _ctx.VehicleLayer);

            Vector3 myPos = t.position;
            Vector3 myVel = t.forward * _ctx.CurrentSpeed;

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;
                if (_ctx.IsOvertaking && other == _ctx.OvertakingTarget) continue;
                if (other.Ctx.CurrentSpeed < 0.1f && Vector3.Dot(t.forward, (other.transform.position - myPos).normalized) < 0.3f) continue;

                Vector3 otherPos = other.transform.position;
                Vector3 otherVel = other.transform.forward * other.Ctx.CurrentSpeed;

                // Xe ngược chiều khác làn → bỏ qua hoàn toàn
                float dotDir = Vector3.Dot(t.forward, other.transform.forward);
                if (dotDir < -0.2f)
                {
                    // Tính lateral distance (khoảng cách vuông góc với hướng đi)
                    Vector3 toOther = otherPos - myPos;
                    toOther.y = 0f;
                    float lateralDist = Mathf.Abs(Vector3.Dot(toOther, t.right));
                    float combinedWidth = (_ctx.VehicleWidth + other.Ctx.VehicleWidth);

                    // Nếu xe ngược chiều cách xa hơn tổng chiều rộng → khác làn rõ ràng, bỏ qua
                    if (lateralDist > combinedWidth * 0.8f) continue;
                }

                Vector3 dPos = otherPos - myPos;
                Vector3 dVel = otherVel - myVel;
                float dVelSq = dVel.sqrMagnitude;
                if (dVelSq < 0.01f) continue;

                float tCPA = -Vector3.Dot(dPos, dVel) / dVelSq;
                tCPA = Mathf.Clamp(tCPA, 0f, PREDICTIVE_TIME_HORIZON);

                Vector3 myFuturePos = myPos + myVel * tCPA;
                Vector3 otherFuturePos = otherPos + otherVel * tCPA;
                float futureDistSq = (myFuturePos - otherFuturePos).sqrMagnitude;

                float safeRadius = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 1.0f + 1.5f;
                float safeRadiusSq = safeRadius * safeRadius;

                if (futureDistSq < safeRadiusSq)
                {
                    float urgency = 1f - Mathf.Clamp01(tCPA / PREDICTIVE_TIME_HORIZON);
                    float futureDist = Mathf.Sqrt(futureDistSq);
                    float penetration = 1f - Mathf.Clamp01(futureDist / safeRadius);
                    float severity = urgency * 0.6f + penetration * 0.4f;

                    // Xe cùng chiều gần → giảm severity (đã handle bởi IDM)
                    if (dotDir > 0.8f) severity *= 0.5f;

                    // Xe ngược chiều nhưng có lateral gap → giảm severity mạnh
                    if (dotDir < -0.2f)
                    {
                        Vector3 futureToOther = otherFuturePos - myFuturePos;
                        futureToOther.y = 0f;
                        float futureLateral = Mathf.Abs(Vector3.Dot(futureToOther, t.right));
                        float combinedW = (_ctx.VehicleWidth + other.Ctx.VehicleWidth);

                        // Lateral >= 60% chiều rộng tổng → khả năng cao khác làn
                        if (futureLateral > combinedW * 0.6f)
                            severity *= Mathf.Clamp01(1f - futureLateral / (combinedW * 1.2f));
                    }

                    if (severity > 0.8f)
                    {
                        _ctx.EmergencyBraking = true;
                        _ctx.DesiredSpeed = 0f;
                    }
                    else if (severity > 0.4f)
                    {
                        float brakeFactor = Mathf.InverseLerp(0.4f, 0.8f, severity);
                        _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, _ctx.CurrentSpeed * (1f - brakeFactor * 0.6f));
                    }

                    float currentDist = Vector3.Distance(myPos, otherPos);
                    if (severity > _ctx.AheadConfidence && Vector3.Dot(t.forward, (otherPos - myPos).normalized) > 0.1f)
                    {
                        _ctx.AheadDistance = currentDist;
                        _ctx.AheadVehicle = other;
                        _ctx.AheadConfidence = severity;
                    }
                }
            }
        }
    }
}
