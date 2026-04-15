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
        public float length = 4.0f; // Chiều dài xe
        
        // Tọa độ 1D (Tiến độ hoàn thành trên Lane)
        public float positionOnLane = 0f;
        public float currentSpeed = 0f;
        
        public SLane currentLane;
        
        // Route path dự định đi
        public Queue<SEdge> route = new Queue<SEdge>();
        
        // Transform thật 3D (nơi Object Unity render)
        public GameObject rendererObject;

        /// <summary>
        /// Ánh xạ 1D sang 3D
        /// </summary>
        public Vector3 Get3DPosition()
        {
            // Tạm thời chưa code nội suy spline, trả về rỗng.
            return Vector3.zero;
        }
    }
}
