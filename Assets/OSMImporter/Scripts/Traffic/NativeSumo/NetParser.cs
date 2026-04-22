using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo.Graph;
using OSMImporter.Traffic.Sumo;

namespace OSMImporter.Traffic.NativeSumo
{
    /// <summary>
    /// Đọc dữ liệu từ đuôi file .net.xml để khởi tạo topology cho hệ thống Traffic Native.
    /// SUMO xml là chuẩn quốc tế, ta sẽ parse vài thẻ quan trọng.
    /// </summary>
    public class NetParser
    {
        /// <summary>
        /// Load mạng lưới SUMO (standalone, tọa độ SUMO gốc, không align OSM)
        /// </summary>
        public static SNetwork Load(string filePath)
        {
            return Load(filePath, null);
        }

        /// <summary>
        /// Load mạng lưới SUMO với mapper để chuyển tọa độ sang hệ Unity/OSM
        /// </summary>
        public static SNetwork Load(string filePath, SumoToUnityMapper mapper)
        {
            SNetwork net = new SNetwork();
            XmlDocument doc = new XmlDocument();
            doc.Load(filePath);

            // 1. Parse junctions -> SNode
            XmlNodeList junctionNodes = doc.SelectNodes("//junction");
            foreach (XmlNode jNode in junctionNodes)
            {
                string jType = jNode.Attributes["type"]?.Value;
                if (jType == "internal") continue;

                string id = jNode.Attributes["id"].Value;
                float x = float.Parse(jNode.Attributes["x"].Value, CultureInfo.InvariantCulture);
                float y = float.Parse(jNode.Attributes["y"].Value, CultureInfo.InvariantCulture);

                SNode node = new SNode
                {
                    id = id,
                    position = mapper != null
                        ? mapper.SumoToUnity(x, y)
                        : new Vector3(x, 0f, y)
                };

                net.Nodes.Add(id, node);
            }

            // 2. Parse edges & lanes (bỏ qua internal)
            XmlNodeList edgeNodes = doc.SelectNodes("//edge");
            foreach (XmlNode edgeNode in edgeNodes)
            {
                if (edgeNode.Attributes["function"] != null && edgeNode.Attributes["function"].Value == "internal")
                    continue;

                string edgeId = edgeNode.Attributes["id"].Value;
                SEdge newEdge = new SEdge { id = edgeId };

                // Link fromNode / toNode
                string fromId = edgeNode.Attributes["from"]?.Value;
                string toId = edgeNode.Attributes["to"]?.Value;
                if (fromId != null && net.Nodes.ContainsKey(fromId))
                {
                    newEdge.fromNode = net.Nodes[fromId];
                    newEdge.fromNode.outgoing.Add(newEdge);
                }
                if (toId != null && net.Nodes.ContainsKey(toId))
                {
                    newEdge.toNode = net.Nodes[toId];
                    newEdge.toNode.incoming.Add(newEdge);
                }

                // Parse lanes
                XmlNodeList laneNodes = edgeNode.SelectNodes("lane");
                foreach (XmlNode laneNode in laneNodes)
                {
                    SLane newLane = new SLane
                    {
                        id = laneNode.Attributes["id"].Value,
                        index = int.Parse(laneNode.Attributes["index"].Value),
                        length = float.Parse(laneNode.Attributes["length"].Value, CultureInfo.InvariantCulture),
                        maxSpeed = float.Parse(laneNode.Attributes["speed"].Value, CultureInfo.InvariantCulture),
                        parentEdge = newEdge
                    };

                    // Parse shape: "x1,y1 x2,y2 x3,y3" -> List<Vector3>
                    string shapeStr = laneNode.Attributes["shape"]?.Value;
                    if (!string.IsNullOrEmpty(shapeStr))
                    {
                        newLane.shape = ParseShape(shapeStr, mapper);
                    }

                    newEdge.lanes.Add(newLane);
                }

                // Tính edge length từ lane đầu tiên
                if (newEdge.lanes.Count > 0)
                {
                    newEdge.length = newEdge.lanes[0].length;
                    newEdge.maxSpeed = newEdge.lanes[0].maxSpeed;
                }

                net.Edges.Add(newEdge.id, newEdge);
            }

            // 3. Parse connections (edge-to-edge routing tại junction)
            XmlNodeList connNodes = doc.SelectNodes("//connection");
            foreach (XmlNode connNode in connNodes)
            {
                string fromEdgeId = connNode.Attributes["from"]?.Value;
                string toEdgeId = connNode.Attributes["to"]?.Value;

                if (fromEdgeId == null || toEdgeId == null) continue;
                if (fromEdgeId.StartsWith(":") || toEdgeId.StartsWith(":")) continue;

                if (net.Edges.ContainsKey(fromEdgeId) && net.Edges.ContainsKey(toEdgeId))
                {
                    SEdge fromEdge = net.Edges[fromEdgeId];
                    SEdge toEdge = net.Edges[toEdgeId];

                    if (!fromEdge.successors.Contains(toEdge))
                        fromEdge.successors.Add(toEdge);
                }
            }

            Debug.Log($"[SUMO Native] Parsed: {net.Nodes.Count} junctions, {net.Edges.Count} edges" +
                (mapper != null ? " (aligned to OSM)" : " (SUMO coords)"));
            return net;
        }

        /// <summary>
        /// Parse SUMO shape string "x1,y1 x2,y2 ..." thành List Vector3
        /// Nếu có mapper -> chuyển sang Unity coords, không thì dùng SUMO raw
        /// </summary>
        private static List<Vector3> ParseShape(string shapeStr, SumoToUnityMapper mapper)
        {
            List<Vector3> points = new List<Vector3>();
            string[] pairs = shapeStr.Split(' ');

            foreach (string pair in pairs)
            {
                string[] coords = pair.Split(',');
                if (coords.Length >= 2)
                {
                    float x = float.Parse(coords[0], CultureInfo.InvariantCulture);
                    float y = float.Parse(coords[1], CultureInfo.InvariantCulture);

                    if (mapper != null)
                        points.Add(mapper.SumoToUnity(x, y));
                    else
                        points.Add(new Vector3(x, 0f, y));
                }
            }

            return points;
        }
    }
}
