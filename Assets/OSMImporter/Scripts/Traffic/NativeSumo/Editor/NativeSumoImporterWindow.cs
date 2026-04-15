using UnityEditor;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo;
using OSMImporter.Traffic.NativeSumo.Graph;

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

            if (GUILayout.Button("1. Đọc File & Xây dựng Graph (Vào RAM)"))
            {
                try 
                {
                    loadedNetwork = NetParser.Load(filePath);
                    if (loadedNetwork != null)
                    {
                        Debug.Log($"[SUMO Native] Parse thành công. Đã nạp vào RAM {loadedNetwork.Edges.Count} đoạn đường (Edges).");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[SUMO Native] Lỗi đọc file (Kiểm tra lại đường dẫn): {ex.Message}");
                }
            }

            EditorGUILayout.Space();

            GUI.enabled = loadedNetwork != null;
            if (GUILayout.Button("2. Sinh GameObjects để xem cấu trúc Data"))
            {
                GenerateDebugVisuals();
            }
            GUI.enabled = true;
        }

        private void GenerateDebugVisuals()
        {
            // Reset nếu đã có bản cũ
            GameObject oldRoot = GameObject.Find("SUMO_Native_Preview");
            if (oldRoot != null) DestroyImmediate(oldRoot);

            GameObject root = new GameObject("SUMO_Native_Preview");

            foreach (var edgePair in loadedNetwork.Edges)
            {
                SEdge edge = edgePair.Value;
                GameObject edgeObj = new GameObject($"Edge_{edge.id}");
                edgeObj.transform.SetParent(root.transform);

                foreach (var lane in edge.lanes)
                {
                    GameObject laneObj = new GameObject($"Lane_{lane.id} | Index:{lane.index} | Speed:{lane.maxSpeed}");
                    laneObj.transform.SetParent(edgeObj.transform);
                    
                    // TODO (Tương lai): Dùng component LineRenderer để vẽ đường nối dựa theo List<Vector3> shape của lane.
                }
            }
            
            Debug.Log("[SUMO Native] Đã sinh Hierarchy trên Scene để kiểm chứng dữ liệu Edges/Lanes.");
        }
    }
}
