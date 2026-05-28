using UnityEditor;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Traffic.Sumo;

namespace OSMImporter.Traffic.NativeSumo.EditorScript
{
    /// <summary>
    /// Công cụ Editor cho phép test thử chức năng đọc mạng lưới Native SUMO trực tiếp từ giao diện Unity
    /// Truy cập qua thanh menu: Tools -> OSM Traffic -> Native SUMO Importer
    /// </summary>
    public class NativeSumoImporterWindow : EditorWindow
    {
        private string filePath = "Assets/OSMImporter/SampleData/hanoi.net.xml";
        private SNetwork loadedNetwork;

        // Coordinate mapping params
        private double centerLat = 21.0330;
        private double centerLon = 105.8500;
        private float mapScale = 1f;
        private bool alignToOSM = true;

        [MenuItem("Tools/OSM Traffic/Native SUMO Importer")]
        public static void ShowWindow()
        {
            GetWindow<NativeSumoImporterWindow>("SUMO Importer Test");
        }

        private void OnGUI()
        {
            GUILayout.Label("Kiểm thử Tích hợp Native SUMO", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            GUILayout.BeginHorizontal();
            filePath = EditorGUILayout.TextField("File .net.xml path:", filePath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFilePanel("Select SUMO Network File", Application.dataPath, "net.xml");
                if (!string.IsNullOrEmpty(selected)) filePath = selected;
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space();
            GUILayout.Label("Coordinate Alignment", EditorStyles.boldLabel);
            alignToOSM = EditorGUILayout.Toggle("Align to OSM Map", alignToOSM);
            if (alignToOSM)
            {
                centerLat = EditorGUILayout.DoubleField("Center Latitude", centerLat);
                centerLon = EditorGUILayout.DoubleField("Center Longitude", centerLon);
                mapScale = EditorGUILayout.FloatField("Map Scale", mapScale);
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("1. Đọc File & Xây dựng Graph (Vào RAM)"))
            {
                try 
                {
                    SumoToUnityMapper mapper = null;
                    if (alignToOSM)
                    {
                        mapper = new SumoToUnityMapper(filePath, centerLat, centerLon, mapScale);
                    }

                    loadedNetwork = NetParser.Load(filePath, mapper);
                    if (loadedNetwork != null)
                    {
                        int totalLanes = 0;
                        int lanesWithShape = 0;
                        foreach (var edge in loadedNetwork.Edges.Values)
                        {
                            foreach (var lane in edge.lanes)
                            {
                                totalLanes++;
                                if (lane.shape != null && lane.shape.Count >= 2) lanesWithShape++;
                            }
                        }

                        Debug.Log($"[SUMO Native] Parse thành công:" +
                            $"\n  • {loadedNetwork.Nodes.Count} Junctions" +
                            $"\n  • {loadedNetwork.Edges.Count} Edges" +
                            $"\n  • {totalLanes} Lanes ({lanesWithShape} có shape 3D)" +
                            (alignToOSM ? "\n  • Aligned to OSM map" : "\n  • Raw SUMO coordinates"));
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[SUMO Native] Lỗi đọc file (Kiểm tra lại đường dẫn): {ex.Message}");
                }
            }

            EditorGUILayout.Space();

            GUI.enabled = loadedNetwork != null;

            if (GUILayout.Button("2. Sinh Preview lên Scene (Hierarchy + LineRenderer)"))
            {
                GenerateFullPreview();
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("3. Gắn SimulationEngine vào Scene (chạy traffic)"))
            {
                AttachSimulationEngine();
            }

            GUI.enabled = true;
        }

        /// <summary>
        /// Gộp tạo Hierarchy + vẽ LineRenderer trong 1 bước
        /// </summary>
        private void GenerateFullPreview()
        {
            // Reset bản cũ
            GameObject oldRoot = GameObject.Find("SUMO_Native_Preview");
            if (oldRoot != null) DestroyImmediate(oldRoot);

            GameObject root = new GameObject("SUMO_Native_Preview");
            int laneCount = 0;

            foreach (var edgePair in loadedNetwork.Edges)
            {
                SEdge edge = edgePair.Value;
                GameObject edgeObj = new GameObject($"Edge_{edge.id}");
                edgeObj.transform.SetParent(root.transform);

                foreach (var lane in edge.lanes)
                {
                    GameObject laneObj = new GameObject($"Lane_{lane.id} | Idx:{lane.index} | Spd:{lane.maxSpeed:F1}");
                    laneObj.transform.SetParent(edgeObj.transform);

                    // Đặt vị trí tại điểm đầu tiên của shape
                    if (lane.shape != null && lane.shape.Count >= 2)
                    {
                        laneObj.transform.position = lane.shape[0];

                        // Vẽ đường bằng LineRenderer
                        LineRenderer lr = laneObj.AddComponent<LineRenderer>();
                        lr.positionCount = lane.shape.Count;
                        lr.SetPositions(lane.shape.ToArray());
                        lr.startWidth = 0.5f;
                        lr.endWidth = 0.5f;
                        lr.material = new Material(Shader.Find("Sprites/Default"));

                        // Màu theo index: lane 0 = xanh dương, lane 1+ = xanh lá
                        Color c = lane.index == 0 
                            ? new Color(0.2f, 0.6f, 1f, 0.8f) 
                            : new Color(0.3f, 0.9f, 0.4f, 0.7f);
                        lr.startColor = c;
                        lr.endColor = c;
                        lr.useWorldSpace = true;

                        laneCount++;
                    }
                }
            }

            Debug.Log($"[SUMO Native] Đã sinh Preview: {loadedNetwork.Edges.Count} Edges, {laneCount} Lanes có đường vẽ trên Scene.");
        }

        /// <summary>
        /// Tạo SimulationEngine GameObject và set network
        /// </summary>
        private void AttachSimulationEngine()
        {
            // Tìm hoặc tạo SimulationEngine
            var existing = FindFirstObjectByType<SimulationEngine>();
            if (existing != null)
            {
                existing.SetNetwork(loadedNetwork);
                Debug.Log("[SUMO Native] Updated existing SimulationEngine with new network.");
                return;
            }

            GameObject go = new GameObject("SUMO_SimulationEngine");
            var engine = go.AddComponent<SimulationEngine>();
            engine.SetNetwork(loadedNetwork);
            
            Debug.Log($"[SUMO Native] Created SimulationEngine with {loadedNetwork.Edges.Count} edges. " +
                "Press Play to start simulation.");
        }
    }
}
