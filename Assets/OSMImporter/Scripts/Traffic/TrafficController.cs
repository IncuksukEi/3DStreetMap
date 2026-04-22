using System.Collections;
using UnityEngine;
using OSMImporter.Navigation;
using OSMImporter.Traffic.Sumo;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Unified Traffic Controller — điểm vào duy nhất cho tất cả traffic modes.
    /// 
    /// 3 modes:
    /// ├── OSM Native    → TrafficSpawner + VehicleAgent (AI trên WaypointGraph)
    /// ├── SUMO TraCI    → SumoBridge + TraCIClient (SUMO process bên ngoài)
    /// └── SUMO Native   → SimulationEngine + Krauss/LC2013 (built-in, đọc .net.xml)
    /// 
    /// Hybrid: cho phép bật > 1 mode cùng lúc.
    /// </summary>
    public class TrafficController : MonoBehaviour
    {
        public enum TrafficMode
        {
            OSMNative,      // Dùng WaypointGraph + VehicleAgent behaviors
            SUMOTraCI,      // Dùng SUMO external process via TraCI TCP  
            SUMONative,     // Dùng built-in SUMO simulation (Krauss/LC2013)
            Hybrid          // OSM + SUMO chạy cùng lúc
        }

        [Header("Mode Selection")]
        public TrafficMode Mode = TrafficMode.OSMNative;

        [Header("SUMO TraCI Settings")]
        [Tooltip("Đường dẫn tới sumo.exe")]
        public string SumoExecutable = "sumo";
        [Tooltip("Đường dẫn file .sumocfg")]
        public string SumoConfigFile = "";
        [Tooltip("Đường dẫn file .net.xml")]
        public string NetXmlFile = "";
        [Tooltip("TraCI port")]
        public int TraCIPort = 8813;
        [Tooltip("Dùng sumo-gui")]
        public bool UseSumoGui = false;
        [Tooltip("Tự động launch SUMO process")]
        public bool AutoLaunchSumo = true;

        [Header("SUMO Native Settings")]
        [Tooltip("SUMO step length (s)")]
        public float SumoStepLength = 0.5f;
        [Tooltip("Max xe Native SUMO")]
        public int NativeMaxVehicles = 100;
        [Tooltip("Native spawn interval (s)")]
        public float NativeSpawnInterval = 0.5f;

        [Header("OSM Settings")]
        [Tooltip("Max xe OSM Native")]
        public int OsmMaxVehicles = 300;
        [Tooltip("OSM spawn interval (s)")]
        public float OsmSpawnInterval = 0.15f;

        [Header("Coordinate Mapping")]
        public double OriginLat = 21.0330;
        public double OriginLon = 105.8500;
        public float MapScale = 1f;

        [Header("Simulation")]
        [Range(0.1f, 10f)]
        public float SimulationSpeed = 1.0f;

        [Header("Status (Read-only)")]
        [SerializeField] private string _activeMode;
        [SerializeField] private int _totalVehicles;

        // Internal references
        private TrafficSpawner _osmSpawner;
        private SumoBridge _sumoBridge;
        private SimulationEngine _nativeEngine;
        private WaypointGraph _graph;

        // ══════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        private void Start()
        {
            _graph = FindFirstObjectByType<WaypointGraph>();

            switch (Mode)
            {
                case TrafficMode.OSMNative:
                    SetupOSMNative();
                    break;
                case TrafficMode.SUMOTraCI:
                    SetupSUMOTraCI();
                    break;
                case TrafficMode.SUMONative:
                    SetupSUMONative();
                    break;
                case TrafficMode.Hybrid:
                    SetupOSMNative();
                    SetupSUMOTraCI();
                    break;
            }

            _activeMode = Mode.ToString();
        }

        private void Update()
        {
            _totalVehicles = 0;

            if (_osmSpawner != null && _osmSpawner.enabled)
            {
                _osmSpawner.SpeedScale = SimulationSpeed;
                // VehicleCount tracked internally by TrafficSpawner
            }

            if (_sumoBridge != null)
            {
                _sumoBridge.SimulationSpeed = SimulationSpeed;
                _totalVehicles += _sumoBridge.ActiveVehicles;
            }

            if (_nativeEngine != null)
            {
                _totalVehicles += _nativeEngine.VehicleCount;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // MODE SETUP
        // ══════════════════════════════════════════════════════════════════

        private void SetupOSMNative()
        {
            _osmSpawner = GetComponent<TrafficSpawner>();
            if (_osmSpawner == null)
                _osmSpawner = gameObject.AddComponent<TrafficSpawner>();

            _osmSpawner.MaxActiveVehicles = OsmMaxVehicles;
            _osmSpawner.SpawnInterval = OsmSpawnInterval;
            _osmSpawner.ContinuousSpawning = true;
            if (_graph != null) _osmSpawner.Graph = _graph;

            Debug.Log("[TrafficController] OSM Native mode activated.");
        }

        private void SetupSUMOTraCI()
        {
            _sumoBridge = GetComponent<SumoBridge>();
            if (_sumoBridge == null)
                _sumoBridge = gameObject.AddComponent<SumoBridge>();

            _sumoBridge.SumoExecutable = SumoExecutable;
            _sumoBridge.ConfigFile = SumoConfigFile;
            _sumoBridge.NetXmlFile = NetXmlFile;
            _sumoBridge.Port = TraCIPort;
            _sumoBridge.UseGui = UseSumoGui;
            _sumoBridge.AutoStartSumo = AutoLaunchSumo;
            _sumoBridge.OriginLat = OriginLat;
            _sumoBridge.OriginLon = OriginLon;
            _sumoBridge.MapScale = MapScale;
            _sumoBridge.SimulationSpeed = SimulationSpeed;

            Debug.Log("[TrafficController] SUMO TraCI mode activated.");
        }

        private void SetupSUMONative()
        {
            // Ưu tiên: dùng SimulationEngine đã được inject từ Editor (OSMImporterWindow Generate)
            _nativeEngine = FindFirstObjectByType<SimulationEngine>();
            if (_nativeEngine != null && _nativeEngine.network != null && _nativeEngine.network.Edges.Count > 0)
            {
                // Network đã sẵn sàng (từ OSM API auto-convert hoặc .net.xml)
                _nativeEngine.stepLength = SumoStepLength;
                _nativeEngine.maxVehicles = NativeMaxVehicles;
                _nativeEngine.spawnInterval = NativeSpawnInterval;
                Debug.Log($"[TrafficController] SUMO Native: reusing existing engine " +
                    $"({_nativeEngine.network.Nodes.Count} junctions, {_nativeEngine.network.Edges.Count} edges).");
                return;
            }

            SNetwork net = null;

            // Path 1: Parse từ .net.xml (chính xác nhất)
            if (!string.IsNullOrEmpty(NetXmlFile) && System.IO.File.Exists(NetXmlFile))
            {
                var mapper = new SumoToUnityMapper(NetXmlFile, OriginLat, OriginLon, MapScale);
                net = NetParser.Load(NetXmlFile, mapper);
            }
            // Path 2: Convert từ WaypointGraph (OSM API download → không cần file SUMO)
            else
            {
                if (_graph == null) _graph = FindFirstObjectByType<WaypointGraph>();
                if (_graph != null && _graph.Waypoints.Count > 0)
                {
                    net = OSMToSumoConverter.Convert(_graph, MapScale);
                    Debug.Log("[TrafficController] SUMO Native: converted from WaypointGraph (no .net.xml needed).");
                }
                else
                {
                    Debug.LogError("[TrafficController] SUMO Native cần file .net.xml hoặc WaypointGraph trong scene!");
                    return;
                }
            }

            if (net == null || net.Edges.Count == 0)
            {
                Debug.LogError("[TrafficController] Parse/convert network thất bại!");
                return;
            }

            // Tạo SimulationEngine
            if (_nativeEngine == null)
                _nativeEngine = GetComponent<SimulationEngine>();
            if (_nativeEngine == null)
                _nativeEngine = gameObject.AddComponent<SimulationEngine>();

            _nativeEngine.SetNetwork(net);
            _nativeEngine.stepLength = SumoStepLength;
            _nativeEngine.maxVehicles = NativeMaxVehicles;
            _nativeEngine.spawnInterval = NativeSpawnInterval;

            Debug.Log($"[TrafficController] SUMO Native mode activated " +
                $"({net.Nodes.Count} junctions, {net.Edges.Count} edges).");
        }

        // ══════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Chuyển mode tại runtime (sẽ clear xe hiện tại)</summary>
        public void SwitchMode(TrafficMode newMode)
        {
            // Cleanup current
            if (_osmSpawner != null) { Destroy(_osmSpawner); _osmSpawner = null; }
            if (_sumoBridge != null) { _sumoBridge.Disconnect(); Destroy(_sumoBridge); _sumoBridge = null; }
            if (_nativeEngine != null) { Destroy(_nativeEngine); _nativeEngine = null; }

            Mode = newMode;
            _activeMode = Mode.ToString();

            switch (Mode)
            {
                case TrafficMode.OSMNative: SetupOSMNative(); break;
                case TrafficMode.SUMOTraCI: SetupSUMOTraCI(); break;
                case TrafficMode.SUMONative: SetupSUMONative(); break;
                case TrafficMode.Hybrid:
                    SetupOSMNative();
                    SetupSUMOTraCI();
                    break;
            }
        }

        public SumoBridge GetSumoBridge() => _sumoBridge;
        public SimulationEngine GetNativeEngine() => _nativeEngine;
        public TrafficSpawner GetOSMSpawner() => _osmSpawner;

        // ══════════════════════════════════════════════════════════════════
        // STATS OVERLAY
        // ══════════════════════════════════════════════════════════════════

        private GUIStyle _titleStyle;
        private GUIStyle _labelStyle;

        private void OnGUI()
        {
            // Chỉ vẽ khi có SUMO mode active (OSM có overlay riêng trong TrafficSpawner)
            if (Mode == TrafficMode.OSMNative) return;

            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 14,
                    normal = { textColor = new Color(1f, 0.85f, 0.3f) }
                };
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    normal = { textColor = Color.white }
                };
            }

            float panW = 230f, pad = 10f;
            float startX = pad;
            float startY = pad;
            float panH = 120f;

            GUI.Box(new Rect(startX, startY, panW, panH), "");

            float y = startY + 5;
            GUI.Label(new Rect(startX + 8, y, panW - 16, 20), $"Traffic: {_activeMode}", _titleStyle);
            y += 22;

            if (_sumoBridge != null)
            {
                GUI.Label(new Rect(startX + 8, y, panW - 16, 18),
                    $"SUMO TraCI: {(_sumoBridge.IsConnected ? "Connected" : "Disconnected")}", _labelStyle);
                y += 18;
                GUI.Label(new Rect(startX + 8, y, panW - 16, 18),
                    $"  Vehicles: {_sumoBridge.ActiveVehicles}", _labelStyle);
                y += 18;
            }

            if (_nativeEngine != null)
            {
                GUI.Label(new Rect(startX + 8, y, panW - 16, 18),
                    $"Native SUMO: {_nativeEngine.VehicleCount} xe", _labelStyle);
                y += 18;
                GUI.Label(new Rect(startX + 8, y, panW - 16, 18),
                    $"  Spawned: {_nativeEngine.TotalSpawned} | Finished: {_nativeEngine.TotalFinished}",
                    _labelStyle);
            }
        }
    }
}
