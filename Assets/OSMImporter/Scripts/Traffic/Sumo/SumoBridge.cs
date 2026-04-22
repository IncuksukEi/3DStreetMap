using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// SumoBridge — MonoBehaviour chính điều phối SUMO ↔ Unity.
    /// 
    /// Chức năng:
    /// 1. Spawn process sumo.exe (hoặc connect tới instance đang chạy)
    /// 2. Mỗi frame: gọi TraCI SimulationStep → lấy vehicle states → sync Unity
    /// 3. On destroy: close connection + kill process
    /// 4. Auto-parse .net.xml để lấy boundaries cho coordinate mapping chính xác
    /// 
    /// Gắn vào 1 GameObject trống trong scene.
    /// </summary>
    public class SumoBridge : MonoBehaviour
    {
        [Header("SUMO Configuration")]
        [Tooltip("Đường dẫn tới sumo.exe hoặc sumo-gui.exe")]
        public string SumoExecutable = "sumo";

        [Tooltip("Đường dẫn tới file .sumocfg")]
        public string ConfigFile = "";

        [Tooltip("Đường dẫn tới file .net.xml (auto-parse boundaries)")]
        public string NetXmlFile = "";

        [Tooltip("Port TCP cho TraCI")]
        public int Port = 8813;

        [Tooltip("Tự động spawn SUMO process khi Start")]
        public bool AutoStartSumo = true;

        [Tooltip("Dùng sumo-gui (có giao diện) thay vì sumo (headless)")]
        public bool UseGui = false;

        [Header("Coordinate Mapping")]
        [Tooltip("Origin latitude của Unity map")]
        public double OriginLat = 21.0330;

        [Tooltip("Origin longitude của Unity map")]
        public double OriginLon = 105.8500;

        [Tooltip("Map scale (khớp với OSMImporter)")]
        public float MapScale = 1f;

        [Tooltip("Tự động detect origin từ OSMAreaRegistry")]
        public bool AutoDetectOrigin = true;

        [Header("Simulation")]
        [Tooltip("Tốc độ mô phỏng (1.0 = realtime, 2.0 = 2x)")]
        [Range(0.1f, 10f)]
        public float SimulationSpeed = 1.0f;

        [Tooltip("SUMO step length (giây). Khớp với .sumocfg")]
        public float StepLength = 0.05f;

        [Header("Rendering")]
        [Tooltip("Tốc độ lerp position (cao = bám sát, thấp = mượt)")]
        public float PositionLerpSpeed = 12f;

        [Tooltip("Tốc độ lerp rotation")]
        public float RotationLerpSpeed = 8f;

        [Header("Status (Read-only)")]
        [SerializeField] private bool _connected;
        [SerializeField] private int _activeVehicles;
        [SerializeField] private float _sumoTime;

        // Internal
        private TraCIClient _client;
        private SumoToUnityMapper _mapper;
        private SumoVehicleSync _sync;
        private Process _sumoProcess;
        private float _stepAccumulator;
        private Transform _vehicleParent;

        public bool IsConnected => _connected;
        public int ActiveVehicles => _activeVehicles;

        // ══════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        private void Start()
        {
            // Container cho xe SUMO
            var parentGo = new GameObject("SUMO_Vehicles");
            _vehicleParent = parentGo.transform;

            // Auto-detect origin từ scene nếu có
            if (AutoDetectOrigin)
                TryDetectOriginFromScene();

            // Khởi tạo mapper — parse .net.xml nếu có
            _mapper = new SumoToUnityMapper(OriginLat, OriginLon, MapScale);
            if (!string.IsNullOrEmpty(NetXmlFile))
            {
                _mapper.ParseNetXml(NetXmlFile);
            }

            _sync = new SumoVehicleSync(_mapper, _vehicleParent, PositionLerpSpeed, RotationLerpSpeed);
            _client = new TraCIClient("127.0.0.1", Port);

            if (AutoStartSumo && !string.IsNullOrEmpty(ConfigFile))
                StartCoroutine(LaunchAndConnect());
            else
                Debug.Log("[SumoBridge] Waiting for manual SUMO connection. Call Connect().");
        }

        private void Update()
        {
            if (!_connected || _client == null) return;

            // Tích lũy thời gian Unity → gọi SUMO step tương ứng
            _stepAccumulator += Time.deltaTime * SimulationSpeed;

            while (_stepAccumulator >= StepLength)
            {
                _stepAccumulator -= StepLength;

                try
                {
                    _client.SimulationStep();
                    _sumoTime += StepLength;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[SumoBridge] SimStep failed: {e.Message}");
                    Disconnect();
                    return;
                }
            }

            // Lấy vehicle states + sync
            try
            {
                var states = _client.GetAllVehicleStates();
                _sync.Sync(states, Time.deltaTime);
                _activeVehicles = _sync.ActiveVehicleCount;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SumoBridge] Vehicle sync failed: {e.Message}");
                Disconnect();
            }
        }

        private void OnDestroy()
        {
            Disconnect();
            KillSumoProcess();
        }

        // ══════════════════════════════════════════════════════════════════
        // CONNECT / DISCONNECT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Connect tới SUMO đang chạy.</summary>
        public bool Connect()
        {
            if (_client == null)
                _client = new TraCIClient("127.0.0.1", Port);

            _connected = _client.Connect(10000);
            if (_connected)
                Debug.Log("[SumoBridge] Connected to SUMO.");
            return _connected;
        }

        /// <summary>Ngắt kết nối.</summary>
        public void Disconnect()
        {
            _connected = false;
            _client?.Close();
            _sync?.Clear();
            _activeVehicles = 0;
        }

        /// <summary>Load boundaries từ .net.xml file mới tại runtime.</summary>
        public void LoadNetXml(string path)
        {
            NetXmlFile = path;
            _mapper?.ParseNetXml(path);
        }

        // ══════════════════════════════════════════════════════════════════
        // AUTO-DETECT ORIGIN
        // ══════════════════════════════════════════════════════════════════

        private void TryDetectOriginFromScene()
        {
            // Tìm Editor-generated origin data (OSMImporterWindow lưu trong PlayerPrefs/EditorPrefs)
            // Fallback: dùng giá trị Inspector
            var editor = FindFirstObjectByType<OSMImporter.OSMAreaRegistry>();
            if (editor != null)
            {
                Debug.Log("[SumoBridge] Found OSMAreaRegistry — using scene-based origin.");
                // OSMAreaRegistry không lưu lat/lon, giữ nguyên Inspector values
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SUMO PROCESS MANAGEMENT
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator LaunchAndConnect()
        {
            Debug.Log("[SumoBridge] Launching SUMO...");

            string exe = UseGui ? SumoExecutable.Replace("sumo", "sumo-gui") : SumoExecutable;
            string args = $"-c \"{ConfigFile}\" --remote-port {Port} --start --quit-on-end";

            try
            {
                _sumoProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = !UseGui,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                _sumoProcess.Start();
                Debug.Log($"[SumoBridge] SUMO started: {exe} {args}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SumoBridge] Failed to start SUMO: {e.Message}");
                Debug.LogError("[SumoBridge] Đảm bảo SUMO đã được cài và thêm vào PATH.");
                yield break;
            }

            // Chờ SUMO khởi tạo network
            yield return new WaitForSeconds(2.0f);

            // Thử connect tối đa 5 lần
            for (int i = 0; i < 5; i++)
            {
                if (Connect())
                    yield break;

                Debug.Log($"[SumoBridge] Retry {i + 1}/5...");
                yield return new WaitForSeconds(1.0f);
            }

            Debug.LogError("[SumoBridge] Cannot connect to SUMO after 5 retries.");
        }

        private void KillSumoProcess()
        {
            if (_sumoProcess != null && !_sumoProcess.HasExited)
            {
                try
                {
                    _sumoProcess.Kill();
                    Debug.Log("[SumoBridge] SUMO process killed.");
                }
                catch { }
            }
            _sumoProcess = null;
        }

        // ══════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Lấy SumoVehicleSync để query vehicle info.</summary>
        public SumoVehicleSync GetVehicleSync() => _sync;

        /// <summary>Lấy TraCI client cho custom queries.</summary>
        public TraCIClient GetClient() => _client;

        /// <summary>Lấy coordinate mapper.</summary>
        public SumoToUnityMapper GetMapper() => _mapper;
    }
}
