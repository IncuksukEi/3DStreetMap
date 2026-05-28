using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter
{
    /// <summary>
    /// Singleton that stores all named OSM areas with their world-space centroid.
    /// Uses [SerializeField] so data generated in Edit mode persists into Play mode.
    /// </summary>
    public class OSMAreaRegistry : MonoBehaviour
    {
        public static OSMAreaRegistry Instance { get; private set; }

        [System.Serializable]
        public class AreaEntry
        {
            public string  Name;
            public Vector3 Centroid;
            public string  AreaType;
        }

        // Serialized backing list — persists through Enter Play Mode
        [SerializeField]
        private List<AreaEntry> _areas = new List<AreaEntry>();

        public List<AreaEntry> Areas => _areas;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Register(string name, Vector3 centroid, string areaType = "")
        {
            _areas.Add(new AreaEntry { Name = name, Centroid = centroid, AreaType = areaType });
        }

        public void Clear() => _areas.Clear();
    }
}
