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

        // ── Congestion tracking (shared across all vehicles) ──
        // Key = waypoint id, Value = congestion penalty multiplier (1.0 = normal, higher = more congested)
        [System.NonSerialized]
        public Dictionary<long, float> CongestionCosts = new Dictionary<long, float>();

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
                case "motorway": return 0.5f;
                case "trunk":    return 0.6f;
                case "primary":  return 0.7f;
                case "secondary":return 0.9f;
                case "tertiary": return 1.0f;
                case "residential": return 1.8f;
                case "living_street": return 2.5f;
                default: return 1.2f;
            }
        }

        // Độ ưu tiên đường (số cao = đường lớn hơn)
        private static int GetRoadPriority(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 2;
            switch(roadType) {
                case "motorway":      return 7;
                case "trunk":         return 6;
                case "primary":       return 5;
                case "secondary":     return 4;
                case "tertiary":      return 3;
                case "residential":   return 1;
                case "living_street": return 0;
                default: return 2;
            }
        }

        /// <summary>Pathfinding chuẩn (không tính congestion)</summary>
        public List<Waypoint> FindPath(long startId, long endId)
            => FindPath(startId, endId, false);

        /// <summary>
        /// A* pathfinding với Google Maps-style rules:
        /// - Turn Penalty: phạt khi rẽ, cấm U-turn
        /// - Road Continuity Bonus: ưu tiên ở lại cùng loại đường
        /// - Congestion avoidance (optional)
        /// </summary>
        public List<Waypoint> FindPath(long startId, long endId, bool useCongestion)
        {
            var path = new List<Waypoint>();
            if (!Waypoints.ContainsKey(startId) || !Waypoints.ContainsKey(endId)) return path;

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
                long current = openList[0].id;
                openList.RemoveAt(0);

                if (closedSet.Contains(current)) continue;
                closedSet.Add(current);

                if (current == endId)
                {
                    long c = endId;
                    while (cameFrom.ContainsKey(c)) { path.Insert(0, Waypoints[c]); c = cameFrom[c]; }
                    path.Insert(0, Waypoints[startId]);
                    return path;
                }

                Waypoint currentWp = Waypoints[current];

                // Tính hướng tiếp cận hiện tại (previous → current) cho turn penalty
                Vector3 approachDir = Vector3.zero;
                bool hasApproach = false;
                if (cameFrom.TryGetValue(current, out long prevId) && Waypoints.ContainsKey(prevId))
                {
                    approachDir = (currentWp.Position - Waypoints[prevId].Position);
                    approachDir.y = 0f;
                    if (approachDir.sqrMagnitude > 0.01f)
                    {
                        approachDir.Normalize();
                        hasApproach = true;
                    }
                }

                foreach (long neighborId in currentWp.ConnectedWaypointIds)
                {
                    if (!Waypoints.ContainsKey(neighborId) || closedSet.Contains(neighborId)) continue;

                    Waypoint neighborWp = Waypoints[neighborId];
                    float edgeDist = Vector3.Distance(currentWp.Position, neighborWp.Position);
                    float roadWeight = GetRoadWeight(neighborWp.RoadType);
                    float randomFactor = Random.Range(0.75f, 1.25f);
                    
                    // ── Turn Penalty: phạt khi đổi hướng ──
                    float turnPenalty = 1f;
                    if (hasApproach)
                    {
                        Vector3 exitDir = (neighborWp.Position - currentWp.Position);
                        exitDir.y = 0f;
                        if (exitDir.sqrMagnitude > 0.01f)
                        {
                            float turnAngle = Vector3.Angle(approachDir, exitDir.normalized);
                            if (turnAngle > 150f)
                                turnPenalty = 5.0f;      // U-turn: gần như cấm
                            else if (turnAngle > 90f)
                                turnPenalty = 1.8f;       // Rẽ gắt
                            else if (turnAngle > 45f)
                                turnPenalty = 1.3f;       // Rẽ nhẹ
                            // <= 45° → không phạt (đi thẳng)
                        }
                    }

                    // ── Road Continuity: ưu tiên ở lại cùng đường ──
                    float continuityMult = 1f;
                    int curPrio = GetRoadPriority(currentWp.RoadType);
                    int nbrPrio = GetRoadPriority(neighborWp.RoadType);
                    if (currentWp.RoadType == neighborWp.RoadType)
                        continuityMult = 0.85f;           // Bonus: cùng đường
                    else if (nbrPrio > curPrio)
                        continuityMult = 0.9f;            // Bonus nhẹ: lên đường lớn hơn
                    else if (nbrPrio < curPrio)
                        continuityMult = 1.3f;            // Penalty: xuống đường nhỏ hơn

                    // ── Congestion penalty ──
                    float congestionMult = 1f;
                    if (useCongestion && CongestionCosts.TryGetValue(neighborId, out float cCost))
                        congestionMult = cCost;

                    float tentativeG = gScore[current] 
                        + edgeDist * roadWeight * randomFactor * turnPenalty * continuityMult * congestionMult;
                    
                    if (!gScore.TryGetValue(neighborId, out float existingG)) existingG = float.MaxValue;

                    if (tentativeG < existingG)
                    {
                        cameFrom[neighborId] = current;
                        gScore[neighborId]   = tentativeG;
                        
                        float f = tentativeG + (Vector3.Distance(neighborWp.Position, Waypoints[endId].Position) * 0.5f);
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
