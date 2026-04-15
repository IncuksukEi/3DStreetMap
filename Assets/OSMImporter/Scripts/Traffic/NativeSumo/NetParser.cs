using System.Xml;
using UnityEngine;
using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic.NativeSumo
{
    /// <summary>
    /// Đọc dữ liệu từ đuôi file .net.xml để khởi tạo topology cho hệ thống Traffic Native.
    /// SUMO xml là chuẩn quốc tế, ta sẽ parse vài thẻ quan trọng.
    /// </summary>
    public class NetParser
    {
        public static SNetwork Load(string filePath)
        {
            SNetwork net = new SNetwork();
            XmlDocument doc = new XmlDocument();
            doc.Load(filePath);

            // Parse edges & lanes
            XmlNodeList edgeNodes = doc.SelectNodes("//edge");
            foreach (XmlNode edgeNode in edgeNodes)
            {
                if (edgeNode.Attributes["function"] != null && edgeNode.Attributes["function"].Value == "internal")
                    continue; // Tạm bỏ qua đường giao lộ (internal) giai đoạn 1

                SEdge newEdge = new SEdge
                {
                    id = edgeNode.Attributes["id"].Value
                };
                
                XmlNodeList laneNodes = edgeNode.SelectNodes("lane");
                foreach (XmlNode laneNode in laneNodes)
                {
                    SLane newLane = new SLane
                    {
                        id = laneNode.Attributes["id"].Value,
                        index = int.Parse(laneNode.Attributes["index"].Value),
                        length = float.Parse(laneNode.Attributes["length"].Value),
                        maxSpeed = float.Parse(laneNode.Attributes["speed"].Value),
                        parentEdge = newEdge
                    };
                    
                    // TODO: Parse shape để ánh xạ sang Unity Vector3 (cách dấu phẩy và cách dòng)
                    
                    newEdge.lanes.Add(newLane);
                }
                
                net.Edges.Add(newEdge.id, newEdge);
            }

            return net;
        }
    }
}
