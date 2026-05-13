using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Spawns and manages a pool of autonomous vehicles on OSM waypoints.
    /// - Spawns Cars, Motorbikes, and Buses with configurable counts
    /// - Assigns each vehicle to a random waypoint on the graph
    /// - Manages a "vehicle" physics layer for braking detection
    /// - Shows a live Stats overlay in Game view
    /// </summary>
    public class TrafficSpawner : MonoBehaviour
    {
        [Header("Continuous Spawning")]
        public bool ContinuousSpawning = true;
        [Range(0.05f, 2f)]
        public float SpawnInterval = 0.15f;
        [Range(10, 1000)]
        public int MaxActiveVehicles = 300;

        [Header("Counts (If not Continuous)")]
        public int CarCount       = 10;
        public int MotoCount      = 12;
        public int BusCount       =  2;

        [Header("Speed Multiplier")]
        [Range(0.1f, 5f)]
        public float SpeedScale   = 1f;

        [Header("Colors — randomised per vehicle")]
        public Color[] CarColors   = {
            new Color(0.0f, 0.5f, 1.0f),   // bright blue
            new Color(1.0f, 0.15f, 0.1f),  // vivid red
            new Color(0.1f, 0.85f, 0.2f),  // lime green
            new Color(1.0f, 0.7f, 0.0f),   // amber
            new Color(0.9f, 0.9f, 0.9f),   // white
            new Color(0.7f, 0.0f, 0.9f),   // purple
            new Color(0.0f, 0.85f, 0.85f), // cyan
        };
        public Color[] MotoColors  = {
            new Color(1.0f, 0.4f, 0.0f),   // orange
            new Color(1.0f, 0.9f, 0.0f),   // yellow
            new Color(0.8f, 0.0f, 0.4f),   // pink
            new Color(0.5f, 1.0f, 0.5f),   // mint
        };
        public Color BusColor      = new Color(1.0f, 0.85f, 0.0f); // golden yellow

        [Header("References")]
        public WaypointGraph Graph;

        // ── private ───────────────────────────────────────────────────────────
        private readonly List<VehicleAgent> _agents = new List<VehicleAgent>();
        private int _vehicleLayer;

        public static TrafficSpawner Instance;
        [HideInInspector] public List<Transform> Buildings = new List<Transform>();
        [HideInInspector] public List<Waypoint>  EdgeNodes = new List<Waypoint>();

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            Instance = this;
            Random.InitState((int)System.DateTime.Now.Ticks);
        }

        private void Start()
        {
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0)
            {
                Debug.LogWarning("[TrafficSpawner] No WaypointGraph found — generate the OSM map first.");
                enabled = false;
                return;
            }

            // Lấy toàn bộ toà nhà trong scene làm điểm Spawn/End
            foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (go.name.StartsWith("Building", System.StringComparison.OrdinalIgnoreCase))
                    Buildings.Add(go.transform);
            }
            if (Buildings.Count == 0)
                Debug.LogWarning("[TrafficSpawner] Không tìm thấy toà nhà nào (tên bắt đầu bằng 'Building'). Xe sẽ spawn ngẫu nhiên trên đường.");

            // Quét các Node rìa bản đồ (chỉ có 1 kết nối - đoạn cắt của OSM) để làm điểm Spawn / End hợp lý
            EdgeNodes.Clear();
            foreach (var kvp in Graph.Waypoints)
            {
                if (kvp.Value.ConnectedWaypointIds.Count <= 1)
                    EdgeNodes.Add(kvp.Value);
            }
            if (EdgeNodes.Count == 0)
                Debug.LogWarning("[TrafficSpawner] Không tìm thấy node rìa (Edge Node). Xe sẽ phải spawn giữa đường.");

            // Ensure a layer named "OsmVehicle" exists (Unity allows up to user layer 31)
            _vehicleLayer = EnsureLayer("OsmVehicle");
            Physics.IgnoreLayerCollision(_vehicleLayer, _vehicleLayer, true);

            if (GetComponent<TrafficLightManager>() == null)
            {
                var tlm = gameObject.AddComponent<TrafficLightManager>();
                tlm.Graph = Graph;
            }

            // Auto-attach VehicleInspector vào Main Camera để click xem xe
            Camera mainCam = Camera.main;
            if (mainCam != null && mainCam.GetComponent<VehicleInspector>() == null)
                mainCam.gameObject.AddComponent<VehicleInspector>();

            if (ContinuousSpawning)
            {
                StartCoroutine(SpawnRoutine());
            }
            else
            {
                SpawnAll();
            }
        }

        private void Update()
        {
            _agents.RemoveAll(a => a == null);
            
            // Cập nhật SpeedScale runtime cho toàn bộ xe đang chạy
            foreach (var a in _agents)
            {
                if (a != null)
                    a.RuntimeSpeedScale = SpeedScale;
            }
        }

        private IEnumerator SpawnRoutine()
        {
            while (true)
            {
                if (_agents.Count < MaxActiveVehicles)
                {
                    float r = Random.value;
                    if (r < 0.15f) Spawn(1, VehicleMeshBuilder.VehicleType.Bus, new[] { BusColor });
                    else if (r < 0.45f) Spawn(1, VehicleMeshBuilder.VehicleType.Motorbike, MotoColors);
                    else Spawn(1, VehicleMeshBuilder.VehicleType.Car, CarColors);
                }
                yield return new WaitForSeconds(SpawnInterval);
            }
        }

        private void OnDestroy()
        {
            foreach (var a in _agents) if (a) Destroy(a.gameObject);
            _agents.Clear();
        }

        // ── Spawn ─────────────────────────────────────────────────────────────

        private void SpawnAll()
        {
            Spawn(CarCount,   VehicleMeshBuilder.VehicleType.Car,       CarColors);
            Spawn(MotoCount,  VehicleMeshBuilder.VehicleType.Motorbike, MotoColors);
            Spawn(BusCount,   VehicleMeshBuilder.VehicleType.Bus,       new[] { BusColor });
            Debug.Log($"[TrafficSpawner] Spawned {_agents.Count} vehicles.");
        }

        private void Spawn(int count, VehicleMeshBuilder.VehicleType type, Color[] palette)
        {
            if (Graph.Waypoints.Count == 0) return;

            for (int i = 0; i < count; i++)
            {
                Waypoint wp = PickSpawnWaypoint();
                if (wp == null) continue;

                Color color = palette[Random.Range(0, palette.Length)];

                GameObject vehicleGO = VehicleMeshBuilder.Build(type, color);
                vehicleGO.transform.SetParent(transform, false);
                vehicleGO.transform.position = wp.Position;

                // Xoay xe theo hướng đường (hướng về node kết nối đầu tiên)
                if (wp.ConnectedWaypointIds.Count > 0)
                {
                    long firstConnId = wp.ConnectedWaypointIds[0];
                    if (Graph.Waypoints.TryGetValue(firstConnId, out Waypoint nextWp))
                    {
                        Vector3 dir = (nextWp.Position - wp.Position);
                        dir.y = 0;
                        if (dir.sqrMagnitude > 0.01f)
                            vehicleGO.transform.rotation = Quaternion.LookRotation(dir.normalized);
                    }
                }

                vehicleGO.layer = _vehicleLayer;
                foreach (Transform c in vehicleGO.GetComponentsInChildren<Transform>())
                    c.gameObject.layer = _vehicleLayer;

                var col    = vehicleGO.AddComponent<BoxCollider>();
                float vLen = GetLength(type);
                float vW   = GetWidth(type);
                col.size   = new Vector3(vW, 1.2f, vLen);
                col.center = new Vector3(0, 0.5f, 0);

                var agent = vehicleGO.AddComponent<VehicleAgent>();
                agent.Graph        = Graph;
                agent.VehicleType  = type;
                agent.VehicleWidth = vW;
                // Lưu base speed gốc (không nhân SpeedScale) để có thể thay đổi tốc độ runtime
                agent.BaseSpeed    = GetBaseSpeed(type) * Random.Range(0.7f, 1.3f);
                agent.RuntimeSpeedScale = SpeedScale;
                agent.VehicleLayer = 1 << _vehicleLayer;
                agent.StopDistance = vLen * 1.5f;
                agent.Wheels       = FindWheels(vehicleGO.transform);
                agent.DestroyOnArrival = ContinuousSpawning;
                int lane = Random.Range(0, 2);
                agent.LaneIndex     = lane;
                agent.PreferredLane = lane;
                agent.Patience      = Random.Range(3f, 8f);

                _agents.Add(agent);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Waypoint PickSpawnWaypoint()
        {
            var list = new List<Waypoint>(Graph.Waypoints.Values);
            
            // Shuffle list để mỗi lần gọi ra thứ tự khác nhau, tránh bias clustering
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            
            // Giảm safe distance khi xe đông để vẫn spawn được
            float safeDist = _agents.Count < MaxActiveVehicles * 0.5f ? 10f : 6f;
            
            for (int attempt = 0; attempt < 60; attempt++)
            {
                Waypoint wp = null;
                
                // Random waypoint từ list đã shuffle
                wp = list[attempt % list.Count];
                if (wp == null) continue;
                
                // Không spawn ở ngã tư, có đèn giao thông, hoặc khu vực đang bị tắc nghẽn
                if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                if (Graph.CongestionCosts != null && Graph.CongestionCosts.ContainsKey(wp.OSMNodeId)) continue;
                
                // Kiểm tra khoảng cách an toàn với xe đã có (dynamic theo mật độ)
                bool tooClose = false;
                foreach (var a in _agents)
                {
                    if (a != null && Vector3.Distance(a.transform.position, wp.Position) < safeDist)
                    { tooClose = true; break; }
                }
                if (!tooClose) return wp;
            }
            
            // Fallback: chấp nhận bất kỳ waypoint hợp lệ nào (giảm safe dist xuống 3m)
            for (int i = 0; i < 20; i++)
            {
                Waypoint wp = list[Random.Range(0, list.Count)];
                if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                
                bool tooClose = false;
                foreach (var a in _agents)
                {
                    if (a != null && Vector3.Distance(a.transform.position, wp.Position) < 3f)
                    { tooClose = true; break; }
                }
                if (!tooClose) return wp;
            }
            
            return null;
        }

        private static Transform[] FindWheels(Transform root)
        {
            var list = new List<Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>())
                if (t.name.StartsWith("Wheel")) list.Add(t);
            return list.ToArray();
        }

        private static float GetBaseSpeed(VehicleMeshBuilder.VehicleType t)
        {
            if (t == VehicleMeshBuilder.VehicleType.Bus)       return 6f;
            if (t == VehicleMeshBuilder.VehicleType.Motorbike) return 12f;
            return 9f;
        }

        private static float GetLength(VehicleMeshBuilder.VehicleType t)
        {
            if (t == VehicleMeshBuilder.VehicleType.Bus)       return 2.5f;
            if (t == VehicleMeshBuilder.VehicleType.Motorbike) return 0.55f;
            return 1.1f;
        }

        private static float GetWidth(VehicleMeshBuilder.VehicleType t)
        {
            if (t == VehicleMeshBuilder.VehicleType.Bus)       return 0.625f;
            if (t == VehicleMeshBuilder.VehicleType.Motorbike) return 0.2f;
            return 0.45f;
        }

        // ── Layer helper (editor only — at runtime layers are read-only) ──────

        private static int EnsureLayer(string name)
        {
            // Try to find existing layer
            for (int i = 8; i <= 31; i++)
                if (LayerMask.LayerToName(i) == name) return i;
            // Fallback to layer 9 if we can't create layers at runtime
            Debug.LogWarning($"[TrafficSpawner] Layer '{name}' not found. Using layer 9. " +
                             "Add it manually in Project Settings → Tags & Layers.");
            return 9;
        }

        // ── Stats overlay ─────────────────────────────────────────────────────

        // Cached to avoid GC allocation every frame
        private bool _panelOpen = true;
        private GUIStyle _titleStyle;
        private GUIStyle _statStyle;
        private GUIStyle _sliderLabelStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _collisionStyle;

        private void OnGUI()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize  = 14,
                    normal    = { textColor = new Color(0.4f, 0.9f, 1f) }
                };
                _statStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    normal   = { textColor = Color.white }
                };
                _sliderLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    normal   = { textColor = new Color(0.9f, 0.9f, 0.7f) }
                };
                _warningStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal   = { textColor = new Color(1f, 0.64f, 0f) } // Orange cho tắc
                };
                _collisionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal   = { textColor = Color.red } // Red cho va chạm
                };
            }

            const float panW = 220f, pad = 10f;
            float startX = Screen.width - panW - pad;
            float startY = pad;

            // Vẽ cảnh báo Tắc Đường / Va Chạm trên đầu các xe gặp sự cố
            Camera cam = Camera.main;
            if (cam != null)
            {
                foreach (var a in _agents)
                {
                    if (a == null) continue;
                    
                    if (a.IsColliding || a.IsStuck)
                    {
                        Vector3 screenPos = cam.WorldToScreenPoint(a.transform.position + Vector3.up * 3f);
                        if (screenPos.z > 0 && screenPos.z < 250f) 
                        {
                            Rect rect = new Rect(screenPos.x - 40, Screen.height - screenPos.y - 15, 80, 30);
                            if (a.IsColliding)
                                GUI.Label(rect, "[Va Chạm]", _collisionStyle);
                            else
                                GUI.Label(rect, "[Tắc Nghẽn]", _warningStyle);
                        }
                    }
                }
            }

            // Nút toggle ẩn/hiện panel
            if (GUI.Button(new Rect(startX + panW - 25, startY, 25, 20), _panelOpen ? "▼" : "▶"))
                _panelOpen = !_panelOpen;

            if (!_panelOpen)
            {
                GUI.Box(new Rect(startX, startY, panW, 22), "");
                GUI.Label(new Rect(startX + 8, startY + 2, panW - 40, 18), "OSM Traffic", _titleStyle);
                return;
            }

            float panH = 195f;
            GUI.Box(new Rect(startX, startY, panW, panH), "");

            float y = startY + 5;
            float labelW = panW - 16;

            // Title + Stats
            GUI.Label(new Rect(startX + 8, y, labelW, 20), "OSM Traffic Control", _titleStyle);
            y += 22;

            int active = _agents.Count;
            GUI.Label(new Rect(startX + 8, y, labelW, 18), $"Vehicles: {active} / {MaxActiveVehicles}", _statStyle);
            y += 20;

            // Slider: Max Vehicles
            GUI.Label(new Rect(startX + 8, y, labelW, 16), $"Max Vehicles: {MaxActiveVehicles}", _sliderLabelStyle);
            y += 16;
            MaxActiveVehicles = Mathf.RoundToInt(GUI.HorizontalSlider(
                new Rect(startX + 8, y, labelW, 16), MaxActiveVehicles, 10, 1000));
            y += 20;

            // Slider: Spawn Interval
            GUI.Label(new Rect(startX + 8, y, labelW, 16), $"Spawn Rate: {SpawnInterval:F2}s", _sliderLabelStyle);
            y += 16;
            SpawnInterval = GUI.HorizontalSlider(
                new Rect(startX + 8, y, labelW, 16), SpawnInterval, 0.05f, 2f);
            y += 20;

            // Slider: Speed
            GUI.Label(new Rect(startX + 8, y, labelW, 16), $"Speed: x{SpeedScale:F1}", _sliderLabelStyle);
            y += 16;
            SpeedScale = GUI.HorizontalSlider(
                new Rect(startX + 8, y, labelW, 16), SpeedScale, 0.1f, 5f);
            y += 22;

            if (TrafficLightManager.Instance != null)
            {
                GUI.Label(new Rect(startX + 8, y, labelW - 20, 16), "Traffic Lights", _statStyle);
                TrafficLightManager.Instance.EnableTrafficLights = GUI.Toggle(
                    new Rect(startX + panW - 25, y, 20, 16), 
                    TrafficLightManager.Instance.EnableTrafficLights, "");
            }
        }
    }
}
