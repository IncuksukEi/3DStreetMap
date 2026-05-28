using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Traffic.NativeSumo.Graph
{
    /// <summary>
    /// Đại diện cho MSVehicle trong SUMO.
    /// Giữ state của từng phương tiện.
    /// </summary>
    public class SVehicle
    {
        public string id;
        public float maxSpeed = 30f;
        public float length = 4.0f;
        public float width = 1.8f;
        public string vehicleType = "car";
        
        // Tọa độ 1D (Tiến độ hoàn thành trên Lane)
        public float positionOnLane = 0f;
        public float currentSpeed = 0f;
        
        public SLane currentLane;
        public SEdge currentEdge; // Edge hiện tại (redundant với currentLane.parentEdge nhưng tiện)
        
        // Route path dự định đi
        public Queue<SEdge> route = new Queue<SEdge>();
        public int routeEdgeIndex = 0;
        public bool isFinished = false;
        
        // Transform thật 3D (nơi Object Unity render)
        public GameObject rendererObject;
        public Color color;

        // Cached positions cho sub-frame interpolation (SimulationEngine ghi)
        public Vector3 prevPosition;
        public Vector3 targetPosition;

        /// <summary>
        /// Ánh xạ 1D (positionOnLane) sang tọa độ 3D dọc theo shape polyline của lane
        /// </summary>
        public Vector3 Get3DPosition()
        {
            if (currentLane == null || currentLane.shape == null || currentLane.shape.Count < 2)
                return Vector3.zero;

            var shape = currentLane.shape;
            float remaining = positionOnLane;

            // Duyệt từng segment, trừ dần distance cho đến khi tìm đúng segment chứa vị trí
            for (int i = 0; i < shape.Count - 1; i++)
            {
                float segLen = Vector3.Distance(shape[i], shape[i + 1]);
                if (segLen <= 0.001f) continue;

                if (remaining <= segLen)
                {
                    // Nội suy trên segment này
                    float t = remaining / segLen;
                    return Vector3.Lerp(shape[i], shape[i + 1], t);
                }
                remaining -= segLen;
            }

            // Nếu vượt quá polyline, trả về điểm cuối
            return shape[shape.Count - 1];
        }

        /// <summary>
        /// Lấy hướng tiếp tuyến tại vị trí hiện tại (dùng để xoay xe)
        /// </summary>
        public Vector3 GetForwardDirection()
        {
            if (currentLane == null || currentLane.shape == null || currentLane.shape.Count < 2)
                return Vector3.forward;

            var shape = currentLane.shape;
            float remaining = positionOnLane;

            for (int i = 0; i < shape.Count - 1; i++)
            {
                float segLen = Vector3.Distance(shape[i], shape[i + 1]);
                if (segLen <= 0.001f) continue;

                if (remaining <= segLen)
                    return (shape[i + 1] - shape[i]).normalized;

                remaining -= segLen;
            }

            // Segment cuối
            return (shape[shape.Count - 1] - shape[shape.Count - 2]).normalized;
        }
    }
}
