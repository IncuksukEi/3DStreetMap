using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Traffic.NativeSumo.Graph
{
    /// <summary>
    /// Định nghĩa cấu trúc Mạng lưới giao thông của SUMO (đối chiếu file .net.xml)
    /// </summary>
    public class SNetwork
    {
        public Dictionary<string, SEdge> Edges = new Dictionary<string, SEdge>();
        public Dictionary<string, SNode> Nodes = new Dictionary<string, SNode>();
    }

    /// <summary>
    /// 1 Node đại diện cho 1 Ngã Tư (Junction)
    /// </summary>
    public class SNode
    {
        public string id;
        public Vector3 position;
        // Danh sách Edges đi vào / đi ra
        public List<SEdge> incoming = new List<SEdge>();
        public List<SEdge> outgoing = new List<SEdge>();
    }

    /// <summary>
    /// 1 Edge đại diện cho 1 đoạn đường (chứa nhiều chiều và 1..N Lane)
    /// </summary>
    public class SEdge
    {
        public string id;
        public SNode fromNode;
        public SNode toNode;
        
        public List<SLane> lanes = new List<SLane>();
        public float length;
        public float maxSpeed;
        
        // Danh sách các edge kế tiếp tại junction (từ connection trong .net.xml)
        public List<SEdge> successors = new List<SEdge>();
    }

    /// <summary>
    /// 1 Lane đại diện cho làn xe cụ thể mà SVehicle chạy bên trong.
    /// SUMO tracking dọc theo Lane (1D route)
    /// </summary>
    public class SLane
    {
        public string id;
        public int index; // 0 là làn sát mép lề phải, tăng dần về trục giữa
        public float length;
        public float maxSpeed;
        public SEdge parentEdge;
        
        // Mảng tọa độ thực tế dưới Unity 3D để nội suy xe
        public List<Vector3> shape = new List<Vector3>();

        // Danh sách các xe đang di chuyển trên lane này, được xếp thứ tự vị trí giảm dần (xe đầu tiên xa nhất)
        // Dùng để chạy thuật toán Krauss gap checking.
        public List<SVehicle> vehicles = new List<SVehicle>();

        /// <summary>
        /// Update cấu trúc topology nội bộ (sắp xếp xe)
        /// Tương đương MSSimulator frame loop
        /// </summary>
        public void SortVehicles()
        {
            // Xe có pos (Distance trôi dọc lane) lớn hơn sẽ nằm đầu bảng
            vehicles.Sort((a, b) => b.positionOnLane.CompareTo(a.positionOnLane));
        }

        public SLane GetLeftLane()
        {
            if (index + 1 < parentEdge.lanes.Count)
                return parentEdge.lanes[index + 1];
            return null;
        }

        public SLane GetRightLane()
        {
            if (index > 0)
                return parentEdge.lanes[index - 1];
            return null;
        }
    }
}
