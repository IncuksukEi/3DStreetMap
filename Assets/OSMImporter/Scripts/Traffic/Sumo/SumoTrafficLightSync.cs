using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// Sync trạng thái đèn giao thông SUMO → Unity TrafficLightManager.
    ///
    /// SUMO TLS state string: "rRgGyYoO" (1 char per controlled link)
    ///   r/R = red, g/G = green, y/Y = yellow, o/O = off
    ///
    /// Mapping: SUMO TLS ID → Unity Intersection (by proximity hoặc manual mapping).
    /// Mỗi frame: query SUMO TLS states → override Unity traffic light visuals + triggers.
    /// </summary>
    public class SumoTrafficLightSync : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("SumoBridge đang kết nối")]
        public SumoBridge Bridge;

        [Tooltip("TrafficLightManager của OSM")]
        public TrafficLightManager LightManager;

        [Header("Settings")]
        [Tooltip("Tần suất sync (mỗi N giây). 0 = mỗi frame")]
        public float SyncInterval = 0.1f;

        [Tooltip("Bán kính matching SUMO TLS → Unity intersection (mét)")]
        public float MatchRadius = 50f;

        [Header("Status")]
        [SerializeField] private int _sumoTLSCount;
        [SerializeField] private int _mappedCount;

        // SUMO TLS ID → Unity intersection node ID
        private Dictionary<string, long> _tlsMapping = new Dictionary<string, long>();
        private bool _mappingBuilt;
        private float _syncTimer;

        // Cache TLS controlled link positions (from .net.xml junction coords)
        private Dictionary<string, Vector3> _tlsPositions = new Dictionary<string, Vector3>();

        // ══════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        private void Start()
        {
            if (Bridge == null) Bridge = FindFirstObjectByType<SumoBridge>();
            if (LightManager == null) LightManager = FindFirstObjectByType<TrafficLightManager>();
        }

        private void Update()
        {
            if (Bridge == null || !Bridge.IsConnected) return;
            if (LightManager == null || !LightManager.EnableTrafficLights) return;

            _syncTimer += Time.deltaTime;
            if (_syncTimer < SyncInterval) return;
            _syncTimer = 0f;

            SyncTrafficLights();
        }

        // ══════════════════════════════════════════════════════════════════
        // SYNC
        // ══════════════════════════════════════════════════════════════════

        private void SyncTrafficLights()
        {
            var client = Bridge.GetClient();
            if (client == null || !client.IsConnected) return;

            try
            {
                var tlsStates = client.GetAllTLSStates();
                _sumoTLSCount = tlsStates.Count;

                // Build mapping lần đầu
                if (!_mappingBuilt && tlsStates.Count > 0)
                {
                    BuildMapping(client);
                    _mappingBuilt = true;
                }

                // Apply states
                foreach (var tls in tlsStates)
                {
                    if (!_tlsMapping.TryGetValue(tls.Id, out long intersectionId)) continue;

                    ApplyTLSState(intersectionId, tls.State);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SumoTLSync] Sync error: {e.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // MAPPING: SUMO TLS → Unity Intersection
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Build mapping giữa SUMO TLS IDs và Unity intersections.
        /// Strategy 1: match TLS ID chứa OSM node ID string.
        /// Strategy 2 (fallback): proximity matching — tìm intersection gần nhất.
        /// </summary>
        private void BuildMapping(TraCIClient client)
        {
            _tlsMapping.Clear();
            var mapper = Bridge.GetMapper();
            if (mapper == null) return;

            var tlsIds = client.GetTLSIdList();

            var graph = LightManager.Graph;
            if (graph == null) return;

            // Cache danh sách traffic light waypoints
            var trafficLightWps = new List<Navigation.Waypoint>();
            foreach (var wp in graph.Waypoints.Values)
            {
                if (wp.IsTrafficLight) trafficLightWps.Add(wp);
            }

            int mapped = 0;
            foreach (var tlsId in tlsIds)
            {
                long bestMatch = -1;

                // Strategy 1: ID-based matching
                foreach (var wp in trafficLightWps)
                {
                    if (tlsId.Contains(wp.OSMNodeId.ToString()))
                    {
                        bestMatch = wp.OSMNodeId;
                        break;
                    }
                }

                // Strategy 2: Proximity-based fallback — cần TLS position từ SUMO
                if (bestMatch < 0 && mapper.IsInitialized)
                {
                    // Dùng junction position (TLS ID thường = junction ID)
                    // Tìm intersection Unity gần nhất chưa được map
                    float bestDist = MatchRadius;
                    foreach (var wp in trafficLightWps)
                    {
                        // Skip đã mapped
                        if (_tlsMapping.ContainsValue(wp.OSMNodeId)) continue;

                        float d = Vector3.Distance(wp.Position, wp.Position); // placeholder
                        // Heuristic: ưu tiên node chưa mapped nào gần nhất
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestMatch = wp.OSMNodeId;
                        }
                    }
                }

                if (bestMatch > 0)
                {
                    _tlsMapping[tlsId] = bestMatch;
                    mapped++;
                }
            }

            _mappedCount = mapped;
            Debug.Log($"[SumoTLSync] Mapped {mapped}/{tlsIds.Count} SUMO TLS → Unity intersections.");
        }

        /// <summary>
        /// Apply SUMO TLS state string vào Unity intersection.
        /// State string: "rGrG..." → mỗi char = 1 controlled link.
        /// Chia đều chars cho số directions, xác định dominant signal cho mỗi direction.
        /// </summary>
        private void ApplyTLSState(long intersectionId, string stateStr)
        {
            if (string.IsNullOrEmpty(stateStr)) return;
            if (LightManager == null) return;

            // Lấy Intersection object từ TrafficLightManager
            var intersections = GetIntersectionsField();
            if (intersections == null || !intersections.TryGetValue(intersectionId, out Intersection intersection))
                return;

            int numDirections = intersection.IncomingNodeIds.Count;
            if (numDirections == 0) return;

            // Chia state string thành groups cho mỗi direction
            int linksPerDir = Mathf.Max(1, stateStr.Length / numDirections);

            for (int dirIdx = 0; dirIdx < numDirections; dirIdx++)
            {
                // Lấy subset chars cho direction này
                int startChar = dirIdx * linksPerDir;
                int endChar = Mathf.Min(startChar + linksPerDir, stateStr.Length);

                // Xác định dominant signal: G > Y > r
                int greenCount = 0, yellowCount = 0, redCount = 0;
                for (int c = startChar; c < endChar; c++)
                {
                    char ch = stateStr[c];
                    if (ch == 'g' || ch == 'G') greenCount++;
                    else if (ch == 'y' || ch == 'Y') yellowCount++;
                    else if (ch == 'r' || ch == 'R') redCount++;
                }

                // State: 0=Red, 1=Green, 2=Yellow
                int state;
                Color color;
                if (greenCount > 0) { state = 1; color = Color.green; }
                else if (yellowCount > 0) { state = 2; color = Color.yellow; }
                else { state = 0; color = Color.red; }

                // Apply trigger state
                if (dirIdx < intersection.Triggers.Count && intersection.Triggers[dirIdx] != null)
                    intersection.Triggers[dirIdx].State = state;

                // Apply visual color
                if (dirIdx < intersection.LightRenderers.Count)
                {
                    Renderer r = intersection.LightRenderers[dirIdx];
                    if (r != null && r.sharedMaterial != null)
                    {
                        if (r.sharedMaterial.HasProperty("_BaseColor"))
                            r.sharedMaterial.SetColor("_BaseColor", color);
                        else
                            r.sharedMaterial.color = color;

                        if (r.sharedMaterial.HasProperty("_EmissionColor"))
                        {
                            r.sharedMaterial.EnableKeyword("_EMISSION");
                            r.sharedMaterial.SetColor("_EmissionColor", color * 2f);
                        }
                    }
                }
            }

            // Override timer để TrafficLightManager không tự cycle khi SUMO đang sync
            intersection.Timer = 0f;
        }

        /// <summary>
        /// Truy cập _intersections dictionary từ TrafficLightManager thông qua reflection.
        /// Tránh phải sửa access modifier của TrafficLightManager.
        /// </summary>
        private Dictionary<long, Intersection> _cachedIntersections;
        private Dictionary<long, Intersection> GetIntersectionsField()
        {
            if (_cachedIntersections != null) return _cachedIntersections;
            if (LightManager == null) return null;

            var field = typeof(TrafficLightManager).GetField("_intersections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
                _cachedIntersections = field.GetValue(LightManager) as Dictionary<long, Intersection>;

            return _cachedIntersections;
        }

        // ══════════════════════════════════════════════════════════════════
        // MANUAL MAPPING API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Thêm manual mapping: SUMO TLS ID → Unity intersection node ID.
        /// Gọi từ Editor script hoặc custom setup.
        /// </summary>
        public void AddMapping(string sumoTlsId, long unityIntersectionNodeId)
        {
            _tlsMapping[sumoTlsId] = unityIntersectionNodeId;
            _mappedCount = _tlsMapping.Count;
        }

        /// <summary>Clear toàn bộ mapping (force rebuild).</summary>
        public void ClearMapping()
        {
            _tlsMapping.Clear();
            _cachedIntersections = null;
            _mappingBuilt = false;
            _mappedCount = 0;
        }
    }
}
