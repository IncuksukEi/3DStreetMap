using System.Xml;
using UnityEngine;

namespace OSMImporter.Data
{
    public static class OSMParser
    {
        public static OSMMapData Parse(string filePath)
        {
            var mapData = new OSMMapData();
            XmlDocument doc = new XmlDocument();
            doc.Load(filePath);

            XmlNodeList boundsNodes = doc.GetElementsByTagName("bounds");
            if (boundsNodes.Count > 0)
            {
                XmlNode b = boundsNodes[0];
                mapData.Bounds.MinLat = GetDouble(b, "minlat");
                mapData.Bounds.MaxLat = GetDouble(b, "maxlat");
                mapData.Bounds.MinLon = GetDouble(b, "minlon");
                mapData.Bounds.MaxLon = GetDouble(b, "maxlon");
            }

            foreach (XmlNode xmlNode in doc.GetElementsByTagName("node"))
            {
                OSMNode node = new OSMNode
                {
                    Id = GetLong(xmlNode, "id"),
                    Latitude = GetDouble(xmlNode, "lat"),
                    Longitude = GetDouble(xmlNode, "lon")
                };
                foreach (XmlNode child in xmlNode.ChildNodes)
                {
                    if (child.Name == "tag")
                    {
                        string k = GetString(child, "k");
                        string v = GetString(child, "v");
                        if (!string.IsNullOrEmpty(k)) node.Tags[k] = v;
                    }
                }
                mapData.Nodes[node.Id] = node;
            }

            foreach (XmlNode xmlWay in doc.GetElementsByTagName("way"))
            {
                OSMWay way = new OSMWay { Id = GetLong(xmlWay, "id") };
                foreach (XmlNode child in xmlWay.ChildNodes)
                {
                    if (child.Name == "nd") way.NodeRefs.Add(GetLong(child, "ref"));
                    else if (child.Name == "tag")
                    {
                        string k = GetString(child, "k");
                        string v = GetString(child, "v");
                        if (!string.IsNullOrEmpty(k)) way.Tags[k] = v;
                    }
                }
                mapData.Ways.Add(way);
            }
            return mapData;
        }

        private static double GetDouble(XmlNode node, string attribute)
        {
            string val = node.Attributes?[attribute]?.Value;
            if (string.IsNullOrEmpty(val)) return 0;
            if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result)) return result;
            return 0;
        }
        private static long GetLong(XmlNode node, string attribute)
        {
            string val = node.Attributes?[attribute]?.Value;
            if (string.IsNullOrEmpty(val)) return 0;
            if (long.TryParse(val, out long result)) return result;
            return 0;
        }
        private static string GetString(XmlNode node, string attribute) => node.Attributes?[attribute]?.Value ?? "";
    }
}
