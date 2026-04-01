using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;

namespace OSMImporter.Navigation
{
    [System.Serializable]
    public class Waypoint
    {
        public long     OSMNodeId;
        public Vector3  Position;
        public List<long> ConnectedWaypointIds = new List<long>();
        public string   RoadType = "";
        public bool     IsTrafficLight = false;
    }

    // ── Serializable container so Dictionary survives Play mode ──────────────
    [System.Serializable]
    public class WaypointEntry
    {
        public long     Id;
        public Vector3  Position;
        public string   RoadType;
        public long[]   Connections;
        public bool     IsTrafficLight;
    }

    public class WaypointGraph : MonoBehaviour
    {
        // Runtime dictionary (rebuilt from _entries on Awake)
        [System.NonSerialized]
        public Dictionary<long, Waypoint> Waypoints = new Dictionary<long, Waypoint>();

        // Serialized backing list — survives Play mode
        [SerializeField] private List<WaypointEntry> _entries = new List<WaypointEntry>();

        [Header("Gizmos")]
        public bool  ShowGizmos       = true;
        public Color WaypointColor    = Color.cyan;
        public Color ConnectionColor  = Color.yellow;
        public float GizmoSize        = 0.5f;

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Awake()  => RebuildFromEntries();
        private void OnEnable() { if (Waypoints.Count == 0) RebuildFromEntries(); }

        // ── Build from OSM data ───────────────────────────────────────────────

        public void BuildFromOSM(OSMMapData mapData, float scale = 1f)
        {
            Waypoints.Clear();
            double originLat = mapData.Bounds.CenterLat;
            double originLon = mapData.Bounds.CenterLon;

            foreach (var way in mapData.GetHighways())
            {
                for (int i = 0; i < way.NodeRefs.Count; i++)
                {
                    long nodeId = way.NodeRefs[i];
                    if (!Waypoints.ContainsKey(nodeId))
                    {
                        if (!mapData.Nodes.TryGetValue(nodeId, out OSMNode osmNode)) continue;
                        Waypoints[nodeId] = new Waypoint
                        {
                            OSMNodeId = nodeId,
                            Position  = MercatorProjection.LatLonToUnityCorrected(
                                            osmNode.Latitude, osmNode.Longitude,
                                            originLat, originLon, scale),
                            RoadType  = way.HighwayType,
                            IsTrafficLight = osmNode.Tags.TryGetValue("highway", out string hw) && hw == "traffic_signals"
                        };
                    }

                    if (i > 0)
                    {
                        long prevId = way.NodeRefs[i - 1];
                        if (Waypoints.ContainsKey(prevId))
                        {
                            bool isOneWay = way.IsOneWay || way.HighwayType == "motorway"; 
                            bool isReverse = way.IsReverseOneWay;

                            // Chiều xuôi (prevId -> nodeId)
                            if (!isReverse)
                            {
                                if (!Waypoints[prevId].ConnectedWaypointIds.Contains(nodeId))
                                    Waypoints[prevId].ConnectedWaypointIds.Add(nodeId);
                            }
                            // Chiều ngược (nodeId -> prevId)
                            if (!isOneWay && !isReverse) // !isReverse để an toàn, nếu isReverse thì được phép ngược
                            {
                                if (!Waypoints[nodeId].ConnectedWaypointIds.Contains(prevId))
                                    Waypoints[nodeId].ConnectedWaypointIds.Add(prevId);
                            }
                            else if (isReverse)
                            {
                                if (!Waypoints[nodeId].ConnectedWaypointIds.Contains(prevId))
                                    Waypoints[nodeId].ConnectedWaypointIds.Add(prevId);
                            }
                        }
                    }
                }
            }

            // Persist to serialized list so it survives Play mode
            SaveToEntries();
        }

        // ── Serialization helpers ─────────────────────────────────────────────

        public void SaveToEntries()
        {
            _entries.Clear();
            foreach (var kv in Waypoints)
            {
                _entries.Add(new WaypointEntry
                {
                    Id          = kv.Value.OSMNodeId,
                    Position    = kv.Value.Position,
                    RoadType    = kv.Value.RoadType,
                    Connections = kv.Value.ConnectedWaypointIds.ToArray(),
                    IsTrafficLight = kv.Value.IsTrafficLight
                });
            }
        }

        private void RebuildFromEntries()
        {
            Waypoints.Clear();
            foreach (var e in _entries)
            {
                Waypoints[e.Id] = new Waypoint
                {
                    OSMNodeId            = e.Id,
                    Position             = e.Position,
                    RoadType             = e.RoadType,
                    ConnectedWaypointIds = new List<long>(e.Connections ?? System.Array.Empty<long>()),
                    IsTrafficLight       = e.IsTrafficLight
                };
            }
        }

