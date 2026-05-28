using System.Collections.Generic;
using UnityEngine;
using Unity.AI.Navigation;
using UnityEngine.AI;
using OSMImporter.Data;
using OSMImporter.Geo;
using OSMImporter.Traffic;

namespace OSMImporter.Generators
{
    public static class RoadGenerator
    {
        private static readonly Dictionary<string, float> DefaultRoadWidths = new Dictionary<string, float>
        {
            { "motorway",       12f }, { "trunk",          10f },
            { "primary",         8f }, { "secondary",       7f },
            { "tertiary",        6f }, { "residential",     5f },
            { "service",         3f }, { "footway",         2f },
            { "pedestrian",      3f }, { "path",            1.5f },
            { "cycleway",        2f }, { "living_street",   4f },
            { "unclassified",    5f },
        };

        public static List<GameObject> Generate(OSMMapData mapData, Transform parent, Material material, float scale = 1f, float widthMultiplier = 1f)
        {
            var roads = new List<GameObject>();
            var highways = mapData.GetHighways();
            double originLat = mapData.Bounds.CenterLat;
            double originLon = mapData.Bounds.CenterLon;

            foreach (var way in highways)
            {
                var positions = new List<Vector3>();
                foreach (long nodeRef in way.NodeRefs)
                {
                    if (mapData.Nodes.TryGetValue(nodeRef, out OSMNode node))
                        positions.Add(MercatorProjection.LatLonToUnityCorrected(node.Latitude, node.Longitude, originLat, originLon, scale));
                }
                if (positions.Count < 2) continue;

                float width = DefaultRoadWidths.TryGetValue(way.HighwayType, out float w) ? w : 4f;
                float baseW = width * widthMultiplier;
                // Vỉa hè được mở rộng thêm một lượng tỷ lệ với lòng đường (sidewalk on each side)
                float sidewalkW = baseW * RoadUtility.SidewalkRatio;
                float halfMeshWidth = (baseW + 2f * sidewalkW) / 2f;

                GameObject roadObj = CreateRoadMesh(way.Id, positions, halfMeshWidth, material);
                roadObj.transform.SetParent(parent, false);
                roadObj.name = $"Road_{way.Id}_{way.HighwayType}";
                roads.Add(roadObj);
                
                float totalW = baseW;
                // Vẽ lane markings cho đường đủ rộng
                if (totalW >= 4f)
                {
                    // Center divider line (vàng) — phân cách 2 chiều
                    Material centerMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    Color yellow = new Color(1f, 0.85f, 0f);
                    centerMat.color = yellow;
                    if (centerMat.HasProperty("_BaseColor")) centerMat.SetColor("_BaseColor", yellow);
                    
                    GameObject centerLine = CreateRoadMesh(way.Id + 1000000, positions, 0.12f, centerMat);
                    centerLine.transform.position = new Vector3(0, 0.06f, 0);
                    centerLine.transform.SetParent(roadObj.transform, false);
                    centerLine.name = $"CenterLine_{way.Id}";
                    
                    // Edge lines (trắng) — hai bên mép đường (là ranh giới giữa lòng đường và vỉa hè)
                    Material edgeMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    edgeMat.color = Color.white;
                    if (edgeMat.HasProperty("_BaseColor")) edgeMat.SetColor("_BaseColor", Color.white);
                    
                    float edgeOffset = totalW / 2f; // Ranh giới lòng đường - vỉa hè
                    
                    // Tạo offset positions cho mép trái và phải
                    var leftEdgePositions  = OffsetPolyline(positions, -edgeOffset);
                    var rightEdgePositions = OffsetPolyline(positions,  edgeOffset);
                    
                    GameObject leftEdge = CreateRoadMesh(way.Id + 2000000, leftEdgePositions, 0.08f, edgeMat);
                    leftEdge.transform.position = new Vector3(0, 0.06f, 0);
                    leftEdge.transform.SetParent(roadObj.transform, false);
                    leftEdge.name = $"EdgeLineL_{way.Id}";
                    
                    GameObject rightEdge = CreateRoadMesh(way.Id + 3000000, rightEdgePositions, 0.08f, edgeMat);
                    rightEdge.transform.position = new Vector3(0, 0.06f, 0);
                    rightEdge.transform.SetParent(roadObj.transform, false);
                    rightEdge.name = $"EdgeLineR_{way.Id}";
                }
            }

            // --- CODE VÁ NGÃ TƯ BẰNG CYLINDER ĐÃ BỊ XOÁ BỎ SAU FEEDBACK CỦA USER ---
            // Tránh tạo ra các hình tròn lồi lõm thiếu thẩm mỹ trên mặt đường.

            // --- TỰ ĐỘNG NƯỚNG (BAKE) NAVMESH BỀ MẶT ---
            GameObject navObj = new GameObject("Road_NavMeshSurface");
            navObj.transform.SetParent(parent, false);
            NavMeshSurface surface = navObj.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            // Build lưới điều hướng ngay sau khi sinh đường
            surface.BuildNavMesh();

            return roads;
        }

