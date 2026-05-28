using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

namespace OSMImporter.Generators
{
    public static class BuildingGenerator
    {
        public static List<GameObject> Generate(OSMMapData mapData, Transform parent, Material material, float scale = 1f, float minHeight = 6f, float maxHeight = 20f)
        {
            var buildings = new List<GameObject>();
            double originLat = mapData.Bounds.CenterLat, originLon = mapData.Bounds.CenterLon;

            foreach (var way in mapData.GetBuildings())
            {
                if (!way.IsClosed) continue;
                var footprint = new List<Vector3>();
                for (int i = 0; i < way.NodeRefs.Count - 1; i++)
                {
                    if (mapData.Nodes.TryGetValue(way.NodeRefs[i], out OSMNode node))
                        footprint.Add(MercatorProjection.LatLonToUnityCorrected(node.Latitude, node.Longitude, originLat, originLon, scale));
                }
                if (footprint.Count < 3) continue;

                float height = Random.Range(minHeight, maxHeight);
                if (way.Tags.TryGetValue("building:levels", out string levelsStr) && int.TryParse(levelsStr, out int levels) && levels > 0)
                    height = levels * 3f;

                GameObject buildingObj = CreateBuildingMesh(way.Id, footprint, height, material);
                buildingObj.transform.SetParent(parent, false);
                buildingObj.name = $"Building_{way.Id}";
                buildings.Add(buildingObj);
            }
            return buildings;
        }

        private static GameObject CreateBuildingMesh(long wayId, List<Vector3> footprint, float height, Material material)
        {
            GameObject obj = new GameObject();
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            MeshCollider mc = obj.AddComponent<MeshCollider>();
            mr.sharedMaterial = material;

            int n = footprint.Count;
            Mesh mesh = new Mesh { name = $"BuildingMesh_{wayId}" };
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            EnsureCCW(footprint);

            // WALLS
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector3 bl = footprint[i], br = footprint[next];
                Vector3 tl = bl + Vector3.up * height, tr = br + Vector3.up * height;
                float wallWidth = Vector3.Distance(bl, br);
                Vector2 uv0 = new Vector2(0, 0), uv1 = new Vector2(wallWidth / 5f, 0), uv2 = new Vector2(0, height / 3f), uv3 = new Vector2(wallWidth / 5f, height / 3f);

                int fIdx = vertices.Count;
                vertices.Add(bl); vertices.Add(br); vertices.Add(tl); vertices.Add(tr);
                uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv2); uvs.Add(uv3);
                triangles.Add(fIdx); triangles.Add(fIdx + 2); triangles.Add(fIdx + 1);
                triangles.Add(fIdx + 1); triangles.Add(fIdx + 2); triangles.Add(fIdx + 3);

                int bIdx = vertices.Count;
                vertices.Add(bl); vertices.Add(br); vertices.Add(tl); vertices.Add(tr);
                uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv2); uvs.Add(uv3);
                triangles.Add(bIdx); triangles.Add(bIdx + 1); triangles.Add(bIdx + 2);
                triangles.Add(bIdx + 1); triangles.Add(bIdx + 3); triangles.Add(bIdx + 2);
            }

            // ROOF
            int roofFrontIdx = vertices.Count;
            for (int i = 0; i < n; i++) { Vector3 v = footprint[i] + Vector3.up * height; vertices.Add(v); uvs.Add(new Vector2(v.x * 0.1f, v.z * 0.1f)); }
            int roofBackIdx = vertices.Count;
            for (int i = 0; i < n; i++) { Vector3 v = footprint[i] + Vector3.up * height; vertices.Add(v); uvs.Add(new Vector2(v.x * 0.1f, v.z * 0.1f)); }

            var polyTris = Triangulate(footprint);
            foreach (int idx in polyTris) triangles.Add(roofFrontIdx + idx);
            for (int i = 0; i < polyTris.Count; i += 3) { triangles.Add(roofBackIdx + polyTris[i + 2]); triangles.Add(roofBackIdx + polyTris[i + 1]); triangles.Add(roofBackIdx + polyTris[i]); }

            // FLOOR
            int floorIdx = vertices.Count;
            for (int i = 0; i < n; i++) { vertices.Add(footprint[i]); uvs.Add(new Vector2(footprint[i].x * 0.1f, footprint[i].z * 0.1f)); }
            for (int i = 0; i < polyTris.Count; i += 3) { triangles.Add(floorIdx + polyTris[i + 2]); triangles.Add(floorIdx + polyTris[i + 1]); triangles.Add(floorIdx + polyTris[i]); }

            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mf.sharedMesh = mc.sharedMesh = mesh;
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
