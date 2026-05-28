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

        [System.Serializable]
        public struct RuleConfig
        {
            public string RuleId;
            public string RuleName;
            [Range(0f, 100f)]
            public float ApplyProbability;
        }

        [System.Serializable]
        public struct RoadTypeSpawnConfig
        {
            public string RoadType;
            [Range(0f, 10f)]
            public float SpawnWeight;
            [Range(0f, 100f)]
            public float CarRatio;
            [Range(0f, 100f)]
            public float MotoRatio;
            [Range(0f, 100f)]
            public float BusRatio;
        }

        [Header("Traffic Rules Assignment Probabilities (%)")]
        public List<RuleConfig> RuleProbabilities = new List<RuleConfig>();

        [Header("Road Type Spawn Configurations (Density & Vehicle Type Ratio)")]
        public List<RoadTypeSpawnConfig> RoadConfigs = new List<RoadTypeSpawnConfig>();

        [Header("Vehicle Personalities Settings (Editable in Inspector)")]
        public List<VehiclePersonality> PersonalitiesList = new List<VehiclePersonality>();

        public static readonly List<VehiclePersonality> PredefinedPersonalities = new List<VehiclePersonality>
        {
            new VehiclePersonality { Name = "Normal", SpeedMultiplier = 1.0f, MinFollowDistanceMultiplier = 1.0f, SafeReactionTimeMultiplier = 1.0f, OvertakeEagerness = 1.0f, RedLightRunChance = 0.5f, YellowLightRunChance = 15f, YieldChance = 90f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Cautious", SpeedMultiplier = 0.8f, MinFollowDistanceMultiplier = 1.5f, SafeReactionTimeMultiplier = 1.4f, OvertakeEagerness = 0.2f, RedLightRunChance = 0f, YellowLightRunChance = 0f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Aggressive", SpeedMultiplier = 1.25f, MinFollowDistanceMultiplier = 0.6f, SafeReactionTimeMultiplier = 0.6f, OvertakeEagerness = 2.0f, RedLightRunChance = 5f, YellowLightRunChance = 70f, YieldChance = 30f, SidewalkSpill = 0.1f, LaneJitter = 0.1f },
            new VehiclePersonality { Name = "Reckless (Đi ẩu)", SpeedMultiplier = 1.5f, MinFollowDistanceMultiplier = 0.4f, SafeReactionTimeMultiplier = 0.4f, OvertakeEagerness = 3.0f, RedLightRunChance = 15f, YellowLightRunChance = 95f, YieldChance = 10f, SidewalkSpill = 0.35f, LaneJitter = 0.2f },
            new VehiclePersonality { Name = "Impatient", SpeedMultiplier = 1.15f, MinFollowDistanceMultiplier = 0.7f, SafeReactionTimeMultiplier = 0.8f, OvertakeEagerness = 2.2f, RedLightRunChance = 2f, YellowLightRunChance = 80f, YieldChance = 50f, SidewalkSpill = 0.05f, LaneJitter = 0.05f },
            new VehiclePersonality { Name = "Drunk (Say xỉn)", SpeedMultiplier = 0.85f, MinFollowDistanceMultiplier = 1.2f, SafeReactionTimeMultiplier = 1.8f, OvertakeEagerness = 1.0f, RedLightRunChance = 10f, YellowLightRunChance = 50f, YieldChance = 40f, SidewalkSpill = 0.25f, LaneJitter = 0.6f },
            new VehiclePersonality { Name = "DeliveryMoto (Shipper)", SpeedMultiplier = 1.35f, MinFollowDistanceMultiplier = 0.5f, SafeReactionTimeMultiplier = 0.5f, OvertakeEagerness = 2.8f, RedLightRunChance = 8f, YellowLightRunChance = 90f, YieldChance = 20f, SidewalkSpill = 0.4f, LaneJitter = 0.15f },
            new VehiclePersonality { Name = "Taxi", SpeedMultiplier = 1.2f, MinFollowDistanceMultiplier = 0.7f, SafeReactionTimeMultiplier = 0.7f, OvertakeEagerness = 2.5f, RedLightRunChance = 3f, YellowLightRunChance = 75f, YieldChance = 40f, SidewalkSpill = 0.15f, LaneJitter = 0.05f },
            new VehiclePersonality { Name = "BusDriver", SpeedMultiplier = 0.75f, MinFollowDistanceMultiplier = 1.1f, SafeReactionTimeMultiplier = 1.1f, OvertakeEagerness = 0.1f, RedLightRunChance = 0f, YellowLightRunChance = 10f, YieldChance = 80f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Elderly", SpeedMultiplier = 0.7f, MinFollowDistanceMultiplier = 1.8f, SafeReactionTimeMultiplier = 1.8f, OvertakeEagerness = 0.1f, RedLightRunChance = 0f, YellowLightRunChance = 0f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Speedy", SpeedMultiplier = 1.4f, MinFollowDistanceMultiplier = 0.9f, SafeReactionTimeMultiplier = 0.8f, OvertakeEagerness = 1.8f, RedLightRunChance = 1f, YellowLightRunChance = 40f, YieldChance = 70f, SidewalkSpill = 0.05f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Tailgater", SpeedMultiplier = 1.0f, MinFollowDistanceMultiplier = 0.35f, SafeReactionTimeMultiplier = 0.7f, OvertakeEagerness = 0.8f, RedLightRunChance = 0.5f, YellowLightRunChance = 20f, YieldChance = 80f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Polite", SpeedMultiplier = 0.95f, MinFollowDistanceMultiplier = 1.3f, SafeReactionTimeMultiplier = 1.1f, OvertakeEagerness = 0.4f, RedLightRunChance = 0f, YellowLightRunChance = 0f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Anxious", SpeedMultiplier = 0.75f, MinFollowDistanceMultiplier = 1.6f, SafeReactionTimeMultiplier = 1.5f, OvertakeEagerness = 0.1f, RedLightRunChance = 0f, YellowLightRunChance = 0f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.05f },
            new VehiclePersonality { Name = "LawAbiding", SpeedMultiplier = 1.0f, MinFollowDistanceMultiplier = 1.1f, SafeReactionTimeMultiplier = 1.0f, OvertakeEagerness = 0.5f, RedLightRunChance = 0f, YellowLightRunChance = 0f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.0f },
            new VehiclePersonality { Name = "Daring (Liều lĩnh)", SpeedMultiplier = 1.2f, MinFollowDistanceMultiplier = 0.5f, SafeReactionTimeMultiplier = 0.5f, OvertakeEagerness = 2.4f, RedLightRunChance = 12f, YellowLightRunChance = 85f, YieldChance = 15f, SidewalkSpill = 0.25f, LaneJitter = 0.1f },
            new VehiclePersonality { Name = "Sleepy", SpeedMultiplier = 0.85f, MinFollowDistanceMultiplier = 1.4f, SafeReactionTimeMultiplier = 1.9f, OvertakeEagerness = 0.3f, RedLightRunChance = 3f, YellowLightRunChance = 30f, YieldChance = 75f, SidewalkSpill = 0.1f, LaneJitter = 0.3f },
            new VehiclePersonality { Name = "Erratic (Thất thường)", SpeedMultiplier = 1.0f, MinFollowDistanceMultiplier = 0.8f, SafeReactionTimeMultiplier = 1.0f, OvertakeEagerness = 1.5f, RedLightRunChance = 4f, YellowLightRunChance = 50f, YieldChance = 50f, SidewalkSpill = 0.15f, LaneJitter = 0.25f },
            new VehiclePersonality { Name = "Rushed", SpeedMultiplier = 1.25f, MinFollowDistanceMultiplier = 0.75f, SafeReactionTimeMultiplier = 0.8f, OvertakeEagerness = 1.7f, RedLightRunChance = 1f, YellowLightRunChance = 60f, YieldChance = 60f, SidewalkSpill = 0.05f, LaneJitter = 0.05f },
            new VehiclePersonality { Name = "Tourist", SpeedMultiplier = 0.7f, MinFollowDistanceMultiplier = 1.4f, SafeReactionTimeMultiplier = 1.5f, OvertakeEagerness = 0.2f, RedLightRunChance = 0f, YellowLightRunChance = 5f, YieldChance = 95f, SidewalkSpill = 0.0f, LaneJitter = 0.1f },
            new VehiclePersonality { Name = "Bully (Hống hách)", SpeedMultiplier = 1.05f, MinFollowDistanceMultiplier = 0.8f, SafeReactionTimeMultiplier = 0.9f, OvertakeEagerness = 1.2f, RedLightRunChance = 2f, YellowLightRunChance = 40f, YieldChance = 5f, SidewalkSpill = 0.1f, LaneJitter = 0.05f },
            new VehiclePersonality { Name = "NervousNewbie", SpeedMultiplier = 0.8f, MinFollowDistanceMultiplier = 1.7f, SafeReactionTimeMultiplier = 1.6f, OvertakeEagerness = 0.1f, RedLightRunChance = 0f, YellowLightRunChance = 5f, YieldChance = 100f, SidewalkSpill = 0.0f, LaneJitter = 0.1f },
            new VehiclePersonality { Name = "Student", SpeedMultiplier = 0.85f, MinFollowDistanceMultiplier = 1.3f, SafeReactionTimeMultiplier = 1.2f, OvertakeEagerness = 0.6f, RedLightRunChance = 0f, YellowLightRunChance = 15f, YieldChance = 90f, SidewalkSpill = 0.02f, LaneJitter = 0.02f },
            new VehiclePersonality { Name = "AdrenalineJunkie", SpeedMultiplier = 1.6f, MinFollowDistanceMultiplier = 0.3f, SafeReactionTimeMultiplier = 0.3f, OvertakeEagerness = 3.5f, RedLightRunChance = 20f, YellowLightRunChance = 100f, YieldChance = 5f, SidewalkSpill = 0.4f, LaneJitter = 0.15f }
        };

        // ── private ───────────────────────────────────────────────────────────
        private readonly List<VehicleAgent> _agents = new List<VehicleAgent>();
        private int _vehicleLayer;

        public static TrafficSpawner Instance;
        [HideInInspector] public List<Transform> Buildings = new List<Transform>();
        [HideInInspector] public List<Waypoint>  EdgeNodes = new List<Waypoint>();

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Reset()
        {
            InitializeDefaultRules();
            InitializeDefaultRoadConfigs();
            InitializeDefaultPersonalities();
        }

        private void OnValidate()
        {
            if (RuleProbabilities == null || RuleProbabilities.Count == 0)
                InitializeDefaultRules();
            if (RoadConfigs == null || RoadConfigs.Count == 0)
                InitializeDefaultRoadConfigs();
            if (PersonalitiesList == null || PersonalitiesList.Count == 0)
                InitializeDefaultPersonalities();
        }

        private void InitializeDefaultPersonalities()
        {
            PersonalitiesList = new List<VehiclePersonality>();
            foreach (var p in PredefinedPersonalities)
            {
                PersonalitiesList.Add(p.Clone());
            }
        }

        private void InitializeDefaultRules()
        {
            RuleProbabilities = new List<RuleConfig>
            {
                new RuleConfig { RuleId = "B1", RuleName = "Avoid Front Collision", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B2", RuleName = "Avoid Lane Change Collision", ApplyProbability = 95f },
                new RuleConfig { RuleId = "B3", RuleName = "Maintain Safe Distance", ApplyProbability = 90f },
                new RuleConfig { RuleId = "B4", RuleName = "Max Speed Limit", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B5", RuleName = "Full Stop When Too Close", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B6", RuleName = "Stop At Red Light", ApplyProbability = 95f },
                new RuleConfig { RuleId = "B7", RuleName = "Go On Green Light", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B8", RuleName = "Prepare Stop Yellow", ApplyProbability = 80f },
                new RuleConfig { RuleId = "B9", RuleName = "Do Not Run Red Light", ApplyProbability = 98f },
                new RuleConfig { RuleId = "B10", RuleName = "Maintain Desired Speed", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B11", RuleName = "Smooth Acceleration", ApplyProbability = 95f },
                new RuleConfig { RuleId = "B12", RuleName = "Smooth Deceleration", ApplyProbability = 95f },
                new RuleConfig { RuleId = "B13", RuleName = "Clamp Acceleration", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B14", RuleName = "Stable Heading", ApplyProbability = 100f },
                new RuleConfig { RuleId = "B15", RuleName = "Keep Current Lane", ApplyProbability = 85f },
                new RuleConfig { RuleId = "B16", RuleName = "Overtake Lane Change", ApplyProbability = 75f },
                new RuleConfig { RuleId = "B17", RuleName = "Prepare Turn Lane Change", ApplyProbability = 90f },
                new RuleConfig { RuleId = "B18", RuleName = "Block Unsafe Lane Change", ApplyProbability = 95f },
                new RuleConfig { RuleId = "B19", RuleName = "Blocked Intersection", ApplyProbability = 85f },
                new RuleConfig { RuleId = "B20", RuleName = "Yield Intersection", ApplyProbability = 90f }
            };
        }

        private void InitializeDefaultRoadConfigs()
        {
            RoadConfigs = new List<RoadTypeSpawnConfig>
            {
                new RoadTypeSpawnConfig { RoadType = "motorway", SpawnWeight = 8f, CarRatio = 70f, MotoRatio = 20f, BusRatio = 10f },
                new RoadTypeSpawnConfig { RoadType = "primary", SpawnWeight = 5f, CarRatio = 45f, MotoRatio = 40f, BusRatio = 15f },
                new RoadTypeSpawnConfig { RoadType = "secondary", SpawnWeight = 3f, CarRatio = 30f, MotoRatio = 60f, BusRatio = 10f },
                new RoadTypeSpawnConfig { RoadType = "tertiary", SpawnWeight = 2f, CarRatio = 20f, MotoRatio = 75f, BusRatio = 5f },
                new RoadTypeSpawnConfig { RoadType = "residential", SpawnWeight = 1f, CarRatio = 10f, MotoRatio = 90f, BusRatio = 0f },
                new RoadTypeSpawnConfig { RoadType = "service", SpawnWeight = 0.5f, CarRatio = 5f, MotoRatio = 95f, BusRatio = 0f }
            };
        }

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
                    SpawnOne();
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

        private void SpawnOne()
        {
            if (Graph.Waypoints.Count == 0) return;

            VehicleMeshBuilder.VehicleType type;
            Waypoint wp = PickSpawnWaypoint(out type);
            if (wp == null) return;

            Color[] palette = CarColors;
            if (type == VehicleMeshBuilder.VehicleType.Motorbike) palette = MotoColors;
            else if (type == VehicleMeshBuilder.VehicleType.Bus) palette = new[] { BusColor };

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

            // Gán tính cách ngẫu nhiên từ PersonalitiesList và Jitter nhẹ
            if (PersonalitiesList != null && PersonalitiesList.Count > 0)
            {
                int pIdx = Random.Range(0, PersonalitiesList.Count);
                VehiclePersonality p = PersonalitiesList[pIdx].Clone();
                p.Jitter(0.08f);
                agent.Personality = p;
            }

            // Copy rule probabilities from TrafficSpawner configuration
            if (RuleProbabilities != null)
            {
                foreach (var rc in RuleProbabilities)
                {
                    agent.RuleProbabilities[rc.RuleId] = rc.ApplyProbability;
                }
            }

            _agents.Add(agent);
        }

        private void Spawn(int count, VehicleMeshBuilder.VehicleType type, Color[] palette)
        {
            if (Graph.Waypoints.Count == 0) return;

            for (int i = 0; i < count; i++)
            {
                VehicleMeshBuilder.VehicleType unused;
                Waypoint wp = PickSpawnWaypoint(out unused, type);
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

                // Gán tính cách ngẫu nhiên từ PersonalitiesList và Jitter nhẹ
                if (PersonalitiesList != null && PersonalitiesList.Count > 0)
                {
                    int pIdx = Random.Range(0, PersonalitiesList.Count);
                    VehiclePersonality p = PersonalitiesList[pIdx].Clone();
                    p.Jitter(0.08f);
                    agent.Personality = p;
                }

                // Copy rule probabilities from TrafficSpawner configuration
                if (RuleProbabilities != null)
                {
                    foreach (var rc in RuleProbabilities)
                    {
                        agent.RuleProbabilities[rc.RuleId] = rc.ApplyProbability;
                    }
                }

                _agents.Add(agent);
            }
        }

        /// <summary>
        /// Spawns a vehicle with a custom personality, starting location, and route.
        /// Useful for testing specific reckless driving scenarios.
        /// </summary>
        public VehicleAgent SpawnCustomVehicle(
            Waypoint startWp, 
            Waypoint destWp, 
            VehicleMeshBuilder.VehicleType type, 
            VehiclePersonality personality, 
            Color? customColor = null)
        {
            if (startWp == null) return null;

            Color[] palette = CarColors;
            if (type == VehicleMeshBuilder.VehicleType.Motorbike) palette = MotoColors;
            else if (type == VehicleMeshBuilder.VehicleType.Bus) palette = new[] { BusColor };

            Color color = customColor ?? palette[Random.Range(0, palette.Length)];

            GameObject vehicleGO = VehicleMeshBuilder.Build(type, color);
            vehicleGO.transform.SetParent(transform, false);
            vehicleGO.transform.position = startWp.Position;

            // Rotate vehicle facing the next node in route
            Waypoint nextWp = destWp;
            if (startWp.ConnectedWaypointIds.Count > 0)
            {
                long firstConnId = startWp.ConnectedWaypointIds[0];
                if (Graph.Waypoints.TryGetValue(firstConnId, out Waypoint wp))
                    nextWp = wp;
            }
            if (nextWp != null)
            {
                Vector3 dir = (nextWp.Position - startWp.Position);
                dir.y = 0;
                if (dir.sqrMagnitude > 0.01f)
                    vehicleGO.transform.rotation = Quaternion.LookRotation(dir.normalized);
            }

            vehicleGO.layer = _vehicleLayer;
            foreach (Transform c in vehicleGO.GetComponentsInChildren<Transform>())
                c.gameObject.layer = _vehicleLayer;

            float vLen = GetLength(type);
            float vW   = GetWidth(type);
            var col    = vehicleGO.AddComponent<BoxCollider>();
            col.size   = new Vector3(vW, 1.2f, vLen);
            col.center = new Vector3(0, 0.5f, 0);

            var agent = vehicleGO.AddComponent<VehicleAgent>();
            agent.Graph        = Graph;
            agent.VehicleType  = type;
            agent.VehicleWidth = vW;
            agent.BaseSpeed    = GetBaseSpeed(type);
            agent.RuntimeSpeedScale = SpeedScale;
            agent.VehicleLayer = 1 << _vehicleLayer;
            agent.StopDistance = vLen * 1.5f;
            agent.Wheels       = FindWheels(vehicleGO.transform);
            agent.DestroyOnArrival = false; // Stay active
            int lane = Random.Range(0, 2);
            agent.LaneIndex     = lane;
            agent.PreferredLane = lane;
            agent.Patience      = Random.Range(3f, 8f);

            // Assign customized/override personality
            agent.Personality = personality ?? PredefinedPersonalities[0].Clone();

            if (RuleProbabilities != null)
            {
                foreach (var rc in RuleProbabilities)
                {
                    agent.RuleProbabilities[rc.RuleId] = rc.ApplyProbability;
                }
            }

            // Trigger initialization immediately to assign the custom destination/path
            agent.InitWithCustomRoute(startWp, destWp);

            _agents.Add(agent);
            return agent;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private float GetRoadTypeSpawnWeight(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) roadType = "default";
            
            // Search in our custom configs
            for (int i = 0; i < RoadConfigs.Count; i++)
            {
                if (string.Equals(RoadConfigs[i].RoadType, roadType, System.StringComparison.OrdinalIgnoreCase))
                {
                    return RoadConfigs[i].SpawnWeight;
                }
            }
            
            // Fallback defaults
            switch (roadType.ToLower())
            {
                case "motorway": return 8f;
                case "primary": return 5f;
                case "secondary": return 3f;
                case "tertiary": return 2f;
                case "residential": return 1f;
                case "service": return 0.5f;
                default: return 1f;
            }
        }

        private VehicleMeshBuilder.VehicleType RollVehicleTypeForRoad(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) roadType = "default";
            
            float carRatio = 35f;
            float motoRatio = 55f;
            float busRatio = 10f;
            
            // Search in our custom configs
            bool found = false;
            for (int i = 0; i < RoadConfigs.Count; i++)
            {
                if (string.Equals(RoadConfigs[i].RoadType, roadType, System.StringComparison.OrdinalIgnoreCase))
                {
                    carRatio = RoadConfigs[i].CarRatio;
                    motoRatio = RoadConfigs[i].MotoRatio;
                    busRatio = RoadConfigs[i].BusRatio;
                    found = true;
                    break;
                }
            }
            
            if (!found)
            {
                // Preset fallbacks
                switch (roadType.ToLower())
                {
                    case "motorway":
                        carRatio = 70f; motoRatio = 20f; busRatio = 10f;
                        break;
                    case "primary":
                        carRatio = 45f; motoRatio = 40f; busRatio = 15f;
                        break;
                    case "secondary":
                        carRatio = 30f; motoRatio = 60f; busRatio = 10f;
                        break;
                    case "tertiary":
                        carRatio = 20f; motoRatio = 75f; busRatio = 5f;
                        break;
                    case "residential":
                        carRatio = 10f; motoRatio = 90f; busRatio = 0f;
                        break;
                    case "service":
                        carRatio = 5f; motoRatio = 95f; busRatio = 0f;
                        break;
                }
            }
            
            float total = carRatio + motoRatio + busRatio;
            if (total <= 0f) return VehicleMeshBuilder.VehicleType.Car;
            
            float r = Random.value * total;
            if (r < carRatio) return VehicleMeshBuilder.VehicleType.Car;
            if (r < carRatio + motoRatio) return VehicleMeshBuilder.VehicleType.Motorbike;
            return VehicleMeshBuilder.VehicleType.Bus;
        }

        private Waypoint PickSpawnWaypoint(out VehicleMeshBuilder.VehicleType vehicleType, VehicleMeshBuilder.VehicleType? forcedType = null)
        {
            vehicleType = forcedType ?? VehicleMeshBuilder.VehicleType.Car; // default fallback
            if (Graph.Waypoints.Count == 0) return null;

            var list = new List<Waypoint>(Graph.Waypoints.Values);
            
            // Rejection sampling based on road type spawn weights
            float maxWeight = 10f; // maximum allowed weight in inspector config
            float safeDist = _agents.Count < MaxActiveVehicles * 0.5f ? 10f : 6f;
            
            for (int attempt = 0; attempt < 100; attempt++)
            {
                Waypoint wp = list[Random.Range(0, list.Count)];
                if (wp == null) continue;
                
                // Do not spawn at intersection, traffic light, or congested areas
                if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                if (Graph.CongestionCosts != null && Graph.CongestionCosts.ContainsKey(wp.OSMNodeId)) continue;
                
                // Get spawn weight for this road type
                float weight = GetRoadTypeSpawnWeight(wp.RoadType);
                
                // Rejection step
                if (Random.value * maxWeight >= weight) continue;
                
                // Distance check
                bool tooClose = false;
                foreach (var a in _agents)
                {
                    if (a != null && Vector3.Distance(a.transform.position, wp.Position) < safeDist)
                    {
                        tooClose = true;
                        break;
                    }
                }
                
                if (!tooClose)
                {
                    if (!forcedType.HasValue)
                    {
                        vehicleType = RollVehicleTypeForRoad(wp.RoadType);
                    }
                    return wp;
                }
            }
            
            // Fallback: simple uniform random if weighted + distance check failed too many times
            for (int i = 0; i < 30; i++)
            {
                Waypoint wp = list[Random.Range(0, list.Count)];
                if (wp.ConnectedWaypointIds.Count > 2 || wp.IsTrafficLight) continue;
                
                bool tooClose = false;
                foreach (var a in _agents)
                {
                    if (a != null && Vector3.Distance(a.transform.position, wp.Position) < 3f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                
                if (!tooClose)
                {
                    if (!forcedType.HasValue)
                    {
                        vehicleType = RollVehicleTypeForRoad(wp.RoadType);
                    }
                    return wp;
                }
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
        private int _activeTabPanel = 0; // 0 = General Stats/Sliders, 1 = Rule Probabilities
        private Vector2 _rulesScrollPosition = Vector2.zero;

        private GUIStyle _titleStyle;
        private GUIStyle _statStyle;
        private GUIStyle _sliderLabelStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _collisionStyle;
        private GUIStyle _tabButtonStyle;
        private GUIStyle _tabButtonActiveStyle;

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
                _tabButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Normal
                };
                _tabButtonActiveStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.4f, 0.9f, 1f) }
                };
            }

            const float panW = 240f, pad = 10f;
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

            float panH = 340f;
            GUI.Box(new Rect(startX, startY, panW, panH), "");

            float y = startY + 5;
            float labelW = panW - 16;

            // Title + Stats
            GUI.Label(new Rect(startX + 8, y, labelW, 20), "OSM Traffic Control", _titleStyle);
            y += 22;

            // Tabs for General Stats vs Rules Adjustment
            float tabW = (panW - 16) / 2f;
            if (GUI.Button(new Rect(startX + 8, y, tabW, 20), "Control Panel", _activeTabPanel == 0 ? _tabButtonActiveStyle : _tabButtonStyle))
                _activeTabPanel = 0;
            if (GUI.Button(new Rect(startX + 8 + tabW, y, tabW, 20), "Rule Probs (%)", _activeTabPanel == 1 ? _tabButtonActiveStyle : _tabButtonStyle))
                _activeTabPanel = 1;
            y += 25;

            if (_activeTabPanel == 0)
            {
                int active = _agents.Count;
                GUI.Label(new Rect(startX + 8, y, labelW, 18), $"Active Vehicles: {active} / {MaxActiveVehicles}", _statStyle);
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
                GUI.Label(new Rect(startX + 8, y, labelW, 16), $"Speed Multiplier: x{SpeedScale:F1}", _sliderLabelStyle);
                y += 16;
                SpeedScale = GUI.HorizontalSlider(
                    new Rect(startX + 8, y, labelW, 16), SpeedScale, 0.1f, 5f);
                y += 22;

                if (TrafficLightManager.Instance != null)
                {
                    GUI.Label(new Rect(startX + 8, y, labelW - 20, 16), "Traffic Lights System", _statStyle);
                    TrafficLightManager.Instance.EnableTrafficLights = GUI.Toggle(
                        new Rect(startX + panW - 25, y, 20, 16), 
                        TrafficLightManager.Instance.EnableTrafficLights, "");
                }
            }
            else if (_activeTabPanel == 1)
            {
                // Draw adjustable rules slider list with a scrollview!
                float scrollHeight = panH - (y - startY) - 10f;
                Rect viewRect = new Rect(startX + 8, y, labelW, scrollHeight);
                Rect contentRect = new Rect(0, 0, labelW - 16, RuleProbabilities.Count * 36f);

                _rulesScrollPosition = GUI.BeginScrollView(viewRect, _rulesScrollPosition, contentRect, false, true);

                float ry = 0;
                for (int i = 0; i < RuleProbabilities.Count; i++)
                {
                    var rc = RuleProbabilities[i];
                    GUI.Label(new Rect(0, ry, contentRect.width, 16), $"{rc.RuleId}: {rc.RuleName} ({rc.ApplyProbability:F0}%)", _sliderLabelStyle);
                    ry += 16;
                    
                    float newVal = GUI.HorizontalSlider(new Rect(0, ry, contentRect.width, 16), rc.ApplyProbability, 0f, 100f);
                    if (Mathf.Abs(newVal - rc.ApplyProbability) > 0.01f)
                    {
                        rc.ApplyProbability = newVal;
                        RuleProbabilities[i] = rc; // Structs must be assigned back to List
                    }
                    ry += 20;
                }

                GUI.EndScrollView();
            }
        }
    }
}