        // ── Pathfinding ───────────────────────────────────────────────────────

        public Waypoint FindNearest(Vector3 position)
        {
            Waypoint nearest = null;
            float minDist = float.MaxValue;
            foreach (var wp in Waypoints.Values)
            {
                float dist = Vector3.SqrMagnitude(wp.Position - position);
                if (dist < minDist) { minDist = dist; nearest = wp; }
            }
            return nearest;
        }

        private float GetRoadWeight(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 1.5f;
            switch(roadType) {
                case "motorway": return 0.5f;    // Ưu tiên đi đường cao tốc (chi phí thấp)
                case "trunk":    return 0.6f;
                case "primary":  return 0.7f;
                case "secondary":return 0.9f;
                case "tertiary": return 1.0f;
                case "residential": return 1.8f; // Tránh đi đường nhỏ khu dân cư
                case "living_street": return 2.5f;
                default: return 1.2f;
            }
        }

        public List<Waypoint> FindPath(long startId, long endId)
        {
            var path = new List<Waypoint>();
            if (!Waypoints.ContainsKey(startId) || !Waypoints.ContainsKey(endId)) return path;

            // Open list: sorted by f-score ascending. Using List + insert to avoid key collision.
            var openList  = new List<(float f, long id)>();
            var cameFrom  = new Dictionary<long, long>();
            var gScore    = new Dictionary<long, float>();
            var closedSet = new HashSet<long>();

            gScore[startId] = 0;
            float h = Vector3.Distance(Waypoints[startId].Position, Waypoints[endId].Position);
            openList.Add((h, startId));

            int safety = 100000;
            while (openList.Count > 0 && safety-- > 0)
            {
                // Pop node with lowest f-score
                long current = openList[0].id;
                openList.RemoveAt(0);

                if (closedSet.Contains(current)) continue; // skip stale entries
                closedSet.Add(current);

                if (current == endId)
                {
                    long c = endId;
                    while (cameFrom.ContainsKey(c)) { path.Insert(0, Waypoints[c]); c = cameFrom[c]; }
                    path.Insert(0, Waypoints[startId]);
                    return path;
                }

                Waypoint currentWp = Waypoints[current];
                foreach (long neighborId in currentWp.ConnectedWaypointIds)
                {
                    if (!Waypoints.ContainsKey(neighborId) || closedSet.Contains(neighborId)) continue;

                    // Tính chi phí bao gồm: Khảng cách vật lý x Trọng số loại đường x Hệ số ngẫu nhiên
                    // Giúp xe ưu tiên đi đường lớn (tối ưu hơn) và phân tán ra các đường song song (không đi dồn 1 đường duy nhất)
                    float edgeDist = Vector3.Distance(currentWp.Position, Waypoints[neighborId].Position);
                    float roadWeight = GetRoadWeight(Waypoints[neighborId].RoadType);
                    float randomFactor = Random.Range(0.85f, 1.15f); // Noise 15% để tránh tình trạng "quãng đường bằng nhau"
                    
                    float tentativeG = gScore[current] + (edgeDist * roadWeight * randomFactor);
                    
                    if (!gScore.TryGetValue(neighborId, out float existingG)) existingG = float.MaxValue;

                    if (tentativeG < existingG)
                    {
                        cameFrom[neighborId] = current;
                        gScore[neighborId]   = tentativeG;
                        
                        // Heuristic sử dụng đường chim bay (gạch nối thẳng) nhân với trọng số tối ưu nhất để đảm bảo thuật toán admissible
                        float f = tentativeG + (Vector3.Distance(Waypoints[neighborId].Position, Waypoints[endId].Position) * 0.5f);
                        // Binary-search insert to keep list sorted
                        int idx = openList.BinarySearch((f, neighborId),
                            Comparer<(float f, long id)>.Create((a, b) => a.f.CompareTo(b.f)));
                        if (idx < 0) idx = ~idx;
                        openList.Insert(idx, (f, neighborId));
                    }
                }
            }
            return path;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!ShowGizmos) return;
            var data = Waypoints.Count > 0 ? Waypoints : null;
            if (data == null && _entries.Count > 0) RebuildFromEntries();
            if (Waypoints.Count == 0) return;

            foreach (var wp in Waypoints.Values)
            {
                Gizmos.color = wp.ConnectedWaypointIds.Count > 2 ? Color.red : WaypointColor;
                Gizmos.DrawSphere(wp.Position, GizmoSize);
                Gizmos.color = ConnectionColor;
                foreach (long connId in wp.ConnectedWaypointIds)
                    if (Waypoints.TryGetValue(connId, out Waypoint conn))
                        Gizmos.DrawLine(wp.Position, conn.Position);
            }
        }
#endif
    }
}
