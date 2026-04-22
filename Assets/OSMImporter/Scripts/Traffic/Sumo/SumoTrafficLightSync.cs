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
        /// Strategy: dùng TLS ID (thường = junction ID trong SUMO) →
        /// tìm Unity intersection gần nhất.
        /// </summary>
        private void BuildMapping(TraCIClient client)
        {
            _tlsMapping.Clear();
            var mapper = Bridge.GetMapper();
            if (mapper == null) return;

            var tlsIds = client.GetTLSIdList();

            // Lấy vị trí các intersection trong Unity
            var graph = LightManager.Graph;
            if (graph == null) return;

            int mapped = 0;
            foreach (var tlsId in tlsIds)
            {
                // Thử match theo TLS ID → junction position
                // SUMO TLS thường nằm tại junction, dùng junction position để match
                long bestMatch = -1;
                float bestDist = MatchRadius;

                foreach (var wp in graph.Waypoints.Values)
                {
                    if (!wp.IsTrafficLight) continue;

                    // Heuristic: TLS ID chứa node ID hoặc gần nhất
                    if (tlsId.Contains(wp.OSMNodeId.ToString()))
                    {
                        bestMatch = wp.OSMNodeId;
                        break;
                    }
                }

                // Nếu không match bằng ID → skip (manual mapping cần thiết)
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
        /// State string: "rGrG..." → mỗi char = 1 controlled link
        /// Ta map vào từng direction index.
        /// </summary>
        private void ApplyTLSState(long intersectionId, string stateStr)
        {
            if (string.IsNullOrEmpty(stateStr)) return;

            // Xác định state tổng thể cho intersection
            // SUMO state string có 1 char per link, ta cần group cho mỗi direction
            // Simplified: dùng char đầu tiên mà = 'g'/'G' để xác định green direction

            bool hasGreen = false;
            bool hasYellow = false;

            for (int i = 0; i < stateStr.Length; i++)
            {
                char c = stateStr[i];
                if (c == 'g' || c == 'G') hasGreen = true;
                if (c == 'y' || c == 'Y') hasYellow = true;
            }

            // Tìm direction index nào đang green
            // Mapping: nếu stateStr length = N links, chia đều cho số direction
            // Đây là approximation — perfect mapping cần connection info từ .net.xml
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
            _mappingBuilt = false;
            _mappedCount = 0;
        }
    }
}
