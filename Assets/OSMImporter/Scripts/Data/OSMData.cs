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
    }
}
