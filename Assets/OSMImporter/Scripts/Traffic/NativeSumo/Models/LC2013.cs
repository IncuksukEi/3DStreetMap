using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic.NativeSumo.Models
{
    /// <summary>
    /// Port of SUMO's MSLCM_LC2013 (Lane-Changing Model)
    /// Reference: vendor/sumo/src/microsim/lcmodels/MSLCM_LC2013.cpp
    /// Quyết định khi nào chuyển làn, và chuyển có an toàn không.
    /// </summary>
    public class LC2013
    {
        public float pushyFactor = 0.5f; // Độ máu chiến khi chuyển làn

        /// <summary>
        /// Checking xe bên trái và phải để xem có an toàn chuyển không.
        /// (Dựa vào Krauss vSafe của cả luồng xe song song).
        /// </summary>
        public bool WantsChange(SVehicle ego, SLane targetLane, CFKrauss cfModel)
        {
            if (targetLane == null) return false;

            // 1. Phân tích TACTICAL: Làn mới có giúp tăng tốc độ không?
            float speedEgo = cfModel.FollowSpeed(ego.currentSpeed, getLeaderV(ego), getLeaderGap(ego), 1000);
            float speedTarget = cfModel.FollowSpeed(ego.currentSpeed, getLeaderVTarget(ego, targetLane), getLeaderGapTarget(ego, targetLane), 1000);

            // 2. SAFETY Check: Mình chuyển vào có làm xe đằng sau tông mình không?
            float followerV = getFollowerVTarget(ego, targetLane);
            float followerGap = getFollowerGapTarget(ego, targetLane);
            float followerSafeV = cfModel.FollowSpeed(followerV, ego.currentSpeed, followerGap, 1000);

            // Chữ lượng (Threshold) - Nếu Làn mới đi nhanh hơn làn cũ ít nhất 1m/s và không gây tai nạn cho xe sau
            if (speedTarget > speedEgo + 1.0f && followerSafeV > followerV - cfModel.decel)
            {
                return true; // Có thể chuyển
            }

            return false;
        }

        // --- Các hàm Utility mô phỏng tìm gap (Cần viết query qua SLane) ---

        private float getLeaderGap(SVehicle ego) { return 100f; /* Tạm trả về 100m, cần logic SNetwork */ }
        private float getLeaderV(SVehicle ego) { return 15f; }

        private float getLeaderGapTarget(SVehicle ego, SLane lane) { return 100f; }
        private float getLeaderVTarget(SVehicle ego, SLane lane) { return 15f; }

        private float getFollowerGapTarget(SVehicle ego, SLane lane) { return 50f; }
        private float getFollowerVTarget(SVehicle ego, SLane lane) { return 10f; }
    }
}
