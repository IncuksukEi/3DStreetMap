using UnityEngine;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Phase 8: PressureSystem — Mô phỏng áp lực không gian (sức ép từ xe to, bám đuôi, chèn hông).
    /// Các xe xung quanh tạo ra các vector áp lực:
    /// - Áp lực bám đuôi (tailgating): tăng tốc nhẹ hoặc lách sang một bên nhường đường.
    /// - Áp lực ép hông (side squeezing): đánh lái né sang hướng ngược lại để duy trì khoảng cách an toàn.
    /// - Áp lực xe lớn (bus dominance): xe bé chủ động dạt ra khi xe bus tiến gần.
    /// </summary>
    public class PressureSystem
    {
        private readonly VehicleContext _ctx;
        private const float SENSE_RADIUS = 8f;

        public PressureSystem(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            if (_ctx.Driver == null || _ctx.Profile == null) return;

            Transform t = _ctx.Transform;
            Collider[] nearby = Physics.OverlapSphere(t.position, SENSE_RADIUS, _ctx.VehicleLayer);

            float offsetDelta = 0f;
            float speedMultiplier = 1.0f;
            float maxPressure = 0f;

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;

                Vector3 toOther = other.transform.position - t.position;
                Vector3 localPos = t.InverseTransformPoint(other.transform.position);
                float dist = toOther.magnitude;

                float otherDominance = other.Ctx.Profile.Dominance;

                // 1. ÁP LỰC BÁM ĐUÔI (Tailgating pressure - từ phía sau)
                if (localPos.z < 0f && Mathf.Abs(localPos.x) < (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.6f)
                {
                    // Xe phía sau bám sát trong khoảng 5m
                    float P_tailgate = Mathf.Clamp01(1f - dist / 5f) * otherDominance;
                    if (P_tailgate > maxPressure) maxPressure = P_tailgate;

                    if (P_tailgate > 0.15f)
                    {
                        // Phản ứng 1: Tăng tốc nhẹ nếu tài xế có aggression cao
                        speedMultiplier += P_tailgate * 0.18f * _ctx.Driver.Aggression;

                        // Phản ứng 2: Dạt nhẹ sang bên để nhường (chỉ xe máy mới né nhường đường khi bị bám đuôi)
                        if (_ctx.IsMoto)
                        {
                            float escapeDirection = (localPos.x >= 0f) ? -1f : 1f;
                            offsetDelta += escapeDirection * P_tailgate * 0.45f * (1f - _ctx.Driver.Assertiveness);
                        }
                    }
                }

                // 2. ÁP LỰC ÉP HÔNG (Side squeezing pressure - hai bên sườn) - Chỉ xe máy mới đánh lái né hông, ô tô/xe buýt giữ làn đi chuẩn
                if (_ctx.IsMoto && localPos.z > -_ctx.Profile.Length * 0.6f && localPos.z < _ctx.Profile.Length * 0.6f)
                {
                    float overlapWidth = (_ctx.VehicleWidth + other.Ctx.VehicleWidth) * 0.5f;
                    float lateralClearance = Mathf.Abs(localPos.x) - overlapWidth;

                    float comfortWidth = _ctx.Profile.ComfortWidth;
                    if (lateralClearance < comfortWidth)
                    {
                        float P_squeeze = Mathf.Clamp01(1f - lateralClearance / Mathf.Max(0.1f, comfortWidth)) * otherDominance;
                        if (P_squeeze > maxPressure) maxPressure = P_squeeze;

                        if (P_squeeze > 0.2f)
                        {
                            // Né sang hướng ngược lại với nguồn ép hông
                            float pushDir = (localPos.x > 0f) ? -1f : 1f;
                            offsetDelta += pushDir * P_squeeze * 0.6f * (1f - _ctx.Driver.Assertiveness);
                        }
                    }
                }

                // 3. ÁP LỰC XE LỚN (Bus/Truck Dominance - phía trước/chéo)
                if (localPos.z > 0f && otherDominance > 0.7f && dist < 6.0f)
                {
                    float P_dominance = Mathf.Clamp01(1f - dist / 6.0f) * otherDominance;
                    if (P_dominance > maxPressure) maxPressure = P_dominance;

                    if (P_dominance > 0.3f)
                    {
                        // Chủ động dạt ra xa xe bus (chỉ xe máy mới né ngang xe bus)
                        if (_ctx.IsMoto)
                        {
                            float pushDir = (localPos.x > 0f) ? -1f : 1f;
                            offsetDelta += pushDir * P_dominance * 0.5f * (1f - _ctx.Driver.Assertiveness);
                        }

                        // Giảm tốc độ nhẹ đề phòng (áp dụng cho mọi loại xe)
                        speedMultiplier -= P_dominance * 0.15f * (1f - _ctx.Driver.RiskTolerance);
                    }
                }
            }

            if (maxPressure > 0.15f)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("PressureSystem");
                d.TargetLateralOffset = _ctx.LaneOffset + offsetDelta;
                d.TargetSpeed = _ctx.BaseSpeed * speedMultiplier;
                // Sự khẩn cấp tỷ lệ thuận với áp lực cảm nhận được và tính nhút nhát (1 - Assertiveness)
                d.Urgency = Mathf.Clamp01(maxPressure * 0.5f + (1f - _ctx.Driver.Assertiveness) * 0.5f);
                _ctx.Agent.Arbitrator.AddDesire(d);
            }
        }
    }
}
