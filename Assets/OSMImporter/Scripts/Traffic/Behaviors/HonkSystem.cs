using UnityEngine;
using System.Collections.Generic;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Phase 8: HonkSystem — Quản lý hành vi bóp còi (honking) đặc trưng của giao thông Việt Nam.
    /// - Các tài xế thiếu kiên nhẫn (Patience thấp) hoặc xe bus to sẽ bóp còi khi bị chặn đầu.
    /// - Khi bóp còi, các xe phía trước trong bán kính ảnh hưởng sẽ nhận tín hiệu còi.
    /// - Phản ứng nhận còi: xe nhút nhát (assertiveness thấp) sẽ dạt ra nhường đường, xe nóng tính (aggression cao) sẽ còi lại.
    /// </summary>
    public class HonkSystem
    {
        private readonly VehicleContext _ctx;

        // Trạng thái còi nội bộ
        public bool IsHonking { get; private set; }
        private float _honkVisualTimer = 0f;
        private float _honkCooldown = 0f;
        private float _impatienceTimer = 0f;

        // Trạng thái khi nhận còi từ xe khác
        private VehicleAgent _lastHonker = null;
        private float _honkReactionTimer = 0f;

        public HonkSystem(VehicleContext ctx) => _ctx = ctx;

        public void Execute(float dt)
        {
            if (_ctx.Driver == null || _ctx.Profile == null) return;

            // Giảm các bộ đếm thời gian
            if (_honkCooldown > 0f) _honkCooldown -= dt;
            if (_honkVisualTimer > 0f)
            {
                _honkVisualTimer -= dt;
                if (_honkVisualTimer <= 0f) IsHonking = false;
            }

            if (_honkReactionTimer > 0f)
            {
                _honkReactionTimer -= dt;
                if (_honkReactionTimer <= 0f)
                {
                    ResolveHonkReaction();
                    _lastHonker = null;
                }
            }

            // Tính toán bóp còi chủ động
            UpdateImpatienceAndHonk(dt);
        }

        private void UpdateImpatienceAndHonk(float dt)
        {
            // Nếu không bị kẹt hoặc vẫn đi nhanh, sự ức chế giảm dần
            if (_ctx.CurrentSpeed > _ctx.BaseSpeed * 0.4f || _ctx.IsWaitingAtRedLight)
            {
                _impatienceTimer = Mathf.Max(0f, _impatienceTimer - dt * 0.5f);
                return;
            }

            // Tăng ức chế khi tốc độ chậm hoặc bị kẹt đầu
            if (_ctx.AheadVehicle != null && _ctx.AheadDistance < 5f)
            {
                // Độ ức chế tăng nhanh hơn nếu tài xế nóng nảy (Aggression cao, Patience thấp)
                float impatienceRate = (1f - _ctx.Driver.Patience) * 1.5f + _ctx.Driver.Aggression * 1.0f;
                _impatienceTimer += dt * impatienceRate;

                // Xe bus cực kỳ hay bóp còi dạt đường
                if (_ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus)
                {
                    _impatienceTimer += dt * 0.5f;
                }
            }

            // Quyết định bóp còi khi độ ức chế vượt ngưỡng kiên nhẫn
            float honkThreshold = Mathf.Lerp(1.5f, 4.0f, _ctx.Driver.Patience);
            if (_impatienceTimer > honkThreshold && _honkCooldown <= 0f)
            {
                TriggerHonk();
            }
        }

        public void TriggerHonk(bool emergency = false)
        {
            IsHonking = true;
            _honkVisualTimer = 0.5f; // Hiện hiệu ứng còi trong 0.5s
            // Cooldown còi giữa các lần bóp (tài xế nóng bóp liên tục)
            _honkCooldown = Mathf.Lerp(0.8f, 3.5f, _ctx.Driver.Patience);
            _impatienceTimer = 0f;

            // Log còi lên console hoặc debug log
            #if UNITY_EDITOR
            Debug.Log($"[HONK] {_ctx.Agent.name} ({_ctx.VehicleType}) còi inh ỏi!");
            #endif

            // Phát tán tiếng còi tới các xe phía trước
            Vector3 myPos = _ctx.Transform.position;
            Vector3 fwd = _ctx.Transform.forward;
            
            Collider[] hits = Physics.OverlapSphere(myPos + fwd * 3f, 6.5f, _ctx.VehicleLayer);
            foreach (var col in hits)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other != null && other != _ctx.Agent)
                {
                    // Chỉ những xe ở phía trước luồng còi mới nghe thấy
                    Vector3 toOther = other.transform.position - myPos;
                    if (Vector3.Dot(fwd, toOther.normalized) > 0.3f)
                    {
                        other.Honk?.ReceiveHonk(_ctx.Agent, emergency);
                    }
                }
            }
        }

        public void ReceiveHonk(VehicleAgent honker, bool emergency)
        {
            if (_ctx.Driver == null) return;

            _lastHonker = honker;
            // Phản ứng trễ 0.2s - 0.5s cho giống thực tế
            _honkReactionTimer = Random.Range(0.2f, 0.5f);
        }

        private void ResolveHonkReaction()
        {
            if (_lastHonker == null || _lastHonker.Ctx == null) return;

            float agg = _ctx.Driver.Aggression;
            float ass = _ctx.Driver.Assertiveness;

            // 1. Phản ứng 1: Bóp còi lại (Honk-back)
            // Tài xế cục cằn (Aggression cực cao) sẽ bóp còi lại thay vì nhường
            if (agg > 0.8f && _honkCooldown <= 0f)
            {
                #if UNITY_EDITOR
                Debug.Log($"[HONK-BACK] {_ctx.Agent.name} bóp còi chửi lại!");
                #endif
                TriggerHonk();
                return;
            }

            // 2. Phản ứng 2: Nhường đường (Dạt ra hoặc đi nhanh lên)
            // Phản ứng nhường tăng lên nếu Assertiveness thấp (nhút nhát)
            if (ass < 0.45f)
            {
                Transform t = _ctx.Transform;
                Vector3 toHonker = _lastHonker.transform.position - t.position;
                Vector3 localPos = t.InverseTransformPoint(_lastHonker.transform.position);

                // Nếu xe bóp còi ở phía sau ta
                if (localPos.z < 0f)
                {
                    float escapeDir = (localPos.x >= 0f) ? -1f : 1f;
                    
                    // Tạo Desire dạt sang lề để nhường đường
                    Rules.DrivingDesire d = Rules.DrivingDesire.CreateDefault("HonkSystemReaction");
                    d.TargetLateralOffset = Mathf.Clamp(_ctx.LaneOffset + escapeDir * 0.7f, -_ctx.CurrentMaxOffset, _ctx.CurrentMaxOffset);
                    d.TargetSpeed = _ctx.BaseSpeed * 1.1f; // Nhấp nhổm đi nhanh hơn một chút để tránh cản trở
                    d.Urgency = 0.7f;
                    _ctx.Agent.Arbitrator.AddDesire(d);
                }
            }
        }
    }
}
