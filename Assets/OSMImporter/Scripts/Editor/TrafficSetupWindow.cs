using System.IO;
using UnityEditor;
using UnityEngine;
using OSMImporter.Traffic.Sumo;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic.EditorScript
{
    /// <summary>
    /// One-click Traffic Setup — Tools → OSM Traffic → Traffic Setup
    /// Tự động detect data, tạo TrafficController, config mọi thứ.
    /// Chỉ cần bấm 1 nút rồi Play.
    /// </summary>
    public class TrafficSetupWindow : EditorWindow
    {
        // Auto-detected state
        private string _netXmlPath;
        private string _sumoCfgPath;
        private string _rouXmlPath;
        private bool _hasSumoInstalled;
        private bool _hasWaypointGraph;
        private double _centerLat = 21.033;
        private double _centerLon = 105.85;
        private float _mapScale = 1f;

        // User selection
        private TrafficController.TrafficMode _selectedMode = TrafficController.TrafficMode.SUMONative;
        private int _maxVehicles = 100;
        private bool _useSumoGui = false;
        private bool _scanned = false;

        // Preview state
        private SNetwork _previewNetwork;

        [MenuItem("Tools/OSM Traffic/Traffic Setup %#t")]
        public static void ShowWindow()
        {
            var win = GetWindow<TrafficSetupWindow>("Traffic Setup");
            win.minSize = new Vector2(400, 500);
            win.Show();
        }

        private void OnEnable()
        {
            ScanProject();
        }

        // ══════════════════════════════════════════════════════════════════
        // GUI
        // ══════════════════════════════════════════════════════════════════

        private void OnGUI()
        {
            // Header
            EditorGUILayout.Space(5);
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("🚦 SUMO + OSM Traffic Setup", headerStyle);
            EditorGUILayout.Space(5);

            if (!_scanned)
            {
                if (GUILayout.Button("Scan Project", GUILayout.Height(30)))
                    ScanProject();
                return;
            }

            // ── Status Box ──
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Detected Data", EditorStyles.boldLabel);

            StatusRow("WaypointGraph", _hasWaypointGraph);
            StatusRow(".net.xml", !string.IsNullOrEmpty(_netXmlPath), _netXmlPath);
            StatusRow(".sumocfg", !string.IsNullOrEmpty(_sumoCfgPath), _sumoCfgPath);
            StatusRow(".rou.xml", !string.IsNullOrEmpty(_rouXmlPath), _rouXmlPath);
            StatusRow("SUMO installed", _hasSumoInstalled);

            if (GUILayout.Button("Re-Scan", GUILayout.Width(80)))
                ScanProject();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);

            // ── Mode Selection ──
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Mode", EditorStyles.boldLabel);

            _selectedMode = (TrafficController.TrafficMode)EditorGUILayout.EnumPopup("Traffic Mode", _selectedMode);

            switch (_selectedMode)
            {
                case TrafficController.TrafficMode.SUMONative:
                    EditorGUILayout.HelpBox(
                        "Built-in SUMO simulation (Krauss + LC2013).\n" +
                        "✅ Không cần cài SUMO\n" +
                        "✅ Có .net.xml → dùng mạng lưới SUMO\n" +
                        "✅ Không .net.xml → tự convert từ WaypointGraph (OSM API)",
                        MessageType.Info);
                    break;
                case TrafficController.TrafficMode.SUMOTraCI:
                    EditorGUILayout.HelpBox(
                        "External SUMO process via TraCI TCP.\n" +
                        (string.IsNullOrEmpty(_sumoCfgPath) ? "⚠️ Cần file .sumocfg\n" : "✅ Có .sumocfg\n") +
                        (_hasSumoInstalled ? "✅ SUMO đã cài" : "❌ SUMO chưa cài — cần cài trước") + "\n" +
                        "Simulation chính xác nhất.",
                        _hasSumoInstalled ? MessageType.Info : MessageType.Warning);
                    break;
                case TrafficController.TrafficMode.OSMNative:
                    EditorGUILayout.HelpBox(
                        "OSM WaypointGraph + VehicleAgent behaviors.\n" +
                        "✅ Không cần file SUMO nào\n" +
                        (_hasWaypointGraph ? "✅ Có WaypointGraph" : "⚠️ Cần import OSM map trước"),
                        _hasWaypointGraph ? MessageType.Info : MessageType.Warning);
                    break;
                case TrafficController.TrafficMode.Hybrid:
                    EditorGUILayout.HelpBox(
                        "OSM Native + SUMO TraCI chạy cùng lúc.\n" +
                        "OSM = ambient traffic, SUMO = controlled traffic.",
                        MessageType.Info);
                    break;
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // ── Parameters ──
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label("Parameters", EditorStyles.boldLabel);

            _maxVehicles = EditorGUILayout.IntSlider("Max Vehicles", _maxVehicles, 10, 500);
            _centerLat = EditorGUILayout.DoubleField("Center Lat", _centerLat);
            _centerLon = EditorGUILayout.DoubleField("Center Lon", _centerLon);
            _mapScale = EditorGUILayout.FloatField("Map Scale", _mapScale);

            if (_selectedMode == TrafficController.TrafficMode.SUMOTraCI)
                _useSumoGui = EditorGUILayout.Toggle("Show SUMO GUI", _useSumoGui);

            // File override
            EditorGUILayout.Space(3);
            GUILayout.Label("File Override (optional)", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            _netXmlPath = EditorGUILayout.TextField(".net.xml", _netXmlPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string sel = EditorUtility.OpenFilePanel("Select .net.xml", Application.dataPath, "xml");
                if (!string.IsNullOrEmpty(sel)) _netXmlPath = sel;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // ── Actions ──
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            bool canSetup = CanSetup();
            GUI.enabled = canSetup;

            if (GUILayout.Button("🚀 Setup & Ready to Play", GUILayout.Height(40)))
            {
                DoSetup();
            }

            GUI.enabled = true;
            GUI.backgroundColor = Color.white;

            if (!canSetup)
            {
                string reason = GetCannotSetupReason();
                EditorGUILayout.HelpBox(reason, MessageType.Error);
            }

            EditorGUILayout.Space(5);

            // Preview button
            if (!string.IsNullOrEmpty(_netXmlPath) &&
                (_selectedMode == TrafficController.TrafficMode.SUMONative ||
                 _selectedMode == TrafficController.TrafficMode.SUMOTraCI))
            {
                if (GUILayout.Button("👁 Preview SUMO Network in Scene"))
                {
                    PreviewNetwork();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SCAN
        // ══════════════════════════════════════════════════════════════════

        private void ScanProject()
        {
            // Tìm .net.xml
            _netXmlPath = FindFile("*.net.xml");

            // Tìm .sumocfg
            _sumoCfgPath = FindFile("*.sumocfg");

            // Tìm .rou.xml
            _rouXmlPath = FindFile("*.rou.xml");

            // Check SUMO installed
            _hasSumoInstalled = CheckSumoInstalled();

            // Check WaypointGraph
            _hasWaypointGraph = FindFirstObjectByType<WaypointGraph>() != null;

            // Auto-detect center từ .net.xml
            if (!string.IsNullOrEmpty(_netXmlPath))
                AutoDetectCenter(_netXmlPath);

            _scanned = true;
            Repaint();
        }

        private string FindFile(string pattern)
        {
            string sampleDataDir = Path.Combine(Application.dataPath, "OSMImporter", "SampleData");
            if (Directory.Exists(sampleDataDir))
            {
                var files = Directory.GetFiles(sampleDataDir, pattern);
                if (files.Length > 0) return files[0];
            }

            // Fallback: search toàn bộ Assets
            var allFiles = Directory.GetFiles(Application.dataPath, pattern, SearchOption.AllDirectories);
            return allFiles.Length > 0 ? allFiles[0] : null;
        }

        private bool CheckSumoInstalled()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sumo", "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private void AutoDetectCenter(string netXmlPath)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(netXmlPath);
                var loc = doc.SelectSingleNode("//location");
                if (loc == null) return;

                string[] orig = loc.Attributes["origBoundary"].Value.Split(',');
                double minLon = double.Parse(orig[0], System.Globalization.CultureInfo.InvariantCulture);
                double minLat = double.Parse(orig[1], System.Globalization.CultureInfo.InvariantCulture);
                double maxLon = double.Parse(orig[2], System.Globalization.CultureInfo.InvariantCulture);
                double maxLat = double.Parse(orig[3], System.Globalization.CultureInfo.InvariantCulture);

                _centerLat = (minLat + maxLat) / 2.0;
                _centerLon = (minLon + maxLon) / 2.0;
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        // VALIDATION
        // ══════════════════════════════════════════════════════════════════

        private bool CanSetup()
        {
            switch (_selectedMode)
            {
                case TrafficController.TrafficMode.OSMNative:
                    return _hasWaypointGraph;
                case TrafficController.TrafficMode.SUMONative:
                    // Chấp nhận .net.xml HOẶC WaypointGraph trong scene
                    bool hasNetXml = !string.IsNullOrEmpty(_netXmlPath) && File.Exists(_netXmlPath);
                    return hasNetXml || _hasWaypointGraph;
                case TrafficController.TrafficMode.SUMOTraCI:
                    return !string.IsNullOrEmpty(_sumoCfgPath) && !string.IsNullOrEmpty(_netXmlPath) && _hasSumoInstalled;
                case TrafficController.TrafficMode.Hybrid:
                    return _hasWaypointGraph && !string.IsNullOrEmpty(_sumoCfgPath) && _hasSumoInstalled;
                default:
                    return false;
            }
        }

        private string GetCannotSetupReason()
        {
            switch (_selectedMode)
            {
                case TrafficController.TrafficMode.OSMNative:
                    return "Cần WaypointGraph trong scene. Import OSM map trước.";
                case TrafficController.TrafficMode.SUMONative:
                    return "Cần file .net.xml hoặc WaypointGraph trong scene. Import OSM map trước.";
                case TrafficController.TrafficMode.SUMOTraCI:
                    if (!_hasSumoInstalled) return "SUMO chưa cài. Download tại: sumo.dlr.de/docs/Downloads.php";
                    if (string.IsNullOrEmpty(_sumoCfgPath)) return "Cần file .sumocfg";
                    return "Cần file .net.xml";
                case TrafficController.TrafficMode.Hybrid:
                    if (!_hasWaypointGraph) return "Cần WaypointGraph trong scene.";
                    if (!_hasSumoInstalled) return "SUMO chưa cài.";
                    return "Cần file .sumocfg";
                default:
                    return "Unknown mode";
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SETUP — tạo / config TrafficController trong scene
        // ══════════════════════════════════════════════════════════════════

        private void DoSetup()
        {
            // Tìm hoặc tạo TrafficController
            var controller = FindFirstObjectByType<TrafficController>();
            GameObject go;

            if (controller != null)
            {
                go = controller.gameObject;
                Debug.Log("[Traffic Setup] Updating existing TrafficController.");
            }
            else
            {
                go = new GameObject("TrafficController");
                controller = go.AddComponent<TrafficController>();
                Undo.RegisterCreatedObjectUndo(go, "Create TrafficController");
                Debug.Log("[Traffic Setup] Created new TrafficController.");
            }

            // Disable existing TrafficSpawner nếu mode không phải OSM
            if (_selectedMode != TrafficController.TrafficMode.OSMNative &&
                _selectedMode != TrafficController.TrafficMode.Hybrid)
            {
                var existingSpawner = FindFirstObjectByType<TrafficSpawner>();
                if (existingSpawner != null)
                {
                    Undo.RecordObject(existingSpawner, "Disable TrafficSpawner");
                    existingSpawner.enabled = false;
                    Debug.Log("[Traffic Setup] Disabled existing TrafficSpawner (SUMO mode active).");
                }
            }

            // Config TrafficController
            Undo.RecordObject(controller, "Configure TrafficController");

            controller.Mode = _selectedMode;
            controller.OriginLat = _centerLat;
            controller.OriginLon = _centerLon;
            controller.MapScale = _mapScale;

            switch (_selectedMode)
            {
                case TrafficController.TrafficMode.SUMONative:
                    // Chỉ gán NetXmlFile nếu file tồn tại, không thì TrafficController sẽ auto-convert từ WaypointGraph
                    controller.NetXmlFile = (!string.IsNullOrEmpty(_netXmlPath) && File.Exists(_netXmlPath)) ? _netXmlPath : "";
                    controller.NativeMaxVehicles = _maxVehicles;
                    break;

                case TrafficController.TrafficMode.SUMOTraCI:
                    controller.SumoConfigFile = _sumoCfgPath;
                    controller.NetXmlFile = _netXmlPath;
                    controller.UseSumoGui = _useSumoGui;
                    controller.AutoLaunchSumo = true;
                    break;

                case TrafficController.TrafficMode.OSMNative:
                    controller.OsmMaxVehicles = _maxVehicles;
                    break;

                case TrafficController.TrafficMode.Hybrid:
                    controller.SumoConfigFile = _sumoCfgPath;
                    controller.NetXmlFile = _netXmlPath;
                    controller.UseSumoGui = _useSumoGui;
                    controller.OsmMaxVehicles = _maxVehicles / 2;
                    break;
            }

            EditorUtility.SetDirty(controller);
            Selection.activeGameObject = go;

            Debug.Log($"[Traffic Setup] ✅ Setup complete! Mode: {_selectedMode}. Press Play to start.");
            EditorUtility.DisplayDialog("Traffic Setup Complete",
                $"Mode: {_selectedMode}\n\n" +
                $"TrafficController đã được config.\n" +
                "Bấm Play để chạy traffic!",
                "OK");
        }

        // ══════════════════════════════════════════════════════════════════
        // PREVIEW
        // ══════════════════════════════════════════════════════════════════

        private void PreviewNetwork()
        {
            if (string.IsNullOrEmpty(_netXmlPath)) return;

            try
            {
                var mapper = new SumoToUnityMapper(_netXmlPath, _centerLat, _centerLon, _mapScale);
                _previewNetwork = NetParser.Load(_netXmlPath, mapper);

                // Reset cũ
                var old = GameObject.Find("SUMO_Network_Preview");
                if (old != null) DestroyImmediate(old);

                var root = new GameObject("SUMO_Network_Preview");
                Undo.RegisterCreatedObjectUndo(root, "Preview SUMO Network");
                int laneCount = 0;

                foreach (var edgePair in _previewNetwork.Edges)
                {
                    var edge = edgePair.Value;
                    var edgeObj = new GameObject($"Edge_{edge.id}");
                    edgeObj.transform.SetParent(root.transform);

                    foreach (var lane in edge.lanes)
                    {
                        if (lane.shape == null || lane.shape.Count < 2) continue;

                        var laneObj = new GameObject($"Lane_{lane.id}");
                        laneObj.transform.SetParent(edgeObj.transform);
                        laneObj.transform.position = lane.shape[0];

                        var lr = laneObj.AddComponent<LineRenderer>();
                        lr.positionCount = lane.shape.Count;
                        lr.SetPositions(lane.shape.ToArray());
                        lr.startWidth = 0.4f;
                        lr.endWidth = 0.4f;
                        lr.material = new Material(Shader.Find("Sprites/Default"));

                        Color c = lane.index == 0
                            ? new Color(0.2f, 0.6f, 1f, 0.8f)
                            : new Color(0.3f, 0.9f, 0.4f, 0.7f);
                        lr.startColor = c;
                        lr.endColor = c;
                        lr.useWorldSpace = true;
                        laneCount++;
                    }
                }

                Debug.Log($"[Traffic Setup] Preview: {_previewNetwork.Nodes.Count} junctions, " +
                    $"{_previewNetwork.Edges.Count} edges, {laneCount} lanes.");

                // Focus camera
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Traffic Setup] Preview failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════

        private void StatusRow(string label, bool ok, string detail = null)
        {
            EditorGUILayout.BeginHorizontal();
            var icon = ok ? "✅" : "❌";
            GUILayout.Label($"{icon} {label}", GUILayout.Width(160));
            if (!string.IsNullOrEmpty(detail))
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                GUILayout.Label(Path.GetFileName(detail), style);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
