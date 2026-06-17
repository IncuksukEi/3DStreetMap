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

            // ── Khử trùng lặp và tiêu thụ các waypoint đã đi qua ──
            while (_ctx.ExactPathIdx < _ctx.ExactPath.Count)
            {
                var currentTarget = _ctx.ExactPath[_ctx.ExactPathIdx];
                Vector3 targetPos = WithY(currentTarget.Position);
                Vector3 toTargetPos = targetPos - t.position;
                toTargetPos.y = 0f;

                Vector3 roadDir = t.forward;
                if (_ctx.ExactPathIdx > 0)
                {
                    Vector3 prePt = WithY(_ctx.ExactPath[_ctx.ExactPathIdx - 1].Position);
                    Vector3 delta = targetPos - prePt;
                    delta.y = 0f;
                    if (delta.sqrMagnitude > 0.01f)
                        roadDir = delta.normalized;
                }

                float distSq = toTargetPos.sqrMagnitude;
                float dotPassed = Vector3.Dot(toTargetPos, roadDir);

                float maxOff = 3.0f;
                if (currentTarget.WaypointRef != null)
                    maxOff = RoadUtility.GetMaxOffset(currentTarget.WaypointRef.RoadType);
                float limitDist = maxOff + 5.0f;
                // Tính khoảng cách chạm đích theo từng loại xe (xe máy nhỏ bám sát hơn, ô tô/bus rộng hơn)
                float reachDist = 1.6f;
                if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) reachDist = 0.8f;
                else if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) reachDist = 2.4f;

                // Kiểm tra vượt qua waypoint: chỉ bỏ qua nếu ở trong phạm vi sát sườn (tránh nhảy làn/đi nhầm đường)
                float maxOvershootDist = reachDist + 1.2f;
                bool hasReached = distSq < (reachDist * reachDist) || (distSq < (maxOvershootDist * maxOvershootDist) && dotPassed < 0f);

                // Hold khi đèn đỏ
                bool holdForRedLight = _ctx.IsWaitingAtRedLight
                    && _ctx.RedLightStopDist >= 0f && _ctx.RedLightStopDist < 2.5f;

                if (hasReached && !holdForRedLight)
                {
                    if (currentTarget.WaypointRef != null)
                        _ctx.StartNodeId = currentTarget.WaypointRef.OSMNodeId;

                    _ctx.ExactPathIdx++;

                    if (_ctx.ExactPathIdx < _ctx.ExactPath.Count)
                    {
                        var nextTarget = _ctx.ExactPath[_ctx.ExactPathIdx];
                        if (nextTarget.WaypointRef != currentTarget.WaypointRef)
                            _ctx.PathIdx = Mathf.Min(_ctx.PathIdx + 1, _ctx.Path.Count - 1);
                    }
                }
                else
                {
                    break; // Điểm này chưa đạt tới, dừng loop consume
                }
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
                return;
            }

            var activeTarget = _ctx.ExactPath[_ctx.ExactPathIdx];
            Vector3 target = WithY(activeTarget.Position);
            Vector3 toTarget = target - t.position;
            toTarget.y = 0f;

            // Hướng đoạn đường vật lý cho segment hiện tại
            Vector3 roadDirActive = t.forward;
            if (_ctx.ExactPathIdx > 0)
            {
                Vector3 prePt = WithY(_ctx.ExactPath[_ctx.ExactPathIdx - 1].Position);
                Vector3 delta = target - prePt;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.01f)
                    roadDirActive = delta.normalized;
            }
            Vector3 roadDir = roadDirActive;

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

            // ── Hướng di chuyển: chỉ áp dụng lateral shift khi đang overtake ──
            float shiftDiff = 0f;
            if (_ctx.IsOvertaking)
            {
                shiftDiff = _ctx.OvertakeOffset - _ctx.LaneOffset;
            }

            // ── Tìm điểm lookahead trên ExactPath cách xe khoảng cách tối thiểu (tỷ lệ chuẩn theo tốc độ để ôm cua khít) ──
            float lookaheadFactor = (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) ? 0.2f : 0.4f;
            float minLookahead = Mathf.Clamp(_ctx.CurrentSpeed * lookaheadFactor, 2.0f, 8.0f);
            Vector3 lookaheadTarget = target;
            int lookaheadIdx = _ctx.ExactPathIdx;
            float accDist = 0f;
            Vector3 lastPt = t.position;
            while (lookaheadIdx < _ctx.ExactPath.Count)
            {
                Vector3 ptPos = WithY(_ctx.ExactPath[lookaheadIdx].Position);
                float dist = Vector3.Distance(lastPt, ptPos);
                if (accDist + dist >= minLookahead && dist > 0.001f)
                {
                    // Nôi suy vị trí chính xác trên đoạn thẳng để lookahead không bị giật cục
                    float tFactor = (minLookahead - accDist) / dist;
                    lookaheadTarget = Vector3.Lerp(lastPt, ptPos, tFactor);
                    break;
                }
                accDist += dist;
                lastPt = ptPos;
                lookaheadIdx++;
            }
            if (lookaheadIdx >= _ctx.ExactPath.Count && _ctx.ExactPath.Count > 0)
            {
                lookaheadTarget = WithY(_ctx.ExactPath[_ctx.ExactPath.Count - 1].Position);
            }

            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;
            Vector3 offsetTarget = lookaheadTarget + right * shiftDiff;
            Vector3 toOffset = offsetTarget - t.position;
            toOffset.y = 0f;

            // ── Bám đường ổn định (thay cho mô hình xe đạp động học dễ bị lạng lách) ──
            Vector3 desiredDir = toOffset.normalized;
            if (_ctx.CurrentSpeed > 0.1f && toOffset.sqrMagnitude > 0.01f)
            {
                float turnSpeed = (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) ? 300f : 180f; // degrees per second
                float maxAngle = turnSpeed * dt;
                Vector3 newDir = Vector3.RotateTowards(t.forward, desiredDir, maxAngle * Mathf.Deg2Rad, 0f);
                t.rotation = Quaternion.LookRotation(newDir);
            }

            // Tính góc lái vô lăng chỉ để hiển thị bánh xe quay cho đẹp (Visual only)
            float angleDiff = Vector3.SignedAngle(t.forward, desiredDir, Vector3.up);
            float maxSteer = (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) ? 55f : 35f;
            float targetSteerAngle = Mathf.Clamp(angleDiff, -maxSteer, maxSteer);
            float steerSpeed = (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) ? 200f : 120f;
            _currentSteerAngle = Mathf.MoveTowards(_currentSteerAngle, targetSteerAngle, dt * steerSpeed);

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
                _ctx.CurrentSpeed = 0f; // Reset tốc độ tức thì để tránh giật cục khi mở chặn
                _ctx.Braking = true;
            }

            // Tính toán chiều cao Y khớp với đường đi
            float targetY = target.y;
            if (_ctx.ExactPathIdx > 0 && _ctx.ExactPathIdx < _ctx.ExactPath.Count)
            {
                Vector3 prevPos = WithY(_ctx.ExactPath[_ctx.ExactPathIdx - 1].Position);
                Vector3 ab = target - prevPos;
                ab.y = 0f;
                Vector3 ap = t.position - prevPos;
                ap.y = 0f;
                
                float abLenSq = ab.sqrMagnitude;
                if (abLenSq > 0.001f)
                {
                    float tFactor = Mathf.Clamp01(Vector3.Dot(ap, ab) / abLenSq);
                    targetY = Mathf.Lerp(prevPos.y, target.y, tFactor);
                }
            }

            t.position += displacement;
            t.position = WithY(t.position, targetY);
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

            // ── Bổ sung góc dốc của đường (Road Slope Pitch) ──
            float roadPitch = 0f;
            if (_ctx.ExactPath != null && _ctx.ExactPathIdx < _ctx.ExactPath.Count && _ctx.ExactPathIdx > 0)
            {
                Vector3 prevPos = _ctx.ExactPath[_ctx.ExactPathIdx - 1].Position;
                Vector3 currPos = _ctx.ExactPath[_ctx.ExactPathIdx].Position;
                Vector3 diff = currPos - prevPos;
                float horizontalDist = Mathf.Sqrt(diff.x * diff.x + diff.z * diff.z);
                if (horizontalDist > 0.1f)
                {
                    roadPitch = Mathf.Atan2(diff.y, horizontalDist) * Mathf.Rad2Deg;
                }
            }

            _bodyPivot.localRotation = Quaternion.Euler(-roadPitch + _bodyPitch, 0f, _bodyRoll);
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

                    // Nếu hai xe đang xếp hàng trước sau (nằm trên trục dọc dọc hành trình), không đẩy ngang để tránh xe bị bắn ra mép đường
                    float fwdDot = Mathf.Abs(Vector3.Dot(t.forward, diff.normalized));
                    if (fwdDot > 0.75f)
                    {
                        float penetration = (minDist - dist) / minDist;
                        float pushStrength = penetration * penetration * 1.2f + 0.2f;
                        Vector3 fwdPush = t.forward * Vector3.Dot(diff.normalized * pushStrength, t.forward);
                        totalPush += fwdPush;
                    }
                    else
                    {
                        overlapCount++;
                        float penetration = (minDist - dist) / minDist;
                        float pushStrength = penetration * penetration * 1.2f + 0.2f;
                        totalPush += diff.normalized * pushStrength;
                    }
                }
            }

            if (totalPush.sqrMagnitude > 0.0001f)
            {
                totalPush *= Time.deltaTime * 6f;
                float maxPush = 0.1f + overlapCount * 0.02f;
                if (totalPush.magnitude > maxPush)
                    totalPush = totalPush.normalized * maxPush;

                // Nếu phanh, dừng đỏ hoặc đứng yên, triệt tiêu lực đẩy tiến và giảm mạnh lực đẩy ngang để tránh xe trôi ma quái
                if (_ctx.Braking || _ctx.IsWaitingAtRedLight || _ctx.CurrentSpeed < 0.05f)
                {
                    float fwdComponent = Vector3.Dot(totalPush, t.forward);
                    if (fwdComponent > 0) totalPush -= t.forward * fwdComponent;
                    
                    Vector3 lateralPush = totalPush - t.forward * Vector3.Dot(totalPush, t.forward);
                    totalPush = t.forward * Vector3.Dot(totalPush, t.forward) + lateralPush * 0.1f;
                }

                t.position += totalPush;
                t.position = WithY(t.position, t.position.y); // Giữ nguyên chiều cao hiện tại, không đè về 0
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
