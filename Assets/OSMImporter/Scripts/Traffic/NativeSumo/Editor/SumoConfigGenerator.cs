using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEngine;
using OSMImporter.Traffic.Sumo;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic.NativeSumo.EditorScript
{
    /// <summary>
    /// Editor tool tạo SUMO route file (.rou.xml) + config file (.sumocfg)
    /// từ .net.xml hiện có.
    /// 
    /// Sinh vehicle demand tự động (random OD pairs) phù hợp cho testing.
    /// Truy cập: Tools → OSM Traffic → SUMO Config Generator
    /// </summary>
    public class SumoConfigGenerator : EditorWindow
    {
        private string _netXmlPath = "Assets/OSMImporter/SampleData/hanoi.net.xml";
        private string _outputDir = "Assets/OSMImporter/SampleData";
        private int _vehicleCount = 200;
        private float _departInterval = 1.0f;
        private float _simDuration = 3600f;

        // Vehicle type distribution
        private float _carRatio = 0.55f;
        private float _motoRatio = 0.30f;
        private float _busRatio = 0.15f;

        // Center for coordinate mapping
        private double _centerLat = 21.0330;
        private double _centerLon = 105.8500;

        [MenuItem("Tools/OSM Traffic/SUMO Config Generator")]
        public static void ShowWindow()
        {
            GetWindow<SumoConfigGenerator>("SUMO Config Generator");
        }

        private void OnGUI()
        {
            GUILayout.Label("SUMO Route & Config Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // File paths
            GUILayout.BeginHorizontal();
            _netXmlPath = EditorGUILayout.TextField("File .net.xml:", _netXmlPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFilePanel("Select SUMO Network", Application.dataPath, "xml");
                if (!string.IsNullOrEmpty(selected))
                    _netXmlPath = selected;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _outputDir = EditorGUILayout.TextField("Output Dir:", _outputDir);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFolderPanel("Output Directory", _outputDir, "");
                if (!string.IsNullOrEmpty(selected))
                    _outputDir = selected;
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space();
            GUILayout.Label("Simulation Parameters", EditorStyles.boldLabel);

            _vehicleCount = EditorGUILayout.IntSlider("Vehicle Count", _vehicleCount, 10, 2000);
            _departInterval = EditorGUILayout.Slider("Depart Interval (s)", _departInterval, 0.1f, 10f);
            _simDuration = EditorGUILayout.FloatField("Sim Duration (s)", _simDuration);

            EditorGUILayout.Space();
            GUILayout.Label("Vehicle Type Distribution", EditorStyles.boldLabel);

            _carRatio = EditorGUILayout.Slider("Car %", _carRatio, 0f, 1f);
            _motoRatio = EditorGUILayout.Slider("Motorbike %", _motoRatio, 0f, 1f);
            _busRatio = EditorGUILayout.Slider("Bus %", _busRatio, 0f, 1f);

            // Normalize
            float total = _carRatio + _motoRatio + _busRatio;
            if (total > 0.001f)
            {
                EditorGUILayout.HelpBox(
                    $"Normalized: Car {_carRatio/total:P0} | Moto {_motoRatio/total:P0} | Bus {_busRatio/total:P0}",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Coordinate Mapping", EditorStyles.boldLabel);

            _centerLat = EditorGUILayout.DoubleField("Center Lat", _centerLat);
            _centerLon = EditorGUILayout.DoubleField("Center Lon", _centerLon);

            EditorGUILayout.Space();

            if (GUILayout.Button("Generate .rou.xml + .sumocfg", GUILayout.Height(30)))
            {
                Generate();
            }
        }

        private void Generate()
        {
            if (!File.Exists(_netXmlPath))
            {
                Debug.LogError($"[SumoConfigGen] File không tồn tại: {_netXmlPath}");
                return;
            }

            // Parse edge IDs từ .net.xml
            var edgeIds = ParseEdgeIds(_netXmlPath);
            if (edgeIds.Count < 2)
            {
                Debug.LogError("[SumoConfigGen] Cần ít nhất 2 edges để tạo routes!");
                return;
            }

            string netFileName = Path.GetFileName(_netXmlPath);
            string baseName = Path.GetFileNameWithoutExtension(_netXmlPath).Replace(".net", "");

            // Generate .rou.xml
            string rouPath = Path.Combine(_outputDir, $"{baseName}.rou.xml");
            GenerateRouteFile(rouPath, edgeIds);

            // Generate .sumocfg
            string cfgPath = Path.Combine(_outputDir, $"{baseName}.sumocfg");
            GenerateConfigFile(cfgPath, netFileName, $"{baseName}.rou.xml");

            Debug.Log($"[SumoConfigGen] Generated:\n  {rouPath}\n  {cfgPath}");
            AssetDatabase.Refresh();
        }

        /// <summary>Parse edge IDs từ .net.xml (bỏ internal)</summary>
        private List<string> ParseEdgeIds(string netPath)
        {
            var result = new List<string>();
            XmlDocument doc = new XmlDocument();
            doc.Load(netPath);

            XmlNodeList edges = doc.SelectNodes("//edge");
            foreach (XmlNode edge in edges)
            {
                // Bỏ internal edges
                if (edge.Attributes["function"]?.Value == "internal") continue;
                string id = edge.Attributes["id"]?.Value;
                if (!string.IsNullOrEmpty(id) && !id.StartsWith(":"))
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Sinh file .rou.xml với vehicle types + random routes</summary>
        private void GenerateRouteFile(string path, List<string> edgeIds)
        {
            float total = _carRatio + _motoRatio + _busRatio;
            if (total < 0.001f) total = 1f;
            float carNorm = _carRatio / total;
            float motoNorm = _motoRatio / total;

            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                writer.WriteLine("<routes xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
                writer.WriteLine();

                // Vehicle type definitions
                writer.WriteLine("    <!-- Vehicle Types -->");
                writer.WriteLine("    <vType id=\"car\" accel=\"2.6\" decel=\"4.5\" sigma=\"0.5\" " +
                    "length=\"4.5\" maxSpeed=\"25\" guiShape=\"passenger\"/>");
                writer.WriteLine("    <vType id=\"motorbike\" accel=\"3.5\" decel=\"5.0\" sigma=\"0.6\" " +
                    "length=\"2.0\" maxSpeed=\"20\" guiShape=\"motorcycle\" width=\"0.8\"/>");
                writer.WriteLine("    <vType id=\"bus\" accel=\"1.5\" decel=\"3.5\" sigma=\"0.3\" " +
                    "length=\"12.0\" maxSpeed=\"15\" guiShape=\"bus\" width=\"2.5\"/>");
                writer.WriteLine();

                // Vehicles với random routes
                writer.WriteLine("    <!-- Vehicles -->");
                float departTime = 0f;

                for (int i = 0; i < _vehicleCount; i++)
                {
                    // Chọn type theo distribution
                    float r = Random.value;
                    string vType;
                    if (r < carNorm) vType = "car";
                    else if (r < carNorm + motoNorm) vType = "motorbike";
                    else vType = "bus";

                    // Random origin-destination edges
                    string fromEdge = edgeIds[Random.Range(0, edgeIds.Count)];
                    string toEdge = edgeIds[Random.Range(0, edgeIds.Count)];
                    if (fromEdge == toEdge) continue;

                    writer.WriteLine($"    <trip id=\"v_{i}\" type=\"{vType}\" " +
                        $"depart=\"{departTime.ToString("F1", CultureInfo.InvariantCulture)}\" " +
                        $"from=\"{fromEdge}\" to=\"{toEdge}\"/>");

                    departTime += _departInterval;
                    if (departTime > _simDuration) break;
                }

                writer.WriteLine();
                writer.WriteLine("</routes>");
            }

            Debug.Log($"[SumoConfigGen] Route file: {path}");
        }

        /// <summary>Sinh file .sumocfg</summary>
        private void GenerateConfigFile(string cfgPath, string netFileName, string rouFileName)
        {
            using (var writer = new StreamWriter(cfgPath))
            {
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                writer.WriteLine("<configuration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
                writer.WriteLine();
                writer.WriteLine("    <input>");
                writer.WriteLine($"        <net-file value=\"{netFileName}\"/>");
                writer.WriteLine($"        <route-files value=\"{rouFileName}\"/>");
                writer.WriteLine("    </input>");
                writer.WriteLine();
                writer.WriteLine("    <time>");
                writer.WriteLine($"        <begin value=\"0\"/>");
                writer.WriteLine($"        <end value=\"{_simDuration.ToString("F0", CultureInfo.InvariantCulture)}\"/>");
                writer.WriteLine("    </time>");
                writer.WriteLine();
                writer.WriteLine("    <processing>");
                writer.WriteLine("        <step-length value=\"0.1\"/>");
                writer.WriteLine("        <lateral-resolution value=\"0.8\"/>");
                writer.WriteLine("    </processing>");
                writer.WriteLine();
                writer.WriteLine("    <report>");
                writer.WriteLine("        <no-step-log value=\"true\"/>");
                writer.WriteLine("    </report>");
                writer.WriteLine();
                writer.WriteLine("</configuration>");
            }

            Debug.Log($"[SumoConfigGen] Config file: {cfgPath}");
        }
    }
}
