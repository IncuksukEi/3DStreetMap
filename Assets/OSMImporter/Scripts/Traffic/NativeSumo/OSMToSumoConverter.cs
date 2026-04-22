using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Data;
using OSMImporter.Geo;
using OSMImporter.Navigation;
using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic.NativeSumo
{
    /// <summary>
    /// Chuyển đổi OSMMapData (từ download/file .osm) → SNetwork (SUMO native graph).
    /// Không cần file .net.xml hoặc netconvert — tạo trực tiếp từ dữ liệu OSM đã parse.
    ///
    /// 2 entry points:
    /// ├── Convert(WaypointGraph)   → nhanh, shape 2 điểm (thẳng)
    /// └── ConvertFromOSMData(...)  → đầy đủ, shape multi-point (theo đường cong OSM)
    /// </summary>
    public static class OSMToSumoConverter
    {
        // ══════════════════════════════════════════════════════════════════
        // CONVERT TỪ OSMMapData (FULL GEOMETRY) — ưu tiên dùng cái này
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert trực tiếp OSMMapData → SNetwork.
        /// Giữ nguyên polyline geometry từ OSM way nodes → shape SLane cong đẹp.
        /// Chia road thành segments giữa các junction (giao lộ).
        /// </summary>
        public static SNetwork ConvertFromOSMData(OSMMapData mapData, float scale = 1f)
        {
            if (mapData == null || mapData.Nodes.Count == 0)
            {
                Debug.LogError("[OSMToSumo] OSMMapData rỗng!");
                return null;
            }

            double originLat = mapData.Bounds.CenterLat;
            double originLon = mapData.Bounds.CenterLon;

            var net = new SNetwork();
            var highways = mapData.GetHighways();

            // 1. Xác định junction nodes (nodes xuất hiện trong >= 2 ways hoặc ở đầu/cuối way)
            var nodeWayCount = new Dictionary<long, int>();
            foreach (var way in highways)
            {
                foreach (long nodeId in way.NodeRefs)
                {
                    if (!nodeWayCount.ContainsKey(nodeId)) nodeWayCount[nodeId] = 0;
                    nodeWayCount[nodeId]++;
                }
                // Đầu và cuối way luôn là junction
                if (way.NodeRefs.Count >= 2)
                {
                    long firstId = way.NodeRefs[0];
                    long lastId = way.NodeRefs[way.NodeRefs.Count - 1];
                    nodeWayCount.TryGetValue(firstId, out int fc);
                    nodeWayCount[firstId] = Mathf.Max(fc, 2);
                    nodeWayCount.TryGetValue(lastId, out int lc);
                    nodeWayCount[lastId] = Mathf.Max(lc, 2);
                }
            }

            // Tạo SNode cho junction nodes
            foreach (var kvp in nodeWayCount)
            {
                if (kvp.Value < 2) continue;
                if (!mapData.Nodes.TryGetValue(kvp.Key, out OSMNode osmNode)) continue;

                Vector3 pos = MercatorProjection.LatLonToUnityCorrected(
                    osmNode.Latitude, osmNode.Longitude, originLat, originLon, scale);

                net.Nodes[kvp.Key.ToString()] = new SNode
                {
                    id = kvp.Key.ToString(),
                    position = pos
                };
            }

            // 2. Chia mỗi way thành segments (junction → junction)
            int edgeCounter = 0;
            var createdEdges = new Dictionary<string, SEdge>();

            foreach (var way in highways)
            {
                if (way.NodeRefs.Count < 2) continue;
                string roadType = way.HighwayType;
                bool isOneWay = way.IsOneWay || roadType == "motorway" || roadType == "motorway_link";
                bool isReverse = way.IsReverseOneWay;

                // Thu thập segments: mỗi segment là 1 list nodes giữa 2 junctions
                var segments = SplitWayIntoSegments(way, nodeWayCount);

                foreach (var segment in segments)
                {
                    if (segment.Count < 2) continue;

                    long fromNodeId = segment[0];
                    long toNodeId = segment[segment.Count - 1];
                    string fromId = fromNodeId.ToString();
                    string toId = toNodeId.ToString();

                    if (!net.Nodes.ContainsKey(fromId) || !net.Nodes.ContainsKey(toId)) continue;

                    // Chiều xuôi (hoặc cả 2 chiều nếu không one-way)
                    if (!isReverse)
                    {
                        CreateSegmentEdge(net, mapData, segment, fromId, toId,
                            roadType, ref edgeCounter, createdEdges,
                            originLat, originLon, scale, false);
                    }

                    // Chiều ngược nếu 2 chiều, hoặc reverse one-way
                    if (!isOneWay || isReverse)
                    {
                        // Đảo ngược segment
                        var reversed = new List<long>(segment);
                        reversed.Reverse();
                        CreateSegmentEdge(net, mapData, reversed, toId, fromId,
                            roadType, ref edgeCounter, createdEdges,
                            originLat, originLon, scale, false);
                    }
                }
            }

            // 3. Build successors
            BuildSuccessors(net);

            // 4. Prune dead-end nodes không có connections
            PruneIsolatedNodes(net);

            Debug.Log($"[OSMToSumo] ConvertFromOSMData: {net.Nodes.Count} junctions, {net.Edges.Count} edges " +
                $"({CountTotalLanes(net)} lanes).");

            return net;
        }

        /// <summary>
        /// Chia 1 OSM way thành nhiều segments tại các junction nodes.
        /// Mỗi segment: [junction_A, intermediate_nodes..., junction_B]
        /// </summary>
        private static List<List<long>> SplitWayIntoSegments(OSMWay way, Dictionary<long, int> nodeWayCount)
        {
            var segments = new List<List<long>>();
            var current = new List<long> { way.NodeRefs[0] };

            for (int i = 1; i < way.NodeRefs.Count; i++)
            {
                long nodeId = way.NodeRefs[i];
                current.Add(nodeId);

                // Node này là junction → kết thúc segment, bắt đầu segment mới
                bool isJunction = nodeWayCount.ContainsKey(nodeId) && nodeWayCount[nodeId] >= 2;
                bool isLast = i == way.NodeRefs.Count - 1;

                if (isJunction || isLast)
                {
                    if (current.Count >= 2)
                        segments.Add(current);
                    // Segment tiếp theo bắt đầu từ junction node này
                    current = new List<long> { nodeId };
                }
            }

            return segments;
        }

        /// <summary>
        /// Tạo 1 SEdge từ segment nodes, với shape polyline đầy đủ.
        /// </summary>
        private static void CreateSegmentEdge(
            SNetwork net, OSMMapData mapData,
            List<long> segment, string fromId, string toId,
            string roadType, ref int edgeCounter,
            Dictionary<string, SEdge> createdEdges,
            double originLat, double originLon, float scale,
            bool reversed)
        {
            string edgeKey = $"{fromId}_{toId}";
            if (createdEdges.ContainsKey(edgeKey)) return;

            SNode fromNode = net.Nodes[fromId];
            SNode toNode = net.Nodes[toId];

            // Build polyline từ tất cả nodes trong segment
            var centerLine = new List<Vector3>();
            float totalLength = 0f;

            for (int i = 0; i < segment.Count; i++)
            {
                if (!mapData.Nodes.TryGetValue(segment[i], out OSMNode osmNode)) continue;
                Vector3 pos = MercatorProjection.LatLonToUnityCorrected(
                    osmNode.Latitude, osmNode.Longitude, originLat, originLon, scale);
                pos.y = 0.15f; // Nâng nhẹ khỏi mặt đường
                centerLine.Add(pos);

                if (i > 0)
                    totalLength += Vector3.Distance(centerLine[centerLine.Count - 2], centerLine[centerLine.Count - 1]);
            }

            if (centerLine.Count < 2 || totalLength < 0.5f) return;

            int numLanes = GetLaneCount(roadType);
            float maxSpeed = GetMaxSpeed(roadType);
            float roadWidth = GetRoadWidth(roadType);

            SEdge edge = new SEdge
            {
                id = $"e_{edgeCounter++}",
                fromNode = fromNode,
                toNode = toNode,
                length = totalLength,
                maxSpeed = maxSpeed
            };

            // Tạo lanes với offset từ center line
            for (int laneIdx = 0; laneIdx < numLanes; laneIdx++)
            {
                float laneWidth = roadWidth / numLanes;
                float offsetFromCenter = -roadWidth / 2f + laneWidth * (laneIdx + 0.5f);

                // Offset mỗi điểm của polyline theo hướng vuông góc
                var laneShape = OffsetPolyline(centerLine, offsetFromCenter);

                SLane lane = new SLane
                {
                    id = $"{edge.id}_{laneIdx}",
                    index = laneIdx,
                    length = totalLength,
                    maxSpeed = maxSpeed,
                    parentEdge = edge,
                    shape = laneShape
                };

                edge.lanes.Add(lane);
            }

            fromNode.outgoing.Add(edge);
            toNode.incoming.Add(edge);

            net.Edges[edge.id] = edge;
            createdEdges[edgeKey] = edge;
        }

        /// <summary>
        /// Offset polyline sang bên phải (perpendicular) theo khoảng cách cho trước.
        /// Dùng để tạo shape cho từng lane từ center line.
        /// </summary>
        private static List<Vector3> OffsetPolyline(List<Vector3> centerLine, float offset)
        {
            var result = new List<Vector3>(centerLine.Count);

            for (int i = 0; i < centerLine.Count; i++)
            {
                Vector3 forward;
                if (i == 0)
                    forward = (centerLine[1] - centerLine[0]).normalized;
                else if (i == centerLine.Count - 1)
                    forward = (centerLine[i] - centerLine[i - 1]).normalized;
                else
                    // Trung bình 2 segment liền kề → mượt hơn tại các góc
                    forward = ((centerLine[i] - centerLine[i - 1]).normalized +
                               (centerLine[i + 1] - centerLine[i]).normalized).normalized;

                // Vector vuông góc sang phải (trên mặt phẳng XZ)
                Vector3 right = new Vector3(forward.z, 0, -forward.x);
                Vector3 offsetPos = centerLine[i] + right * offset;
                offsetPos.y = 0.15f;
                result.Add(offsetPos);
            }

            return result;
        }

        /// <summary>
        /// Xoá nodes không có bất kỳ edge nào
        /// </summary>
        private static void PruneIsolatedNodes(SNetwork net)
        {
            var toRemove = new List<string>();
            foreach (var node in net.Nodes.Values)
            {
                if (node.incoming.Count == 0 && node.outgoing.Count == 0)
                    toRemove.Add(node.id);
            }
            foreach (var id in toRemove)
                net.Nodes.Remove(id);
        }

        // ══════════════════════════════════════════════════════════════════
        // CONVERT TỪ WAYPOINTGRAPH (LEGACY — shape 2 điểm)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tạo SNetwork từ WaypointGraph đã build từ OSM data.
        /// Mỗi road segment (2 waypoint liên tiếp) = 1 SEdge + 1-N SLane.
        /// Mỗi junction waypoint = 1 SNode.
        /// </summary>
        public static SNetwork Convert(WaypointGraph graph, float scale = 1f)
        {
            if (graph == null || graph.Waypoints.Count == 0)
            {
                Debug.LogError("[OSMToSumo] WaypointGraph rỗng!");
                return null;
            }

            var net = new SNetwork();

            // 1. Tạo SNode cho mỗi waypoint
            foreach (var wp in graph.Waypoints.Values)
            {
                var node = new SNode
                {
                    id = wp.OSMNodeId.ToString(),
                    position = wp.Position
                };
                net.Nodes[node.id] = node;
            }

            // 2. Tạo SEdge + SLane cho mỗi connection giữa waypoints
            int edgeCounter = 0;
            var createdEdges = new Dictionary<string, SEdge>(); // "fromId_toId" → edge

            foreach (var wp in graph.Waypoints.Values)
            {
                string fromId = wp.OSMNodeId.ToString();
                if (!net.Nodes.ContainsKey(fromId)) continue;
                SNode fromNode = net.Nodes[fromId];

                foreach (var connId in wp.ConnectedWaypointIds)
                {
                    if (!graph.Waypoints.ContainsKey(connId)) continue;
                    var toWp = graph.Waypoints[connId];
                    string toId = toWp.OSMNodeId.ToString();
                    if (!net.Nodes.ContainsKey(toId)) continue;

                    // Tránh tạo edge trùng
                    string edgeKey = $"{fromId}_{toId}";
                    if (createdEdges.ContainsKey(edgeKey)) continue;

                    SNode toNode = net.Nodes[toId];

                    // Tính thuộc tính từ road type
                    int numLanes = GetLaneCount(wp.RoadType);
                    float maxSpeed = GetMaxSpeed(wp.RoadType);
                    float roadWidth = GetRoadWidth(wp.RoadType);

                    // Tạo edge
                    SEdge edge = new SEdge
                    {
                        id = $"e_{edgeCounter++}",
                        fromNode = fromNode,
                        toNode = toNode
                    };

                    // Tính length + direction
                    Vector3 dir = toNode.position - fromNode.position;
                    float edgeLength = dir.magnitude;
                    if (edgeLength < 0.5f) continue; // Bỏ edge quá ngắn

                    edge.length = edgeLength;
                    edge.maxSpeed = maxSpeed;

                    // Vector vuông góc (sang phải) cho offset lanes
                    Vector3 forward = dir.normalized;
                    Vector3 right = new Vector3(forward.z, 0, -forward.x);

                    // Tạo lanes
                    for (int i = 0; i < numLanes; i++)
                    {
                        // Offset ngang: lane 0 bên phải ngoài cùng
                        float laneWidth = roadWidth / numLanes;
                        float offsetFromCenter = -roadWidth / 2f + laneWidth * (i + 0.5f);

                        Vector3 start = fromNode.position + right * offsetFromCenter;
                        Vector3 end = toNode.position + right * offsetFromCenter;

                        // Nâng nhẹ Y để khỏi xuyên mặt đường
                        start.y = 0.15f;
                        end.y = 0.15f;

                        SLane lane = new SLane
                        {
                            id = $"{edge.id}_{i}",
                            index = i,
                            length = edgeLength,
                            maxSpeed = maxSpeed,
                            parentEdge = edge,
                            shape = new List<Vector3> { start, end }
                        };

                        edge.lanes.Add(lane);
                    }

                    // Link node connections
                    fromNode.outgoing.Add(edge);
                    toNode.incoming.Add(edge);

                    net.Edges[edge.id] = edge;
                    createdEdges[edgeKey] = edge;
                }
            }

            // 3. Build successors (edge → edge tại junction)
            BuildSuccessors(net);

            Debug.Log($"[OSMToSumo] Converted: {net.Nodes.Count} junctions, {net.Edges.Count} edges " +
                $"({CountTotalLanes(net)} lanes).");

            return net;
        }

        // ══════════════════════════════════════════════════════════════════
        // SHARED HELPERS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Build successor relationships: nếu edge A kết thúc tại node X
        /// và edge B bắt đầu tại node X → A.successors.Add(B)
        /// </summary>
        private static void BuildSuccessors(SNetwork net)
        {
            foreach (var node in net.Nodes.Values)
            {
                foreach (var inEdge in node.incoming)
                {
                    foreach (var outEdge in node.outgoing)
                    {
                        // Không cho phép quay đầu (from node == to node)
                        if (inEdge.fromNode == outEdge.toNode) continue;

                        if (!inEdge.successors.Contains(outEdge))
                            inEdge.successors.Add(outEdge);
                    }
                }
            }
        }

        private static int CountTotalLanes(SNetwork net)
        {
            int count = 0;
            foreach (var e in net.Edges.Values) count += e.lanes.Count;
            return count;
        }

        // ══════════════════════════════════════════════════════════════════
        // ROAD TYPE PARAMS (khớp với OSM highway tags)
        // ══════════════════════════════════════════════════════════════════

        private static int GetLaneCount(string roadType)
        {
            switch (roadType)
            {
                case "motorway": return 3;
                case "trunk": return 2;
                case "primary": return 2;
                case "secondary": return 2;
                case "tertiary": return 1;
                case "residential": return 1;
                case "service": return 1;
                case "living_street": return 1;
                default: return 1;
            }
        }

        private static float GetMaxSpeed(string roadType)
        {
            switch (roadType)
            {
                case "motorway": return 30f;    // ~108 km/h
                case "trunk": return 22f;       // ~80 km/h
                case "primary": return 16.7f;   // ~60 km/h
                case "secondary": return 13.9f; // ~50 km/h
                case "tertiary": return 11.1f;  // ~40 km/h
                case "residential": return 8.3f;// ~30 km/h
                case "service": return 5.6f;    // ~20 km/h
                case "living_street": return 5.6f;
                default: return 11.1f;          // ~40 km/h
            }
        }

        private static float GetRoadWidth(string roadType)
        {
            switch (roadType)
            {
                case "motorway": return 12f;
                case "trunk": return 10f;
                case "primary": return 8f;
                case "secondary": return 7f;
                case "tertiary": return 6f;
                case "residential": return 5f;
                case "service": return 3f;
                case "living_street": return 4f;
                default: return 5f;
            }
        }
    }
}
