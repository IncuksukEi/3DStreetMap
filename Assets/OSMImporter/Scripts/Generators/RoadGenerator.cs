using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

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
                GameObject roadObj = CreateRoadMesh(way.Id, positions, (width * widthMultiplier) / 2f, material);
                roadObj.transform.SetParent(parent, false);
                roadObj.name = $"Road_{way.Id}_{way.HighwayType}";
                roads.Add(roadObj);
            }
            return roads;
        }

        private static GameObject CreateRoadMesh(long wayId, List<Vector3> points, float halfWidth, Material material)
        {
            GameObject obj = new GameObject();
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;

            Mesh mesh = new Mesh { name = $"RoadMesh_{wayId}" };
            int pointCount = points.Count;
            var vertices = new Vector3[pointCount * 2];
            var uvs = new Vector2[pointCount * 2];
            var triangles = new int[(pointCount - 1) * 6];

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
                vertices[i * 2] = points[i] - right * halfWidth;
                vertices[i * 2 + 1] = points[i] + right * halfWidth;

                float v = (totalLength > 0) ? lengths[i] / totalLength : 0;
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int idx = i * 6; int vi = i * 2;
                triangles[idx] = vi; triangles[idx + 1] = vi + 2; triangles[idx + 2] = vi + 1;
                triangles[idx + 3] = vi + 1; triangles[idx + 4] = vi + 2; triangles[idx + 5] = vi + 3;
            }

            mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            obj.transform.position = new Vector3(0, 0.02f, 0);
            return obj;
        }
    }
}
