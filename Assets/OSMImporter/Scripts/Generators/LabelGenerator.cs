using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

namespace OSMImporter.Generators
{
    /// <summary>
    /// Generates:
    ///   - Street name labels: flat on road, rotated along direction
    ///   - Area labels: floating billboard that maintains constant screen size (Google Maps style)
    ///   - Place node labels: billboard
    ///
    /// Also registers all named areas into OSMAreaRegistry for the UI panel.
    /// </summary>
    public static class LabelGenerator
    {
        public static void Generate(
            OSMMapData    mapData,
            Transform     parent,
            LabelSettings settings,
            float         scale = 1f)
        {
            double originLat = mapData.Bounds.CenterLat;
            double originLon = mapData.Bounds.CenterLon;

            // Ensure registry exists in scene
            OSMAreaRegistry registry = GetOrCreateRegistry(parent);

            foreach (var way in mapData.Ways)
            {
                if (!way.Tags.TryGetValue("name", out string name)) continue;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pts = BuildPositions(way, mapData, originLat, originLon, scale);
                if (pts.Count < 2) continue;

                bool isRoad = way.IsHighway;
                bool isArea = way.IsClosed && !isRoad;

                if (isRoad && settings.ShowStreetNames)
                    PlaceRoadLabel(name, pts, parent, settings);

                else if (isArea && settings.ShowAreaNames)
                {
                    string areaType = GetAreaType(way);
                    Vector3 centroid = ComputeCentroid(pts);
                    PlaceAreaLabel(name, centroid, parent, settings, areaType);
                    registry?.Register(name, centroid, areaType);
                }
            }

            if (settings.ShowPlaceNames)
            {
                foreach (var node in mapData.Nodes.Values)
                {
                    if (!node.Tags.TryGetValue("name", out string name)) continue;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    Vector3 pos = MercatorProjection.LatLonToUnityCorrected(
                        node.Latitude, node.Longitude, originLat, originLon, scale);

                    CreateFloatingLabel(node.Tags.TryGetValue("place", out string pt) ? pt : "",
                        name, pos + Vector3.up * settings.PlaceYOffset,
                        parent, settings.PlaceLabelColor, settings.PlaceFontSize,
                        settings.PlaceLabelScale);
                }
            }
        }

        // ── Road label ───────────────────────────────────────────────────────

        private static void PlaceRoadLabel(string name, List<Vector3> pts,
            Transform parent, LabelSettings s)
        {
            float total = 0f;
            for (int i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i], pts[i - 1]);

            float     target   = total * 0.5f, acc = 0f;
            Vector3   labelPos = pts[pts.Count / 2];
            Quaternion rot     = Quaternion.Euler(90f, 0f, 0f);

            for (int i = 1; i < pts.Count; i++)
            {
                float seg = Vector3.Distance(pts[i], pts[i - 1]);
                if (acc + seg >= target)
                {
                    float t = (target - acc) / seg;
                    labelPos = Vector3.Lerp(pts[i - 1], pts[i], t);
                    Vector3 dir  = (pts[i] - pts[i - 1]).normalized;
                    float   ang  = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    if (ang > 90f || ang < -90f) ang += 180f;
                    rot = Quaternion.Euler(90f, ang, 0f);
                    break;
                }
                acc += seg;
            }

            // Street labels are flat (no billboard)
            CreateFlatLabel(name, labelPos + Vector3.up * s.StreetYOffset,
                rot, parent, s.StreetLabelColor, s.StreetFontSize);
        }

        // ── Area label (Google Maps floating style) ───────────────────────────

        private static void PlaceAreaLabel(string name, Vector3 centroid,
            Transform parent, LabelSettings s, string areaType)
        {
            // Scale font proportionally to settings
            float fontSize = s.AreaFontSize;

            CreateFloatingLabel(areaType, name,
                centroid + Vector3.up * s.AreaYOffset,
                parent, s.AreaLabelColor, fontSize, s.AreaLabelScale);
        }

        // ── Label factories ───────────────────────────────────────────────────

        /// <summary>
        /// Creates a flat text object (for road labels).
        /// </summary>
        private static void CreateFlatLabel(string text, Vector3 pos, Quaternion rot,
            Transform parent, Color color, float fontSize)
        {
            GameObject obj = CreateTextMeshObject(text, pos, rot, parent, color, fontSize);
            if (obj == null) return;
            // Flat labels don't need billboard component
        }

        /// <summary>
        /// Creates a floating billboard label that optionally maintains constant screen size.
        /// </summary>
        private static void CreateFloatingLabel(string subText, string mainText,
            Vector3 pos, Transform parent, Color color, float fontSize, float screenScale)
        {
            // Outer object — holds the billboard rotator
            GameObject root = new GameObject($"Label_{mainText}");
            root.transform.SetParent(parent, false);
            root.transform.position = pos;

            // Text mesh child
            GameObject textObj = CreateTextMeshObject(mainText, Vector3.zero,
                Quaternion.identity, root.transform, color, fontSize);

            // Billboard component on root
            var bb = root.AddComponent<OSMLabelBillboard>();
            bb.ScreenSizeScale = screenScale;

            // Optional sub-label (area type, smaller, below)
            if (!string.IsNullOrEmpty(subText))
            {
                Color subCol = new Color(color.r * 0.8f, color.g * 0.85f, color.b + 0.1f, color.a * 0.75f);
                CreateTextMeshObject(subText, Vector3.down * (fontSize * 0.012f),
                    Quaternion.identity, root.transform, subCol, fontSize * 0.65f);
            }
        }

