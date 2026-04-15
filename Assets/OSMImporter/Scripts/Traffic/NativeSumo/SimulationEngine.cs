using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Traffic.NativeSumo.Models;

namespace OSMImporter.Traffic.NativeSumo
{
    /// <summary>
    /// Vòng lặp giả lập chính (Mô phỏng lại MSEdgeControl::planMovements)
    /// Đảm bảo tính toán Lane-changing -> Krauss Car-Following đồng bộ giữa các xe trước khi Update Pos.
    /// </summary>
    public class SimulationEngine : MonoBehaviour
    {
        public float stepLength = 0.5f; // SUMO Mặc định 1.0, chạy 0.5 cho mượt hơn
        private float timer = 0f;

        public SNetwork network = new SNetwork();
        public List<SVehicle> allVehicles = new List<SVehicle>();

        private CFKrauss cfModel = new CFKrauss();
        private LC2013 lcModel = new LC2013();

        void Update()
        {
            timer += Time.deltaTime;
            
            // Xử lý logic SUMO Core tick (Chậm hơn FPS)
            if (timer >= stepLength)
            {
                SimulationStep(Mathf.RoundToInt(stepLength * 1000f));
                timer = 0f;
            }

            // Xử lý việc di chuyển mượt mà (Lerp state trong 1 Frame)
            foreach (var veh in allVehicles)
            {
                if (veh.rendererObject != null)
                {
                    // Logic Lerp position từ veh.positionOnLane sang tọa độ gốc Unity 3D
                }
            }
        }

        private void SimulationStep(int stepMillis)
        {
            float dt = stepLength;

            // 1. Phân loại lại thứ tự xe trên Lane (Ai tới trước tính trước)
            foreach (var edge in network.Edges.Values)
            {
                foreach (var lane in edge.lanes)
                {
                    lane.SortVehicles();
                }
            }

            // 2. Giải quyết Lane-Changing (Ai chuyển được cho chuyển ngay để sang bước 3 tính CF không bị đụng)
            foreach (var veh in allVehicles)
            {
                SLane left = veh.currentLane.GetLeftLane();
                if (lcModel.WantsChange(veh, left, cfModel))
                {
                    veh.currentLane.vehicles.Remove(veh);
                    left.vehicles.Add(veh);
                    veh.currentLane = left;
                }
            }

            // 3. Tính toán Vận Tốc An Toàn (Car-following phase)
            foreach (var edge in network.Edges.Values)
            {
                foreach (var lane in edge.lanes)
                {
                    for (int i = 0; i < lane.vehicles.Count; i++)
                    {
                        var veh = lane.vehicles[i];
                        float leaderV = 0f;
                        float gap = 9999f; // Vô cực nếu đường trống

                        // Nếu không phải xe đi đầu tiên, lấy data xe đằng trước nó
                        if (i > 0)
                        {
                            var leader = lane.vehicles[i - 1];
                            leaderV = leader.currentSpeed;
                            gap = (leader.positionOnLane - leader.length) - veh.positionOnLane;
                        }

                        // Krauss Formula
                        veh.currentSpeed = cfModel.FollowSpeed(veh.currentSpeed, leaderV, gap, stepMillis);
                    }
                }
            }

            // 4. Execute Cập nhật Position
            foreach (var veh in allVehicles)
            {
                veh.positionOnLane += veh.currentSpeed * dt;
                
                // TODO: Xử lý giao lộ nếu position vượt quá chiều dài edge
            }
        }
    }
}
