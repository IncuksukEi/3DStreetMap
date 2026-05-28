using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OSMImporter.Data;
using OSMImporter.Generators;
using OSMImporter.Navigation;
using OSMImporter.Traffic;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Traffic.Sumo;
using Unity.AI.Navigation;
using UnityEngine.AI;

namespace OSMImporter.Editor
{
    public class OSMImporterWindow : EditorWindow
    {
        // ── Tabs ──────────────────────────────────────────────────────────────
        private enum Tab { Download, File }
        private Tab _activeTab = Tab.Download;

        // ── Download tab ──────────────────────────────────────────────────────
        private enum InputMode { Center, BoundingBox }
        private InputMode _inputMode = InputMode.Center;

        // Center + radius
        private double _centerLat = 21.0285;   // Hanoi default
        private double _centerLon = 105.8542;
        private float  _radiusMeters = 500f;

        // Bounding box
        private double _minLat = 21.0245, _minLon = 105.8488;
        private double _maxLat = 21.0320, _maxLon = 105.8600;

        // Download state
        private bool   _isDownloading = false;
        private string _downloadStatus = "";
        private bool   _downloadFailed = false;

        // ── File tab ──────────────────────────────────────────────────────────
        private string _osmFilePath = "";

        private void OnEnable()
        {
            // Auto-populate with the sample file if field is empty
            if (string.IsNullOrEmpty(_osmFilePath))
            {
                string sample = System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "OSMImporter", "SampleData", "sample_hanoi.osm");
                if (System.IO.File.Exists(sample))
                    _osmFilePath = sample;
            }
        }

        // ── MS Building Footprints ────────────────────────────────────────────
        private bool   _useMSBuildings = true;
        private double _lastMinLat, _lastMinLon, _lastMaxLat, _lastMaxLon;

        // ── Shared settings ───────────────────────────────────────────────────
        private float  _scale = 1f;
        private float  _roadWidthMultiplier = 1f;
        private bool   _generateRoads = true, _generateBuildings = true, _generateWaypoints = true;
        private bool   _generateWater = true, _generateDecorations = true;
        private bool   _generateGround = true;
        private float  _buildingMinHeight = 6f, _buildingMaxHeight = 20f;
        private Color  _roadColor     = new Color(0.2f, 0.2f, 0.2f, 1f);
        private Color  _buildingColor = new Color(0.7f, 0.7f, 0.65f, 1f);
        private Color  _waterColor    = new Color(0.3f, 0.6f, 0.8f, 1f);
        private Color  _groundColor   = new Color(0.35f, 0.48f, 0.35f, 1f);

        // ── Label settings ────────────────────────────────────────────────────
        private bool          _generateLabels  = true;
        private bool          _labelsFoldout   = true;
        private LabelSettings _labelSettings   = new LabelSettings();

        // ── Traffic settings ──────────────────────────────────────────────────
        private bool  _generateTraffic   = true;
        private bool  _trafficFoldout    = true;
        private int   _carCount          = 10;
        private int   _motoCount         = 12;
        private int   _busCount          = 2;
        private float _trafficSpeedScale = 1f;

        // ── SUMO Native Traffic ──────────────────────────────────────────────
        private bool   _useSumoNative    = true;
        private enum SumoSource { AutoFromOSM, ManualFile }
        private SumoSource _sumoSource    = SumoSource.AutoFromOSM;
        private string _sumoNetFilePath  = "";
        private string _sumoRouFilePath  = "";

        // ── Runtime ───────────────────────────────────────────────────────────
        private GameObject _generatedRoot;
        private Vector2    _scrollPos;

        // ────────────────────────────────────────────────────────────────────
        [MenuItem("Tools/OSM Importer")]
        public static void ShowWindow() =>
            GetWindow<OSMImporterWindow>("OSM Importer").minSize = new Vector2(390, 660);

        // ────────────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            GUILayout.Space(8);

            // Header
            EditorGUILayout.LabelField("OSM Street Importer", EditorStyles.boldLabel);
            GUILayout.Space(4);

            // Tabs
            _activeTab = (Tab)GUILayout.Toolbar((int)_activeTab,
                new[] { "📡  Download from OSM", "📂  Load .osm File" },
                GUILayout.Height(28));
            GUILayout.Space(10);

            if (_activeTab == Tab.Download) DrawDownloadTab();
            else                            DrawFileTab();

