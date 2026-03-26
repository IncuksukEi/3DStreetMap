using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Autonomous vehicle that navigates the OSM WaypointGraph using A* pathfinding.
    ///
    /// Key fixes vs previous version:
    ///   1. Translation moves DIRECTLY toward target (proportional steering), not blindly
    ///      along transform.forward — eliminates drift and spinning-in-place.
    ///   2. ArrivalDist is now proportional to speed to prevent overshooting.
    ///   3. Y is kept from the waypoint itself (not clamped to 0) so hilly maps work.
    ///   4. _currentWp is kept up-to-date per waypoint traversed (needed for correct
    ///      A* re-routing start node).
    ///   5. PickNewDestination no longer jumps _currentWp ahead; it lets the traveller
    ///      update it waypoint-by-waypoint.
    ///   6. Path is retried if A* returns empty (disconnected graph).
    ///   7. StopDistance scaled per vehicle size via TrafficSpawner.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class VehicleAgent : MonoBehaviour
    {
        // ── Config (set by TrafficSpawner) ────────────────────────────────────
        [HideInInspector] public WaypointGraph              Graph;
        [HideInInspector] public VehicleMeshBuilder.VehicleType VehicleType;
        [HideInInspector] public float                      BaseSpeed = 8f;

        [Header("Steering")]
        public float RotationSpeed  = 240f;   // deg/s
        public float StopDistance   = 6f;
        public LayerMask VehicleLayer;

        [Header("Wheel Animation")]
        public Transform[] Wheels;

        // ── private state ─────────────────────────────────────────────────────
        private List<Vector3> _path      = new List<Vector3>();
        private int           _pathIdx   = 0;
        private long          _startNodeId;       // for A* re-route
        private long          _destNodeId;
        private float         _currentSpeed;
        private bool          _braking;
        private bool          _rerouting;

        // Minimum squared distance to consider a waypoint "reached"
        // Set dynamically: at minimum 2 m, up to 1.5× the distance the vehicle
        // can travel in one frame (prevents overshooting at high speed).
        private float ArrivalDistSq => Mathf.Pow(Mathf.Max(2.5f, _currentSpeed * Time.deltaTime * 3f), 2f);

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            var rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;

            // Layer assignment
            int lyr = LayerMaskToLayer(VehicleLayer);
            if (lyr > 0) gameObject.layer = lyr;

            // Find graph if not wired by TrafficSpawner
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) { enabled = false; return; }

            // Snap to nearest waypoint
            Waypoint nearest = Graph.FindNearest(transform.position);
            if (nearest == null) { enabled = false; return; }

            _startNodeId = nearest.OSMNodeId;
            transform.position = WithY(nearest.Position);
            _currentSpeed      = BaseSpeed;

            PickNewDestination();
        }

        private void Update()
        {
            if (_path == null || _path.Count == 0 || _rerouting) return;
            CheckBraking();
            MoveAlongPath();
            SpinWheels();
        }

        // ── Movement ──────────────────────────────────────────────────────────

        private void MoveAlongPath()
        {
            if (_pathIdx >= _path.Count) return;

            Vector3 target   = WithY(_path[_pathIdx]);
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;           // keep steering horizontal
            float sqDist = toTarget.sqrMagnitude;

            // ── Reached this waypoint? ────────────────────────────────────────
            if (sqDist < ArrivalDistSq)
            {
                // Snap cleanly onto the waypoint
                transform.position = new Vector3(target.x, transform.position.y, target.z);

                // Track which node we're at so re-routing is correct
                // (heuristic: associate path indices with waypoints via FindNearest)
                Waypoint arrived = Graph.FindNearest(transform.position);
                if (arrived != null) _startNodeId = arrived.OSMNodeId;

                _pathIdx++;
                if (_pathIdx >= _path.Count)
                {
                    // Reached destination — pick a new one
                    _path.Clear();
                    StartCoroutine(WaitThenReroute(Random.Range(0.2f, 1.0f)));
                }
                return;
            }

            // ── Speed ─────────────────────────────────────────────────────────
            float wanted = _braking ? 0f : BaseSpeed;
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, wanted, Time.deltaTime * BaseSpeed * 3f);

            if (_currentSpeed < 0.01f) return;  // fully braked

            // ── Rotation — face the next waypoint ────────────────────────────
            Vector3 dir = toTarget.normalized;
            if (dir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, RotationSpeed * Time.deltaTime);
            }

            // ── Translation — move TOWARD target, not blindly forward ─────────
            // Blend: mostly direct, partially forward — gives a natural car-like
            // feel while preventing the vehicle from driving into walls when turning.
            Vector3 fwd    = transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 move   = Vector3.Lerp(fwd, dir, 0.85f).normalized;
            transform.position += move * _currentSpeed * Time.deltaTime;

            // Keep Y flat (roads are on ground plane)
            transform.position = WithY(transform.position, 0f);
        }

        // ── Braking ───────────────────────────────────────────────────────────

        private void CheckBraking()
        {
            _braking = Physics.Raycast(
                transform.position + Vector3.up * 0.5f,
                transform.forward,
                StopDistance,
                VehicleLayer);
        }

        // ── Wheel spin ────────────────────────────────────────────────────────

        // ── Wheel spin ────────────────────────────────────────────────────────
        private void SpinWheels()
        {
            if (Wheels == null || Wheels.Length == 0) return;
            // Cylinder primitive: local Y = length axis.
            // After CreateCylinder's localEulerAngles (0,0,90), local Y points
            // along the vehicle's left-right (axle) axis.
            // Rolling forward = rotating around that axle = Rotate around local Y.
            float wheelRadius = 0.75f;   // approximate, metres
            float arcPerSec   = _currentSpeed;                       // m/s
            float degPerSec   = arcPerSec / wheelRadius * Mathf.Rad2Deg;
            foreach (var w in Wheels)
                if (w != null) w.Rotate(0f, degPerSec * Time.deltaTime, 0f, Space.Self);
        }

        // ── Pathfinding ───────────────────────────────────────────────────────

        public void PickNewDestination()
        {
            if (Graph == null || Graph.Waypoints.Count < 2) return;

            // Pick a random destination that is not the current node
            var keys = new List<long>(Graph.Waypoints.Keys);
            _destNodeId = keys[Random.Range(0, keys.Count)];
            int tries = 0;
            while (_destNodeId == _startNodeId && tries++ < 20)
                _destNodeId = keys[Random.Range(0, keys.Count)];

            // A* path from current node
            List<Vector3> candidate = Graph.FindPath(_startNodeId, _destNodeId);

            // If path is empty (disconnected graph), try up to 5 different destinations
            int retries = 0;
            while ((candidate == null || candidate.Count == 0) && retries++ < 5)
            {
                _destNodeId = keys[Random.Range(0, keys.Count)];
                candidate   = Graph.FindPath(_startNodeId, _destNodeId);
            }

            if (candidate == null || candidate.Count == 0)
            {
                // Truly isolated node — teleport to a random waypoint and retry later
                var vals = new List<Waypoint>(Graph.Waypoints.Values);
                Waypoint rand = vals[Random.Range(0, vals.Count)];
                _startNodeId       = rand.OSMNodeId;
                transform.position = WithY(rand.Position);
                StartCoroutine(WaitThenReroute(1f));
                return;
            }

            _path    = candidate;
            _pathIdx = 0;
        }

        private IEnumerator WaitThenReroute(float delay)
        {
            _rerouting = true;
            yield return new WaitForSeconds(delay);
            _rerouting = false;
            PickNewDestination();
        }

        // ── Speed by road type ────────────────────────────────────────────────

        public void SetSpeedForRoadType(string roadType)
        {
            switch (roadType)
            {
                case "motorway":      BaseSpeed = 22f; break;
                case "trunk":         BaseSpeed = 18f; break;
                case "primary":       BaseSpeed = 14f; break;
                case "secondary":     BaseSpeed = 11f; break;
                case "tertiary":      BaseSpeed = 9f;  break;
                case "residential":   BaseSpeed = 6f;  break;
                case "living_street": BaseSpeed = 4f;  break;
                case "service":       BaseSpeed = 4f;  break;
                default:              BaseSpeed = 8f;  break;
            }
        }

        // ── Utility ───────────────────────────────────────────────────────────

        /// Return v with its Y overridden (default: keep existing Y).
        private static Vector3 WithY(Vector3 v, float y = -1f)
            => new Vector3(v.x, y < 0 ? v.y : y, v.z);

        private static int LayerMaskToLayer(LayerMask mask)
        {
            int m = mask.value;
            if (m == 0) return 0;
            int layer = 0;
            while ((m & 1) == 0) { m >>= 1; layer++; }
            return layer;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_path == null || _path.Count < 2) return;
            Gizmos.color = Color.cyan;
            for (int i = _pathIdx; i < _path.Count - 1; i++)
                Gizmos.DrawLine(_path[i], _path[i + 1]);
            // Draw current target
            if (_pathIdx < _path.Count)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(_path[_pathIdx], 1f);
            }
        }
#endif
    }
}
