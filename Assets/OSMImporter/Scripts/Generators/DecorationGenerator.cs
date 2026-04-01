using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

namespace OSMImporter.Generators
{
    public static class DecorationGenerator
    {
        public static List<GameObject> Generate(OSMMapData mapData, Transform parent, float scale = 1f)
        {
            var decos = new List<GameObject>();
            double originLat = mapData.Bounds.CenterLat, originLon = mapData.Bounds.CenterLon;

            // Use URP Lit if available, fallback to Standard
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (sh == null)
            {
                Debug.LogWarning("[DecorationGenerator] No shader found. Skipping decorations.");
                return decos;
            }

            Material trafficMat = new Material(sh);
            SetColor(trafficMat, new Color(0.25f, 0.25f, 0.25f));

            Material redMat = new Material(sh);
            SetColor(redMat, Color.red);
            redMat.EnableKeyword("_EMISSION");
            redMat.SetColor("_EmissionColor", Color.red * 2f);

            Material greenMat = new Material(sh);
            SetColor(greenMat, Color.green);
            greenMat.EnableKeyword("_EMISSION");
            greenMat.SetColor("_EmissionColor", Color.green * 2f);

            var decorations = mapData.GetDecorations();
            Debug.Log($"[DecorationGenerator] Found {decorations.Count} decoration nodes (signals/stops).");

            foreach (var node in decorations)
            {
                Vector3 pos = MercatorProjection.LatLonToUnityCorrected(node.Latitude, node.Longitude, originLat, originLon, scale);

                // Traffic signals đã được TrafficLightManager xử lý — chỉ tạo biển Stop
                if (node.IsStopSign)
                {
                    GameObject sign = CreateStopSign(trafficMat, redMat);
                    sign.transform.SetParent(parent, false);
                    sign.transform.position = pos;
                    sign.name = $"StopSign_{node.Id}";
                    decos.Add(sign);
                }
            }

            Debug.Log($"[DecorationGenerator] Generated {decos.Count} decoration objects.");
            return decos;
        }

        private static void SetColor(Material mat, Color color)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
        }

        private static GameObject CreateTrafficSignal(Material poleMat, Material redMat, Material greenMat)
        {
            GameObject root = new GameObject("TrafficSignal");

            // Pole
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.transform.SetParent(root.transform, false);
            pole.transform.localScale = new Vector3(0.2f, 2f, 0.2f);
            pole.transform.localPosition = new Vector3(0, 2f, 0);
            pole.GetComponent<MeshRenderer>().sharedMaterial = poleMat;

            // Box
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.SetParent(root.transform, false);
            box.transform.localScale = new Vector3(0.6f, 1.2f, 0.6f);
            box.transform.localPosition = new Vector3(0, 4.2f, 0);
            box.GetComponent<MeshRenderer>().sharedMaterial = poleMat;

            // Red Light
            GameObject redL = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            redL.transform.SetParent(root.transform, false);
            redL.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            redL.transform.localPosition = new Vector3(0, 4.55f, -0.31f);
            redL.GetComponent<MeshRenderer>().sharedMaterial = redMat;

            // Green Light
            GameObject greenL = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            greenL.transform.SetParent(root.transform, false);
            greenL.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            greenL.transform.localPosition = new Vector3(0, 3.9f, -0.31f);
            greenL.GetComponent<MeshRenderer>().sharedMaterial = greenMat;

            Object.DestroyImmediate(pole.GetComponent<Collider>());
            Object.DestroyImmediate(box.GetComponent<Collider>());
            Object.DestroyImmediate(redL.GetComponent<Collider>());
            Object.DestroyImmediate(greenL.GetComponent<Collider>());

            return root;
        }

        private static GameObject CreateStopSign(Material poleMat, Material signMat)
        {
            GameObject root = new GameObject("StopSign");

            // Pole
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.transform.SetParent(root.transform, false);
            pole.transform.localScale = new Vector3(0.15f, 1.5f, 0.15f);
            pole.transform.localPosition = new Vector3(0, 1.5f, 0);
            pole.GetComponent<MeshRenderer>().sharedMaterial = poleMat;

            // Sign (octagonal approximated with a sphere)
            GameObject sign = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sign.transform.SetParent(root.transform, false);
            sign.transform.localScale = new Vector3(1f, 1f, 0.1f);
            sign.transform.localPosition = new Vector3(0, 3.2f, 0);
            sign.GetComponent<MeshRenderer>().sharedMaterial = signMat;

            Object.DestroyImmediate(pole.GetComponent<Collider>());
            Object.DestroyImmediate(sign.GetComponent<Collider>());

            return root;
        }
    }
}
