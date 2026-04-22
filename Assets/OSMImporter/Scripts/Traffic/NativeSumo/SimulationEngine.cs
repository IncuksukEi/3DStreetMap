using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Traffic.NativeSumo.Models;

namespace OSMImporter.Traffic.NativeSumo
{
    /// <summary>
    /// Vòng lặp giả lập chính (Mô phỏng lại MSEdgeControl::planMovements)
    /// Đảm bảo tính toán Lane-changing → Krauss Car-Following đồng bộ.
    /// 
    /// Hoàn chỉnh:
    /// - Junction transitions (xe chuyển edge khi hết lane)
    /// - Vehicle spawning/despawning với route generation
    /// - Shortest-path routing trên SNetwork
    /// </summary>
    public class SimulationEngine : MonoBehaviour
    {
        [Header("Simulation")]
        [Tooltip("Thời gian mỗi bước SUMO (giây). Nhỏ hơn = mượt hơn.")]
        public float stepLength = 0.05f;

        [Tooltip("Max xe trong simulation")]
        [Range(10, 500)]
        public int maxVehicles = 100;

        [Tooltip("Khoảng cách spawn (giây)")]
        public float spawnInterval = 0.5f;

        [Header("Rendering")]
        [Tooltip("Tốc độ smooth position (cao = bám sát, thấp = mượt hơn)")]
        public float positionLerp = 15f;

        [Tooltip("Tốc độ smooth rotation")]
        public float rotationLerp = 10f;

        [Header("Status (Read-only)")]
        [SerializeField] private int _vehicleCount;
        [SerializeField] private int _totalSpawned;
        [SerializeField] private int _totalFinished;

        private float _simTimer;
        private float _spawnTimer;
        private int _nextVehicleId;

        public SNetwork network;
        public List<SVehicle> allVehicles = new List<SVehicle>();
        private List<SVehicle> _toRemove = new List<SVehicle>();

        // Cached edge lists cho random spawn
        private List<SEdge> _spawnableEdges;

        private CFKrauss cfModel = new CFKrauss();
        private LC2013 lcModel = new LC2013();

        private Transform _vehicleParent;

        // ══════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        void Start()
        {
            var go = new GameObject("NativeSumo_Vehicles");
            _vehicleParent = go.transform;
        }

        void Update()
        {
            if (network == null || network.Edges.Count == 0) return;

            // Cache spawnable edges
            if (_spawnableEdges == null)
                CacheSpawnableEdges();

            // Spawn timer
            _spawnTimer += Time.deltaTime;
            if (_spawnTimer >= spawnInterval && allVehicles.Count < maxVehicles)
            {
                _spawnTimer = 0f;
                SpawnRandomVehicle();
            }

            // Simulation tick — chạy nhiều sub-steps nếu frame dài
            _simTimer += Time.deltaTime;
            int steps = 0;
            while (_simTimer >= stepLength && steps < 10)
            {
                // Lưu vị trí trước step (dùng cho interpolation)
                foreach (var veh in allVehicles)
                    veh.prevPosition = veh.Get3DPosition();

                SimulationStep(Mathf.RoundToInt(stepLength * 1000f));
                _simTimer -= stepLength;
                steps++;

                // Cache target position sau step
                foreach (var veh in allVehicles)
                    veh.targetPosition = veh.Get3DPosition();
            }

            // Tỷ lệ nội suy giữa prev → target (0..1)
            float interpRatio = Mathf.Clamp01(_simTimer / stepLength);

            // Render interpolation — mượt sub-frame
            float dt = Time.deltaTime;
            foreach (var veh in allVehicles)
            {
                if (veh.rendererObject == null || veh.isFinished) continue;

                // Bảo vệ: nếu cả prev và target đều zero → chưa init → skip
                if (veh.targetPosition.sqrMagnitude < 0.001f) continue;

                // Sub-frame lerp: blend giữa vị trí trước và sau sim step
                Vector3 interpPos = Vector3.Lerp(veh.prevPosition, veh.targetPosition, interpRatio);
                
                // Smooth final position (damping)
                veh.rendererObject.transform.position = Vector3.Lerp(
                    veh.rendererObject.transform.position, interpPos, dt * positionLerp);

                // Smooth rotation
                Vector3 fwd = veh.GetForwardDirection();
                if (fwd.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(fwd);
                    veh.rendererObject.transform.rotation = Quaternion.Slerp(
                        veh.rendererObject.transform.rotation, targetRot, dt * rotationLerp);
                }
            }

            // Cleanup finished vehicles
            RemoveFinishedVehicles();
            _vehicleCount = allVehicles.Count;
        }

