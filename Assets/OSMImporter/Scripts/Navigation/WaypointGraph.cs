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
    }

    // ── Serializable container so Dictionary survives Play mode ──────────────
    [System.Serializable]
    public class WaypointEntry
    {
        public long     Id;
        public Vector3  Position;
        public string   RoadType;
        public long[]   Connections;
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
                            RoadType  = way.HighwayType
                        };
                    }

                    if (i > 0)
                    {
                        long prevId = way.NodeRefs[i - 1];
                        if (Waypoints.ContainsKey(prevId))
                        {
                            if (!Waypoints[nodeId].ConnectedWaypointIds.Contains(prevId))
                                Waypoints[nodeId].ConnectedWaypointIds.Add(prevId);
                            if (!Waypoints[prevId].ConnectedWaypointIds.Contains(nodeId))
                                Waypoints[prevId].ConnectedWaypointIds.Add(nodeId);
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
                    Connections = kv.Value.ConnectedWaypointIds.ToArray()
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
                    ConnectedWaypointIds = new List<long>(e.Connections ?? System.Array.Empty<long>())
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

        public List<Vector3> FindPath(long startId, long endId)
        {
            var path = new List<Vector3>();
            if (!Waypoints.ContainsKey(startId) || !Waypoints.ContainsKey(endId)) return path;

            var openSet     = new SortedDictionary<float, long>();
            var cameFrom    = new Dictionary<long, long>();
            var gScore      = new Dictionary<long, float>();
            var openSetHash = new HashSet<long>();

            gScore[startId] = 0;
            float h = Vector3.Distance(Waypoints[startId].Position, Waypoints[endId].Position);
            openSet[h] = startId;
            openSetHash.Add(startId);

            int safety = 100000;
            while (openSet.Count > 0 && safety-- > 0)
            {
                var enumerator = openSet.GetEnumerator(); enumerator.MoveNext();
                float currentKey = enumerator.Current.Key;
                long  current    = enumerator.Current.Value;
                openSet.Remove(currentKey);
                openSetHash.Remove(current);

                if (current == endId)
                {
                    long c = endId;
                    while (cameFrom.ContainsKey(c)) { path.Insert(0, Waypoints[c].Position); c = cameFrom[c]; }
                    path.Insert(0, Waypoints[startId].Position);
                    return path;
                }

                Waypoint currentWp = Waypoints[current];
                foreach (long neighborId in currentWp.ConnectedWaypointIds)
                {
                    if (!Waypoints.ContainsKey(neighborId)) continue;
                    float tentativeG = gScore[current] +
                        Vector3.Distance(currentWp.Position, Waypoints[neighborId].Position);
                    if (!gScore.TryGetValue(neighborId, out float existingG)) existingG = float.MaxValue;

                    if (tentativeG < existingG)
                    {
                        cameFrom[neighborId] = current;
                        gScore[neighborId]   = tentativeG;
                        float f = tentativeG + Vector3.Distance(Waypoints[neighborId].Position, Waypoints[endId].Position);
                        if (!openSetHash.Contains(neighborId))
                        {
                            while (openSet.ContainsKey(f)) f += 0.001f;
                            openSet[f] = neighborId;
                            openSetHash.Add(neighborId);
                        }
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
