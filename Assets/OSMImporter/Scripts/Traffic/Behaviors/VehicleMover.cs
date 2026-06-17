using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ⑥ VehicleMover — Ưu tiên TRUNG BÌNH.
    /// MoveAlongPath + Rotation + Pre-move collision check + ResolveOverlap + SpinWheels.
    /// </summary>
    public class VehicleMover
    {
        private readonly VehicleContext _ctx;

        // Visual pivot references
        private Transform _steerPivotL;
        private Transform _steerPivotR;
        private Transform _bodyPivot;
        private bool _pivotsInitialized;

        // Kinematics and suspension states
        private float _currentSteerAngle = 0f;
        private float _prevSpeed = 0f;
        private float _bodyPitch = 0f;
        private float _bodyRoll = 0f;

        public VehicleMover(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt, Transform[] wheels)
        {
            InitializePivots();
            MoveAlongPath(dt);
            ResolveOverlap();
            SpinWheels(wheels);
            SteerWheels();
            UpdateBodySuspension(dt);
        }

        private void InitializePivots()
        {
            if (_pivotsInitialized) return;
            _pivotsInitialized = true;

            Transform t = _ctx.Transform;
            if (t != null)
            {
                _steerPivotL = t.Find("SteerPivot_FL");
                _steerPivotR = t.Find("SteerPivot_FR");
                _bodyPivot = t.Find("BodyPivot");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // MOVE ALONG PATH — di chuyển dọc đường với Kinematic Bicycle Model
        // ══════════════════════════════════════════════════════════════════

        private void MoveAlongPath(float dt)
        {
            if (_ctx.ExactPathIdx >= _ctx.ExactPath.Count) return;

            Transform t = _ctx.Transform;
            var currentTarget = _ctx.ExactPath[_ctx.ExactPathIdx];
            Vector3 target = WithY(currentTarget.Position);
            Vector3 toTarget = target - t.position;
            toTarget.y = 0f;

            // Hướng đoạn đường vật lý
            Vector3 roadDir = t.forward;
            if (_ctx.ExactPathIdx > 0)
            {
                Vector3 prePt = WithY(_ctx.ExactPath[_ctx.ExactPathIdx - 1].Position);
                Vector3 delta = target - prePt;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.01f)
                    roadDir = delta.normalized;
            }

            float distSq = toTarget.sqrMagnitude;
            float dotPassed = Vector3.Dot(toTarget, roadDir);

            // ── Reached waypoint ──
            bool hasReached = distSq < 1.5f || dotPassed < 0f;

            if (hasReached)
            {
                // Hold khi đèn đỏ
                bool holdForRedLight = _ctx.IsWaitingAtRedLight
                    && _ctx.RedLightStopDist >= 0f && _ctx.RedLightStopDist < 2.5f;

                if (holdForRedLight)
                {
                    // Đứng tại vạch, không consume waypoint
                }
                else
                {
                    if (currentTarget.WaypointRef != null)
                        _ctx.StartNodeId = currentTarget.WaypointRef.OSMNodeId;

                    _ctx.ExactPathIdx++;

                    // PathIdx chỉ tăng khi WaypointRef thay đổi
                    if (_ctx.ExactPathIdx < _ctx.ExactPath.Count)
                    {
                        var nextTarget = _ctx.ExactPath[_ctx.ExactPathIdx];
                        if (nextTarget.WaypointRef != currentTarget.WaypointRef)
                            _ctx.PathIdx = Mathf.Min(_ctx.PathIdx + 1, _ctx.Path.Count - 1);
                    }

                    if (_ctx.ExactPathIdx >= _ctx.ExactPath.Count)
                    {
                        _ctx.Path.Clear();

                        if (_ctx.DestroyOnArrival)
                        {
                            Object.Destroy(_ctx.Agent.gameObject);
                            return;
                        }

                        _ctx.Agent.StartCoroutine(_ctx.Agent.WaitThenReroute(Random.Range(0.2f, 1.0f)));
                    }
                    return;
                }
            }

            // ── Tính tốc độ — Dynamic Acceleration/Deceleration with Inertia ──
            float timeConstant = 0.8f; // Phản hồi tăng tốc
            if (_ctx.EmergencyBraking)
            {
                timeConstant = 0.15f; // Phanh khẩn cấp cực nhanh
            }
            else if (_ctx.DesiredSpeed < _ctx.CurrentSpeed)
            {
                timeConstant = 0.45f; // Phanh thường nhanh hơn tăng tốc
            }

            float speedDiff = _ctx.DesiredSpeed - _ctx.CurrentSpeed;
            _ctx.CurrentSpeed += (speedDiff / timeConstant) * dt;

            // Giới hạn để tránh overshoot
            if (_ctx.DesiredSpeed > _ctx.CurrentSpeed)
                _ctx.CurrentSpeed = Mathf.Min(_ctx.CurrentSpeed, _ctx.DesiredSpeed);
            else
                _ctx.CurrentSpeed = Mathf.Max(_ctx.CurrentSpeed, _ctx.DesiredSpeed);

            if (Mathf.Abs(_ctx.CurrentSpeed) < 0.01f)
            {
                _ctx.CurrentSpeed = 0f;
                return;
            }

            // ── Hướng di chuyển + lateral offset ──
            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;
            float shiftDiff = _ctx.OvertakeOffset - _ctx.LaneOffset;

            // Bổ sung chuyển động lắc lư (weaving) - Chỉ áp dụng cho xe máy (Motorbike), ô tô và xe buýt phải đi thẳng hàng chuẩn làn
            if (_ctx.IsMoto && _ctx.Agent != null && _ctx.Agent.Personality != null && _ctx.Agent.Personality.LaneJitter > 0.01f)
            {
                float weave = Mathf.Sin(Time.time * 2.2f) * _ctx.Agent.Personality.LaneJitter * 0.75f;
                shiftDiff += weave;
            }

            Vector3 offsetTarget = target + right * shiftDiff;
            Vector3 toOffset = offsetTarget - t.position;
            toOffset.y = 0f;

            // ── Mô hình Lái Xe Đạp Động Học (Kinematic Bicycle Model) ──
            float length = 1.1f;
            if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) length = 2.5f;
            else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) length = 0.55f;
            float wheelbase = length * 0.66f;

            // Chuyển đích đến về hệ tọa độ cục bộ của xe
            Vector3 localTarget = t.InverseTransformPoint(offsetTarget);

            // Tính góc lái mong muốn: delta = arctan(2 * L * x / d^2)
            float distToTarget = toOffset.magnitude;
            float targetSteerAngle = 0f;
            if (distToTarget > 0.1f)
            {
                float steerRad = Mathf.Atan2(2f * wheelbase * localTarget.x, toOffset.sqrMagnitude);
                targetSteerAngle = Mathf.Clamp(steerRad * Mathf.Rad2Deg, -35f, 35f);
            }

            // Tốc độ bẻ lái vô lăng
            float steerSpeed = 120f;
            _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, dt * steerSpeed);

            // Tính vận tốc góc (yaw rate = v * tan(steer) / L)
            float steerRadActual = _currentSteerAngle * Mathf.Deg2Rad;
            float yawRate = 0f;
            if (Mathf.Abs(steerRadActual) > 0.001f)
            {
                yawRate = (_ctx.CurrentSpeed / wheelbase) * Mathf.Tan(steerRadActual);
            }

            // Xoay xe
            t.Rotate(0f, yawRate * Mathf.Rad2Deg * dt, 0f);

            // Di chuyển thuần forward (tiến hoặc lùi)
            Vector3 displacement = t.forward * _ctx.CurrentSpeed * dt;

            // Pre-move collision check
            Vector3 newPos = t.position + displacement;
            float preCheckRadius = _ctx.VehicleWidth * 0.5f;
            bool blocked = false;

            Collider[] preHits = Physics.OverlapSphere(newPos, preCheckRadius, _ctx.VehicleLayer);
            foreach (var ph in preHits)
            {
                VehicleAgent pOther = ph.GetComponentInParent<VehicleAgent>();
                if (pOther == null || pOther == _ctx.Agent || pOther.Ctx == null) continue;

                Vector3 toDiff = pOther.transform.position - newPos;
                toDiff.y = 0f;
                float toDist = toDiff.magnitude;
                float minGap = (_ctx.VehicleWidth + pOther.Ctx.VehicleWidth) * 0.45f;

                if (toDist < minGap)
                {
                    float dotDir2 = Vector3.Dot(t.forward, toDiff.normalized);

                    if (dotDir2 > 0.3f)
                    {
                        blocked = true;
                        break;
                    }
                }
            }

            if (blocked)
            {
                displacement = Vector3.zero;
                _ctx.DesiredSpeed = 0f;
                _ctx.Braking = true;
            }

            t.position += displacement;
            t.position = WithY(t.position, 0f);
        }

        // ── Steering Wheels ──
        private void SteerWheels()
        {
            if (_steerPivotL != null)
                _steerPivotL.localRotation = Quaternion.Euler(0f, _currentSteerAngle, 0f);
            if (_steerPivotR != null)
                _steerPivotR.localRotation = Quaternion.Euler(0f, _currentSteerAngle, 0f);
        }

        // ── Body Suspensions (Pitch & Roll) ──
        private void UpdateBodySuspension(float dt)
        {
            if (_bodyPivot == null || dt < 0.001f) return;

            // 1. Tính Pitch (chúc mũi khi phanh, ngóc lên khi ga)
            float accel = (_ctx.CurrentSpeed - _prevSpeed) / dt;
            _prevSpeed = _ctx.CurrentSpeed;

            // Lọc các đột biến do teleport hoặc khựng lại quá nhanh
            accel = Mathf.Clamp(accel, -15f, 15f);

            // Phanh gấp -> pitch âm (chúc đầu).
            float pitchTarget = accel * 1.2f; 
            pitchTarget = Mathf.Clamp(pitchTarget, -8f, 8f);

            // 2. Tính Roll (nghiêng khi cua ly tâm)
            float length = 1.1f;
            if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) length = 2.5f;
            else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) length = 0.55f;
            float wheelbase = length * 0.66f;

            float steerRadActual = _currentSteerAngle * Mathf.Deg2Rad;
            float yawRate = (_ctx.CurrentSpeed / wheelbase) * Mathf.Tan(steerRadActual);
            float centrifugalAcc = _ctx.CurrentSpeed * yawRate;

            // Nghiêng ngược hướng cua
            float rollTarget = -centrifugalAcc * 2.2f;
            rollTarget = Mathf.Clamp(rollTarget, -12f, 12f);

            // Smooth góc nhún nghiêng
            _bodyPitch = Mathf.MoveTowards(_bodyPitch, pitchTarget, dt * 25f);
            _bodyRoll = Mathf.MoveTowards(_bodyRoll, rollTarget, dt * 35f);

            _bodyPivot.localRotation = Quaternion.Euler(_bodyPitch, 0f, _bodyRoll);
        }

        // ══════════════════════════════════════════════════════════════════
        // RESOLVE OVERLAP — đẩy xe ra khi chồng lên nhau (soft collision)
        // ══════════════════════════════════════════════════════════════════

        private void ResolveOverlap()
        {
            Transform t = _ctx.Transform;
            float checkRadius = Mathf.Max(3f, _ctx.VehicleWidth * 3.0f);
            Collider[] nearby = Physics.OverlapSphere(t.position, checkRadius, _ctx.VehicleLayer);

            Vector3 totalPush = Vector3.zero;
            int overlapCount = 0;
            _ctx.IsColliding = false;

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;

                Vector3 diff = t.position - other.transform.position;
                diff.y = 0f;
                float dist = diff.magnitude;

                float minDist = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.8f + 0.5f;
                float realCollisionDist = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.55f;

                if (dist < minDist && dist > 0.001f)
                {
                    if (dist < realCollisionDist)
                        _ctx.IsColliding = true;

                    overlapCount++;
                    float penetration = (minDist - dist) / minDist;
                    float pushStrength = penetration * penetration * 1.2f + 0.2f;
                    totalPush += diff.normalized * pushStrength;
                }
            }

            if (totalPush.sqrMagnitude > 0.0001f)
            {
                totalPush *= Time.deltaTime * 6f;
                float maxPush = 0.1f + overlapCount * 0.02f;
                if (totalPush.magnitude > maxPush)
                    totalPush = totalPush.normalized * maxPush;

                if ((_ctx.Braking || _ctx.IsWaitingAtRedLight) && _ctx.StuckTimer < 1.0f)
                {
                    float fwdComponent = Vector3.Dot(totalPush, t.forward);
                    if (fwdComponent > 0) totalPush -= t.forward * (fwdComponent * 0.8f);
                }

                t.position += totalPush;
                t.position = WithY(t.position, 0f);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // WHEEL SPIN
        // ══════════════════════════════════════════════════════════════════

        private void SpinWheels(Transform[] wheels)
        {
            if (wheels == null || wheels.Length == 0) return;
            
            float wheelRadius = 0.11f;
            if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) wheelRadius = 0.15f;
            else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) wheelRadius = 0.1f;
            
            float degPerSec = _ctx.CurrentSpeed / wheelRadius * Mathf.Rad2Deg;
            foreach (var w in wheels)
                if (w != null) w.Rotate(0f, degPerSec * Time.deltaTime, 0f, Space.Self);
        }

        // ── Utility ──
        private static Vector3 WithY(Vector3 v, float y = -1f)
            => new Vector3(v.x, y < 0 ? v.y : y, v.z);
    }
}
