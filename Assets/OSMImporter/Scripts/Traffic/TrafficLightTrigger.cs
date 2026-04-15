using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Gắn lên mỗi cột đèn giao thông. Xe dùng Physics query tìm component này
    /// thay vì tra waypoint graph → đơn giản, chính xác, không phụ thuộc path index.
    /// </summary>
    public class TrafficLightTrigger : MonoBehaviour
    {
        [HideInInspector] public int DirectionIndex;   // Hướng tiếp cận nào (0,1,2,3)
        [HideInInspector] public long IntersectionId;  // ID intersection trong TLM
        [HideInInspector] public Vector3 ApproachDir;  // Hướng xe tiến vào (normalized)

        /// <summary>
        /// Trạng thái hiện tại: 0=Red, 1=Green, 2=Yellow
        /// Được cập nhật mỗi frame bởi TrafficLightManager.
        /// </summary>
        [HideInInspector] public int State; // 0=Red, 1=Green, 2=Yellow

        public bool IsRed    => State == 0;
        public bool IsGreen  => State == 1;
        public bool IsYellow => State == 2;
    }
}