        private static GameObject CreateTextMeshObject(string text, Vector3 localPos,
            Quaternion localRot, Transform parent, Color color, float fontSize)
        {
            GameObject obj = new GameObject(text);
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPos;
            obj.transform.localRotation = localRot;

            TextMesh tm      = obj.AddComponent<TextMesh>();
            tm.text          = text;
            tm.fontSize      = Mathf.Max(1, Mathf.RoundToInt(fontSize * 3));  // was ×10 — kept smaller
            tm.characterSize = 0.008f;    // was 0.1f — 12× smaller so base world size ≈ fontSize*0.024 units
            tm.color         = color;
            tm.anchor        = TextAnchor.MiddleCenter;
            tm.alignment     = TextAlignment.Center;
            tm.fontStyle     = FontStyle.Bold;
            tm.GetComponent<MeshRenderer>().sortingOrder = 20;
            return obj;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<Vector3> BuildPositions(OSMWay way, OSMMapData map,
            double oLat, double oLon, float scale)
        {
            var pts = new List<Vector3>();
            foreach (long id in way.NodeRefs)
                if (map.Nodes.TryGetValue(id, out OSMNode n))
                    pts.Add(MercatorProjection.LatLonToUnityCorrected(
                        n.Latitude, n.Longitude, oLat, oLon, scale));
            return pts;
        }

        private static Vector3 ComputeCentroid(List<Vector3> pts)
        {
            Vector3 c    = Vector3.zero;
            int     cnt  = (pts.Count > 1 && pts[pts.Count - 1] == pts[0])
                               ? pts.Count - 1 : pts.Count;
            for (int i = 0; i < cnt; i++) c += pts[i];
            return c / cnt;
        }

        private static string GetAreaType(OSMWay way)
        {
            string[] keys = { "landuse", "leisure", "amenity", "natural", "building" };
            foreach (string k in keys)
                if (way.Tags.TryGetValue(k, out string v)) return v;
            return "";
        }

        private static OSMAreaRegistry GetOrCreateRegistry(Transform sceneParent)
        {
            var reg = Object.FindFirstObjectByType<OSMAreaRegistry>();
            if (reg != null) { reg.Clear(); return reg; }

            GameObject go = new GameObject("OSMAreaRegistry");
            // Don't parent registry to the map root so it persists after Clear
            return go.AddComponent<OSMAreaRegistry>();
        }
    }

    // ── Enums & Settings ─────────────────────────────────────────────────────

    public enum LabelStyle { FlatTopDown, Billboard }

    [System.Serializable]
    public class LabelSettings
    {
        // ── Street ────────────────────────────────────────────────────────────
        public bool   ShowStreetNames  = true;
        public Color  StreetLabelColor = new Color(1f, 1f, 1f, 1f);
        public float  StreetFontSize   = 8f;
        public float  StreetYOffset    = 1f;

        // ── Area (floating billboard) ─────────────────────────────────────────
        public bool   ShowAreaNames    = true;
        public Color  AreaLabelColor   = new Color(1f, 0.95f, 0.6f, 1f);
        public float  AreaFontSize     = 10f;
        public float  AreaYOffset      = 20f;   // float high above buildings
        public float  AreaLabelScale   = 80f;   // desired screen height in pixels

        // ── Place nodes ───────────────────────────────────────────────────────
        public bool   ShowPlaceNames   = false;
        public Color  PlaceLabelColor  = new Color(0.7f, 0.9f, 1f, 1f);
        public float  PlaceFontSize    = 7f;
        public float  PlaceYOffset     = 5f;
        public float  PlaceLabelScale  = 50f;
    }

    // ── Billboard component ───────────────────────────────────────────────────

    /// <summary>
    /// Attaches to the root of each floating label.
    /// Every frame: rotate to face active camera and scale to keep constant screen size.
    /// </summary>
    public class OSMLabelBillboard : MonoBehaviour
    {
        /// <summary>Target apparent height in screen pixels. 0 = no scaling.</summary>
        public float ScreenSizeScale = 80f;
        private Vector3 _originalScale;

        private void Start()
        {
            _originalScale = transform.localScale;
        }

        private void LateUpdate()
        {
            Camera cam = GetActiveCamera();
            if (cam == null) return;

            // ── Billboard rotation ────────────────────────────────────────────
            Vector3 camToLabel = transform.position - cam.transform.position;
            if (camToLabel.sqrMagnitude < 0.001f) return;

            transform.rotation = Quaternion.LookRotation(camToLabel, Vector3.up);

            // ── Constant screen-size scaling ──────────────────────────────────
            if (ScreenSizeScale > 0f)
            {
                float dist  = camToLabel.magnitude;
                // fov scaling: how many world units per screen pixel at this distance
                float unitsPerPixel = (2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad))
                                      / cam.pixelHeight;
                float targetWorldSize = ScreenSizeScale * unitsPerPixel;

                // Keep aspect but set to desired world height
                float baseSize = _originalScale.y > 0f ? _originalScale.y : 1f;
                float s = targetWorldSize / baseSize;
                transform.localScale = _originalScale * Mathf.Clamp(s, 0.1f, 50f);
            }
        }

        private static Camera GetActiveCamera()
        {
#if UNITY_EDITOR
            if (UnityEditor.SceneView.lastActiveSceneView?.camera is Camera sv)
                return sv;
#endif
            return Camera.main;
        }
    }
}
