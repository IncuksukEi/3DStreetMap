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
    }

    public class TrafficLightManager : MonoBehaviour
    {
        public WaypointGraph Graph;
        public float GreenLightDuration = 12f;
        public float YellowLightDuration = 2f;

        private Dictionary<long, Intersection> _intersections = new Dictionary<long, Intersection>();

        private void Start()
        {
            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) return;

            InitIntersections();
        }

        private void InitIntersections()
        {
            _intersections.Clear();
            // Spawning visual lights
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            List<Vector3> spawnedPositions = new List<Vector3>();

            foreach (var wp in Graph.Waypoints.Values)
            {
                if (wp.IsTrafficLight || wp.ConnectedWaypointIds.Count > 2)
                {
                    // Lọc để không sinh trạm đèn đỏ sát nhau (cụm ngã tư OSM có nhiều node)
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

                    // Tìm các node trỏ tới ngã tư này (Incoming Nodes)
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
                    
                    // Giới hạn tối đa 4 hướng (tránh node OSM có quá nhiều connection)
                    if (intersection.IncomingNodeIds.Count > 4)
                        intersection.IncomingNodeIds.RemoveRange(4, intersection.IncomingNodeIds.Count - 4);
                    
                    // Spawn actual GameObject pillars
                    for (int i = 0; i < intersection.IncomingNodeIds.Count; i++)
                    {
                        long incomingId = intersection.IncomingNodeIds[i];
                        if (Graph.Waypoints.TryGetValue(incomingId, out Waypoint incomingWp))
                        {
                            // approachDir: hướng xe đi VÀO ngã tư
                            Vector3 approachDir = (intersection.Position - incomingWp.Position).normalized;
                            // rightSide: bên phải của hướng tiếp cận
                            Vector3 rightSide = new Vector3(approachDir.z, 0, -approachDir.x).normalized;
                            
                            float halfW = GetRoadHalfWidth(incomingWp.RoadType);
                            
                            // Đặt cột đèn ngoài mép đường — lùi xa và đẩy hẳn ra lề
                            Vector3 lightPos = intersection.Position
                                - approachDir * (halfW * 0.8f)   // lùi về phía incoming
                                + rightSide * (halfW + 1.5f);    // ra ngoài mép đường
                            
                            // Pillar
                            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                            pole.name = $"TrafficLight_{wp.OSMNodeId}_{i}";
                            pole.transform.position = lightPos + Vector3.up * 1.5f;
                            pole.transform.localScale = new Vector3(0.15f, 1.5f, 0.15f);
                            pole.transform.SetParent(this.transform);
                            
                            // Light sphere — đặt phía trên đỉnh cột
                            GameObject lightOrb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                            lightOrb.transform.position = lightPos + Vector3.up * 3.2f;
                            lightOrb.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                            lightOrb.transform.SetParent(pole.transform);
                            
                            Renderer r = lightOrb.GetComponent<Renderer>();
                            r.sharedMaterial = new Material(mat);
                            intersection.LightRenderers.Add(r);
                            
                            Destroy(pole.GetComponent<Collider>());
                            Destroy(lightOrb.GetComponent<Collider>());
                        }
                        else
                        {
                            intersection.LightRenderers.Add(null);
                        }
                    }

                    _intersections[wp.OSMNodeId] = intersection;
                }
            }
            Debug.Log($"[TrafficLightManager] Found {_intersections.Count} intersections.");
        }

        private void Update()
        {
            foreach (var intersection in _intersections.Values)
            {
                intersection.Timer += Time.deltaTime;
                float totalCycle = GreenLightDuration + YellowLightDuration;
                if (intersection.Timer >= totalCycle)
                {
                    intersection.Timer = 0f;
                    if (intersection.IncomingNodeIds.Count > 0)
                    {
                        intersection.CurrentGreenIndex = (intersection.CurrentGreenIndex + 1) % intersection.IncomingNodeIds.Count;
                    }
                }
                
                // Update visuals
                for (int i = 0; i < intersection.LightRenderers.Count; i++)
                {
                    Renderer r = intersection.LightRenderers[i];
                    if (r == null) continue;
                    
                    bool isGreen = (i == intersection.CurrentGreenIndex);
                    bool isYellow = isGreen && (intersection.Timer >= GreenLightDuration);
                    
                    Color c = isYellow ? Color.yellow : (isGreen ? Color.green : Color.red);
                    
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
        /// Trả về true nếu hướng từ fromNodeId tới intersectionNodeId đang là đèn Xanh.
        /// </summary>
        public bool IsGreenLight(long fromNodeId, long intersectionNodeId)
        {
            if (!_intersections.TryGetValue(intersectionNodeId, out Intersection intersection))
                return true; // Không phải ngã tư được đăng ký thì luôn xanh

            int index = intersection.IncomingNodeIds.IndexOf(fromNodeId);
            
            if (index < 0)
            {
                // fromId không nằm trong danh sách incoming → Mặc định ĐỎ (để an toàn)
                // Trừ khi không có hướng nào được đăng ký
                return intersection.IncomingNodeIds.Count == 0;
            }

            bool isGreenDirection = (index == intersection.CurrentGreenIndex);

            // Trong pha Vàng: hướng đang xanh bị chặn lại (chuẩn bị đỏ)
            if (intersection.Timer >= GreenLightDuration && isGreenDirection)
                return false;

            return isGreenDirection;
        }

        /// <summary>
        /// Tìm intersection gần nhất trong bán kính 40m của một vị trí.
        /// Dùng khi node đèn đỏ trên path không trùng với intersection được đăng ký.
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
            
            if (bestIntersection == null) return true; // Không tìm thấy → cho đi
            
            int index = bestIntersection.IncomingNodeIds.IndexOf(fromNodeId);
            if (index < 0)
            {
                // fromId không khớp chính xác → tìm hướng incoming gần nhất
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
                    
                    // Nếu tìm được hướng gần nhất và góc lệch < 60° thì dùng hướng đó
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
                        // Đồng bộ logic vị trí với InitIntersections
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