            GUILayout.Space(12);
            DrawSharedSettings();
            GUILayout.Space(12);
            DrawActionButtons();

            EditorGUILayout.EndScrollView();
        }

        // ── Download Tab ─────────────────────────────────────────────────────
        private void DrawDownloadTab()
        {
            EditorGUILayout.HelpBox(
                "Fetch map data directly from OpenStreetMap via the Overpass API.\n" +
                "Choose a center point + radius, or specify an exact bounding box.",
                MessageType.Info);
            GUILayout.Space(6);

            // Input mode toggle
            _inputMode = (InputMode)EditorGUILayout.EnumPopup("Input Mode", _inputMode);
            GUILayout.Space(4);

            if (_inputMode == InputMode.Center)
            {
                _centerLat    = EditorGUILayout.DoubleField("Center Latitude",  _centerLat);
                _centerLon    = EditorGUILayout.DoubleField("Center Longitude", _centerLon);
                _radiusMeters = EditorGUILayout.Slider("Radius (m)", _radiusMeters, 100f, 5000f);

                // Preview bbox
                var (mn, mw, mx, me) = OSMDownloader.BBoxFromCenter(_centerLat, _centerLon, _radiusMeters);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("→ BBox",
                    $"{mn:F5},{mw:F5}  →  {mx:F5},{me:F5}",
                    EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.LabelField("South-West corner", EditorStyles.boldLabel);
                _minLat = EditorGUILayout.DoubleField("  Min Lat (South)", _minLat);
                _minLon = EditorGUILayout.DoubleField("  Min Lon (West)",  _minLon);
                EditorGUILayout.LabelField("North-East corner", EditorStyles.boldLabel);
                _maxLat = EditorGUILayout.DoubleField("  Max Lat (North)", _maxLat);
                _maxLon = EditorGUILayout.DoubleField("  Max Lon (East)",  _maxLon);
            }

            GUILayout.Space(8);

            // Status area
            if (_isDownloading)
            {
                EditorGUILayout.HelpBox(_downloadStatus, MessageType.None);
                Repaint();
            }
            else if (_downloadFailed)
                EditorGUILayout.HelpBox(_downloadStatus, MessageType.Error);
            else if (!string.IsNullOrEmpty(_downloadStatus))
                EditorGUILayout.HelpBox(_downloadStatus, MessageType.None);

            GUILayout.Space(4);

            // Download + Generate button
            EditorGUI.BeginDisabledGroup(_isDownloading);
            if (GUILayout.Button(_isDownloading ? "⏳  Downloading..." : "⬇  Download & Generate", GUILayout.Height(38)))
                _ = DownloadAndGenerateAsync();
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(4);
            EditorGUILayout.LabelField(
                "💡 Tip: Keep the area small (< 1 km²) to stay within Overpass limits.",
                EditorStyles.wordWrappedMiniLabel);

            // Quick preset buttons
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Quick Presets", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Hanoi Center")) SetCenter(21.0285, 105.8542);
            if (GUILayout.Button("Bách Khoa HN")) SetCenter(21.0048, 105.8455);
            if (GUILayout.Button("Ho Chi Minh"))   SetCenter(10.7769, 106.7009);
            if (GUILayout.Button("Da Nang"))       SetCenter(16.0544, 108.2022);
            EditorGUILayout.EndHorizontal();
        }

        private void SetCenter(double lat, double lon)
        {
            _centerLat = lat; _centerLon = lon;
            _inputMode = InputMode.Center;
        }

        // ── File Tab ──────────────────────────────────────────────────────────
        private void DrawFileTab()
        {
            EditorGUILayout.HelpBox(
                "1. Export a .osm file from openstreetmap.org\n" +
                "2. Place it anywhere on disk\n" +
                "3. Select it below and click Generate",
                MessageType.Info);
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            _osmFilePath = EditorGUILayout.TextField("File Path", _osmFilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string p = EditorUtility.OpenFilePanel("Select OSM File", Application.dataPath, "osm");
                if (!string.IsNullOrEmpty(p)) _osmFilePath = p;
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Shared Settings ───────────────────────────────────────────────────
        private void DrawSharedSettings()
        {
            EditorGUILayout.LabelField("Generation Settings", EditorStyles.boldLabel);
            _scale               = EditorGUILayout.Slider("Scale", _scale, 0.1f, 5f);
            _roadWidthMultiplier = EditorGUILayout.Slider("Road Width Multiplier", _roadWidthMultiplier, 0.5f, 3f);

            GUILayout.Space(4);
            _generateRoads     = EditorGUILayout.Toggle("Generate Roads",     _generateRoads);
            _generateBuildings = EditorGUILayout.Toggle("Generate Buildings", _generateBuildings);
            _generateWater     = EditorGUILayout.Toggle("Generate Water",     _generateWater);
            _generateDecorations= EditorGUILayout.Toggle("Generate Decorations", _generateDecorations);
            _generateWaypoints = EditorGUILayout.Toggle("Generate Waypoints", _generateWaypoints);
            _generateLabels    = EditorGUILayout.Toggle("Generate Labels",    _generateLabels);
            _generateGround    = EditorGUILayout.Toggle("Generate Ground Plane", _generateGround);

            GUILayout.Space(4);
            _useMSBuildings = EditorGUILayout.Toggle("🏘 MS Building Footprints", _useMSBuildings);
            if (_useMSBuildings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Bổ sung nhà nhỏ từ Microsoft AI Building Footprints.\n" +
                    "Dữ liệu vệ tinh AI phủ toàn bộ VN, bao gồm nhà dân/nhà ống.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }

            if (_generateBuildings)
            {
                EditorGUILayout.LabelField("Building Settings", EditorStyles.boldLabel);
                _buildingMinHeight = EditorGUILayout.Slider("Min Height (m)", _buildingMinHeight, 3f, 15f);
                _buildingMaxHeight = EditorGUILayout.Slider("Max Height (m)", _buildingMaxHeight, 6f, 100f);
            }

            if (_generateLabels)
                DrawLabelSettings();

            EditorGUILayout.LabelField("Colors", EditorStyles.boldLabel);
            _roadColor     = EditorGUILayout.ColorField("Road Color",     _roadColor);
            _buildingColor = EditorGUILayout.ColorField("Building Color", _buildingColor);
            _waterColor    = EditorGUILayout.ColorField("Water Color",    _waterColor);
            if (_generateGround)
                _groundColor = EditorGUILayout.ColorField("Ground Color", _groundColor);

            GUILayout.Space(8);
            DrawTrafficSettings();
        }

        private void DrawTrafficSettings()
        {
            _generateTraffic = EditorGUILayout.Toggle("Generate Traffic", _generateTraffic);
            if (!_generateTraffic) return;

            _trafficFoldout = EditorGUILayout.Foldout(_trafficFoldout, "Traffic Settings", true);
            if (!_trafficFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("🚗 Vehicles spawn at runtime (Press Play). Add MapCameraController to Camera for smooth navigation.", MessageType.Info);
            _carCount          = EditorGUILayout.IntSlider("🚗  Cars",        _carCount,          0, 50);
            _motoCount         = EditorGUILayout.IntSlider("🚴  Motorbikes",  _motoCount,         0, 80);
            _busCount          = EditorGUILayout.IntSlider("🚌  Buses",       _busCount,          0, 10);
            _trafficSpeedScale = EditorGUILayout.Slider("Speed ×",            _trafficSpeedScale, 0.1f, 5f);

            GUILayout.Space(6);
            _useSumoNative = EditorGUILayout.Toggle("🚦 SUMO Native Simulation", _useSumoNative);
            if (_useSumoNative)
            {
                EditorGUI.indentLevel++;

                // Download tab → luôn auto, File tab → cho chọn source
                if (_activeTab == Tab.Download)
                {
                    _sumoSource = SumoSource.AutoFromOSM;
                    EditorGUILayout.HelpBox(
                        "Krauss car-following + LC2013 lane-changing.\n" +
                        "Mạng lưới SUMO sẽ được tự động tạo từ dữ liệu OSM API — không cần file .net.xml.",
                        MessageType.Info);
                }
                else
                {
                    _sumoSource = (SumoSource)EditorGUILayout.EnumPopup("SUMO Source", _sumoSource);

                    if (_sumoSource == SumoSource.AutoFromOSM)
                    {
                        EditorGUILayout.HelpBox(
                            "Tự convert mạng lưới SUMO từ dữ liệu .osm đã load — không cần file .net.xml.",
                            MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "Load mạng lưới từ file .net.xml — chính xác nhất (dùng netconvert output).",
                            MessageType.Info);

                        EditorGUILayout.BeginHorizontal();
                        _sumoNetFilePath = EditorGUILayout.TextField("SUMO .net.xml:", _sumoNetFilePath);
                        if (GUILayout.Button("...", GUILayout.Width(30)))
                        {
                            string p = EditorUtility.OpenFilePanel("Select SUMO Network", Application.dataPath, "xml");
                            if (!string.IsNullOrEmpty(p)) _sumoNetFilePath = p;
                        }
                        EditorGUILayout.EndHorizontal();

                        EditorGUILayout.BeginHorizontal();
                        _sumoRouFilePath = EditorGUILayout.TextField("SUMO .rou.xml:", _sumoRouFilePath);
                        if (GUILayout.Button("...", GUILayout.Width(30)))
                        {
                            string p = EditorUtility.OpenFilePanel("Select SUMO Routes", Application.dataPath, "xml");
                            if (!string.IsNullOrEmpty(p)) _sumoRouFilePath = p;
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
        }

        private void DrawLabelSettings()
        {
            _labelsFoldout = EditorGUILayout.Foldout(_labelsFoldout, "Label Settings", true);
            if (!_labelsFoldout) return;

            EditorGUI.indentLevel++;

            // ── Street names (flat, aligned to road)
            _labelSettings.ShowStreetNames = EditorGUILayout.Toggle("Street Names", _labelSettings.ShowStreetNames);
            if (_labelSettings.ShowStreetNames)
            {
                EditorGUI.indentLevel++;
                _labelSettings.StreetLabelColor = EditorGUILayout.ColorField("Color",    _labelSettings.StreetLabelColor);
                _labelSettings.StreetFontSize   = EditorGUILayout.Slider("Font Size",    _labelSettings.StreetFontSize, 1f, 30f);
                _labelSettings.StreetYOffset    = EditorGUILayout.Slider("Y Offset (m)", _labelSettings.StreetYOffset, 0f, 20f);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(3);

            // ── Area / building names (floating billboard, Google Maps style)
            _labelSettings.ShowAreaNames = EditorGUILayout.Toggle("Area / Building Names", _labelSettings.ShowAreaNames);
            if (_labelSettings.ShowAreaNames)
            {
                EditorGUI.indentLevel++;
                _labelSettings.AreaLabelColor  = EditorGUILayout.ColorField("Color",           _labelSettings.AreaLabelColor);
                _labelSettings.AreaFontSize    = EditorGUILayout.Slider("Font Size",            _labelSettings.AreaFontSize,  1f, 30f);
                _labelSettings.AreaYOffset     = EditorGUILayout.Slider("Float Height (m)",     _labelSettings.AreaYOffset,   0f, 100f);
                _labelSettings.AreaLabelScale  = EditorGUILayout.Slider("Screen Size (px)",    _labelSettings.AreaLabelScale, 10f, 200f);
                EditorGUILayout.HelpBox("🌐 Billboard style: floats above area and always faces camera (Google Maps style). Registered in Area Panel.", MessageType.Info);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(3);

            // ── Place node names
            _labelSettings.ShowPlaceNames = EditorGUILayout.Toggle("Place Names (nodes)", _labelSettings.ShowPlaceNames);
            if (_labelSettings.ShowPlaceNames)
            {
                EditorGUI.indentLevel++;
                _labelSettings.PlaceLabelColor = EditorGUILayout.ColorField("Color",          _labelSettings.PlaceLabelColor);
                _labelSettings.PlaceFontSize   = EditorGUILayout.Slider("Font Size",          _labelSettings.PlaceFontSize,  1f, 20f);
                _labelSettings.PlaceYOffset    = EditorGUILayout.Slider("Y Offset (m)",       _labelSettings.PlaceYOffset,   0f, 30f);
                _labelSettings.PlaceLabelScale = EditorGUILayout.Slider("Screen Size (px)",  _labelSettings.PlaceLabelScale, 10f, 150f);
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
        }

        // ── Action Buttons ───────────────────────────────────────────────────
        private void DrawActionButtons()
        {
            if (_activeTab == Tab.File)
            {
                EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_osmFilePath) || _isDownloading);
                if (GUILayout.Button(_isDownloading ? "⏳  Loading MS Buildings..." : "Generate", GUILayout.Height(38)))
                {
                    if (_useMSBuildings)
                        _ = GenerateFromFileAsync(_osmFilePath);
                    else
                        GenerateFromFile(_osmFilePath);
                }
                EditorGUI.EndDisabledGroup();

                // Hiện trạng thái download MS Buildings
                if (_isDownloading)
                {
                    EditorGUILayout.HelpBox(_downloadStatus, MessageType.None);
                    Repaint();
                }
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Clear Generated Objects", GUILayout.Height(28)))
                ClearGenerated();
        }

        // ── Download & Generate ───────────────────────────────────────────────
        private async Task DownloadAndGenerateAsync()
        {
            _isDownloading  = true;
            _downloadFailed = false;
            _downloadStatus = "Starting download...";
            Repaint();

            double mn, mw, mx, me;
            if (_inputMode == InputMode.Center)
                (mn, mw, mx, me) = OSMDownloader.BBoxFromCenter(_centerLat, _centerLon, _radiusMeters);
            else
                (mn, mw, mx, me) = (_minLat, _minLon, _maxLat, _maxLon);

            // Lưu bbox cho MS Buildings
            _lastMinLat = mn; _lastMinLon = mw; _lastMaxLat = mx; _lastMaxLon = me;

            string filePath = await OSMDownloader.DownloadAsync(
                mn, mw, mx, me,
                progress =>
                {
                    _downloadStatus = progress;
                    Repaint();
                },
                error =>
                {
                    _downloadStatus = error;
                    _downloadFailed = true;
                    Repaint();
                });

            _isDownloading = false;

            if (filePath != null)
            {
                _downloadStatus = $"✔ Downloaded → {Path.GetFileName(filePath)}";

                // Download MS Buildings nếu bật
                List<BuildingFootprint> msBuildings = null;
                if (_useMSBuildings)
                {
                    _downloadStatus = "Downloading Microsoft Building Footprints...";
                    Repaint();
                    msBuildings = await MSBuildingDownloader.DownloadBuildingsForBBox(
                        mn, mw, mx, me,
                        progress => { _downloadStatus = progress; Repaint(); },
                        error => { Debug.LogWarning($"[MS Buildings] {error}"); });
                    _downloadStatus = $"✔ MS Buildings: {msBuildings?.Count ?? 0} footprints";
                    Repaint();
                }

                GenerateFromFile(filePath, msBuildings);
            }

            Repaint();
        }

        // ── Generate from File + MS Buildings ──────────────────────────────────
        private async Task GenerateFromFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) { Debug.LogError($"File not found: {filePath}"); return; }

            _isDownloading  = true;
            _downloadFailed = false;
            _downloadStatus = "Parsing .osm file for bounding box...";
            Repaint();

            // Parse file để lấy bbox
            var tempData = OSMParser.Parse(filePath);
            double mn = tempData.Bounds.MinLat, mw = tempData.Bounds.MinLon;
            double mx = tempData.Bounds.MaxLat, me = tempData.Bounds.MaxLon;

            // Download MS Buildings
            List<BuildingFootprint> msBuildings = null;
            _downloadStatus = "Downloading Microsoft Building Footprints...";
            Repaint();
            msBuildings = await MSBuildingDownloader.DownloadBuildingsForBBox(
                mn, mw, mx, me,
                progress => { _downloadStatus = progress; Repaint(); },
                error    => { Debug.LogWarning($"[MS Buildings] {error}"); });
            _downloadStatus = $"✔ MS Buildings: {msBuildings?.Count ?? 0} footprints";

            _isDownloading = false;
            Repaint();

            GenerateFromFile(filePath, msBuildings);
        }

        // ── Generate from a file path ─────────────────────────────────────────
        private void GenerateFromFile(string filePath, List<BuildingFootprint> msBuildings = null)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"[OSM Importer] File not found: {filePath}");
                return;
            }

            EditorUtility.DisplayProgressBar("OSM Import", "Parsing OSM data...", 0.1f);
            try
            {
                var mapData = OSMParser.Parse(filePath);
                if (mapData.Nodes.Count == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("OSM Importer",
                        "No nodes found in the downloaded data.\nTry a larger area or different location.", "OK");
                    return;
                }

                Debug.Log($"[OSM Import] Parsed: {mapData.Nodes.Count} nodes, {mapData.Ways.Count} ways");
                Debug.Log($"[OSM Import] Water bodies: {mapData.GetWaterBodies().Count}, Rivers: {mapData.GetRivers().Count}");
                Debug.Log($"[OSM Import] Decorations (signals/stops): {mapData.GetDecorations().Count}");

                ClearGenerated();
                _generatedRoot = new GameObject("OSM_Map");
                Undo.RegisterCreatedObjectUndo(_generatedRoot, "Generate OSM Map");

                if (_generateRoads)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Generating roads...", 0.35f);
                    var p = new GameObject("Roads");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    RoadGenerator.Generate(mapData, p.transform, CreateMaterial("OSM_Road", _roadColor), _scale, _roadWidthMultiplier);
                }

                if (_generateBuildings)
                {
                    // Merge MS Building Footprints vào mapData trước khi sinh mesh
                    if (msBuildings != null && msBuildings.Count > 0)
                    {
                        EditorUtility.DisplayProgressBar("OSM Import", $"Merging {msBuildings.Count} MS Buildings...", 0.55f);
                        MergeMSBuildings(mapData, msBuildings);
                        Debug.Log($"[OSM Import] Merged {msBuildings.Count} MS Building Footprints");
                    }

                    EditorUtility.DisplayProgressBar("OSM Import", "Generating buildings...", 0.65f);
                    var p = new GameObject("Buildings");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    BuildingGenerator.Generate(mapData, p.transform, CreateMaterial("OSM_Building", _buildingColor), _scale, _buildingMinHeight, _buildingMaxHeight);
                }

                if (_generateWater)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Generating water...", 0.70f);
                    var p = new GameObject("Water");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    WaterGenerator.Generate(mapData, p.transform, CreateMaterial("OSM_Water", _waterColor), _scale);
                }

                if (_generateDecorations)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Generating decorations...", 0.75f);
                    var p = new GameObject("Decorations");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    DecorationGenerator.Generate(mapData, p.transform, _scale);
                }

                if (_generateWaypoints)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Building waypoint graph...", 0.85f);
                    var p = new GameObject("WaypointGraph");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    var graph = p.AddComponent<WaypointGraph>();
                    graph.BuildFromOSM(mapData, _scale);
                    graph.SaveToEntries();
                    UnityEditor.EditorUtility.SetDirty(p);
                }

                EditorUtility.DisplayProgressBar("OSM Import", "Baking NavMesh Area...", 0.90f);
                var navSurface = _generatedRoot.AddComponent<NavMeshSurface>();
                navSurface.collectObjects = CollectObjects.Children;
                navSurface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
                navSurface.BuildNavMesh();

                if (_generateLabels)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Generating labels...", 0.93f);
                    var p = new GameObject("Labels");
                    p.transform.SetParent(_generatedRoot.transform, false);
                    LabelGenerator.Generate(mapData, p.transform, _labelSettings, _scale);

                    // Spawn MapAreaPanel if not already in scene
                    if (UnityEngine.Object.FindFirstObjectByType<OSMImporter.MapAreaPanel>() == null)
                    {
                        var uiGO = new GameObject("OSM_AreaPanel");
                        uiGO.AddComponent<OSMImporter.MapAreaPanel>();
                        Undo.RegisterCreatedObjectUndo(uiGO, "Spawn OSM Area Panel");
                    }

                    // Auto-attach MapCameraController to Main Camera if not present
                    Camera mainCam = Camera.main;
                    if (mainCam != null && mainCam.GetComponent<OSMImporter.MapCameraController>() == null)
                    {
                        var ctrl = mainCam.gameObject.AddComponent<OSMImporter.MapCameraController>();
                        // Position camera top-down over the map centre
                        mainCam.transform.position = new Vector3(0, 150f * _scale, 0);
                        mainCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                        Undo.RegisterCreatedObjectUndo(ctrl, "Add MapCameraController");
                        Debug.Log("[OSM] MapCameraController added to Main Camera. WASD/scroll/middle-drag to navigate.");
                    }
                }

                if (_generateGround)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Creating ground plane...", 0.96f);
                    CreateGroundPlane(_generatedRoot.transform, mapData, _scale);
                }

                if (_generateTraffic)
                {
                    EditorUtility.DisplayProgressBar("OSM Import", "Setting up traffic...", 0.97f);
                    // Clean up old spawner
                    if (GameObject.Find("OSM_Traffic") is GameObject oldTraffic)
                        Undo.DestroyObjectImmediate(oldTraffic);

                    var trafficGO = new GameObject("OSM_Traffic");
                    Undo.RegisterCreatedObjectUndo(trafficGO, "Generate OSM Traffic");
                    var spawner          = trafficGO.AddComponent<TrafficSpawner>();
                    spawner.CarCount     = _carCount;
                    spawner.MotoCount    = _motoCount;
                    spawner.BusCount     = _busCount;
                    spawner.SpeedScale   = _trafficSpeedScale;
                    Debug.Log($"[OSM] TrafficSpawner added — {_carCount} cars, {_motoCount} motos, {_busCount} buses. Press Play to start traffic.");

                    // SUMO Native Integration
                    if (_useSumoNative)
                    {
                        SNetwork sumoNetwork = null;
                        string sourceDesc = "";

                        if (_sumoSource == SumoSource.ManualFile
                            && !string.IsNullOrEmpty(_sumoNetFilePath)
                            && File.Exists(_sumoNetFilePath))
                        {
                            // ManualFile: load từ .net.xml
                            EditorUtility.DisplayProgressBar("OSM Import", "Loading SUMO network from .net.xml...", 0.98f);
                            var mapper = new SumoToUnityMapper(
                                _sumoNetFilePath,
                                mapData.Bounds.CenterLat,
                                mapData.Bounds.CenterLon,
                                _scale);
                            sumoNetwork = NetParser.Load(_sumoNetFilePath, mapper);
                            sourceDesc = $".net.xml ({sumoNetwork?.Edges.Count ?? 0} edges)";
                        }
                        else
                        {
                            // AutoFromOSM: convert trực tiếp từ OSMMapData
                            EditorUtility.DisplayProgressBar("OSM Import", "Converting OSM → SUMO network (full geometry)...", 0.98f);
                            sumoNetwork = OSMToSumoConverter.ConvertFromOSMData(mapData, _scale);
                            if (sumoNetwork != null && sumoNetwork.Edges.Count > 0)
                                sourceDesc = $"OSM API → auto ({sumoNetwork.Edges.Count} edges)";
                            else
                                Debug.LogWarning("[OSM+SUMO] Convert thất bại — kiểm tra dữ liệu OSM có highway không.");
                        }

                        // Attach SimulationEngine nếu convert thành công
                        if (sumoNetwork != null && sumoNetwork.Edges.Count > 0)
                        {
                            if (GameObject.Find("SUMO_Simulation") is GameObject oldSumo)
                                Undo.DestroyObjectImmediate(oldSumo);

                            var sumoGO = new GameObject("SUMO_Simulation");
                            Undo.RegisterCreatedObjectUndo(sumoGO, "Generate SUMO Simulation");
                            var engine = sumoGO.AddComponent<SimulationEngine>();
                            engine.network = sumoNetwork;

                            Debug.Log($"[OSM+SUMO] ✅ SimulationEngine ready — source: {sourceDesc}. Press Play to run traffic.");
                        }
                    }
                }

                if (SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.LookAt(Vector3.zero, Quaternion.Euler(60, 0, 0), 500f * _scale);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void ClearGenerated()
        {
            if (_generatedRoot != null) Undo.DestroyObjectImmediate(_generatedRoot);
            if (GameObject.Find("OSM_Map")             is GameObject e)    Undo.DestroyObjectImmediate(e);
            if (GameObject.Find("OSM_AreaPanel")       is GameObject ui)   Undo.DestroyObjectImmediate(ui);
            if (GameObject.Find("OSMAreaRegistry")     is GameObject reg)  Undo.DestroyObjectImmediate(reg);
            if (GameObject.Find("OSM_Traffic")         is GameObject tr)   Undo.DestroyObjectImmediate(tr);
            if (GameObject.Find("SUMO_Simulation")     is GameObject sumo) Undo.DestroyObjectImmediate(sumo);
            if (GameObject.Find("SUMO_Native_Preview") is GameObject prev) Undo.DestroyObjectImmediate(prev);
        }

        /// <summary>
        /// Creates a flat ground quad sized to the OSM bounding box so the map
        /// rests on a clearly visible coloured surface during Play mode.
        /// </summary>
        private void CreateGroundPlane(Transform parent, OSMMapData mapData, float scale)
        {
            // Estimate world-space extents from the OSM bounds
            double latSpan = (mapData.Bounds.MaxLat - mapData.Bounds.MinLat);
            double lonSpan = (mapData.Bounds.MaxLon - mapData.Bounds.MinLon);
            const double MetersPerDeg = 111320.0;
            float width  = (float)(lonSpan * MetersPerDeg * scale) * 3.5f;
            float depth  = (float)(latSpan * MetersPerDeg * scale) * 3.5f;
            // Ensure a sensible minimum
            width = Mathf.Max(width, 800f * scale);
            depth = Mathf.Max(depth, 800f * scale);

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "GroundPlane";
            go.transform.SetParent(parent, false);
            go.transform.localPosition    = new Vector3(0f, -0.05f, 0f);
            go.transform.localRotation    = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale       = new Vector3(width, depth, 1f);

            // Remove collider — it's purely cosmetic
            Object.DestroyImmediate(go.GetComponent<Collider>());

            // Apply ground material
            var mat = CreateMaterial("OSM_Ground", _groundColor);
            // Make it slightly rougher / unlit so it doesn't wash out under directional light
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic",   0f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            Undo.RegisterCreatedObjectUndo(go, "Generate Ground Plane");
        }

        private Material CreateMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool isUrp = shader != null;
            if (!isUrp) shader = Shader.Find("Standard");

            Material mat = new Material(shader) { name = name };
            if (isUrp) mat.SetColor("_BaseColor", color); else mat.color = color;

            string matsDir = "Assets/OSMImporter/Materials";
            if (!AssetDatabase.IsValidFolder(matsDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/OSMImporter"))
                    AssetDatabase.CreateFolder("Assets", "OSMImporter");
                AssetDatabase.CreateFolder("Assets/OSMImporter", "Materials");
            }

            string path = $"{matsDir}/{name}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                if (isUrp) existing.SetColor("_BaseColor", color); else existing.color = color;
                return existing;
            }
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// Merge Microsoft Building Footprints vào OSMMapData.
        /// Skip footprint trùng với OSM building có sẵn (de-duplicate).
        /// </summary>
        private void MergeMSBuildings(OSMMapData mapData, List<BuildingFootprint> msBuildings)
        {
            // Tính centroid của tất cả OSM buildings hiện có để de-duplicate
            var existingCentroids = new List<Vector2>();
            foreach (var way in mapData.GetBuildings())
            {
                double cx = 0, cy = 0;
                int count = 0;
                foreach (var nodeId in way.NodeRefs)
                {
                    if (mapData.Nodes.TryGetValue(nodeId, out var n))
                    {
                        cx += n.Latitude;
                        cy += n.Longitude;
                        count++;
                    }
                }
                if (count > 0)
                    existingCentroids.Add(new Vector2((float)(cx / count), (float)(cy / count)));
            }

            long nextId = 9_000_000_000L; // ID range cho MS buildings (tránh trùng OSM)
            int merged = 0;

            foreach (var fp in msBuildings)
            {
                if (fp.Coordinates == null || fp.Coordinates.Count < 3) continue;

                // Tính centroid của MS footprint
                double fpCx = 0, fpCy = 0;
                foreach (var coord in fp.Coordinates) { fpCx += coord[0]; fpCy += coord[1]; }
                fpCx /= fp.Coordinates.Count;
                fpCy /= fp.Coordinates.Count;

                // De-duplicate: skip nếu centroid quá gần 1 OSM building (~15m)
                bool duplicate = false;
                float threshold = 0.00015f; // ~15m tại Hà Nội
                foreach (var ec in existingCentroids)
                {
                    if (Mathf.Abs(ec.x - (float)fpCx) < threshold && Mathf.Abs(ec.y - (float)fpCy) < threshold)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;

                // Tạo nodes cho footprint
                var way = new Data.OSMWay { Id = nextId++ };
                way.Tags["building"] = "yes";
                way.Tags["source"] = "Microsoft";

                // Ước lượng chiều cao từ diện tích nếu MS không có height
                if (fp.Height > 0)
                    way.Tags["building:levels"] = Mathf.Max(1, Mathf.RoundToInt(fp.Height / 3f)).ToString();

                foreach (var coord in fp.Coordinates)
                {
                    long nodeId = nextId++;
                    var node = new Data.OSMNode
                    {
                        Id = nodeId,
                        Latitude = coord[0],
                        Longitude = coord[1]
                    };
                    mapData.Nodes[nodeId] = node;
                    way.NodeRefs.Add(nodeId);
                }

                // Close polygon
                if (way.NodeRefs.Count > 0 && way.NodeRefs[0] != way.NodeRefs[way.NodeRefs.Count - 1])
                    way.NodeRefs.Add(way.NodeRefs[0]);

                mapData.Ways.Add(way);
                merged++;
            }

            Debug.Log($"[MS Buildings] Merged {merged} new buildings (skipped {msBuildings.Count - merged} duplicates)");
        }
    }
}
