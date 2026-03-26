using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Spawns and manages a pool of autonomous vehicles on OSM waypoints.
    /// - Spawns Cars, Motorbikes, and Buses with configurable counts
    /// - Assigns each vehicle to a random waypoint on the graph
    /// - Manages a "vehicle" physics layer for braking detection
    /// - Shows a live Stats overlay in Game view
    /// </summary>
    public class TrafficSpawner : MonoBehaviour
    {
        [Header("Counts")]
        public int CarCount       = 10;
        public int MotoCount      = 12;
        public int BusCount       =  2;

        [Header("Speed Multiplier")]
        [Range(0.1f, 5f)]
        public float SpeedScale   = 1f;

        [Header("Colors — randomised per vehicle")]
        public Color[] CarColors   = {
            new Color(0.0f, 0.5f, 1.0f),   // bright blue
            new Color(1.0f, 0.15f, 0.1f),  // vivid red
            new Color(0.1f, 0.85f, 0.2f),  // lime green
            new Color(1.0f, 0.7f, 0.0f),   // amber
            new Color(0.9f, 0.9f, 0.9f),   // white
            new Color(0.7f, 0.0f, 0.9f),   // purple
            new Color(0.0f, 0.85f, 0.85f), // cyan
        };
        public Color[] MotoColors  = {
            new Color(1.0f, 0.4f, 0.0f),   // orange
            new Color(1.0f, 0.9f, 0.0f),   // yellow
            new Color(0.8f, 0.0f, 0.4f),   // pink
            new Color(0.5f, 1.0f, 0.5f),   // mint
        };
        public Color BusColor      = new Color(1.0f, 0.85f, 0.0f); // golden yellow

        [Header("References")]
        public WaypointGraph Graph;

        // ── private ───────────────────────────────────────────────────────────
        private readonly List<VehicleAgent> _agents = new List<VehicleAgent>();
        private int _vehicleLayer;

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0)
            {
                Debug.LogWarning("[TrafficSpawner] No WaypointGraph found — generate the OSM map first.");
                enabled = false;
                return;
            }

            // Ensure a layer named "OsmVehicle" exists (Unity allows up to user layer 31)
            _vehicleLayer = EnsureLayer("OsmVehicle");
            Physics.IgnoreLayerCollision(_vehicleLayer, _vehicleLayer, true);

            SpawnAll();
        }

        private void OnDestroy()
        {
            foreach (var a in _agents) if (a) Destroy(a.gameObject);
            _agents.Clear();
        }

        // ── Spawn ─────────────────────────────────────────────────────────────

        private void SpawnAll()
        {
            Spawn(CarCount,   VehicleMeshBuilder.VehicleType.Car,       CarColors);
            Spawn(MotoCount,  VehicleMeshBuilder.VehicleType.Motorbike, MotoColors);
            Spawn(BusCount,   VehicleMeshBuilder.VehicleType.Bus,       new[] { BusColor });
            Debug.Log($"[TrafficSpawner] Spawned {_agents.Count} vehicles.");
        }

        private void Spawn(int count, VehicleMeshBuilder.VehicleType type, Color[] palette)
        {
            var waypointList = new List<Waypoint>(Graph.Waypoints.Values);
            if (waypointList.Count == 0) return;

            for (int i = 0; i < count; i++)
            {
                // Pick a random waypoint that's far enough from existing vehicles
                Waypoint wp = PickSpawnWaypoint(waypointList);
                Color color = palette[Random.Range(0, palette.Length)];

                GameObject vehicleGO = VehicleMeshBuilder.Build(type, color);
                vehicleGO.transform.SetParent(transform, false);
                vehicleGO.transform.position  = wp.Position;
                vehicleGO.transform.rotation  = Quaternion.Euler(0, Random.Range(0, 360f), 0);
                vehicleGO.layer = _vehicleLayer;
                foreach (Transform c in vehicleGO.GetComponentsInChildren<Transform>())
                    c.gameObject.layer = _vehicleLayer;

                // BoxCollider sized to new vehicle dimensions for braking raycast
                var col    = vehicleGO.AddComponent<BoxCollider>();
                float vLen = GetLength(type);
                col.size   = new Vector3(2f, 1.5f, vLen);
                col.center = new Vector3(0, 0.75f, 0);

                var agent = vehicleGO.AddComponent<VehicleAgent>();
                agent.Graph        = Graph;
                agent.VehicleType  = type;
                agent.BaseSpeed    = GetBaseSpeed(type) * SpeedScale;
                agent.VehicleLayer = 1 << _vehicleLayer;
                agent.StopDistance = vLen * 1.5f;   // brake distance proportional to vehicle length
                agent.Wheels       = FindWheels(vehicleGO.transform);

                _agents.Add(agent);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Waypoint PickSpawnWaypoint(List<Waypoint> list)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var wp = list[Random.Range(0, list.Count)];
                bool tooClose = false;
                foreach (var a in _agents)
                {
                    if (a != null && Vector3.Distance(a.transform.position, wp.Position) < 20f)
                    { tooClose = true; break; }
                }
                if (!tooClose) return wp;
            }
            return list[Random.Range(0, list.Count)];
        }

        private static Transform[] FindWheels(Transform root)
        {
            var list = new List<Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>())
                if (t.name.StartsWith("Wheel")) list.Add(t);
            return list.ToArray();
        }

        private static float GetBaseSpeed(VehicleMeshBuilder.VehicleType t)
        {
            if (t == VehicleMeshBuilder.VehicleType.Bus)       return 6f;
            if (t == VehicleMeshBuilder.VehicleType.Motorbike) return 12f;
            return 9f;
        }

        private static float GetLength(VehicleMeshBuilder.VehicleType t)
        {
            if (t == VehicleMeshBuilder.VehicleType.Bus)       return 14f;
            if (t == VehicleMeshBuilder.VehicleType.Motorbike) return 4f;
            return 7f;
        }

        // ── Layer helper (editor only — at runtime layers are read-only) ──────

        private static int EnsureLayer(string name)
        {
            // Try to find existing layer
            for (int i = 8; i <= 31; i++)
                if (LayerMask.LayerToName(i) == name) return i;
            // Fallback to layer 9 if we can't create layers at runtime
            Debug.LogWarning($"[TrafficSpawner] Layer '{name}' not found. Using layer 9. " +
                             "Add it manually in Project Settings → Tags & Layers.");
            return 9;
        }

        // ── Stats overlay ─────────────────────────────────────────────────────

        // Cached to avoid GC allocation every frame
        private GUIStyle _titleStyle;
        private GUIStyle _statStyle;

        private void OnGUI()
        {
            // Lazy init — GUISkin is only valid inside OnGUI
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize  = 13,
                    normal    = { textColor = new Color(0.4f, 0.9f, 1f) }
                };
                _statStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    normal   = { textColor = Color.white }
                };
            }

            int active = 0;
            foreach (var a in _agents) if (a && a.enabled) active++;
            int wps = Graph != null ? Graph.Waypoints.Count : 0;

            const float panW = 220f, panH = 74f, pad = 10f;
            GUI.color = new Color(0.05f, 0.05f, 0.05f, 0.78f);
            GUI.DrawTexture(new Rect(pad, pad, panW, panH), Texture2D.whiteTexture, ScaleMode.StretchToFill);
            GUI.color = Color.white;

            GUI.Label(new Rect(pad + 8, pad +  5, panW - 16, 22), "🗺  OSM Traffic", _titleStyle);
            GUI.Label(new Rect(pad + 8, pad + 26, panW - 16, 18), $"🚗  Vehicles: {active} / {_agents.Count}", _statStyle);
            GUI.Label(new Rect(pad + 8, pad + 44, panW - 16, 18), $"⏩  Speed ×{SpeedScale:F1}   🧭  WPs: {wps}", _statStyle);
        }
    }
}
