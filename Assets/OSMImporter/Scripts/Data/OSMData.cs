using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Data
{
    [System.Serializable]
    public class OSMNode
    {
        public long Id;
        public double Latitude;
        public double Longitude;
        public Vector3 WorldPosition;
        public Dictionary<string, string> Tags = new Dictionary<string, string>();

        public bool IsTrafficSignal => Tags.TryGetValue("highway", out string val) && val == "traffic_signals";
        public bool IsStopSign => Tags.TryGetValue("highway", out string val) && val == "stop";
    }

    [System.Serializable]
    public class OSMWay
    {
        public long Id;
        public List<long> NodeRefs = new List<long>();
        public Dictionary<string, string> Tags = new Dictionary<string, string>();

        public bool IsClosed => NodeRefs.Count > 2 && NodeRefs[0] == NodeRefs[NodeRefs.Count - 1];
        public bool IsHighway => Tags.ContainsKey("highway");
        public bool IsBuilding => Tags.ContainsKey("building");
        public bool IsWater => Tags.ContainsKey("water") || (Tags.TryGetValue("natural", out string n) && n == "water") || (Tags.TryGetValue("waterway", out string w1) && (w1 == "riverbank" || w1 == "dock"));
        public bool IsRiver => Tags.TryGetValue("waterway", out string w) && (w == "river" || w == "stream" || w == "canal" || w == "drain");
        public bool IsOneWay => Tags.TryGetValue("oneway", out string val) && (val == "yes" || val == "true" || val == "1");
        public bool IsReverseOneWay => Tags.TryGetValue("oneway", out string val) && val == "-1";

        public string HighwayType
        {
            get
            {
                string val;
                Tags.TryGetValue("highway", out val);
                return val ?? "";
            }
        }
    }

    [System.Serializable]
    public class OSMBounds
    {
        public double MinLat;
        public double MaxLat;
        public double MinLon;
        public double MaxLon;

        public double CenterLat => (MinLat + MaxLat) / 2.0;
        public double CenterLon => (MinLon + MaxLon) / 2.0;
    }

    public class OSMMapData
    {
        public OSMBounds Bounds = new OSMBounds();
        public Dictionary<long, OSMNode> Nodes = new Dictionary<long, OSMNode>();
        public List<OSMWay> Ways = new List<OSMWay>();

        public List<OSMWay> GetHighways()
        {
            var highways = new List<OSMWay>();
            foreach (var way in Ways) if (way.IsHighway) highways.Add(way);
            return highways;
        }

        public List<OSMWay> GetBuildings()
        {
            var buildings = new List<OSMWay>();
            foreach (var way in Ways) if (way.IsBuilding) buildings.Add(way);
            return buildings;
        }

        public List<OSMWay> GetWaterBodies()
        {
            var water = new List<OSMWay>();
            foreach (var way in Ways) if (way.IsWater) water.Add(way);
            return water;
        }

        public List<OSMWay> GetRivers()
        {
            var rivers = new List<OSMWay>();
            foreach (var way in Ways) if (way.IsRiver) rivers.Add(way);
            return rivers;
        }

        public List<OSMNode> GetDecorations()
        {
            var decos = new List<OSMNode>();
            foreach (var node in Nodes.Values) if (node.IsTrafficSignal || node.IsStopSign) decos.Add(node);
            return decos;
        }
    }
}