        void OnDestroy()
        {
            foreach (var v in allVehicles)
            {
                if (v.rendererObject != null)
                    Destroy(v.rendererObject);
            }
            allVehicles.Clear();
        }

        // ══════════════════════════════════════════════════════════════════
        // SIMULATION STEP
        // ══════════════════════════════════════════════════════════════════

        private void SimulationStep(int stepMillis)
        {
            float dt = stepLength;

            // 1. Sắp xếp xe trên mỗi lane
            foreach (var edge in network.Edges.Values)
            {
                foreach (var lane in edge.lanes)
                    lane.SortVehicles();
            }

            // 2. Lane-changing
            foreach (var veh in allVehicles)
            {
                if (veh.isFinished) continue;
                SLane left = veh.currentLane?.GetLeftLane();
                if (lcModel.WantsChange(veh, left, cfModel))
                {
                    veh.currentLane.vehicles.Remove(veh);
                    left.vehicles.Add(veh);
                    veh.currentLane = left;
                }
            }

            // 3. Car-following (Krauss)
            foreach (var edge in network.Edges.Values)
            {
                foreach (var lane in edge.lanes)
                {
                    for (int i = 0; i < lane.vehicles.Count; i++)
                    {
                        var veh = lane.vehicles[i];
                        float leaderV = 0f;
                        float gap = 9999f;

                        if (i > 0)
                        {
                            var leader = lane.vehicles[i - 1];
                            leaderV = leader.currentSpeed;
                            gap = (leader.positionOnLane - leader.length) - veh.positionOnLane;
                        }

                        veh.currentSpeed = cfModel.FollowSpeed(veh.currentSpeed, leaderV, gap, stepMillis);
                    }
                }
            }

            // 4. Update position + junction transitions
            foreach (var veh in allVehicles)
            {
                if (veh.isFinished) continue;

                veh.positionOnLane += veh.currentSpeed * dt;

                // Kiểm tra vượt quá lane → chuyển sang edge tiếp theo
                if (veh.currentLane != null && veh.positionOnLane >= veh.currentLane.length)
                {
                    TransitionToNextEdge(veh);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // JUNCTION TRANSITION
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Chuyển xe sang edge tiếp theo trong route.
        /// Nếu hết route → đánh dấu isFinished.
        /// </summary>
        private void TransitionToNextEdge(SVehicle veh)
        {
            // Tính phần dư (xe đã đi quá bao nhiêu mét)
            float overshoot = veh.positionOnLane - veh.currentLane.length;

            // Xoá xe khỏi lane hiện tại
            veh.currentLane.vehicles.Remove(veh);

            // Kiểm tra route còn edge nào không
            if (veh.route.Count == 0)
            {
                veh.isFinished = true;
                return;
            }

            // Lấy edge tiếp theo từ route
            SEdge nextEdge = veh.route.Dequeue();
            veh.routeEdgeIndex++;
            veh.currentEdge = nextEdge;

            // Chọn lane có shape hợp lệ
            SLane nextLane = null;
            foreach (var lane in nextEdge.lanes)
            {
                if (lane.shape != null && lane.shape.Count >= 2)
                {
                    nextLane = lane;
                    break;
                }
            }

            // Nếu không có lane hợp lệ → kết thúc xe
            if (nextLane == null)
            {
                veh.isFinished = true;
                return;
            }

            veh.currentLane = nextLane;
            veh.positionOnLane = Mathf.Max(0f, overshoot);
            nextLane.vehicles.Add(veh);

            // Clamp speed cho lane mới
            veh.currentSpeed = Mathf.Min(veh.currentSpeed, nextLane.maxSpeed);
        }

        // ══════════════════════════════════════════════════════════════════
        // VEHICLE SPAWNING
        // ══════════════════════════════════════════════════════════════════

        private void CacheSpawnableEdges()
        {
            _spawnableEdges = new List<SEdge>();
            foreach (var edge in network.Edges.Values)
            {
                // Chỉ spawn ở edge có ít nhất 1 lane với shape hợp lệ
                if (edge.lanes.Count > 0 && edge.length > 5f)
                {
                    bool hasValidShape = false;
                    foreach (var lane in edge.lanes)
                    {
                        if (lane.shape != null && lane.shape.Count >= 2)
                        {
                            hasValidShape = true;
                            break;
                        }
                    }
                    if (hasValidShape)
                        _spawnableEdges.Add(edge);
                }
            }
            Debug.Log($"[SimulationEngine] {_spawnableEdges.Count} spawnable edges (có shape hợp lệ).");
        }

        /// <summary>Sinh 1 xe tại random edge, route = shortest path tới edge ngẫu nhiên</summary>
        private void SpawnRandomVehicle()
        {
            if (_spawnableEdges == null || _spawnableEdges.Count < 2) return;

            // Chọn edge khởi đầu và edge đích
            SEdge startEdge = _spawnableEdges[Random.Range(0, _spawnableEdges.Count)];
            SEdge endEdge = _spawnableEdges[Random.Range(0, _spawnableEdges.Count)];
            if (startEdge == endEdge) return;

            // Chọn lane có shape valid
            SLane startLane = null;
            foreach (var lane in startEdge.lanes)
            {
                if (lane.shape != null && lane.shape.Count >= 2)
                {
                    startLane = lane;
                    break;
                }
            }
            if (startLane == null) return;

            // Kiểm tra lane đầu không quá đông
            if (startLane.vehicles.Count >= 3) return;

            // Tạo route bằng BFS
            var route = FindRoute(startEdge, endEdge);
            if (route == null || route.Count == 0) return;

            // Random loại xe
            string vType = "car";
            float r = Random.value;
            if (r < 0.15f) vType = "bus";
            else if (r < 0.45f) vType = "motorbike";

            var veh = new SVehicle
            {
                id = $"nv_{_nextVehicleId++}",
                vehicleType = vType,
                maxSpeed = GetMaxSpeedForType(vType),
                length = GetLengthForType(vType),
                width = GetWidthForType(vType),
                currentLane = startLane,
                currentEdge = startEdge,
                positionOnLane = 0.1f,
                currentSpeed = 0f,
                color = Random.ColorHSV(0f, 1f, 0.5f, 0.8f, 0.6f, 0.9f)
            };

            // Khởi tạo vị trí ban đầu để tránh flash ở Vector3.zero
            Vector3 initPos = veh.Get3DPosition();
            veh.prevPosition = initPos;
            veh.targetPosition = initPos;

            // Nạp route (bỏ edge đầu tiên vì xe đã ở đó)
            foreach (var edge in route)
                veh.route.Enqueue(edge);

            startLane.vehicles.Add(veh);
            allVehicles.Add(veh);
            _totalSpawned++;

            // Tạo renderer
            CreateRenderer(veh);
        }

        /// <summary>BFS tìm đường edge→edge trên SUMO graph</summary>
        private List<SEdge> FindRoute(SEdge from, SEdge to)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<List<SEdge>>();
            queue.Enqueue(new List<SEdge> { from });
            visited.Add(from.id);

            int maxSteps = 500; // Giới hạn BFS
            int steps = 0;

            while (queue.Count > 0 && steps++ < maxSteps)
            {
                var path = queue.Dequeue();
                var current = path[path.Count - 1];

                if (current == to)
                {
                    // Bỏ edge đầu (xe đã ở edge đầu) 
                    path.RemoveAt(0);
                    return path;
                }

                foreach (var successor in current.successors)
                {
                    if (!visited.Contains(successor.id))
                    {
                        visited.Add(successor.id);
                        var newPath = new List<SEdge>(path) { successor };
                        queue.Enqueue(newPath);
                    }
                }
            }

            return null; // Không tìm được đường
        }

        // ══════════════════════════════════════════════════════════════════
        // VEHICLE REMOVAL
        // ══════════════════════════════════════════════════════════════════

        private void RemoveFinishedVehicles()
        {
            _toRemove.Clear();
            foreach (var veh in allVehicles)
            {
                if (veh.isFinished)
                    _toRemove.Add(veh);
            }

            foreach (var veh in _toRemove)
            {
                // Xoá khỏi lane
                veh.currentLane?.vehicles.Remove(veh);

                // Xoá renderer
                if (veh.rendererObject != null)
                    Destroy(veh.rendererObject);

                allVehicles.Remove(veh);
                _totalFinished++;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // RENDERING
        // ══════════════════════════════════════════════════════════════════

        private void CreateRenderer(SVehicle veh)
        {
            var meshType = MapVehicleType(veh.vehicleType);
            var go = VehicleMeshBuilder.Build(meshType, veh.color);
            go.name = $"NativeSumo_{veh.id}";

            if (_vehicleParent != null)
                go.transform.SetParent(_vehicleParent, false);

            // Đặt vị trí ban đầu (dùng cached position — đã init trong SpawnRandomVehicle)
            go.transform.position = veh.targetPosition;
            Vector3 fwd = veh.GetForwardDirection();
            if (fwd.sqrMagnitude > 0.001f)
                go.transform.rotation = Quaternion.LookRotation(fwd);

            // Collider cho raycast inspection
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(veh.width, 1.2f, veh.length);
            col.center = new Vector3(0, 0.5f, 0);

            veh.rendererObject = go;
        }

        private static VehicleMeshBuilder.VehicleType MapVehicleType(string sumoType)
        {
            if (string.IsNullOrEmpty(sumoType)) return VehicleMeshBuilder.VehicleType.Car;
            string lower = sumoType.ToLowerInvariant();
            if (lower.Contains("bus")) return VehicleMeshBuilder.VehicleType.Bus;
            if (lower.Contains("moto") || lower.Contains("bike")) return VehicleMeshBuilder.VehicleType.Motorbike;
            return VehicleMeshBuilder.VehicleType.Car;
        }

        // ══════════════════════════════════════════════════════════════════
        // VEHICLE TYPE PARAMS
        // ══════════════════════════════════════════════════════════════════

        private static float GetMaxSpeedForType(string type)
        {
            switch (type)
            {
                case "bus": return 15f;
                case "motorbike": return 20f;
                default: return 25f;
            }
        }

        private static float GetLengthForType(string type)
        {
            switch (type)
            {
                case "bus": return 12f;
                case "motorbike": return 2f;
                default: return 4.5f;
            }
        }

        private static float GetWidthForType(string type)
        {
            switch (type)
            {
                case "bus": return 2.5f;
                case "motorbike": return 0.8f;
                default: return 1.8f;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════

        public int VehicleCount => allVehicles.Count;
        public int TotalSpawned => _totalSpawned;
        public int TotalFinished => _totalFinished;

        /// <summary>Đặt network mới (gọi sau khi parse .net.xml)</summary>
        public void SetNetwork(SNetwork net)
        {
            network = net;
            _spawnableEdges = null; // Force re-cache
        }
    }
}
