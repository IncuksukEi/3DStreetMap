using UnityEngine;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Phase 5: MotorbikeController — Xử lý các hành vi đặc trưng của xe máy Việt Nam.
    /// Bao gồm: lách khe (lane-splitting), điền vào khoảng trống, leo lề nhẹ (nếu tính cách cho phép),
    /// chen lên đầu hàng khi chờ đèn đỏ (filtering to front), và tăng tốc giải phóng nhanh khi đèn xanh (swarm start).
    /// </summary>
    public class MotorbikeController
    {
        private readonly VehicleContext _ctx;
        private float _swarmTimer = 0f;

        public MotorbikeController(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            if (_ctx.VehicleType != VehicleMeshBuilder.VehicleType.Motorbike) return;
            if (_ctx.Driver == null || _ctx.Profile == null) return;

            // Logic swarm start: chuẩn bị xuất phát nhanh khi đèn xanh
            if (_ctx.IsWaitingAtRedLight)
            {
                _swarmTimer = 2.0f; // Sẵn sàng bốc đầu/tăng tốc nhanh khi đèn xanh lá
            }
            else if (_swarmTimer > 0f)
            {
                _swarmTimer -= dt;
            }

            // 1. Chen lên đầu hàng khi chờ đèn đỏ (Filtering to front)
            if (TryFilterToFront(dt)) return;

            // 2. Điền vào khe hở di chuyển (Lane splitting / Swarm flow)
            TryLaneSplit(dt);
        }

        private bool TryFilterToFront(float dt)
        {
            // Chỉ chen khi phía trước bị cản bởi xe đang dừng/chậm và có đèn đỏ hoặc hàng dài kẹt xe
            bool queueAhead = _ctx.IsWaitingAtRedLight || 
                              (_ctx.AheadVehicle != null && _ctx.AheadVehicle.Ctx.CurrentSpeed < 1.0f);

            if (!queueAhead) return false;

            Transform t = _ctx.Transform;
            float maxOff = _ctx.CurrentMaxOffset;
            float motorbikeWidth = _ctx.Profile.Width;
            float clearance = _ctx.Profile.MinSideClearance;

            float bestOffset = float.NaN;
            float bestProgressDist = 0f;
            float currentOffset = _ctx.OvertakeOffset;

            // Quét các lane offset từ trái sang phải để tìm hành lang trống xa nhất
            Vector3 roadDir = t.forward;
            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;

            for (float off = -maxOff; off <= maxOff; off += 0.2f)
            {
                Vector3 scanPos = t.position + right * (off - currentOffset);
                float clearDist = GetClearDistanceForward(scanPos, motorbikeWidth + clearance * 1.5f);

                // Ưu tiên các offset đi được xa nhất lên phía trước đầu hàng
                if (clearDist > 2.0f && clearDist > bestProgressDist)
                {
                    bestProgressDist = clearDist;
                    bestOffset = off;
                }
            }

            if (!float.IsNaN(bestOffset) && bestProgressDist > 3.0f)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("MotorbikeController");
                d.TargetLateralOffset = bestOffset;
                // Bò lên phía trước với tốc độ chậm (1.5m/s - 3.0m/s)
                d.TargetSpeed = Mathf.Lerp(1.5f, 3.0f, _ctx.Driver.Opportunism);
                d.Urgency = 0.7f + _ctx.Driver.Opportunism * 0.3f;
                _ctx.Agent.Arbitrator.AddDesire(d);
                return true;
            }

            return false;
        }

        private void TryLaneSplit(float dt)
        {
            if (!_ctx.Profile.CanLaneSplit) return;

            // Chỉ lách khi xe trước đi chậm hơn tốc độ bình thường của ta
            if (_ctx.AheadVehicle == null || _ctx.AheadDistance > 15f) return;

            Transform t = _ctx.Transform;
            float maxOff = _ctx.CurrentMaxOffset;
            float motorbikeWidth = _ctx.Profile.Width;
            float clearance = _ctx.Profile.MinSideClearance;

            // Swarm start tăng tốc cực nhanh
            float speedBoost = (_swarmTimer > 0f) ? 1.35f : 1.0f;
            float desiredBaseSpeed = _ctx.BaseSpeed * speedBoost;

            float currentOffset = _ctx.OvertakeOffset;
            float bestOffset = float.NaN;
            float bestClearDist = 0f;

            // Thử lách sang 2 bên của xe phía trước
            float searchOffsetWidth = _ctx.Profile.ComfortWidth * 1.2f;
            float[] testOffsets = new float[]
            {
                currentOffset - searchOffsetWidth,
                currentOffset + searchOffsetWidth,
                currentOffset - searchOffsetWidth * 0.5f,
                currentOffset + searchOffsetWidth * 0.5f
            };

            Vector3 roadDir = t.forward;
            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;

            foreach (float off in testOffsets)
            {
                if (off < -maxOff || off > maxOff) continue;

                Vector3 scanPos = t.position + right * (off - currentOffset);
                float clearDist = GetClearDistanceForward(scanPos, motorbikeWidth + clearance * 1.5f);

                if (clearDist > _ctx.AheadDistance + 1.5f && clearDist > bestClearDist)
                {
                    bestClearDist = clearDist;
                    bestOffset = off;
                }
            }

            if (!float.IsNaN(bestOffset) && bestClearDist > _ctx.AheadDistance)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("MotorbikeController");
                d.TargetLateralOffset = bestOffset;
                d.TargetSpeed = desiredBaseSpeed * (1f + _ctx.Driver.Aggression * 0.2f);
                d.Urgency = 0.5f + _ctx.Driver.Opportunism * 0.5f;
                _ctx.Agent.Arbitrator.AddDesire(d);
            }
        }

        private float GetClearDistanceForward(Vector3 originPos, float width)
        {
            float maxCheckDist = 20f;
            float startOffset = _ctx.Profile.Length * 0.5f + 0.05f;
            Vector3 origin = originPos + Vector3.up * 0.5f + _ctx.Transform.forward * startOffset;
            Vector3 fwd = _ctx.Transform.forward;
            Vector3 halfExtents = new Vector3(width * 0.5f, 0.4f, 0.1f);
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);

            if (Physics.BoxCast(origin, halfExtents, fwd, out RaycastHit hit, rot, maxCheckDist - startOffset, _ctx.VehicleLayer))
            {
                var other = hit.collider.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent)
                {
                    return hit.distance + startOffset;
                }
            }
            return maxCheckDist;
        }
    }
}
