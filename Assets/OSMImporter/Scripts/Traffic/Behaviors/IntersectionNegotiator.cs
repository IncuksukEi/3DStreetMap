using UnityEngine;
using OSMImporter.Navigation;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Phase 6 & 7: IntersectionNegotiator — Xử lý giao lộ kiểu Việt Nam (điền vào chỗ trống, nhích mũi xe).
    /// Tính toán điểm ưu tiên (priority score) dựa trên: thế xe nhích mũi trước (nose inserted),
    /// độ lớn xe (dominance), tính quyết đoán (assertiveness), và động lượng bầy đàn (group momentum).
    /// Ngoài ra xử lý vượt đèn đỏ nhẹ (red-light creeping) và bám đuôi đám đông qua giao lộ.
    /// </summary>
    public class IntersectionNegotiator
    {
        private readonly VehicleContext _ctx;

        public IntersectionNegotiator(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            if (_ctx.Driver == null || _ctx.Profile == null) return;

            // 1. Đi chậm bò qua đèn đỏ (Red light creeping) / Chạy cố đèn vàng
            if (HandleRedLightCreeping(dt)) return;

            // 2. Thương thảo quyền đi trước tại ngã tư không đèn / đông đúc
            HandleIntersectionNegotiation(dt);
        }

        private bool HandleRedLightCreeping(float dt)
        {
            if (!_ctx.IsWaitingAtRedLight) return false;

            float law = _ctx.Driver.Lawfulness;
            float opp = _ctx.Driver.Opportunism;
            float agg = _ctx.Driver.Aggression;

            // Nếu cực kỳ chấp hành luật thì không bò đèn đỏ
            if (law > 0.8f) return false;

            bool isMoto = _ctx.IsMoto;
            float creepThreshold = isMoto ? 0.45f : 0.7f;

            // Tỷ lệ bò đèn đỏ tăng theo opp/agg và giảm theo law. Xe máy bò nhiều hơn ô tô.
            float creepScore = (opp * 0.5f + agg * 0.3f + (isMoto ? 0.25f : 0f)) * (1f - law);
            if (creepScore < creepThreshold) return false;

            float stopDist = _ctx.RedLightStopDist;
            // Chỉ bò lên khi khoảng cách tới vạch dừng còn hợp lý và không đè chết người đi bộ
            if (stopDist > 0.2f && stopDist < 8.0f)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("IntersectionNegotiator");
                d.TargetSpeed = Mathf.Lerp(0.6f, 1.6f, agg);
                d.Urgency = 0.5f;
                _ctx.Agent.Arbitrator.AddDesire(d);

                // Ghi đè trạng thái phanh cứng từ StopAtRedLight
                _ctx.IsWaitingAtRedLight = false;
                _ctx.Braking = false;
                return true;
            }

            return false;
        }

        private void HandleIntersectionNegotiation(float dt)
        {
            if (_ctx.Path == null || _ctx.PathIdx >= _ctx.Path.Count) return;

            Waypoint nextWp = _ctx.Path[_ctx.PathIdx];
            // Chỉ thương thảo khi sắp tới ngã tư (có hơn 2 nhánh rẽ hoặc là đèn giao thông)
            bool isIntersection = nextWp.ConnectedWaypointIds.Count > 2 || nextWp.IsTrafficLight;
            if (!isIntersection) return;

            Transform t = _ctx.Transform;
            float myDist = Vector3.Distance(t.position, nextWp.Position);
            if (myDist > 12f) return; // Chỉ tính toán trong phạm vi 12m tới ngã tư

            // ── TÍNH TOÁN PRIORITY SCORE ──
            // 1. Thế nhích mũi (Nose inserted): Càng sát tâm ngã tư càng được ưu tiên
            float noseInsertedBonus = (myDist < 3.5f) ? 0.5f : 0f;

            // 2. Độ lớn xe (Dominance): Xe to ép xe nhỏ (Bus > Car > Motorbike)
            float dominance = _ctx.Profile.Dominance;

            // 3. Tính quyết đoán tài xế (Assertiveness)
            float assertiveness = _ctx.Driver.Assertiveness;

            // 4. Động lượng bầy đàn (Group momentum): Nếu có nhiều xe cùng hướng đang bò qua ngã tư
            float groupMomentum = 0f;
            Collider[] nearby = Physics.OverlapSphere(nextWp.Position, 14f, _ctx.VehicleLayer);
            int friendlyCount = 0;

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent && other.Ctx != null)
                {
                    // Xe khác đang đi chung waypoint với ta
                    if (other.Ctx.PathIdx < other.Ctx.Path.Count && other.Ctx.Path[other.Ctx.PathIdx] == nextWp)
                    {
                        friendlyCount++;
                    }
                }
            }
            groupMomentum = Mathf.Min(0.35f, friendlyCount * 0.12f);

            // 5. Nguy cơ va chạm (Collision risk): Check xe cắt ngang luồng
            float collisionRisk = 0f;
            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent && other.Ctx != null)
                {
                    Vector3 toOther = other.transform.position - t.position;
                    float angle = Vector3.Angle(t.forward, other.transform.forward);

                    // Xe đi vuông góc/cắt ngang
                    if (angle > 35f && angle < 145f)
                    {
                        float otherDist = Vector3.Distance(other.transform.position, nextWp.Position);
                        if (otherDist < 8f && other.Ctx.CurrentSpeed > 0.5f)
                        {
                            float riskWeight = 1f - _ctx.Driver.RiskTolerance;
                            float currentRisk = (other.Ctx.CurrentSpeed / (otherDist + 0.1f)) * riskWeight;
                            collisionRisk = Mathf.Max(collisionRisk, currentRisk);
                        }
                    }
                }
            }

            // Công thức tổ hợp độ ưu tiên chen lách ngã tư
            float priority = noseInsertedBonus + (dominance * 0.25f) + (assertiveness * 0.35f) + groupMomentum - (collisionRisk * 0.45f);

            // ── QUYẾT ĐỊNH HÀNH VI ──
            if (priority > 0.42f)
            {
                // Độ ưu tiên cao -> Nhích mũi đi tiếp, ép xe khác nhường đường
                DrivingDesire d = DrivingDesire.CreateDefault("IntersectionNegotiator");
                d.TargetSpeed = Mathf.Lerp(1.8f, 3.6f, _ctx.Driver.Aggression);
                d.Urgency = 0.55f + _ctx.Driver.Opportunism * 0.35f;
                _ctx.Agent.Arbitrator.AddDesire(d);

                // Cho phép bỏ phanh dừng để tiếp tục nhích mũi
                _ctx.Braking = false;
            }
            else
            {
                // Độ ưu tiên thấp -> Nhường đường từ từ (creep chậm)
                DrivingDesire d = DrivingDesire.CreateDefault("IntersectionNegotiator");
                d.TargetSpeed = 0.5f;
                d.BrakeIntent = 0.5f;
                d.Urgency = 0.65f;
                _ctx.Agent.Arbitrator.AddDesire(d);
            }
        }
    }
}
