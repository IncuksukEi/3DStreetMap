using UnityEngine;
using System.Collections;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Sets up a hardcoded scenario where a pedestrian crosses the road at a waypoint.
    /// </summary>
    public class PedestrianScenario : MonoBehaviour
    {
        public float CrossSpeed = 1.3f;
        public Color PedestrianColor = Color.magenta;

        private GameObject _pedestrianInstance;
        private WaypointGraph _graph;

        private void Start()
        {
            StartCoroutine(SetupScenarioRoutine());
        }

        private IEnumerator SetupScenarioRoutine()
        {
            // Wait for WaypointGraph to initialize
            _graph = FindFirstObjectByType<WaypointGraph>();
            while (_graph == null || _graph.Waypoints.Count == 0)
            {
                yield return new WaitForSeconds(0.5f);
                _graph = FindFirstObjectByType<WaypointGraph>();
            }

            // Find a road segment waypoint (not junction, not traffic light)
            Waypoint targetWp = null;
            foreach (var wp in _graph.Waypoints.Values)
            {
                // We want a waypoint on a normal road (2 connections), not a junction (3+ connections)
                // and not already a traffic light.
                if (wp.ConnectedWaypointIds.Count == 2 && !wp.IsTrafficLight)
                {
                    targetWp = wp;
                    break;
                }
            }

            if (targetWp == null)
            {
                // Fallback to any waypoint
                foreach (var wp in _graph.Waypoints.Values)
                {
                    targetWp = wp;
                    break;
                }
            }

            if (targetWp != null)
            {
                // Determine road direction
                Vector3 roadDir = Vector3.forward;
                if (targetWp.ConnectedWaypointIds.Count > 0)
                {
                    long nextId = targetWp.ConnectedWaypointIds[0];
                    if (_graph.Waypoints.TryGetValue(nextId, out Waypoint nextWp))
                    {
                        roadDir = (nextWp.Position - targetWp.Position).normalized;
                    }
                }

                // Perpendicular vector for crossing direction
                Vector3 crossingDir = new Vector3(-roadDir.z, 0f, roadDir.x).normalized;

                // Define crossing start & end positions across the road (approx road width ~6m)
                float roadHalfWidth = 3.5f;
                Vector3 startPos = targetWp.Position - crossingDir * roadHalfWidth;
                Vector3 endPos = targetWp.Position + crossingDir * roadHalfWidth;

                startPos.y = targetWp.Position.y;
                endPos.y = targetWp.Position.y;

                // Instantiate pedestrian
                _pedestrianInstance = PedestrianAgent.CreatePedestrianObject(PedestrianColor);
                _pedestrianInstance.transform.SetParent(transform, false);

                var agent = _pedestrianInstance.GetComponent<PedestrianAgent>();
                agent.StartCrossing(startPos, endPos, CrossSpeed);

                Debug.Log($"[PedestrianScenario] Initialized pedestrian at {targetWp.Position} crossing path: {startPos} <-> {endPos}");
            }
            else
            {
                Debug.LogWarning("[PedestrianScenario] No suitable Waypoint found to initialize scenario.");
            }
        }
    }
}
