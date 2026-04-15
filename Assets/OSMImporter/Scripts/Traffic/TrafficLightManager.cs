using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    public class Intersection
    {
        public long NodeId;
        public Vector3 Position;
        public List<long> IncomingNodeIds = new List<long>();
        public int CurrentGreenIndex = 0;
        public float Timer = 0f;
        
        // --- Visuals ---
        public List<Renderer> LightRenderers = new List<Renderer>();
        // --- Triggers cho vehicle detection ---
        public List<TrafficLightTrigger> Triggers = new List<TrafficLightTrigger>();
    }

    public class TrafficLightManager : MonoBehaviour
    {
        public static TrafficLightManager Instance;

        public WaypointGraph Graph;
        public bool EnableTrafficLights = true;
        public float GreenLightDuration = 12f;
        public float YellowLightDuration = 2f;

        // Layer riêng cho cột đèn để xe query nhanh
        public static int TrafficLightLayer { get; private set; } = 0;
        public static LayerMask TrafficLightMask { get; private set; }

        private Dictionary<long, Intersection> _intersections = new Dictionary<long, Intersection>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) return;

            // Tìm hoặc fallback layer cho traffic light triggers
            TrafficLightLayer = FindOrFallbackLayer("OsmTrafficLight", 10);
            TrafficLightMask = 1 << TrafficLightLayer;

            InitIntersections();
        }

        private void InitIntersections()
        {
            _intersections.Clear();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            List<Vector3> spawnedPositions = new List<Vector3>();

            foreach (var wp in Graph.Waypoints.Values)
            {
                // Chỉ đặt đèn đỏ tại vị trí có tag traffic_signals trong OSM
                if (!wp.IsTrafficLight) continue;

                // Lọc để không sinh trạm đèn đỏ sát nhau
                bool tooClose = false;
                foreach (Vector3 p in spawnedPositions)
                {
                    if (Vector3.Distance(wp.Position, p) < 40f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                spawnedPositions.Add(wp.Position);

                List<long> incomingNodes = new List<long>();
                foreach (var otherWp in Graph.Waypoints.Values)
                {
                    if (otherWp.ConnectedWaypointIds.Contains(wp.OSMNodeId))
                        incomingNodes.Add(otherWp.OSMNodeId);
                }

                Intersection intersection = new Intersection
                {
                    NodeId = wp.OSMNodeId,
                    Position = wp.Position,
                    IncomingNodeIds = incomingNodes,
                    CurrentGreenIndex = incomingNodes.Count > 0 ? Random.Range(0, incomingNodes.Count) : 0
                };

                if (intersection.IncomingNodeIds.Count > 4)
                    intersection.IncomingNodeIds.RemoveRange(4, intersection.IncomingNodeIds.Count - 4);

                // Spawn cột đèn + Trigger cho từng hướng
                for (int i = 0; i < intersection.IncomingNodeIds.Count; i++)
                {
                    long incomingId = intersection.IncomingNodeIds[i];
                    if (Graph.Waypoints.TryGetValue(incomingId, out Waypoint incomingWp))
                    {
                        Vector3 approachDir = (intersection.Position - incomingWp.Position).normalized;
                        Vector3 rightSide = new Vector3(approachDir.z, 0, -approachDir.x).normalized;

                        float halfW = GetRoadHalfWidth(incomingWp.RoadType);

                        // Vị trí cột đèn ngoài mép đường
                        Vector3 lightPos = intersection.Position
                            - approachDir * (halfW * 0.8f)
                            + rightSide * (halfW + 1.5f);

                        // Pillar
                        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        pole.name = $"TrafficLight_{wp.OSMNodeId}_{i}";
                        pole.transform.position = lightPos + Vector3.up * 1.5f;
                        pole.transform.localScale = new Vector3(0.15f, 1.5f, 0.15f);
                        pole.transform.SetParent(this.transform);

                        // Light sphere
                        GameObject lightOrb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        lightOrb.transform.position = lightPos + Vector3.up * 3.2f;
                        lightOrb.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                        lightOrb.transform.SetParent(pole.transform);

                        Renderer r = lightOrb.GetComponent<Renderer>();
                        r.sharedMaterial = new Material(mat);
                        intersection.LightRenderers.Add(r);

                        Destroy(pole.GetComponent<Collider>());
                        Destroy(lightOrb.GetComponent<Collider>());

                        // ── Vùng dừng (Stop Zone) ──
                        Vector3 stopZonePos = intersection.Position - approachDir * 4f;

                        GameObject triggerGO = new GameObject($"StopZone_{wp.OSMNodeId}_{i}");
                        triggerGO.transform.position = stopZonePos;
                        triggerGO.transform.SetParent(pole.transform);
                        triggerGO.layer = TrafficLightLayer;

                        SphereCollider sc = triggerGO.AddComponent<SphereCollider>();
                        sc.isTrigger = true;
                        sc.radius = Mathf.Max(halfW + 1f, 4f);

                        TrafficLightTrigger trigger = triggerGO.AddComponent<TrafficLightTrigger>();
                        trigger.DirectionIndex = i;
                        trigger.IntersectionId = wp.OSMNodeId;
                        trigger.ApproachDir = approachDir;
                        trigger.State = 0;

                        intersection.Triggers.Add(trigger);
                    }
                    else
                    {
                        intersection.LightRenderers.Add(null);
                        intersection.Triggers.Add(null);
                    }
                }

                _intersections[wp.OSMNodeId] = intersection;
            }
            Debug.Log($"[TrafficLightManager] Found {_intersections.Count} intersections with trigger zones.");
        }

        private void Update()
        {
            float blinkTimer = Mathf.PingPong(Time.time, 0.5f) > 0.25f ? 1f : 0f;
            float allRedDuration = 2f; // Tất cả đỏ giữa các phase để xe thoát ngã tư

            foreach (var intersection in _intersections.Values)
            {
                if (!EnableTrafficLights)
                {
                    // Chế độ TẮT: Tất cả nháy Vàng, trigger luôn Xanh
                    for (int i = 0; i < intersection.LightRenderers.Count; i++)
                    {
                        if (i < intersection.Triggers.Count && intersection.Triggers[i] != null)
                            intersection.Triggers[i].State = 1;

                        Renderer r = intersection.LightRenderers[i];
                        if (r == null) continue;
                        
                        Color c = blinkTimer > 0f ? Color.yellow : Color.black;
                        if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", c);
                        else r.sharedMaterial.color = c;
                        
                        if (r.sharedMaterial.HasProperty("_EmissionColor")) 
                        {
                            if (blinkTimer > 0f) r.sharedMaterial.EnableKeyword("_EMISSION");
                            else r.sharedMaterial.DisableKeyword("_EMISSION");
                            r.sharedMaterial.SetColor("_EmissionColor", c * 2f);
                        }
                    }
                    continue;
                }

                // Tính số phase (nhóm 2 hướng đối diện thành 1 phase)
                int numDirections = intersection.IncomingNodeIds.Count;
                int numPhases = Mathf.Max(1, (numDirections + 1) / 2);
                float totalCycle = GreenLightDuration + YellowLightDuration + allRedDuration;

                intersection.Timer += Time.deltaTime;
                if (intersection.Timer >= totalCycle)
                {
                    intersection.Timer = 0f;
                    intersection.CurrentGreenIndex = (intersection.CurrentGreenIndex + 1) % numPhases;
                }

                // Xác định trạng thái trong cycle hiện tại
                bool isAllRedPhase = intersection.Timer >= (GreenLightDuration + YellowLightDuration);
                bool isYellowPhase = !isAllRedPhase && intersection.Timer >= GreenLightDuration;

                for (int i = 0; i < intersection.LightRenderers.Count; i++)
                {
                    // Paired: direction i và (i+numPhases) cùng 1 phase
                    int phaseOfDir = i % numPhases;
                    bool isGreenDir = (phaseOfDir == intersection.CurrentGreenIndex) && !isAllRedPhase;
                    bool isYellow = isGreenDir && isYellowPhase;
                    bool isGreen = isGreenDir && !isYellowPhase;

                    // Cập nhật trigger state
                    if (i < intersection.Triggers.Count && intersection.Triggers[i] != null)
                    {
                        if (isAllRedPhase) intersection.Triggers[i].State = 0;      // All-red
                        else if (isYellow) intersection.Triggers[i].State = 2;       // Vàng
                        else if (isGreen)  intersection.Triggers[i].State = 1;       // Xanh
                        else               intersection.Triggers[i].State = 0;       // Đỏ
                    }

                    // Cập nhật visual
                    Renderer r = intersection.LightRenderers[i];
                    if (r == null) continue;
                    
                    Color c;
                    if (isAllRedPhase) c = Color.red;
                    else if (isYellow) c = Color.yellow;
                    else if (isGreen)  c = Color.green;
                    else               c = Color.red;
                    
                    if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", c);
                    else r.sharedMaterial.color = c;
                    
                    if (r.sharedMaterial.HasProperty("_EmissionColor")) 
                    {
                        r.sharedMaterial.EnableKeyword("_EMISSION");
                        r.sharedMaterial.SetColor("_EmissionColor", c * 2f);
                    }
                }
            }
        }
        
        private float GetRoadHalfWidth(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 2.5f;
            switch (roadType)
            {
                case "motorway": return 6f;
                case "trunk": return 5f;
                case "primary": return 4f;
                case "secondary": return 3.5f;
                case "tertiary": return 3f;
                case "residential": return 2.5f;
                case "service": return 1.5f;
                case "living_street": return 2f;
                default: return 2.5f;
            }
        }

        /// <summary>
        /// [Legacy] Giữ lại cho backward compatibility. Xe mới nên dùng Physics query.
        /// </summary>
        public bool IsGreenLight(long fromNodeId, long intersectionNodeId)
        {
            if (!_intersections.TryGetValue(intersectionNodeId, out Intersection intersection))
                return true;

            int index = intersection.IncomingNodeIds.IndexOf(fromNodeId);
            
            if (index < 0)
                return intersection.IncomingNodeIds.Count == 0;

            // Paired phase: direction i → phase (i % numPhases)
            int numPhases = Mathf.Max(1, (intersection.IncomingNodeIds.Count + 1) / 2);
            int phaseOfDir = index % numPhases;
            bool isGreenDirection = (phaseOfDir == intersection.CurrentGreenIndex);
            
            // All-red clearance check
            float allRedStart = GreenLightDuration + YellowLightDuration;
            if (intersection.Timer >= allRedStart) return false;
            
            // Yellow phase check
            if (intersection.Timer >= GreenLightDuration && isGreenDirection) return false;

            return isGreenDirection;
        }

        /// <summary>
        /// [Legacy] Giữ lại cho backward compatibility.
        /// </summary>
        public bool IsGreenLightByPosition(Vector3 lightPos, long fromNodeId)
        {
            float bestDist = 40f;
            Intersection bestIntersection = null;
            
            foreach (var inter in _intersections.Values)
            {
                float d = Vector3.Distance(inter.Position, lightPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIntersection = inter;
                }
            }
            
            if (bestIntersection == null) return true;
            
            int index = bestIntersection.IncomingNodeIds.IndexOf(fromNodeId);
            if (index < 0)
            {
                if (Graph != null && bestIntersection.IncomingNodeIds.Count > 0)
                {
                    float bestAngle = float.MaxValue;
                    int bestIdx = -1;
                    
                    Vector3 myApproach = Vector3.zero;
                    if (Graph.Waypoints.TryGetValue(fromNodeId, out Waypoint myFromWp))
                    {
                        myApproach = (bestIntersection.Position - myFromWp.Position).normalized;
                    }
                    else
                    {
                        return bestIntersection.IncomingNodeIds.Count == 0;
                    }
                    
                    for (int j = 0; j < bestIntersection.IncomingNodeIds.Count; j++)
                    {
                        if (Graph.Waypoints.TryGetValue(bestIntersection.IncomingNodeIds[j], out Waypoint inWp))
                        {
                            Vector3 approachDir = (bestIntersection.Position - inWp.Position).normalized;
                            float angle = Vector3.Angle(myApproach, approachDir);
                            if (angle < bestAngle)
                            {
                                bestAngle = angle;
                                bestIdx = j;
                            }
                        }
                    }
                    
                    if (bestIdx >= 0 && bestAngle < 60f)
                        index = bestIdx;
                    else
                        return bestIntersection.IncomingNodeIds.Count == 0;
                }
                else
                {
                    return bestIntersection.IncomingNodeIds.Count == 0;
                }
            }
            
            bool isGreen = (index == bestIntersection.CurrentGreenIndex);
            if (bestIntersection.Timer >= GreenLightDuration && isGreen)
                return false;
            return isGreen;
        }

        private static int FindOrFallbackLayer(string name, int fallback)
        {
            for (int i = 8; i <= 31; i++)
                if (LayerMask.LayerToName(i) == name) return i;
            return fallback;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (_intersections == null || _intersections.Count == 0 || Graph == null) return;

            foreach (var intersection in _intersections.Values)
            {
                for (int i = 0; i < intersection.IncomingNodeIds.Count; i++)
                {
                    long incomingId = intersection.IncomingNodeIds[i];
                    if (Graph.Waypoints.TryGetValue(incomingId, out Waypoint incomingWp))
                    {
                        Vector3 approachDir = (intersection.Position - incomingWp.Position).normalized;
                        Vector3 rightSide = new Vector3(approachDir.z, 0, -approachDir.x).normalized;
                        float halfW = GetRoadHalfWidth(incomingWp.RoadType);
                        Vector3 lightPos = intersection.Position
                            - approachDir * (halfW * 0.8f)
                            + rightSide * (halfW + 1.5f);

                        bool isGreen = (i == intersection.CurrentGreenIndex);
                        bool isYellow = isGreen && (intersection.Timer >= GreenLightDuration);

                        Gizmos.color = isYellow ? Color.yellow : (isGreen ? Color.green : Color.red);
                        Gizmos.DrawSphere(lightPos + Vector3.up * 3.2f, 0.6f);
                    }
                }
            }
        }
#endif
    }
}
