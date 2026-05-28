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

        public VehicleMover(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt, Transform[] wheels)
        {
            MoveAlongPath(dt);
            ResolveOverlap();
            SpinWheels(wheels);
        }

        // ══════════════════════════════════════════════════════════════════
        // MOVE ALONG PATH — di chuyển dọc đường với lateral offset
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
            bool hasReached = distSq < 1.0f || (distSq < 9.0f && dotPassed < 0f);

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

                    // PathIdx chỉ tăng khi WaypointRef thay đổi (điểm nội suy giữ cùng WaypointRef)
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

            // ── Tính tốc độ — Dynamic Acceleration/Deceleration ──
            float decelRate = _ctx.EmergencyBraking ? _ctx.BaseSpeed * _ctx.EmergencyBrakePwr : _ctx.BaseSpeed * 4f;
            float accelRate = _ctx.DesiredSpeed > _ctx.CurrentSpeed ? _ctx.BaseSpeed * 6f : decelRate;
            _ctx.CurrentSpeed = Mathf.MoveTowards(_ctx.CurrentSpeed, _ctx.DesiredSpeed, dt * accelRate);

            if (Mathf.Abs(_ctx.CurrentSpeed) < 0.01f) return;

            // ── Hướng di chuyển + lateral offset ──
            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;
            float shiftDiff = _ctx.OvertakeOffset - _ctx.LaneOffset;

            // Bổ sung chuyển động lắc lư (weaving) của lái xe đi ẩu / say rượu (Drunk/Reckless)
            if (_ctx.Agent != null && _ctx.Agent.Personality != null && _ctx.Agent.Personality.LaneJitter > 0.01f)
            {
                float weave = Mathf.Sin(Time.time * 2.2f) * _ctx.Agent.Personality.LaneJitter * 0.75f;
                shiftDiff += weave;
            }

            Vector3 offsetTarget = target + right * shiftDiff;

            Vector3 toOffset = offsetTarget - t.position;
            toOffset.y = 0f;
            Vector3 moveDir = toOffset.normalized;

            // ── Rotation ──
            if (moveDir.sqrMagnitude > 0.001f && _ctx.CurrentSpeed > 0f)
            {
                float rotSpeed = _ctx.RotationSpeed;
                if (toOffset.sqrMagnitude < 40f) rotSpeed *= 1.5f;

                Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
                t.rotation = Quaternion.RotateTowards(t.rotation, targetRot, rotSpeed * dt);
            }

            // ── Translation — Blend steering ──
            float speedMlt = (_ctx.CurrentSpeed >= 0f) ? 1f : -1f;
            Vector3 moveForce = t.forward * speedMlt;
            moveForce = Vector3.Lerp(moveForce, moveDir * speedMlt, 0.15f).normalized;

            Vector3 displacement = moveForce * Mathf.Abs(_ctx.CurrentSpeed) * dt;

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
            float wheelRadius = 0.75f;
            float degPerSec = _ctx.CurrentSpeed / wheelRadius * Mathf.Rad2Deg;
            foreach (var w in wheels)
                if (w != null) w.Rotate(0f, degPerSec * Time.deltaTime, 0f, Space.Self);
        }

        // ── Utility ──
        private static Vector3 WithY(Vector3 v, float y = -1f)
            => new Vector3(v.x, y < 0 ? v.y : y, v.z);
    }
}
