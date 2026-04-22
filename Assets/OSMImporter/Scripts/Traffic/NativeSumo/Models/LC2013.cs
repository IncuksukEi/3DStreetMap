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
        public float pushyFactor = 0.5f;

        /// <summary>
        /// Checking xe bên trái và phải để xem có an toàn chuyển không.
        /// (Dựa vào Krauss vSafe của cả luồng xe song song).
        /// </summary>
        public bool WantsChange(SVehicle ego, SLane targetLane, CFKrauss cfModel)
        {
            if (targetLane == null) return false;

            // 1. Phân tích TACTICAL: Làn mới có giúp tăng tốc độ không?
            float leaderV = FindLeaderSpeed(ego, ego.currentLane);
            float leaderGap = FindLeaderGap(ego, ego.currentLane);
            float speedEgo = cfModel.FollowSpeed(ego.currentSpeed, leaderV, leaderGap, 1000);

            float targetLeaderV = FindLeaderSpeed(ego, targetLane);
            float targetLeaderGap = FindLeaderGap(ego, targetLane);
            float speedTarget = cfModel.FollowSpeed(ego.currentSpeed, targetLeaderV, targetLeaderGap, 1000);

            // 2. SAFETY Check: Mình chuyển vào có làm xe đằng sau tông mình không?
            float followerV = FindFollowerSpeed(ego, targetLane);
            float followerGap = FindFollowerGap(ego, targetLane);
            float followerSafeV = cfModel.FollowSpeed(followerV, ego.currentSpeed, followerGap, 1000);

            // Threshold: Làn mới nhanh hơn ít nhất 1m/s và không gây tai nạn cho xe sau
            if (speedTarget > speedEgo + 1.0f && followerSafeV > followerV - cfModel.decel)
                return true;

            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        // REAL GAP QUERIES — quét lane.vehicles list
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Tìm tốc độ xe phía trước trên lane chỉ định</summary>
        private float FindLeaderSpeed(SVehicle ego, SLane lane)
        {
            if (lane == null || lane.vehicles.Count == 0) return ego.maxSpeed;

            // lane.vehicles đã sorted: index 0 = xe xa nhất (pos lớn nhất)
            for (int i = 0; i < lane.vehicles.Count; i++)
            {
                var v = lane.vehicles[i];
                if (v == ego) continue;
                if (v.positionOnLane > ego.positionOnLane)
                    return v.currentSpeed;
            }
            return ego.maxSpeed; // Không có xe trước → tốc độ tối đa
        }

        /// <summary>Tìm khoảng cách tới xe phía trước trên lane chỉ định</summary>
        private float FindLeaderGap(SVehicle ego, SLane lane)
        {
            if (lane == null || lane.vehicles.Count == 0) return 9999f;

            float minGap = 9999f;
            for (int i = 0; i < lane.vehicles.Count; i++)
            {
                var v = lane.vehicles[i];
                if (v == ego) continue;
                if (v.positionOnLane > ego.positionOnLane)
                {
                    float gap = (v.positionOnLane - v.length) - ego.positionOnLane;
                    if (gap < minGap) minGap = gap;
                }
            }
            return minGap;
        }

        /// <summary>Tìm tốc độ xe phía sau trên lane đích</summary>
        private float FindFollowerSpeed(SVehicle ego, SLane targetLane)
        {
            if (targetLane == null || targetLane.vehicles.Count == 0) return 0f;

            float closestDist = float.MaxValue;
            float followerSpeed = 0f;

            foreach (var v in targetLane.vehicles)
            {
                if (v.positionOnLane < ego.positionOnLane)
                {
                    float dist = ego.positionOnLane - v.positionOnLane;
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        followerSpeed = v.currentSpeed;
                    }
                }
            }
            return followerSpeed;
        }

        /// <summary>Tìm khoảng cách tới xe phía sau trên lane đích</summary>
        private float FindFollowerGap(SVehicle ego, SLane targetLane)
        {
            if (targetLane == null || targetLane.vehicles.Count == 0) return 9999f;

            float closestGap = 9999f;
            foreach (var v in targetLane.vehicles)
            {
                if (v.positionOnLane < ego.positionOnLane)
                {
                    float gap = (ego.positionOnLane - ego.length) - v.positionOnLane;
                    if (gap < closestGap) closestGap = gap;
                }
            }
            return closestGap;
        }
    }
}
