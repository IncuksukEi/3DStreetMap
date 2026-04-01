using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// V2: Chạy tự do trên mặt phẳng NavMesh, tự lách, không văng ra cỏ.
    /// Có tương thích với trạm đèn đỏ, nhường đường mặc định của Unity.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class VehicleAgent : MonoBehaviour
    {
        [HideInInspector] public WaypointGraph              Graph;
        [HideInInspector] public VehicleMeshBuilder.VehicleType VehicleType;
        [HideInInspector] public float                      BaseSpeed = 8f;
        [HideInInspector] public float                      RuntimeSpeedScale = 1f;
        [HideInInspector] public float                      VehicleWidth;
        [HideInInspector] public bool                       DestroyOnArrival;
        [HideInInspector] public int                        LaneIndex;
        [HideInInspector] public int                        PreferredLane;
        [HideInInspector] public float                      Patience = 1.5f; // Hết kiên nhẫn nhanh hơn để gỡ kẹt

        [Header("Steering & Behaviors")]
        public float RotationSpeed  = 240f;
        public float StopDistance   = 6f;
        public LayerMask VehicleLayer;
        
        [Header("Advanced Parameters")]
        public float MaxSpeedLimit = 15f;
        public float MinFollowDistance = 1.0f; // Bám đuôi sát hơn
        public float SafeReactionTime = 0.35f; // Phản xạ nhanh, giảm khoảng cách an toàn ảo
        public float EmergencyBrakePwr = 5f;

        [Header("Wheel Animation")]
        public Transform[] Wheels;

        // ── Private — navigation ──────────────────────────────────────────────
        private TrafficLightManager _tlm;
        private List<Waypoint> _path  = new List<Waypoint>();
        private int            _pathIdx = 0;
        
        public class PathPoint {
            public Vector3 Position;
            public Waypoint WaypointRef;
        }
        private List<PathPoint> _exactPath = new List<PathPoint>();
        private int             _exactPathIdx = 0;

        private long           _startNodeId;
        private long           _destNodeId;
        private float          _currentSpeed;
        private bool           _rerouting;

        // ── Private — driving behavior ────────────────────────────────────────
        private float   _desiredSpeed;
        private bool    _braking;
        private bool    _emergencyBraking;
        private bool    _isWaitingAtRedLight;
        private float   _reversingTimer;

        // Đèn đỏ: khoảng cách tới vạch dừng (âm = không có đèn đỏ phía trước)
        private float   _redLightStopDist = -1f;

        // Scan ahead results (cached per frame)
        private VehicleAgent _aheadVehicle;
        private float        _aheadDistance;

        // Overtaking
        private float _overtakeOffset;        // hiện tại (lerp mượt)
        private float _targetOvertakeOffset;  // mục tiêu
        private bool  _isOvertaking;
        private float _overtakeCooldown;
        private VehicleAgent _overtakingTarget;

        // Lane offset mặc định (bám lề phải)
        private float _laneOffset;

        // Deadlock / anti-stuck
        private float _stuckTimer;
        private int   _deadlockLevel;  // 0=chờ, 1=nudge, 2=reroute, 3=teleport
        private float _stuckCheckSpeed = 0.5f;

        // ── Constants ─────────────────────────────────────────────────────────
        private const float SAFE_FOLLOW_TIME  = 0.6f;   // giây — giảm để xe xếp sát nhau hơn
        private const float MIN_FOLLOW_DIST   = 2f;
        private const float OVERTAKE_SPEED_RATIO = 0.85f; // vượt nếu xe trước chậm hơn 15% (dễ trigger hơn)
        private const float OVERTAKE_RANGE    = 10f;
        private const float OVERTAKE_CLEAR    = 10f;     // giảm để trả lane nhanh hơn
        private const float SCAN_RANGE        = 15f;     // giảm từ 25 → 15 để tránh cascade braking
        private const float INTERSECTION_YIELD_RANGE = 8f; // giảm từ 15 → 8 để bớt nhường vô lý
        private const float CURVE_SLOWDOWN_ANGLE = 30f;  // bắt đầu giảm tốc từ 30°

        // Arrival distance
        private float ArrivalDistSq => Mathf.Pow(Mathf.Max(1.0f, _currentSpeed * Time.deltaTime * 1.5f), 2f);

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (!TryGetComponent<Rigidbody>(out var rb))
                rb = gameObject.AddComponent<Rigidbody>();
                
            rb.isKinematic = true;
            rb.useGravity  = false;

            // Tắt tính năng tự động ghi đè Transform của NavMeshAgent 
            // vì code hiện tại đang tự tính toán và Translate vị trí thủ công.
            if (TryGetComponent<NavMeshAgent>(out var agent))
            {
                agent.updatePosition = false;
                agent.updateRotation = false;
            }

            int lyr = LayerMaskToLayer(VehicleLayer);
            if (lyr > 0) gameObject.layer = lyr;

            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) { enabled = false; return; }

            Waypoint nearest = Graph.FindNearest(transform.position);
            if (nearest == null) { enabled = false; return; }

            _startNodeId   = nearest.OSMNodeId;
            transform.position = WithY(nearest.Position);
            _currentSpeed  = BaseSpeed;
            _desiredSpeed  = BaseSpeed;

            _tlm = FindFirstObjectByType<TrafficLightManager>();

            // Mức độ lệch xe sang phải (phân làn giao thông)
            bool isMoto = VehicleType == VehicleMeshBuilder.VehicleType.Motorbike;
            _laneOffset = GetLaneOffset(nearest.RoadType) + (isMoto ? 0.4f : 0f);

            // Phía phải = offset dương (cross product convention)
            _targetOvertakeOffset = _laneOffset;
            _overtakeOffset       = _laneOffset;

            PickNewDestination();
        }

        private float GetMaxOffset(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 2.0f;
            switch(roadType) {
                case "motorway": return 4.5f;
                case "trunk":    return 3.5f;
                case "primary":  return 3.0f;
                case "secondary":return 2.5f;
                case "tertiary": return 2.0f;
                case "residential": return 1.5f;
                case "living_street": return 1.0f;
                default: return 2.0f;
            }
        }

        private float GetLaneOffset(string roadType)
        {
            float maxOffset = GetMaxOffset(roadType);
            
            // Đường chia làn (lớn hơn 2.0m) -> Phải bám mép phải (min = 1.0m)
            // Đường vắng/nhỏ (<= 2.0m) -> Được phép ra giữa tim đường (min = 0.2m)
            bool isLanedRoad = maxOffset > 2.0f;
            float minOffset = isLanedRoad ? 1.0f : 0.2f; 

            if (maxOffset <= 1.5f) return minOffset;
            
            // Dàn đều làn 
            int numLanes = Mathf.FloorToInt((maxOffset - minOffset) / 1.5f) + 1;
            int pickedLane = Random.Range(0, numLanes);
            
            return minOffset + pickedLane * 1.5f;
        }

        private void Update()
        {
            if (_path == null || _path.Count == 0 || _rerouting) return;

            float dt = Time.deltaTime;
            _braking = false;
            _emergencyBraking = false;
            _isWaitingAtRedLight = false;

            // 1. Thu thập thông tin xung quanh
            ScanAhead();

            // 2. Tính tốc độ mong muốn tự do trước (IDM + curve)
            AdaptSpeed();

            // 3. Đèn đỏ (Có thể bóp _desiredSpeed hoặc kích hoạt _braking)
            CheckTrafficLight();

            // 4. Nhường đường tại ngã tư không đèn
            CheckYielding();

            // 4.5. Phanh khẩn cấp khi xe bên cạnh sáp quá gần (từ mọi hướng)
            ProximityBrake();
            
            // 5. Nếu có cờ phanh cứng thì tốc độ phải về 0
            if (_braking) _desiredSpeed = 0f;

            // 5.5. Ép xe đi chậm nếu đang lùi
            if (_reversingTimer > 0f)
            {
                _reversingTimer -= dt;
                _desiredSpeed = BaseSpeed * 0.3f;
                _braking = false;
            }

            // 6. Vượt xe
            TryOvertake(dt);

            // 7. Anti-deadlock
            HandleDeadlock(dt);

            // 8. Di chuyển
            MoveAlongPath(dt);

            // 9. Đẩy xe ra khi chồng lên nhau (soft collision)
            ResolveOverlap();

            // 10. Bánh xe
            SpinWheels();
        }

        // ══════════════════════════════════════════════════════════════════════
        // 1. SCAN AHEAD — phát hiện xe phía trước
        // ══════════════════════════════════════════════════════════════════════

        private void ScanAhead()
        {
            _aheadVehicle  = null;
            _aheadDistance = float.MaxValue;

            Vector3 origin = transform.position + Vector3.up * 0.5f;
            Vector3 fwd    = transform.forward;
            // BoxCast thu hẹp để chỉ phát hiện xe thực sự cản đúng trước mũi, không quét quá rộng sang bên
            float halfW = Mathf.Clamp(VehicleWidth * 0.25f, 0.2f, 0.6f);
            Vector3 halfExtents = new Vector3(halfW, 0.4f, 0.1f);

            RaycastHit[] hits = Physics.BoxCastAll(origin, halfExtents, fwd, transform.rotation, SCAN_RANGE, VehicleLayer);
            foreach (var hit in hits)
            {
                var other = hit.collider.GetComponentInParent<VehicleAgent>();
                // Bỏ qua bản thân
                if (other != null && other != this && hit.distance < _aheadDistance)
                {
                    // Nhận diện xe đi ngược chiều
                    bool isOncoming = Vector3.Dot(transform.forward, other.transform.forward) < -0.2f; 
                    
                    if (isOncoming) 
                    {
                        // Nếu đường nhỏ/vắng, 2 xe đang đối đầu sát nhau -> Tự dạt phải vỉa hè để lách qua nhau
                        if (_overtakeOffset < 1.0f && hit.distance < 12f)
                        {
                            float curMax = _pathIdx < _path.Count ? GetMaxOffset(_path[_pathIdx].RoadType) : 2.0f;
                            
                            // Nếu đầu kia là xe tải/xe bus to hơn hẳn, xe nhỏ nhường toàn tập: né kịch lề ngoài và dừng lại chờ xe to đi qua
                            if (other.VehicleWidth > this.VehicleWidth + 0.5f)
                            {
                                _targetOvertakeOffset = curMax + 0.5f;
                                _braking = true;
                            }
                            else 
                            {
                                _targetOvertakeOffset = Mathf.Min(1.2f, curMax); 
                            }
                            
                            _isOvertaking = false;
                            _overtakeCooldown = 1.5f; // Trả lái nhanh hơn sau khi lách
                        }
                        
                        // Nếu ta đang vượt thì mới quan tâm xe ngược chiều phía xa để phanh, nếu không thì ngó lơ
                        if (!_isOvertaking) continue;
                    }

                    _aheadDistance = hit.distance;
                    _aheadVehicle = other;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 1b. PROXIMITY BRAKE — phanh khi xe bên cạnh quá gần (từ mọi hướng)
        // ══════════════════════════════════════════════════════════════════════

        private void ProximityBrake()
        {
            // Quét hình cầu nhỏ quanh xe để phát hiện xe từ mọi hướng
            float checkRadius = Mathf.Max(1.8f, VehicleWidth * 2.5f);
            Collider[] nearby = Physics.OverlapSphere(transform.position, checkRadius, VehicleLayer);
            
            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == this) continue;
                
                Vector3 diff = transform.position - other.transform.position;
                diff.y = 0f;
                float dist = diff.magnitude;
                
                // Khoảng cách tối thiểu dựa trên kích thước 2 xe
                float minSafeDist = (VehicleWidth + other.VehicleWidth) * 1.2f + 0.5f;
                
                if (dist < minSafeDist && dist > 0.01f)
                {
                    // Xe kia ở phía trước ta?
                    float dotFwd = Vector3.Dot(transform.forward, -diff.normalized);
                    
                    if (dotFwd > 0.2f)
                    {
                        // Xe cản phía trước → phanh tỉ lệ
                        float brakeFactor = 1f - Mathf.Clamp01(dist / minSafeDist);
                        _desiredSpeed = Mathf.Min(_desiredSpeed, _currentSpeed * (1f - brakeFactor));
                        
                        if (dist < minSafeDist * 0.4f)
                        {
                            _emergencyBraking = true;
                            _desiredSpeed = 0f;
                        }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 1c. RESOLVE OVERLAP — đẩy xe ra khi chồng lên nhau
        // ══════════════════════════════════════════════════════════════════════

        private void ResolveOverlap()
        {
            float checkRadius = Mathf.Max(2f, VehicleWidth * 2.5f);
            Collider[] nearby = Physics.OverlapSphere(transform.position, checkRadius, VehicleLayer);
            
            Vector3 totalPush = Vector3.zero;
            
            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == this) continue;
                
                Vector3 diff = transform.position - other.transform.position;
                diff.y = 0f;
                float dist = diff.magnitude;
                
                // Khoảng cách tối thiểu (xe không được chồng lên nhau)
                float minDist = (VehicleWidth + other.VehicleWidth) * 0.7f + 0.3f;
                
                if (dist < minDist && dist > 0.001f)
                {
                    float pushStrength = (minDist - dist) * 0.4f;
                    totalPush += diff.normalized * pushStrength;
                }
            }
            
            if (totalPush.sqrMagnitude > 0.0001f)
            {
                // Giới hạn lực đẩy tối đa để không văng xe
                if (totalPush.magnitude > 0.5f)
                    totalPush = totalPush.normalized * 0.5f;

                // Nếu xe đang khóa phanh tại đèn đỏ, nó là vật thể cố định, không bị đẩy trượt lên
                if (_braking || _isWaitingAtRedLight)
                    totalPush = Vector3.zero;
                    
                transform.position += totalPush;
                transform.position = WithY(transform.position, 0f);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 2. TRAFFIC LIGHT — dừng đèn đỏ
        // ══════════════════════════════════════════════════════════════════════

        private void CheckTrafficLight()
        {
            _redLightStopDist = -1f;
            if (_pathIdx >= _path.Count || _tlm == null) return;

            // Quét vòng lặp tăng số waypoint vì đôi khi đoạn đường cong waypoint phân bố rất đặc
            float maxLookDist = Mathf.Max(25f, _currentSpeed * 3.5f);
            float accumDist = 0f;
            Vector3 prevPos = transform.position;

            int lookAhead = Mathf.Min(_path.Count, _pathIdx + 15);
            for (int i = _pathIdx; i < lookAhead; i++)
            {
                Waypoint wp = _path[i];
                accumDist += Vector3.Distance(prevPos, wp.Position);
                prevPos = wp.Position;

                if (accumDist > maxLookDist) break;

                if (wp.IsTrafficLight)
                {
                    long fromId = (i > 0) ? _path[i - 1].OSMNodeId : _startNodeId;

                    // 2 bước tra: ID chính xác → fallback tìm intersection gần nhất theo vị trí
                    bool isGreen = _tlm.IsGreenLight(fromId, wp.OSMNodeId);
                    
                    // Nếu IsGreenLight trả true nhưng node không nằm trong _intersections,
                    // thử tìm intersection gần nhất theo vị trí (cho node bị filter tooClose)
                    if (isGreen)
                    {
                        isGreen = _tlm.IsGreenLightByPosition(wp.Position, fromId);
                    }

                    if (!isGreen)
                    {
                        _isWaitingAtRedLight = true;

                        // Vạch dừng = 3m trước node đèn đỏ
                        const float STOP_LINE_OFFSET = 3f;
                        float distToStopLine = Mathf.Max(0f, accumDist - STOP_LINE_OFFSET);
                        _redLightStopDist = distToStopLine;

                        if (distToStopLine < 1.0f)
                        {
                            // Đã tới vạch dừng → phanh cứng
                            _braking = true;
                        }
                        else
                        {
                            // Giảm tốc mượt: v = sqrt(2 * a * d)
                            float decel = 2.5f;
                            float maxSafeSpeed = Mathf.Sqrt(2f * decel * distToStopLine);
                            _desiredSpeed = Mathf.Min(_desiredSpeed, maxSafeSpeed);
                        }
                        return;
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 3. YIELDING — nhường đường tại ngã tư không đèn
        // ══════════════════════════════════════════════════════════════════════

        private void CheckYielding()
        {
            if (_pathIdx >= _path.Count) return;

            Waypoint nextWp = _path[_pathIdx];
            // Ngã tư = node có > 2 connections và KHÔNG phải đèn giao thông
            if (nextWp.ConnectedWaypointIds.Count <= 2 || nextWp.IsTrafficLight) return;

            float myDist = Vector3.Distance(transform.position, nextWp.Position);
            if (myDist > INTERSECTION_YIELD_RANGE) return;

            // Nếu đã chờ quá lâu → bỏ qua yield hoàn toàn, cứ đi (anti-deadlock sớm)
            if (_stuckTimer > Patience * 0.5f) return;

            // Quét xung quanh ngã tư tìm xe khác cũng đang tiến vào
            Collider[] nearby = Physics.OverlapSphere(nextWp.Position, INTERSECTION_YIELD_RANGE, VehicleLayer);
            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == this) continue;

                // Bỏ qua nếu xe kia đã đi qua ngã tư (đang hướng ra xa tâm ngã tư)
                Vector3 toIntersection = nextWp.Position - other.transform.position;
                if (Vector3.Dot(other.transform.forward, toIntersection) < 0) continue;

                float otherDist = Vector3.Distance(other.transform.position, nextWp.Position);

                // Bỏ qua xe đã đứng yên (kẹt/dừng) — chỉ nhường xe ĐANG chạy
                if (other._currentSpeed < 0.3f) continue;
                
                // Quyền ưu tiên: Nhường xe đi sát hơn, HOẶC nhường xe đến từ bên PHẢI
                Vector3 toOther = other.transform.position - transform.position;
                bool isComingFromRight = Vector3.Cross(transform.forward, toOther).y > 0;

                bool shouldYield = false;
                // Chỉ nhường khi xe kia GẦN hơn đáng kể HOẶC cùng tầm mà đến từ phải
                if (otherDist < myDist - 2f)
                    shouldYield = true;
                else if (Mathf.Abs(otherDist - myDist) <= 2f && isComingFromRight)
                    shouldYield = true;

                if (shouldYield)
                {
                    // Chỉ giảm tốc nhẹ, KHÔNG phanh cứng → xe vẫn trôi từ từ qua thay vì dừng khựng
                    if (myDist > 3f)
                    {
                        _desiredSpeed = Mathf.Min(_desiredSpeed, (myDist - 2.5f) * 3f);
                    }
                    else
                    {
                        _desiredSpeed = Mathf.Min(_desiredSpeed, 1.0f); // Lết qua chậm thay vì dừng hẳn
                    }
                    return;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 4. ADAPT SPEED — IDM + curve slowdown
        // ══════════════════════════════════════════════════════════════════════

        private float GetRoadTypeSpeedMlt(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 1f;
            switch (roadType) {
                case "motorway": return 1.5f;
                case "trunk": return 1.25f;
                case "primary": return 1f;
                case "secondary": return 0.8f;
                case "tertiary": return 0.7f;
                case "residential": return 0.5f;
                case "living_street": return 0.35f;
                default: return 1f;
            }
        }

        private void AdaptSpeed()
        {
            // Tốc độ tối đa giới hạn theo thuộc tính con đường (Speed Limits)
            float speedLimit = BaseSpeed * RuntimeSpeedScale * GetRoadTypeSpeedMlt(_pathIdx < _path.Count ? _path[_pathIdx].RoadType : "");
            _desiredSpeed = Mathf.Min(speedLimit, MaxSpeedLimit * RuntimeSpeedScale);

            // ── Giảm tốc khi cua gấp ──
            if (_pathIdx > 0 && _pathIdx < _path.Count - 1)
            {
                Vector3 currentDir = (_path[_pathIdx].Position - _path[_pathIdx - 1].Position);
                currentDir.y = 0;
                Vector3 nextDir = (_path[_pathIdx + 1].Position - _path[_pathIdx].Position);
                nextDir.y = 0;

                if (currentDir.sqrMagnitude > 0.01f && nextDir.sqrMagnitude > 0.01f)
                {
                    float angle = Vector3.Angle(currentDir, nextDir);
                    if (angle > CURVE_SLOWDOWN_ANGLE)
                    {
                        float factor = Mathf.Lerp(1f, 0.2f, Mathf.InverseLerp(CURVE_SLOWDOWN_ANGLE, 120f, angle));
                        _desiredSpeed *= factor;
                    }
                }
            }

            // ── IDM: giữ khoảng cách an toàn, xử lý phanh gấp ──
            if (_aheadVehicle != null && _aheadDistance < SCAN_RANGE)
            {
                float emergencyBorder = MinFollowDistance + (_currentSpeed * SafeReactionTime * 0.25f);
                float safeFollowDist  = MinFollowDistance + (_currentSpeed * SafeReactionTime);

                if (_aheadDistance < emergencyBorder)
                {
                    _emergencyBraking = true;
                    _desiredSpeed = 0f;
                }
                else if (_aheadDistance < safeFollowDist)
                {
                    float ratio = Mathf.Clamp01((_aheadDistance - emergencyBorder) / (safeFollowDist - emergencyBorder));
                    // Match tốc độ xe trước thay vì chậm hơn → giữ lưu lượng
                    _desiredSpeed = Mathf.Min(_desiredSpeed, Mathf.Max(_aheadVehicle._currentSpeed * ratio, _aheadVehicle._currentSpeed * 0.5f));
                }
                else
                {
                    // Xa → giữ nguyên tốc độ, chỉ giảm khi bắt đầu bám sát
                    // KHÔNG giảm tốc khi xe trước vẫn chạy bình thường ở xa
                    if (_aheadVehicle._currentSpeed < _currentSpeed * 0.5f)
                    {
                        float gapRatio = Mathf.Clamp01((_aheadDistance - safeFollowDist) / (SCAN_RANGE - safeFollowDist));
                        float sprintSpeed = Mathf.Lerp(_aheadVehicle._currentSpeed + 3f, _desiredSpeed, gapRatio * gapRatio);
                        _desiredSpeed = Mathf.Min(_desiredSpeed, sprintSpeed);
                    }
                    // Nếu xe trước chạy nhanh tương đương → không cần giảm tốc
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 5. OVERTAKE — vượt xe chậm
        // ══════════════════════════════════════════════════════════════════════

        private void TryOvertake(float dt)
        {
            _overtakeCooldown -= dt;
            bool isMoto = VehicleType == VehicleMeshBuilder.VehicleType.Motorbike;

            if (_isOvertaking)
            {
                // Kiểm tra đã vượt xong chưa: Phải vượt qua đuôi xe ít nhất OVERTAKE_CLEAR mét
                bool cleared = true;
                if (_overtakingTarget != null)
                {
                    Vector3 toTarget = _overtakingTarget.transform.position - transform.position;
                    // Tính khoảng cách dọc theo trục xe (dương = xe kia ở phía trước, âm = đã ở phía sau)
                    float distLong = Vector3.Dot(toTarget, transform.forward);
                    if (distLong > -OVERTAKE_CLEAR) cleared = false;
                }

                if (cleared)
                {
                    // Trả về lane gốc
                    _isOvertaking = false;
                    _overtakingTarget = null;
                    _targetOvertakeOffset = _laneOffset;
                    _overtakeCooldown = 1.5f;
                }
            }
            else 
            {
                // Tự động kéo xe nhả dần về lane gốc nếu vừa dạt lề chướng ngại vật xong
                if (_overtakeCooldown <= 0f && Mathf.Abs(_targetOvertakeOffset - _laneOffset) > 0.1f)
                {
                    _targetOvertakeOffset = _laneOffset;
                }

                // CẤM vượt khi đang chờ đèn đỏ hoặc ĐANG TRONG ngã tư
                bool isNearIntersection = false;
                for (int i = _pathIdx; i < Mathf.Min(_path.Count, _pathIdx + 3); i++) {
                    if (_path[i].ConnectedWaypointIds.Count > 2 || _path[i].IsTrafficLight) {
                        isNearIntersection = true;
                        break;
                    }
                }

                if (_overtakeCooldown <= 0f && _aheadVehicle != null && !_isWaitingAtRedLight && !isNearIntersection)
                {
                    bool shouldOvertake = _aheadDistance < OVERTAKE_RANGE
                        && (_aheadVehicle._currentSpeed < BaseSpeed * OVERTAKE_SPEED_RATIO
                            || _aheadVehicle._currentSpeed < 0.5f);

                    if (shouldOvertake)
                    {
                        Vector3 origin = transform.position + Vector3.up * 0.5f;
                        float passWidth = Mathf.Max(1.5f, VehicleWidth * 1.1f);
                        float lateralCheck = isMoto ? 1.5f : passWidth + 0.5f;
                        
                        float maxWid = _pathIdx < _path.Count ? GetMaxOffset(_path[_pathIdx].RoadType) : 2.0f;

                        bool leftClear = false;
                        float targetLeft = _laneOffset - passWidth;
                        // Mở khóa: cho phép lấn toàn bộ sang làn ngược chiều (tới -maxWid) để vượt nếu đường kẹt
                        if (targetLeft >= -maxWid)
                        {
                            leftClear = true;
                            // Kiểm tra song song bên hông xe
                            foreach (var h in Physics.RaycastAll(origin, -transform.right, lateralCheck, VehicleLayer))
                                if (h.collider.GetComponentInParent<VehicleAgent>() != this) leftClear = false;

                            // Quét an toàn: Dõi mắt 25m về phía trước trên làn ngược chiều xem có xe nào đang đi tới không
                            if (leftClear && targetLeft < 0.5f) {
                                Vector3 leftOrigin = origin + (-transform.right * lateralCheck);
                                foreach (var h in Physics.RaycastAll(leftOrigin, transform.forward, 25f, VehicleLayer)) {
                                    VehicleAgent colAgent = h.collider.GetComponentInParent<VehicleAgent>();
                                    // Bỏ qua bản thân và chiếc xe mình đang cố lách qua
                                    if (colAgent != null && colAgent != this && colAgent != _aheadVehicle) leftClear = false;
                                }
                            }
                        }

                        bool rightClear = false;
                        float targetRight = _laneOffset + passWidth;
                        // Nới rộng thêm 1 chút viền ngoài cùng để xe bus chịu tạt sát lề hơn
                        if (targetRight <= maxWid + 1.0f)
                        {
                            rightClear = true;
                            foreach (var h in Physics.RaycastAll(origin, transform.right, lateralCheck, VehicleLayer))
                                if (h.collider.GetComponentInParent<VehicleAgent>() != this) rightClear = false;
                        }

                        // Ưu tiên lách trái (luật giao thông chuẩn), nếu kẹt mới lách phải
                        if (leftClear)
                        {
                            _isOvertaking = true;
                            _overtakingTarget = _aheadVehicle;
                            _targetOvertakeOffset = targetLeft;
                        }
                        else if (rightClear)
                        {
                            _isOvertaking = true;
                            _overtakingTarget = _aheadVehicle;
                            _targetOvertakeOffset = targetRight;
                        }
                    }
                }
            }

            // Smooth lerp lateral offset - Tăng tốc độ đánh lái chuyển làn (nhảy bén hơn)
            _overtakeOffset = Mathf.Lerp(_overtakeOffset, _targetOvertakeOffset, dt * 6f);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 6. ANTI-DEADLOCK — escalation khi kẹt
        // ══════════════════════════════════════════════════════════════════════

        public bool IsIntentionallyStopped(int depth = 0)
        {
            // Xe dừng vì đèn đỏ mới được quyền chờ lâu quá giới hạn patience.
            // (Nếu xe đang dừng vì nhường đường nhau thì KHÔNG tính là cố tình, để nó còn bị "hết kiên nhẫn" và tự gỡ rối khỏi vòng lặp nhường đường đâm nhau).
            if (_isWaitingAtRedLight) return true;
            
            if (depth > 15) return false; // Tránh đệ quy vô hạn nếu bị deadlock quấn vòng tròn

            if (_aheadVehicle != null && _aheadVehicle._currentSpeed < _stuckCheckSpeed)
            {
                return _aheadVehicle.IsIntentionallyStopped(depth + 1);
            }
            return false;
        }

        private void HandleDeadlock(float dt)
        {
            if (IsIntentionallyStopped())
            {
                _stuckTimer = 0f;
                _deadlockLevel = 0;
                return;
            }

            if (Mathf.Abs(_currentSpeed) < _stuckCheckSpeed && !_rerouting)
            {
                _stuckTimer += dt;

                if (_stuckTimer > Patience)
                {
                    _deadlockLevel++;
                    _stuckTimer = 0f;

                    switch (_deadlockLevel)
                    {
                        case 1:
                            // Đánh lái né ra ngoài/vào trong + ép tốc độ tối thiểu để xé rào
                            float curMax = _pathIdx < _path.Count ? GetMaxOffset(_path[_pathIdx].RoadType) : 2.0f;
                            _targetOvertakeOffset = Mathf.Clamp(_laneOffset + Random.Range(-2f, 2f), -curMax, curMax + 1f); 
                            _reversingTimer = 0.8f;
                            _braking = false; // Bỏ phanh cưỡng bức
                            _desiredSpeed = BaseSpeed * 0.5f;
                            break;

                        case 2:
                            // Reroute — tìm đường khác
                            _path.Clear();
                            _deadlockLevel = 0;
                            StartCoroutine(WaitThenReroute(0.3f));
                            break;

                        default:
                            // Teleport — fallback cuối cùng
                            var vals = new List<Waypoint>(Graph.Waypoints.Values);
                            Waypoint rand = vals[Random.Range(0, vals.Count)];
                            _startNodeId = rand.OSMNodeId;
                            transform.position = WithY(rand.Position);
                            _deadlockLevel = 0;
                            _path.Clear();
                            StartCoroutine(WaitThenReroute(0.3f));
                            break;
                    }
                }
            }
            else
            {
                _stuckTimer = 0f;
                _deadlockLevel = 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 7. MOVE — di chuyển dọc đường với lateral offset
        // ══════════════════════════════════════════════════════════════════════

        private void MoveAlongPath(float dt)
        {
            if (_exactPathIdx >= _exactPath.Count) return;

            var currentTarget = _exactPath[_exactPathIdx];
            Vector3 target   = WithY(currentTarget.Position);
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;

            // Hướng đoạn đường vật lý (Exact Path)
            Vector3 roadDir = transform.forward;
            if (_exactPathIdx > 0)
            {
                Vector3 prePt = WithY(_exactPath[_exactPathIdx - 1].Position);
                Vector3 delta = target - prePt;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.01f)
                    roadDir = delta.normalized;
            }

            // Dùng sqrMagnitude đo trực tiếp thay cho Dot để không bị tính sai lệch khi xe xoay
            float distSq = toTarget.sqrMagnitude;
            float dotPassed = Vector3.Dot(toTarget, roadDir);

            // ── Reached waypoint (vượt qua hoặc cách node rất nhỏ) ──
            bool hasReached = distSq < 1.0f || (distSq < 25.0f && dotPassed < 0f);

            if (hasReached)
            {
                // Giữ xe tại vạch dừng đèn đỏ: hold BẤT KỲ waypoint nào
                // khi xe đã gần vạch dừng. Không check dotPassed để tránh lỗi văng quá đà rồi bị consume
                bool holdForRedLight = _isWaitingAtRedLight 
                    && _redLightStopDist >= 0f && _redLightStopDist < 2.5f;

                if (holdForRedLight)
                {
                    // Đứng tại vạch, không consume waypoint đèn đỏ
                }
                else
                {
                    if (currentTarget.WaypointRef != null) 
                        _startNodeId = currentTarget.WaypointRef.OSMNodeId;

                    _exactPathIdx++;
                    _pathIdx++; // Giữ đồng bộ logic topological với list thật

                    if (_exactPathIdx >= _exactPath.Count)
                    {
                        _path.Clear();

                        if (DestroyOnArrival)
                        {
                            Destroy(gameObject);
                            return;
                        }

                        StartCoroutine(WaitThenReroute(Random.Range(0.2f, 1.0f)));
                    }
                    return;
                }
            }

            // ── Tính tốc độ — Dynamic Acceleration/Deceleration ──
            float decelRate = _emergencyBraking ? BaseSpeed * EmergencyBrakePwr : BaseSpeed * 4f;
            // Tăng tốc độ đề-pa vọt lên sau khi tắc nghẽn (từ x2 lên x4)
            float accelRate = _desiredSpeed > _currentSpeed ? BaseSpeed * 4f : decelRate;
            // Cho phép trừ tốc độ xuống ngưỡng âm
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, _desiredSpeed, dt * accelRate);

            if (Mathf.Abs(_currentSpeed) < 0.01f) return;

            // ── Hướng di chuyển + lateral offset ──
            Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;
            // Áp dụng độ lệch rẽ vượt (Trừ đi offset gốc vì exactPath đã mang sẵn offset gốc bên trong)
            float shiftDiff = _overtakeOffset - _laneOffset;
            Vector3 offsetTarget = target + right * shiftDiff;

            Vector3 toOffset = (offsetTarget - transform.position);
            toOffset.y = 0f;
            Vector3 moveDir = toOffset.normalized;

            // ── Rotation — quay mặt về hướng di chuyển ──
            // Vẫn giữ mắt nhìn về phía trước ngay cả khi lùi (kiểu cài số Revert)
            if (moveDir.sqrMagnitude > 0.001f && _currentSpeed > 0f)
            {
                // Bẻ lái gắt hơn khi rẽ để ôm sát lề, nhắm thẳng mục tiêu (ngăn trôi xe lên vỉa hè)
                float rotSpeed = RotationSpeed;
                if (toOffset.sqrMagnitude < 40f) rotSpeed *= 1.5f;

                Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, rotSpeed * dt);
            }

            // ── Translation — Blend steering tự nhiên ──
            float speedMlt = (_currentSpeed >= 0f) ? 1f : -1f;
            Vector3 moveForce = transform.forward * speedMlt;
            
            // Xoay nhẹ mồi theo hướng di chuyển chính để chống xe drift quá trớn ở tốc độ cao
            moveForce = Vector3.Lerp(moveForce, moveDir * speedMlt, 0.15f).normalized;
            
            transform.position += moveForce * Mathf.Abs(_currentSpeed) * dt;

            // Giữ Y = 0
            transform.position = WithY(transform.position, 0f);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 8. WHEEL SPIN
        // ══════════════════════════════════════════════════════════════════════

        private void SpinWheels()
        {
            if (Wheels == null || Wheels.Length == 0) return;
            float wheelRadius = 0.75f;
            float degPerSec   = _currentSpeed / wheelRadius * Mathf.Rad2Deg;
            foreach (var w in Wheels)
                if (w != null) w.Rotate(0f, degPerSec * Time.deltaTime, 0f, Space.Self);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PATHFINDING
        // ══════════════════════════════════════════════════════════════════════

        public void PickNewDestination()
        {
            // Reset toàn bộ thông số đánh lái/lách xe để tránh xe nhớ offset cũ rồi tự drift vào tường
            _targetOvertakeOffset = _laneOffset;
            _overtakeOffset = _laneOffset;
            _isOvertaking = false;

            if (Graph == null || Graph.Waypoints.Count < 2) return;

            var keys = new List<long>(Graph.Waypoints.Keys);
            
            bool pickedTarget = false;
            if (TrafficSpawner.Instance != null)
            {
                // Ưu tiên 80% đích là đi ra viền Map (Edge Nodes)
                if (TrafficSpawner.Instance.EdgeNodes != null && TrafficSpawner.Instance.EdgeNodes.Count > 0 && Random.value < 0.8f)
                {
                    Waypoint wp = TrafficSpawner.Instance.EdgeNodes[Random.Range(0, TrafficSpawner.Instance.EdgeNodes.Count)];
                    if (wp != null && wp.OSMNodeId != _startNodeId)
                    {
                        _destNodeId = wp.OSMNodeId;
                        pickedTarget = true;
                    }
                }
                // 20% chạy về toà nhà
                else if (TrafficSpawner.Instance.Buildings != null && TrafficSpawner.Instance.Buildings.Count > 0)
                {
                    Transform targetBldg = TrafficSpawner.Instance.Buildings[Random.Range(0, TrafficSpawner.Instance.Buildings.Count)];
                    Waypoint wp = Graph.FindNearest(targetBldg.position);
                    if (wp != null && wp.OSMNodeId != _startNodeId)
                    {
                        _destNodeId = wp.OSMNodeId;
                        pickedTarget = true;
                    }
                }
            }

            if (!pickedTarget)
            {
                _destNodeId = keys[Random.Range(0, keys.Count)];
                int tries = 0;
                while (_destNodeId == _startNodeId && tries++ < 20)
                    _destNodeId = keys[Random.Range(0, keys.Count)];
            }

            var candidateWp = Graph.FindPath(_startNodeId, _destNodeId);

            int retries = 0;
            while ((candidateWp == null || candidateWp.Count == 0) && retries++ < 5)
            {
                _destNodeId = keys[Random.Range(0, keys.Count)];
                candidateWp = Graph.FindPath(_startNodeId, _destNodeId);
            }

            if (candidateWp == null || candidateWp.Count == 0)
            {
                // Isolated node → teleport
                var vals = new List<Waypoint>(Graph.Waypoints.Values);
                Waypoint rand = vals[Random.Range(0, vals.Count)];
                _startNodeId       = rand.OSMNodeId;
                transform.position = WithY(rand.Position);
                StartCoroutine(WaitThenReroute(1f));
                return;
            }

            _path    = candidateWp;
            BuildExactPath(_path);
        }

        private void BuildExactPath(List<Waypoint> rawPath)
        {
            _exactPath.Clear();
            _exactPathIdx = 0;
            _pathIdx = 0;
            if (rawPath == null || rawPath.Count < 2) return;

            for (int i = 0; i < rawPath.Count; i++)
            {
                Waypoint current = rawPath[i];
                Vector3 wCurr = current.Position;
                
                // Giới hạn offset thực tế để khi từ đường lớn rẽ đường nhỏ không văng lên cỏ
                float curMax = GetMaxOffset(current.RoadType);
                float curOffset = Mathf.Min(_laneOffset, curMax);

                if (i == 0)
                {
                    Vector3 d = (rawPath[1].Position - rawPath[0].Position).normalized;
                    if (d.sqrMagnitude < 0.01f) d = transform.forward;
                    Vector3 r = Vector3.Cross(Vector3.up, d).normalized;
                    _exactPath.Add(new PathPoint { Position = wCurr + r * curOffset, WaypointRef = current });
                }
                else if (i == rawPath.Count - 1)
                {
                    Vector3 d = (rawPath[i].Position - rawPath[i - 1].Position).normalized;
                    if (d.sqrMagnitude < 0.01f) d = transform.forward;
                    Vector3 r = Vector3.Cross(Vector3.up, d).normalized;
                    _exactPath.Add(new PathPoint { Position = wCurr + r * curOffset, WaypointRef = current });
                }
                else
                {
                    Vector3 wPrev = rawPath[i - 1].Position;
                    Vector3 wNext = rawPath[i + 1].Position;

                    Vector3 d1 = (wCurr - wPrev).normalized;
                    if (d1.sqrMagnitude < 0.01f) d1 = transform.forward;
                    Vector3 r1 = Vector3.Cross(Vector3.up, d1).normalized;

                    Vector3 d2 = (wNext - wCurr).normalized;
                    if (d2.sqrMagnitude < 0.01f) d2 = d1;
                    Vector3 r2 = Vector3.Cross(Vector3.up, d2).normalized;

                    // Sử dụng phương pháp bo tròn miter joint (chia đôi góc) thay vì giao điểm 2 đường thẳng
                    // Giúp các góc cua gắt không bao giờ bị cắt chéo (shortcut) vào thảm cỏ/nhà dân.
                    Vector3 r_avg = (r1 + r2).normalized;
                    if (r_avg.sqrMagnitude < 0.01f) r_avg = r1;

                    // Tính hệ số mở rộng góc miter
                    float angleDot = Vector3.Dot(d1, d2); 
                    float cosHalf = Mathf.Sqrt(Mathf.Max(0.001f, (1f + angleDot) / 2f));
                    float miterDist = curOffset / cosHalf;

                    // Cap giới hạn miter để vác góc nhọn ngã tư (không chĩa góc quá nhọn văng lề)
                    miterDist = Mathf.Min(miterDist, curOffset * 1.5f);

                    Vector3 cornerPos = wCurr + r_avg * miterDist;
                    cornerPos.y = wCurr.y;

                    _exactPath.Add(new PathPoint { Position = cornerPos, WaypointRef = current });
                }
            }
        }

        private IEnumerator WaitThenReroute(float delay)
        {
            _rerouting = true;
            yield return new WaitForSeconds(delay);
            _rerouting = false;
            PickNewDestination();
        }

        // ══════════════════════════════════════════════════════════════════════
        // UTILITY
        // ══════════════════════════════════════════════════════════════════════

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
                Gizmos.DrawLine(_path[i].Position, _path[i + 1].Position);
            if (_pathIdx < _path.Count)
            {
                Gizmos.color = _isOvertaking ? Color.magenta : Color.yellow;
                Gizmos.DrawSphere(_path[_pathIdx].Position, 1f);
            }

            // Hiện scan range
            Gizmos.color = new Color(1, 0, 0, 0.15f);
            Gizmos.DrawWireSphere(transform.position, SCAN_RANGE);
        }
#endif
    }
}
