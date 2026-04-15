using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ④ OvertakeController — Ưu tiên TRUNG BÌNH.
    /// Vượt xe chậm, hỗ trợ chain overtake, kiểm tra bên hông trước khi chuyển làn.
    /// + PrepareTurn — dịch xe về đúng làn trước khi cua.
    /// </summary>
    public class OvertakeController
    {
        private const float OVERTAKE_SPEED_RATIO = 0.85f;
        private const float OVERTAKE_RANGE = 12f;
        private const float OVERTAKE_CLEAR = 6f;
        private const float CHAIN_OVERTAKE_SCAN = 20f;

        private readonly VehicleContext _ctx;

        public OvertakeController(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            PrepareTurn();
            TryOvertake(dt);
        }

        // ══════════════════════════════════════════════════════════════════
        // PREPARE TURN — dịch xe sang trái/phải trước khi rẽ
        // ══════════════════════════════════════════════════════════════════

        private void PrepareTurn()
        {
            if (_ctx.IsOvertaking || _ctx.IsWaitingAtRedLight) return;
            var path = _ctx.Path;
            int idx = _ctx.PathIdx;
            if (idx >= path.Count - 2) return;

            int lookAhead = Mathf.Min(8, path.Count - idx - 1);
            float maxTurnAngle = 0f;
            float turnSign = 0f;

            for (int i = idx; i < idx + lookAhead - 1; i++)
            {
                Vector3 d1 = path[i + 1].Position - path[i].Position;
                d1.y = 0f;
                if (d1.sqrMagnitude < 0.01f) continue;

                if (i + 2 >= path.Count) break;
                Vector3 d2 = path[i + 2].Position - path[i + 1].Position;
                d2.y = 0f;
                if (d2.sqrMagnitude < 0.01f) continue;

                float angle = Vector3.Angle(d1, d2);
                if (angle > maxTurnAngle)
                {
                    maxTurnAngle = angle;
                    turnSign = Vector3.Cross(d1.normalized, d2.normalized).y;
                }
            }

            if (maxTurnAngle < 35f) return;

            float curMax = RoadUtility.GetMaxOffset(path[idx].RoadType);
            float shiftRatio = Mathf.InverseLerp(35f, 100f, maxTurnAngle);

            if (turnSign < -0.1f)
            {
                float leftTarget = Mathf.Lerp(_ctx.LaneOffset, -curMax * 0.3f, shiftRatio);
                _ctx.TargetOvertakeOffset = Mathf.Min(_ctx.TargetOvertakeOffset, leftTarget);
            }
            else if (turnSign > 0.1f)
            {
                float rightTarget = Mathf.Lerp(_ctx.LaneOffset, curMax * 0.8f, shiftRatio * 0.5f);
                _ctx.TargetOvertakeOffset = Mathf.Max(_ctx.TargetOvertakeOffset, rightTarget);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // TRY OVERTAKE — vượt xe chậm (hỗ trợ chain overtake)
        // ══════════════════════════════════════════════════════════════════

        private void TryOvertake(float dt)
        {
            _ctx.OvertakeCooldown -= dt;
            bool isMoto = _ctx.IsMoto;

            if (_ctx.IsOvertaking)
            {
                HandleActiveOvertake(isMoto);
            }
            else
            {
                HandleOvertakeSearch(isMoto);
            }

            // Smooth lerp lateral offset
            float lerpSpeed = isMoto ? 8f : 5f;
            _ctx.OvertakeOffset = Mathf.Lerp(_ctx.OvertakeOffset, _ctx.TargetOvertakeOffset, dt * lerpSpeed);
        }

        private void HandleActiveOvertake(bool isMoto)
        {
            // Kiểm tra đã vượt xong chưa
            bool cleared = true;
            if (_ctx.OvertakingTarget != null)
            {
                Transform t = _ctx.Transform;
                Vector3 toTarget = _ctx.OvertakingTarget.transform.position - t.position;
                float distLong = Vector3.Dot(toTarget, t.forward);
                if (distLong > -OVERTAKE_CLEAR) cleared = false;
            }

            if (cleared)
            {
                if (HasSlowVehicleAheadOnOriginalLane())
                {
                    _ctx.OvertakingTarget = null;
                }
                else
                {
                    _ctx.IsOvertaking = false;
                    _ctx.OvertakingTarget = null;
                    _ctx.OvertakeSide = 0f;
                    _ctx.TargetOvertakeOffset = _ctx.LaneOffset;
                    _ctx.OvertakeCooldown = 1.0f;
                }
            }
            else
            {
                // Tăng tốc để dứt khoát vượt
                float boostMlt = isMoto ? 1.4f : 1.25f;
                _ctx.DesiredSpeed = Mathf.Max(_ctx.DesiredSpeed, _ctx.BaseSpeed * boostMlt);
            }
        }

        private void HandleOvertakeSearch(bool isMoto)
        {
            // Tự nhả về lane gốc
            if (_ctx.OvertakeCooldown <= 0f && Mathf.Abs(_ctx.TargetOvertakeOffset - _ctx.LaneOffset) > 0.1f)
                _ctx.TargetOvertakeOffset = _ctx.LaneOffset;

            // CẤM vượt khi đang chờ đèn đỏ hoặc gần ngã tư
            bool isNearIntersection = false;
            var path = _ctx.Path;
            int idx = _ctx.PathIdx;
            for (int i = idx; i < Mathf.Min(path.Count, idx + 3); i++)
            {
                if (path[i].ConnectedWaypointIds.Count > 2 || path[i].IsTrafficLight)
                {
                    isNearIntersection = true;
                    break;
                }
            }

            if (_ctx.OvertakeCooldown > 0f || _ctx.AheadVehicle == null || _ctx.AheadVehicle.Ctx == null || _ctx.IsWaitingAtRedLight || isNearIntersection)
                return;

            float overtakeRange = isMoto ? OVERTAKE_RANGE * 1.8f : OVERTAKE_RANGE;
            var ahead = _ctx.AheadVehicle;

            bool shouldOvertake = _ctx.AheadDistance < overtakeRange
                && (ahead.Ctx.CurrentSpeed < _ctx.BaseSpeed * OVERTAKE_SPEED_RATIO
                    || ahead.Ctx.CurrentSpeed < 0.5f
                    || (isMoto && ahead.Ctx.CurrentSpeed < _ctx.BaseSpeed * 0.95f));

            if (!shouldOvertake) return;

            Transform t = _ctx.Transform;
            float passWidth = isMoto ? Mathf.Max(1.0f, _ctx.VehicleWidth * 1.1f) : Mathf.Max(1.5f, _ctx.VehicleWidth * 1.2f);
            float lateralCheck = isMoto ? 1.2f : passWidth + 0.8f;
            float maxWid = _ctx.CurrentMaxOffset;

            // Kiểm tra bên TRÁI
            bool leftClear = false;
            float targetLeft = _ctx.LaneOffset - passWidth;
            if (targetLeft >= -maxWid)
            {
                leftClear = IsLateralClear(-t.right, lateralCheck);

                // Quét an toàn làn ngược chiều
                if (leftClear && targetLeft < 0.5f)
                {
                    Vector3 leftOrigin = t.position + Vector3.up * 0.5f + (-t.right * lateralCheck);
                    foreach (var h in Physics.RaycastAll(leftOrigin, t.forward, 25f, _ctx.VehicleLayer))
                    {
                        VehicleAgent colAgent = h.collider.GetComponentInParent<VehicleAgent>();
                        if (colAgent != null && colAgent != _ctx.Agent && colAgent != ahead) leftClear = false;
                    }
                }
            }

            // Kiểm tra bên PHẢI
            bool rightClear = false;
            float targetRight = _ctx.LaneOffset + passWidth;
            if (targetRight <= maxWid + 1.0f)
                rightClear = IsLateralClear(t.right, lateralCheck);

            // Ưu tiên lách trái
            if (leftClear)
            {
                _ctx.IsOvertaking = true;
                _ctx.OvertakingTarget = ahead;
                _ctx.OvertakeSide = -1f;
                _ctx.TargetOvertakeOffset = targetLeft;
            }
            else if (rightClear)
            {
                _ctx.IsOvertaking = true;
                _ctx.OvertakingTarget = ahead;
                _ctx.OvertakeSide = 1f;
                _ctx.TargetOvertakeOffset = targetRight;
            }
        }

        // ── Kiểm tra làn gốc có xe chậm chặn không (chain overtake) ──
        private bool HasSlowVehicleAheadOnOriginalLane()
        {
            Transform t = _ctx.Transform;
            Vector3 origin = t.position + Vector3.up * 0.5f;
            float halfW = Mathf.Clamp(_ctx.VehicleWidth * 0.4f, 0.3f, 1.0f);
            Vector3 halfExtents = new Vector3(halfW, 0.4f, 0.15f);

            Vector3 roadDir = t.forward;
            if (_ctx.ExactPathIdx > 0 && _ctx.ExactPathIdx < _ctx.ExactPath.Count)
            {
                Vector3 d = _ctx.ExactPath[_ctx.ExactPathIdx].Position - _ctx.ExactPath[_ctx.ExactPathIdx - 1].Position;
                d.y = 0;
                if (d.sqrMagnitude > 0.01f) roadDir = d.normalized;
            }

            RaycastHit[] hits = Physics.BoxCastAll(origin, halfExtents, roadDir, Quaternion.LookRotation(roadDir), CHAIN_OVERTAKE_SCAN, _ctx.VehicleLayer);
            foreach (var hit in hits)
            {
                var other = hit.collider.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other == _ctx.OvertakingTarget || other.Ctx == null) continue;
                if (Vector3.Dot(t.forward, other.transform.forward) < 0.3f) continue;
                if (other.Ctx.CurrentSpeed < _ctx.BaseSpeed * OVERTAKE_SPEED_RATIO || other.Ctx.CurrentSpeed < 1.0f)
                    return true;
            }
            return false;
        }

        // ── Kiểm tra an toàn bên hông (SphereCast) ──
        private bool IsLateralClear(Vector3 direction, float checkDist)
        {
            Transform t = _ctx.Transform;
            Vector3 origin = t.position + Vector3.up * 0.5f;
            float radius = Mathf.Max(0.3f, _ctx.VehicleWidth * 0.3f);

            RaycastHit[] sideHits = Physics.SphereCastAll(origin, radius, direction, checkDist, _ctx.VehicleLayer);
            foreach (var h in sideHits)
            {
                VehicleAgent other = h.collider.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent) return false;
            }

            Vector3 diagDir = (t.forward + direction).normalized;
            RaycastHit[] diagHits = Physics.SphereCastAll(origin, radius * 0.8f, diagDir, checkDist * 1.2f, _ctx.VehicleLayer);
            foreach (var h in diagHits)
            {
                VehicleAgent other = h.collider.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent) return false;
            }

            return true;
        }
    }
}
