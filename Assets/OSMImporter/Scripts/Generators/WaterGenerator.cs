using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

namespace OSMImporter.Generators
{
    public static class WaterGenerator
    {
        public static List<GameObject> Generate(OSMMapData mapData, Transform parent, Material material, float scale = 1f)
        {
            var waterObjs = new List<GameObject>();
            double originLat = mapData.Bounds.CenterLat, originLon = mapData.Bounds.CenterLon;

            // 1. Generate Water Bodies (lakes, ponds — closed polygons)
            var waterBodies = mapData.GetWaterBodies();
            Debug.Log($"[WaterGenerator] Found {waterBodies.Count} water bodies.");
            foreach (var way in waterBodies)
            {
                if (!way.IsClosed) continue;
                var footprint = new List<Vector3>();
                for (int i = 0; i < way.NodeRefs.Count - 1; i++)
                {
                    if (mapData.Nodes.TryGetValue(way.NodeRefs[i], out OSMNode node))
                        footprint.Add(MercatorProjection.LatLonToUnityCorrected(node.Latitude, node.Longitude, originLat, originLon, scale));
                }
                if (footprint.Count < 3) continue;

                GameObject obj = CreateWaterPolygon(way.Id, footprint, material);
                obj.transform.SetParent(parent, false);
                obj.name = $"WaterBody_{way.Id}";
                waterObjs.Add(obj);
            }

            // 2. Generate Rivers (open lines)
            var rivers = mapData.GetRivers();
            Debug.Log($"[WaterGenerator] Found {rivers.Count} rivers/streams.");
            foreach (var way in rivers)
            {
                var positions = new List<Vector3>();
                foreach (long nodeRef in way.NodeRefs)
                {
                    if (mapData.Nodes.TryGetValue(nodeRef, out OSMNode node))
                        positions.Add(MercatorProjection.LatLonToUnityCorrected(node.Latitude, node.Longitude, originLat, originLon, scale));
                }
                if (positions.Count < 2) continue;

                float riverWidth = 6f;
                // Wider for actual rivers vs streams
                if (way.Tags.TryGetValue("waterway", out string wt))
                {
                    if (wt == "river") riverWidth = 12f;
                    else if (wt == "canal") riverWidth = 8f;
                }

                GameObject obj = CreateRiverMesh(way.Id, positions, riverWidth / 2f, material);
                obj.transform.SetParent(parent, false);
                obj.name = $"River_{way.Id}";
                waterObjs.Add(obj);
            }

            Debug.Log($"[WaterGenerator] Generated {waterObjs.Count} water objects total.");
            return waterObjs;
        }

        private static GameObject CreateWaterPolygon(long wayId, List<Vector3> footprint, Material material)
        {
            GameObject obj = new GameObject();
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Mesh mesh = new Mesh { name = $"WaterMesh_{wayId}" };
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            EnsureCCW(footprint);

            // Front face
            int frontIdx = vertices.Count;
            for (int i = 0; i < footprint.Count; i++) {
                vertices.Add(footprint[i]);
                uvs.Add(new Vector2(footprint[i].x * 0.05f, footprint[i].z * 0.05f));
            }
            var polyTris = Triangulate(footprint);
            foreach (int idx in polyTris) triangles.Add(frontIdx + idx);

            // Back face (flip winding)
            int backIdx = vertices.Count;
            for (int i = 0; i < footprint.Count; i++) {
                vertices.Add(footprint[i]);
                uvs.Add(new Vector2(footprint[i].x * 0.05f, footprint[i].z * 0.05f));
            }
            for (int i = 0; i < polyTris.Count; i += 3)
            {
                triangles.Add(backIdx + polyTris[i + 2]);
                triangles.Add(backIdx + polyTris[i + 1]);
                triangles.Add(backIdx + polyTris[i]);
            }

            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            mf.sharedMesh = mesh;
            obj.transform.position = new Vector3(0, 0.03f, 0);
            return obj;
        }

        private static GameObject CreateRiverMesh(long wayId, List<Vector3> points, float halfWidth, Material material)
        {
            GameObject obj = new GameObject();
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Mesh mesh = new Mesh { name = $"RiverMesh_{wayId}" };
            int pointCount = points.Count;
            var vertices = new Vector3[pointCount * 2];
            var uvs = new Vector2[pointCount * 2];
            var triangles = new int[(pointCount - 1) * 6];

            float totalLength = 0f;
            var lengths = new float[pointCount];
            for (int i = 1; i < pointCount; i++) {
                totalLength += Vector3.Distance(points[i], points[i - 1]);
                lengths[i] = totalLength;
            }

            for (int i = 0; i < pointCount; i++) {
                Vector3 forward;
                if (i == 0) forward = (points[1] - points[0]).normalized;
                else if (i == pointCount - 1) forward = (points[i] - points[i - 1]).normalized;
                else forward = ((points[i + 1] - points[i]).normalized + (points[i] - points[i - 1]).normalized).normalized;

                Vector3 right = new Vector3(forward.z, 0, -forward.x).normalized;
                vertices[i * 2] = points[i] - right * halfWidth;
                vertices[i * 2 + 1] = points[i] + right * halfWidth;

                float v = (totalLength > 0) ? lengths[i] / totalLength : 0;
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);
            }

            for (int i = 0; i < pointCount - 1; i++) {
                int idx = i * 6; int vi = i * 2;
                triangles[idx] = vi; triangles[idx + 1] = vi + 2; triangles[idx + 2] = vi + 1;
                triangles[idx + 3] = vi + 1; triangles[idx + 4] = vi + 2; triangles[idx + 5] = vi + 3;
            }

            mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            obj.transform.position = new Vector3(0, 0.03f, 0);
            return obj;
        }

        private static List<int> Triangulate(List<Vector3> polygon)
        {
            var indices = new List<int>();
            var remaining = new List<int>();
            for (int i = 0; i < polygon.Count; i++) remaining.Add(i);

            int safety = polygon.Count * 3;
            while (remaining.Count > 2 && safety > 0)
            {
                safety--; bool earFound = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int prev = (i - 1 + remaining.Count) % remaining.Count, next = (i + 1) % remaining.Count;
                    Vector3 a = polygon[remaining[prev]], b = polygon[remaining[i]], c = polygon[remaining[next]];

                    if ((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x) <= 0) continue;

                    bool containsPoint = false;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        if (j == prev || j == i || j == next) continue;
                        float d1 = Sign(polygon[remaining[j]], a, b), d2 = Sign(polygon[remaining[j]], b, c), d3 = Sign(polygon[remaining[j]], c, a);
                        if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0))) { containsPoint = true; break; }
                    }

                    if (!containsPoint)
                    {
                        indices.Add(remaining[prev]); indices.Add(remaining[i]); indices.Add(remaining[next]);
                        remaining.RemoveAt(i); earFound = true; break;
                    }
                }
                if (!earFound) break;
            }
            return indices;
        }

        private static float Sign(Vector3 p1, Vector3 p2, Vector3 p3) => (p1.x - p3.x) * (p2.z - p3.z) - (p2.x - p3.x) * (p1.z - p3.z);
        private static void EnsureCCW(List<Vector3> polygon)
        {
            float area = 0;
            for (int i = 0; i < polygon.Count; i++) area += polygon[i].x * polygon[(i + 1) % polygon.Count].z - polygon[(i + 1) % polygon.Count].x * polygon[i].z;
            if (area < 0) polygon.Reverse();
        }
    }
}
