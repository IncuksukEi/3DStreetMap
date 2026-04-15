using UnityEngine;

namespace OSMImporter.Traffic.NativeSumo.Models
{
    /// <summary>
    /// Port of SUMO's MSCFModel_Krauss (Krauss Car-Following Model)
    /// Reference: vendor/sumo/src/microsim/cfmodels/MSCFModel_Krauss.cpp
    /// </summary>
    public class CFKrauss
    {
        // Tham số Krauss mặc định (tương tự vType mặc định của SUMO)
        public float accel = 2.6f;        // Gia tốc max (m/s^2)
        public float decel = 4.5f;        // Giảm tốc max (m/s^2)
        public float tau = 1.0f;          // Thời gian phản xạ (Reaction time - s)
        public float maxSpeed = 30.0f;    // Vận tốc max của loại xe này (m/s)

        public CFKrauss(float a = 2.6f, float d = 4.5f, float t = 1.0f, float maxV = 30.0f)
        {
            accel = a;
            decel = d;
            tau = t;
            maxSpeed = maxV;
        }

        /// <summary>
        /// Tính toán vận tốc tiếp theo đảm bảo không đâm vào xe phía trước.
        /// </summary>
        /// <param name="v">Vận tốc hiện tại (m/s)</param>
        /// <param name="v_leader">Vận tốc xe đi trước (m/s)</param>
        /// <param name="gap">Khoảng cách từ cản trước xe mình đến cản sau xe trước (m)</param>
        /// <param name="stepMillis">Độ dài 1 tick (thường 1000ms)</param>
        /// <returns>Vận tốc an toàn tiếp theo</returns>
        public float FollowSpeed(float v, float v_leader, float gap, int stepMillis)
        {
            float ts = (float)stepMillis / 1000f; // Chuyển ms sang giây
            
            // 1. Nếu không có khoảng cách an toàn, buộc phải dừng
            if (gap <= 0) return 0f;

            // 2. Tính khoảng cách dừng lại an toàn của xe phía trước
            // Dựa trên công thức gia tốc: v^2 = u^2 + 2as -> s = v^2 / 2a
            float dLeader = (v_leader * v_leader) / (2f * decel);
            
            // 3. Khoảng cách mình có thể tiếp tục di chuyển trước khi đâm
            float dAvailable = gap + dLeader;
            
            // 4. Tính vận tốc mục tiêu vSafe sử dụng phương trình Euler-Krauss 
            // Giả định xe này có thể dừng lại kịp thời với mức giảm tốc b (decel)
            // v_safe * tau + v_safe^2 / (2b) = dAvailable
            
            // Nghiệm phương trình bậc 2: v = -b_tau + sqrt((b_tau)^2 + 2*b*dAvailable)
            float b_tau = decel * tau;
            
            float safeDist = dAvailable;
            if (safeDist < 0) safeDist = 0; // Đề phòng lỗi dấu phẩy động
            
            float vSafe = -b_tau + Mathf.Sqrt(b_tau * b_tau + 2f * decel * safeDist);
            
            // Tính V tự do (tăng tốc tối đa)
            float vMaxAcc = v + accel * ts;

            // Vận tốc thực tế là con số nhỏ nhất giữa 3 yếu tố: V_safe, V tăng tốc tối đa, và MaxSpeed của đường
            return Mathf.Min(Mathf.Min(vMaxAcc, maxSpeed), vSafe);
        }

        /// <summary>
        /// Update tốc độ gốc (trả về V sau khi áp dụng vận tốc cho phép)
        /// Đây là điểm bắt đầu của 1 xe trong 1 Tick.
        /// </summary>
        public float FinalSpeed(float vSafe, float dt)
        {
            // Nếu SUMO có dawdle (độ trễ chú ý), sẽ trừ bớt random ở đây. Tạm thời bỏ qua.
            return Mathf.Max(0.0f, vSafe);
        }
    }
}
