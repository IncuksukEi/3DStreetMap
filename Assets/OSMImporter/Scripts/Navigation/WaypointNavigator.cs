using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Navigation
{
    public class WaypointNavigator : MonoBehaviour
    {
        public WaypointGraph Graph;
        public float MoveSpeed = 8f;
        public float RotationSpeed = 120f;
        public float ArrivalThreshold = 1.5f;
        public bool UsePathfinding = true;
        public bool AutoLoop = true;
        public bool ShowPathGizmos = true;
        public Color PathColor = Color.green;

        private List<Waypoint> _currentPath = new List<Waypoint>();
        private int _currentPathIndex = 0;
        private Waypoint _currentWaypoint;
        private bool _isMoving = false;

        private void Start()
        {
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) return;

            _currentWaypoint = Graph.FindNearest(transform.position);
            if (_currentWaypoint != null)
            {
                transform.position = _currentWaypoint.Position;
                PickNewDestination();
            }
        }

        private void Update()
        {
            if (!_isMoving || _currentPath.Count == 0) return;

            Vector3 target = _currentPath[_currentPathIndex].Position;
            Vector3 direction = target - transform.position; direction.y = 0;
            float distance = direction.magnitude;

            if (distance < ArrivalThreshold)
            {
                // Update current waypoint as we traverse path
                _currentWaypoint = _currentPath[_currentPathIndex];
                if (++_currentPathIndex >= _currentPath.Count)
                {
                    _isMoving = false;
                    if (AutoLoop) PickNewDestination();
                }
            }
            else
            {
                Vector3 moveDir = direction.normalized;
                transform.position += moveDir * Mathf.Min(MoveSpeed, distance / Time.deltaTime) * Time.deltaTime;
                if (moveDir.sqrMagnitude > 0.001f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(moveDir, Vector3.up), RotationSpeed * Time.deltaTime);
            }
        }

        public void NavigateTo(long targetWaypointId)
        {
            if (Graph == null || _currentWaypoint == null) return;
            _currentPath = Graph.FindPath(_currentWaypoint.OSMNodeId, targetWaypointId);
            if (_currentPath.Count > 0) { _currentPathIndex = 0; _isMoving = true; }
        }

        public void PickNewDestination()
        {
            if (Graph == null || Graph.Waypoints.Count == 0) return;
            var waypointList = new List<long>(Graph.Waypoints.Keys);
            long targetId = waypointList[Random.Range(0, waypointList.Count)];
            int attempts = 0;
            while (targetId == _currentWaypoint.OSMNodeId && attempts++ < 10) targetId = waypointList[Random.Range(0, waypointList.Count)];

            if (UsePathfinding) NavigateTo(targetId);
            else RandomWalk();
        }

        private void RandomWalk()
        {
            if (_currentWaypoint == null || _currentWaypoint.ConnectedWaypointIds.Count == 0) return;
            long nextId = _currentWaypoint.ConnectedWaypointIds[Random.Range(0, _currentWaypoint.ConnectedWaypointIds.Count)];
            if (Graph.Waypoints.TryGetValue(nextId, out Waypoint nextWp))
            {
                _currentPath.Clear(); _currentPath.Add(nextWp);
                _currentPathIndex = 0; _currentWaypoint = nextWp; _isMoving = true;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!ShowPathGizmos || _currentPath == null || _currentPath.Count == 0) return;
            Gizmos.color = PathColor;
            for (int i = _currentPathIndex; i < _currentPath.Count - 1; i++) { Gizmos.DrawLine(_currentPath[i].Position, _currentPath[i + 1].Position); Gizmos.DrawSphere(_currentPath[i].Position, 0.3f); }
            if (_currentPath.Count > 0) Gizmos.DrawSphere(_currentPath[_currentPath.Count - 1].Position, 0.5f);
        }
#endif
    }
}
