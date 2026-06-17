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
        public long     OriginalOSMNodeId;
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
        public long     OriginalOSMNodeId;
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

        [Header("Road Settings")]
        [Tooltip("Hệ số nhân chiều rộng đường (phải khớp với giá trị dùng khi Import OSM)")]
        public float RoadWidthMultiplier = 1f;

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Awake()  => RebuildFromEntries();
        private void OnEnable() { if (Waypoints.Count == 0) RebuildFromEntries(); }

        // ── Nested LaneInfo for build logic ──────────────────────────────────
        private class LaneInfo
        {
            public long WayId;
            public string RoadType;
            public bool IsForward;
            public List<Waypoint> Waypoints = new List<Waypoint>();
            public List<int> NodeIndices = new List<int>(); // index trong way.NodeRefs ban đầu
        }

        // ── Build from OSM data ───────────────────────────────────────────────

        public void BuildFromOSM(OSMMapData mapData, float scale = 1f)
        {
            Waypoints.Clear();
            double originLat = mapData.Bounds.CenterLat;
            double originLon = mapData.Bounds.CenterLon;

            List<LaneInfo> allLanes = new List<LaneInfo>();
            // Key = OSM Node ID, Value = List of (lane, node index in lane)
            Dictionary<long, List<(LaneInfo lane, int nodeIdx)>> lanesAtOSMNode = new Dictionary<long, List<(LaneInfo lane, int nodeIdx)>>();

            long nextWaypointId = 1000000; // ID tự tăng duy nhất cho từng waypoint làn đường

            var highways = mapData.GetHighways();
            foreach (var way in highways)
            {
                int count = way.NodeRefs.Count;
                if (count < 2) continue;

                // 1. Tính toán danh sách vị trí Unity cho các node
                List<Vector3> osmPositions = new List<Vector3>();
                List<long> osmNodeIds = new List<long>();
                foreach (long nodeRef in way.NodeRefs)
                {
                    if (mapData.Nodes.TryGetValue(nodeRef, out OSMNode node))
                    {
                        osmPositions.Add(MercatorProjection.LatLonToUnityCorrected(
                                            node.Latitude, node.Longitude,
                                            originLat, originLon, scale));
                        osmNodeIds.Add(nodeRef);
                    }
                }

                if (osmPositions.Count < 2) continue;
                count = osmPositions.Count;

                // 2. Tính toán directions tại mỗi node
                List<Vector3> directions = new List<Vector3>();
                for (int i = 0; i < count; i++)
                {
                    Vector3 fwd;
                    if (i == 0)
                        fwd = (osmPositions[1] - osmPositions[0]).normalized;
                    else if (i == count - 1)
                        fwd = (osmPositions[count - 1] - osmPositions[count - 2]).normalized;
                    else
                        fwd = ((osmPositions[i + 1] - osmPositions[i]).normalized + (osmPositions[i] - osmPositions[i - 1]).normalized).normalized;
                    
                    if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                    directions.Add(fwd.normalized);
                }

                // 3. Phân chia làn dựa trên loại đường
                float baseW = RoadUtility.GetRoadBaseWidth(way.HighwayType);
                float maxOffset = baseW / 2f;
                bool isOneWay = way.IsOneWay || way.HighwayType == "motorway";
                bool isReverse = way.IsReverseOneWay;

                // Cấu hình làn cho way này: (float offset, bool isForward)
                List<(float offset, bool isForward)> laneConfigs = new List<(float offset, bool isForward)>();

                if (isOneWay)
                {
                    // Lòng đường rộng chia làn, ví dụ mỗi làn rộng 1.5m
                    int numLanes = Mathf.Max(1, Mathf.FloorToInt(maxOffset * 2f / 1.5f));
                    float laneW = (maxOffset * 2f) / numLanes;
                    for (int L = 0; L < numLanes; L++)
                    {
                        float offset = -maxOffset + (L + 0.5f) * laneW;
                        laneConfigs.Add((offset, !isReverse)); // Nếu reverse thì đi ngược, ngược lại đi xuôi
                    }
                }
                else
                {
                    // Đường hai chiều: bên phải đi xuôi (offset > 0), bên trái đi ngược (offset < 0)
                    int numLanes = Mathf.Max(1, Mathf.FloorToInt(maxOffset / 1.5f));
                    float laneW = maxOffset / numLanes;
                    
                    // Bên phải (đi xuôi)
                    for (int L = 0; L < numLanes; L++)
                    {
                        float offset = (L + 0.5f) * laneW;
                        laneConfigs.Add((offset, true));
                    }
                    // Bên trái (đi ngược)
                    for (int L = 0; L < numLanes; L++)
                    {
                        float offset = -(L + 0.5f) * laneW;
                        laneConfigs.Add((offset, false));
                    }
                }

                // 4. Tạo các waypoint cho từng cấu hình làn
                foreach (var config in laneConfigs)
                {
                    LaneInfo lane = new LaneInfo
                    {
                        WayId = way.Id,
                        RoadType = way.HighwayType,
                        IsForward = config.isForward
                    };

                    for (int i = 0; i < count; i++)
                    {
                        long osmNodeId = osmNodeIds[i];
                        Vector3 dir = directions[i];
                        Vector3 right = new Vector3(dir.z, 0, -dir.x).normalized;
                        Vector3 lanePos = osmPositions[i] + right * config.offset;

                        // Check traffic signals
                        bool isTrafficLight = mapData.Nodes.TryGetValue(osmNodeId, out OSMNode osmNode)
                            && osmNode.Tags.TryGetValue("highway", out string hw) && hw == "traffic_signals";

                        long wpId = nextWaypointId++;
                        Waypoint wp = new Waypoint
                        {
                            OSMNodeId = wpId,
                            Position = lanePos,
                            RoadType = way.HighwayType,
                            IsTrafficLight = isTrafficLight,
                            OriginalOSMNodeId = osmNodeId
                        };

                        Waypoints[wpId] = wp;
                        lane.Waypoints.Add(wp);
                        lane.NodeIndices.Add(i);

                        // Đăng ký làn tại node OSM này
                        if (!lanesAtOSMNode.ContainsKey(osmNodeId))
                            lanesAtOSMNode[osmNodeId] = new List<(LaneInfo lane, int nodeIdx)>();
                        lanesAtOSMNode[osmNodeId].Add((lane, i));
                    }

                    allLanes.Add(lane);
                }
            }

            // 5. Kết nối các waypoint trong cùng một làn
            foreach (var lane in allLanes)
            {
                int wpCount = lane.Waypoints.Count;
                if (lane.IsForward)
                {
                    // Đi xuôi: waypoint i nối sang waypoint i + 1
                    for (int i = 0; i < wpCount - 1; i++)
                    {
                        lane.Waypoints[i].ConnectedWaypointIds.Add(lane.Waypoints[i + 1].OSMNodeId);
                    }
                }
                else
                {
                    // Đi ngược: waypoint i + 1 nối sang waypoint i
                    for (int i = wpCount - 1; i > 0; i--)
                    {
                        lane.Waypoints[i].ConnectedWaypointIds.Add(lane.Waypoints[i - 1].OSMNodeId);
                    }
                }
            }

            // 6. Kết nối các làn tại giao lộ (Ngã 3, Ngã 4...)
            foreach (var kv in lanesAtOSMNode)
            {
                long J = kv.Key;
                var laneList = kv.Value;

                // Lọc các node giao lộ (giao giữa >= 2 ways khác nhau)
                HashSet<long> uniqueWays = new HashSet<long>();
                foreach (var item in laneList)
                {
                    uniqueWays.Add(item.lane.WayId);
                }
                if (uniqueWays.Count < 2) continue; // chỉ là node nối tiếp của cùng 1 way, không phải giao lộ

                // Duyệt qua từng làn đi vào ngã tư
                foreach (var inItem in laneList)
                {
                    LaneInfo laneIn = inItem.lane;
                    int idxIn = inItem.nodeIdx;
                    Waypoint wpInAtJ = laneIn.Waypoints[idxIn];

                    // Duyệt sang các làn của các way khác để rẽ sang
                    foreach (var outItem in laneList)
                    {
                        LaneInfo laneOut = outItem.lane;
                        if (laneOut.WayId == laneIn.WayId) continue; // Tránh nối sang làn ngược của cùng 1 đường tại giao lộ

                        int idxOut = outItem.nodeIdx;

                        if (laneOut.IsForward)
                        {
                            // Đi xuôi: điểm tiếp theo đi ra khỏi J là index J + 1
                            if (idxOut + 1 < laneOut.Waypoints.Count)
                            {
                                Waypoint targetWp = laneOut.Waypoints[idxOut + 1];
                                if (!wpInAtJ.ConnectedWaypointIds.Contains(targetWp.OSMNodeId))
                                    wpInAtJ.ConnectedWaypointIds.Add(targetWp.OSMNodeId);
                            }
                        }
                        else
                        {
                            // Đi ngược: điểm tiếp theo đi ra khỏi J là index J - 1
                            if (idxOut - 1 >= 0)
                            {
                                Waypoint targetWp = laneOut.Waypoints[idxOut - 1];
                                if (!wpInAtJ.ConnectedWaypointIds.Contains(targetWp.OSMNodeId))
                                    wpInAtJ.ConnectedWaypointIds.Add(targetWp.OSMNodeId);
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
                    IsTrafficLight = kv.Value.IsTrafficLight,
                    OriginalOSMNodeId = kv.Value.OriginalOSMNodeId
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
                    IsTrafficLight       = e.IsTrafficLight,
                    OriginalOSMNodeId    = e.OriginalOSMNodeId
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
                        + edgeDist * roadWeight * turnPenalty * continuityMult * congestionMult;
                    
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