        /// <summary>Offset polyline sang trái (âm) hoặc phải (dương) theo perpendicular.</summary>
        private static List<Vector3> OffsetPolyline(List<Vector3> points, float offset)
        {
            var result = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 forward = (i == 0) ? (points[1] - points[0]).normalized :
                                  (i == points.Count - 1) ? (points[i] - points[i - 1]).normalized :
                                  ((points[i + 1] - points[i]).normalized + (points[i] - points[i - 1]).normalized).normalized;
                // Perpendicular trên mặt phẳng XZ
                Vector3 right = new Vector3(forward.z, 0, -forward.x).normalized;
                result.Add(points[i] + right * offset);
            }
            return result;
        }

        private static GameObject CreateRoadMesh(long wayId, List<Vector3> points, float halfWidth, Material material)
        {
            GameObject obj = new GameObject();
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.receiveShadows = false;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            
            MeshCollider mc = obj.AddComponent<MeshCollider>();

            Mesh mesh = new Mesh { name = $"RoadMesh_{wayId}" };
            int pointCount = points.Count;
            // 2 verts per point for front, 2 for back
            var vertices = new Vector3[pointCount * 4];
            var uvs = new Vector2[pointCount * 4];
            var triangles = new int[(pointCount - 1) * 12];

            float totalLength = 0f;
            var lengths = new float[pointCount];
            for (int i = 1; i < pointCount; i++)
            {
                totalLength += Vector3.Distance(points[i], points[i - 1]);
                lengths[i] = totalLength;
            }

            for (int i = 0; i < pointCount; i++)
            {
                Vector3 forward = (i == 0) ? (points[1] - points[0]).normalized :
                                  (i == pointCount - 1) ? (points[i] - points[i - 1]).normalized :
                                  (((points[i + 1] - points[i]).normalized + (points[i] - points[i - 1]).normalized).normalized);

                Vector3 right = new Vector3(forward.z, 0, -forward.x).normalized;

                Vector3 leftPos = points[i] - right * halfWidth;
                Vector3 rightPos = points[i] + right * halfWidth;
                float v = (totalLength > 0) ? lengths[i] / totalLength : 0;

                // Front face vertices
                vertices[i * 2] = leftPos;
                vertices[i * 2 + 1] = rightPos;
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);

                // Back face vertices
                int bIdx = pointCount * 2 + i * 2;
                vertices[bIdx] = leftPos;
                vertices[bIdx + 1] = rightPos;
                uvs[bIdx] = new Vector2(0f, v);
                uvs[bIdx + 1] = new Vector2(1f, v);
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int idx = i * 12;
                int vi = i * 2;
                // Front face
                triangles[idx]     = vi;     triangles[idx + 1] = vi + 2; triangles[idx + 2] = vi + 1;
                triangles[idx + 3] = vi + 1; triangles[idx + 4] = vi + 2; triangles[idx + 5] = vi + 3;

                // Back face (reversed winding, distinct vertices)
                int bvi = pointCount * 2 + vi;
                triangles[idx + 6] = bvi;     triangles[idx + 7] = bvi + 1; triangles[idx + 8]  = bvi + 2;
                triangles[idx + 9] = bvi + 1; triangles[idx + 10] = bvi + 3; triangles[idx + 11] = bvi + 2;
            }

            mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            obj.GetComponent<MeshCollider>().sharedMesh = mesh;
            obj.transform.position = new Vector3(0, 0.02f, 0);
            return obj;
        }
    }
}
