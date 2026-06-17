using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// VehicleAgent — Orchestrator.
    /// Không chứa logic hành vi. Chỉ khởi tạo, giữ shared state (VehicleContext),
    /// và gọi các behavior module theo đúng thứ tự ưu tiên mỗi frame.
    /// 
    /// Pipeline (thứ tự thực thi — giữ nguyên từ code gốc):
    ///   ① ObstacleSensor       — quét chướng ngại + dự đoán va chạm
    ///   ② SpeedController.AdaptSpeed — IDM + curve slowdown
    ///   ③ TrafficRuleHandler   — đèn đỏ + nhường đường + don't block box
    ///   ④ SpeedController.ProximityBrake — phanh khi xe bên cạnh quá gần
    ///   ⑤ OvertakeController   — chuẩn bị rẽ + vượt xe
    ///   ⑥ DeadlockResolver     — nudge → reroute → teleport
    ///   ⑦ VehicleMover         — di chuyển + overlap + bánh xe
    ///   ⑧ CongestionTracker    — phát hiện + báo cáo tắc nghẽn
    /// </summary>
    public class VehicleAgent : MonoBehaviour
    {
        // ── Inspector fields ──────────────────────────────────────────────
        [HideInInspector] public WaypointGraph              Graph;
        [HideInInspector] public VehicleMeshBuilder.VehicleType VehicleType;
        [HideInInspector] public float                      BaseSpeed = 8f;
        [HideInInspector] public float                      RuntimeSpeedScale = 1f;
        [HideInInspector] public float                      VehicleWidth;
        [HideInInspector] public bool                       DestroyOnArrival;
        [HideInInspector] public int                        LaneIndex;
        [HideInInspector] public int                        PreferredLane;
        [HideInInspector] public float                      Patience = 1.5f;
        [HideInInspector] public bool                       IsColliding;
        [HideInInspector] public bool                       IsStuck;

        [Header("Personality Settings")]
        public VehiclePersonality Personality;

        [Header("Steering & Behaviors")]
        public float RotationSpeed  = 240f;
        public float StopDistance   = 6f;
        public LayerMask VehicleLayer;

        [Header("Advanced Parameters")]
        public float MaxSpeedLimit = 15f;
        public float MinFollowDistance = 2.0f;
        public float SafeReactionTime = 0.55f;
        public float EmergencyBrakePwr = 8f;

        [Header("Wheel Animation")]
        public Transform[] Wheels;

        // ── Shared state & behavior modules ───────────────────────────────

        /// <summary>Shared mutable state — các module đọc/ghi trực tiếp.</summary>
        public VehicleContext Ctx { get; private set; }

        // Behavior modules (public để các module con có thể gọi lẫn nhau khi cần)
        public ObstacleSensor      Sensor           { get; private set; }
        public SpeedController     SpeedCtrl        { get; private set; }
        public TrafficRuleHandler  TrafficRules     { get; private set; }
        public OvertakeController  OvertakeCtrl     { get; private set; }
        public DeadlockResolver    DeadlockResolver { get; private set; }
        public VehicleMover        Mover            { get; private set; }
        public CongestionTracker   CongestionTracker{ get; private set; }
        public PathNavigator       Navigator        { get; private set; }

        public GapExploitationController GapExploitation   { get; private set; }
        public MotorbikeController       MotorbikeCtrl     { get; private set; }
        public IntersectionNegotiator   IntersectionNeg   { get; private set; }
        public PressureSystem            Pressure          { get; private set; }
        public HonkSystem                Honk              { get; private set; }

        // Rule pipeline (safety constraints that sit on top of behavior modules)
        private TrafficRulePipeline<OsmVehicleRuleContext> _rulePipeline;
        private OsmVehicleRuleContext _ruleCtx;
        public BehaviorArbitrator Arbitrator { get; private set; }

        [HideInInspector]
        public Dictionary<string, float> RuleProbabilities = new Dictionary<string, float>();

        // ── Nested types (giữ lại cho backward compat với VehicleInspector) ──

        public class PathPoint
        {
            public Vector3 Position;
            public Waypoint WaypointRef;
        }

        // ══════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        private void Start()
        {
            if (Ctx != null) return; // Đã khởi tạo thủ công qua InitWithCustomRoute

            InitializeAgent(null, null);
        }

        private void InitializeAgent(Waypoint customStart, Waypoint customDest)
        {
            if (!TryGetComponent<Rigidbody>(out var rb))
                rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;

            int lyr = LayerMaskToLayer(VehicleLayer);
            if (lyr > 0) gameObject.layer = lyr;

            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) { enabled = false; return; }

            Waypoint startWp = customStart;
            if (startWp == null)
            {
                startWp = Graph.FindNearest(transform.position);
            }
            if (startWp == null) { enabled = false; return; }

            // ── Khởi tạo Tính cách (Personality) ──
            if (Personality == null)
            {
                if (TrafficSpawner.Instance != null && TrafficSpawner.Instance.PersonalitiesList.Count > 0)
                {
                    int randIdx = Random.Range(0, TrafficSpawner.Instance.PersonalitiesList.Count);
                    Personality = TrafficSpawner.Instance.PersonalitiesList[randIdx].Clone();
                }
                else
                {
                    int randIdx = Random.Range(0, TrafficSpawner.PredefinedPersonalities.Count);
                    Personality = TrafficSpawner.PredefinedPersonalities[randIdx].Clone();
                }
                Personality.Jitter(0.08f);
            }

            // Áp dụng các tỷ lệ multiplier từ tính cách
            BaseSpeed *= Personality.SpeedMultiplier;
            MaxSpeedLimit *= Personality.SpeedMultiplier;
            MinFollowDistance *= Personality.MinFollowDistanceMultiplier;
            SafeReactionTime *= Personality.SafeReactionTimeMultiplier;

            // ── Khởi tạo Profile và Driver Personality ──
            var profile = VehicleProfile.CreateDefault(VehicleType);
            var driver = DriverPersonality.CreateRandom();
            if (Personality != null)
            {
                driver.Aggression = Personality.OvertakeEagerness * 0.25f;
                driver.Patience = Personality.YieldChance * 0.01f;
                driver.Lawfulness = 1f - Personality.RedLightRunChance * 0.01f;
                driver.ReactionTime = Personality.SafeReactionTimeMultiplier;
            }

            // ── Khởi tạo Context ──
            bool isMoto = VehicleType == VehicleMeshBuilder.VehicleType.Motorbike;
            float laneOffset = RoadUtility.GetLaneOffset(startWp.RoadType) + (isMoto ? 0.4f : 0f);

            Ctx = new VehicleContext
            {
                Agent              = this,
                Transform          = transform,
                VehicleType        = VehicleType,
                VehicleWidth       = VehicleWidth,
                BaseSpeed          = BaseSpeed,
                RuntimeSpeedScale  = RuntimeSpeedScale,
                VehicleLayer       = VehicleLayer,
                Graph              = Graph,
                StartNodeId        = startWp.OSMNodeId,
                CurrentSpeed       = BaseSpeed,
                DesiredSpeed       = BaseSpeed,
                LaneOffset         = laneOffset,
                TargetOvertakeOffset = laneOffset,
                OvertakeOffset     = laneOffset,
                DestroyOnArrival   = DestroyOnArrival,
                RotationSpeed      = RotationSpeed,
                StopDistance        = StopDistance,
                MaxSpeedLimit      = MaxSpeedLimit,
                MinFollowDistance   = MinFollowDistance,
                SafeReactionTime   = SafeReactionTime,
                EmergencyBrakePwr  = EmergencyBrakePwr,
                Patience           = Patience,
                Profile            = profile,
                Driver             = driver
            };

            Vector3 startPos = startWp.Position;
            if (startWp.ConnectedWaypointIds.Count > 0)
            {
                long nextId = startWp.ConnectedWaypointIds[0];
                if (Graph.Waypoints.TryGetValue(nextId, out var nextWp))
                {
                    Vector3 fwdDir = (nextWp.Position - startWp.Position);
                    fwdDir.y = 0;
                    if (fwdDir.sqrMagnitude > 0.01f)
                    {
                        fwdDir.Normalize();
                        Vector3 rightDir = Vector3.Cross(Vector3.up, fwdDir).normalized;
                        float curMax = RoadUtility.GetMaxOffset(startWp.RoadType);
                        float curOffset = Mathf.Min(laneOffset, curMax);
                        startPos = startWp.Position + rightDir * curOffset;
                        transform.rotation = Quaternion.LookRotation(fwdDir);
                    }
                }
            }

            transform.position = WithY(startPos);

            // ── Khởi tạo Behavior Modules ──
            Sensor            = new ObstacleSensor(Ctx);
            SpeedCtrl         = new SpeedController(Ctx);
            TrafficRules      = new TrafficRuleHandler(Ctx);
            OvertakeCtrl      = new OvertakeController(Ctx);
            DeadlockResolver  = new DeadlockResolver(Ctx);
            Mover             = new VehicleMover(Ctx);
            CongestionTracker = new CongestionTracker(Ctx);
            Navigator         = new PathNavigator(Ctx);

            GapExploitation   = new GapExploitationController(Ctx);
            MotorbikeCtrl     = new MotorbikeController(Ctx);
            IntersectionNeg   = new IntersectionNegotiator(Ctx);
            Pressure          = new PressureSystem(Ctx);
            Honk              = new HonkSystem(Ctx);

            // ── Khởi tạo Rule Pipeline & Arbitrator ──
            _ruleCtx = new OsmVehicleRuleContext(this);
            _rulePipeline = new TrafficRulePipeline<OsmVehicleRuleContext>();
            Arbitrator = new BehaviorArbitrator();
            TrafficRuleRegistry<OsmVehicleRuleContext>.Instance.PopulateDefaults(_rulePipeline);
            RegisterDefaultOsmRules();

            // ── Chọn điểm đích đầu tiên ──
            if (customDest != null)
            {
                Ctx.DestNodeId = customDest.OSMNodeId;
                var candidatePath = Graph.FindPath(Ctx.StartNodeId, Ctx.DestNodeId, true);
                if (candidatePath != null && candidatePath.Count > 0)
                {
                    Ctx.Path = candidatePath;
                    Navigator.BuildExactPath(candidatePath);
                }
                else
                {
                    Navigator.PickNewDestination();
                }
            }
            else
            {
                Navigator.PickNewDestination();
            }
        }

        public void InitWithCustomRoute(Waypoint startWp, Waypoint destWp)
        {
            InitializeAgent(startWp, destWp);
        }

        // ══════════════════════════════════════════════════════════════════
        // UPDATE — Driving Pipeline (20 dòng thay vì 1800)
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (Ctx == null || Ctx.Path == null || Ctx.Path.Count == 0 || Ctx.Rerouting) return;

            // Sync runtime parameters (TrafficSpawner cập nhật trực tiếp trên MonoBehaviour)
            Ctx.RuntimeSpeedScale = RuntimeSpeedScale;

            float dt = Time.deltaTime;

            // Reset per-frame flags
            Ctx.Braking = false;
            Ctx.EmergencyBraking = false;
            Ctx.IsWaitingAtRedLight = false;
            Ctx.IsColliding = false;

            // ① Quét chướng ngại (ưu tiên cao nhất — cung cấp data cho tất cả module sau)
            Sensor.Execute();

            // ② Tính tốc độ mong muốn (IDM + curve)
            SpeedCtrl.AdaptSpeed();

            // ③ Luật giao thông (đèn đỏ, nhường đường, don't block the box)
            TrafficRules.Execute();

            // ④ Phanh khẩn cấp khi xe bên cạnh sáp quá gần (sau traffic rules, đúng thứ tự gốc)
            SpeedCtrl.ProximityBrake();

            // Ép tốc độ = 0 nếu phanh cứng
            if (Ctx.Braking) Ctx.DesiredSpeed = 0f;

            // Cài số lùi thoát kẹt
            if (Ctx.ReversingTimer > 0f)
            {
                Ctx.ReversingTimer -= dt;
                Ctx.DesiredSpeed = -Ctx.BaseSpeed * 0.4f;
                Ctx.Braking = false;
            }

            // ⑤ Vượt xe + chuẩn bị rẽ
            OvertakeCtrl.Execute(dt);

            // Enforce hard stop: EmergencyBraking cannot be overridden by later modules
            if (Ctx.EmergencyBraking && Ctx.ReversingTimer <= 0f)
            {
                Ctx.DesiredSpeed = 0f;
            }

            // ⑥ Anti-deadlock
            DeadlockResolver.Execute(dt);

            _ruleCtx.DeltaTime = dt;
            var commands = _rulePipeline.Execute(_ruleCtx);
            ApplyRuleCommands(commands);

            // ⑦ Di chuyển + overlap + bánh xe
            Mover.Execute(dt, Wheels);

            // ⑧ Phát hiện + báo cáo tắc nghẽn
            CongestionTracker.Execute(dt);

            // Sync flags ngược lại cho Inspector
            IsStuck = Ctx.IsStuck;
            IsColliding = Ctx.IsColliding;
        }

        // ══════════════════════════════════════════════════════════════════
        // RULE PIPELINE — apply resolved commands from the rule pipeline
        // ══════════════════════════════════════════════════════════════════

        private void ApplyRuleCommands(TrafficRuleCommandBuffer commands)
        {
            Arbitrator.Clear();

            // Convert rule command resolutions into intermediate desires to arbitrate
            if (commands.ShouldHardStop)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("SafetyOverride");
                d.TargetSpeed = 0f;
                d.BrakeIntent = 1.0f;
                d.Urgency = 1.0f;
                d.Risk = 1.0f;
                Arbitrator.AddDesire(d);
            }

            if (commands.ResolvedMaxSpeed < float.MaxValue)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("MaxSpeedLimitRule");
                d.TargetSpeed = commands.ResolvedMaxSpeed;
                d.Urgency = 0.8f;
                Arbitrator.AddDesire(d);
            }

            if (commands.ResolvedTargetSpeed >= 0f)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("TargetSpeedRule");
                d.TargetSpeed = commands.ResolvedTargetSpeed;
                d.Urgency = 0.7f;
                Arbitrator.AddDesire(d);
            }

            if (commands.ResolvedBrakeForce > 0f)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("BrakeForceRule");
                d.BrakeIntent = commands.ResolvedBrakeForce;
                d.TargetSpeed = Ctx.CurrentSpeed * (1f - commands.ResolvedBrakeForce);
                d.Urgency = 0.9f;
                Arbitrator.AddDesire(d);
            }

            if (commands.ResolvedLateralOffset.HasValue)
            {
                DrivingDesire d = DrivingDesire.CreateDefault("LateralOffsetRule");
                d.TargetLateralOffset = commands.ResolvedLateralOffset.Value;
                d.Urgency = 0.6f;
                Arbitrator.AddDesire(d);
            }

            // Execute opportunistic behaviors to add their desires before arbitration
            if (GapExploitation != null) GapExploitation.Execute(Time.deltaTime);
            if (MotorbikeCtrl != null) MotorbikeCtrl.Execute(Time.deltaTime);
            if (IntersectionNeg != null) IntersectionNeg.Execute(Time.deltaTime);
            if (Pressure != null) Pressure.Execute(Time.deltaTime);
            if (Honk != null) Honk.Execute(Time.deltaTime);

            // Arbitrate and update desired speed & lateral offsets on context
            Arbitrator.Arbitrate(Ctx);

            // Handle non-arbitrated command flags
            if (commands.IsLaneChangeDenied && Ctx.IsOvertaking)
            {
                Ctx.IsOvertaking = false;
                Ctx.OvertakingTarget = null;
                Ctx.OvertakeSide = 0f;
                Ctx.TargetOvertakeOffset = Ctx.LaneOffset;
                Ctx.OvertakeCooldown = 1.0f;
            }
        }

        private void AddRuleWithProbability(ITrafficRule<OsmVehicleRuleContext> rule)
        {
            float prob = 100f; // default 100%
            if (RuleProbabilities != null && RuleProbabilities.TryGetValue(rule.RuleId, out float p))
            {
                prob = p;
            }

            if (Random.Range(0f, 100f) < prob)
            {
                _rulePipeline.AddRule(rule);
            }
        }

        private void RegisterDefaultOsmRules()
        {
            AddRuleWithProbability(new AvoidFrontCollisionRule());
            AddRuleWithProbability(new AvoidLaneChangeCollisionRule());
            AddRuleWithProbability(new MaintainSafeDistanceRule());
            AddRuleWithProbability(new MaxSpeedLimitRule());
            AddRuleWithProbability(new FullStopWhenTooCloseRule());
            AddRuleWithProbability(new StopAtRedLightRule());
            AddRuleWithProbability(new GoOnGreenLightRule());
            AddRuleWithProbability(new PrepareStopYellowRule());
            AddRuleWithProbability(new DoNotRunRedLightRule());
            AddRuleWithProbability(new MaintainDesiredSpeedRule());
            AddRuleWithProbability(new SmoothAccelerationRule());
            AddRuleWithProbability(new SmoothDecelerationRule());
            AddRuleWithProbability(new ClampAccelerationRule());
            AddRuleWithProbability(new StableHeadingRule());
            AddRuleWithProbability(new KeepCurrentLaneRule());
            AddRuleWithProbability(new OvertakeLaneChangeRule());
            AddRuleWithProbability(new PrepareTurnLaneChangeRule());
            AddRuleWithProbability(new BlockUnsafeLaneChangeRule());
            AddRuleWithProbability(new BlockedIntersectionRule());
            AddRuleWithProbability(new YieldIntersectionRule());

            // Safe reverse checking rule
            AddRuleWithProbability(new ReverseReluctanceRule());
        }

        // ══════════════════════════════════════════════════════════════════
        // PUBLIC API — cho các module con gọi (Coroutine, Reroute)
        // ══════════════════════════════════════════════════════════════════

        public IEnumerator WaitThenReroute(float delay, bool avoidCongestion = false)
        {
            Ctx.Rerouting = true;
            yield return new WaitForSeconds(delay);
            Ctx.Rerouting = false;

            if (avoidCongestion)
                Navigator.RerouteKeepDestination();
            else
                Navigator.PickNewDestination();
        }

        public void PickNewDestination() => Navigator.PickNewDestination();

        // ══════════════════════════════════════════════════════════════════
        // PUBLIC ACCESSORS — cho VehicleInspector (backward compat)
        // ══════════════════════════════════════════════════════════════════

        public List<Waypoint> GetCurrentPath() => Ctx?.Path;
        public int GetCurrentPathIndex() => Ctx?.PathIdx ?? 0;
        public float GetCurrentSpeed() => Ctx?.CurrentSpeed ?? 0f;
        public List<PathPoint> GetExactPath() => Ctx?.ExactPath;
        public int GetExactPathIndex() => Ctx?.ExactPathIdx ?? 0;

        public Vector3 GetStartPosition()
        {
            if (Ctx != null && Graph != null && Graph.Waypoints.TryGetValue(Ctx.StartNodeId, out var wp))
                return wp.Position;
            return transform.position;
        }

        public Vector3 GetDestinationPosition()
        {
            if (Ctx != null && Graph != null && Graph.Waypoints.TryGetValue(Ctx.DestNodeId, out var wp))
                return wp.Position;
            return transform.position;
        }

        // ══════════════════════════════════════════════════════════════════
        // UTILITY
        // ══════════════════════════════════════════════════════════════════

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
            if (Ctx == null || Ctx.Path == null || Ctx.Path.Count < 2) return;
            Gizmos.color = Color.cyan;
            for (int i = Ctx.PathIdx; i < Ctx.Path.Count - 1; i++)
                Gizmos.DrawLine(Ctx.Path[i].Position, Ctx.Path[i + 1].Position);
            if (Ctx.PathIdx < Ctx.Path.Count)
            {
                Gizmos.color = Ctx.IsOvertaking ? Color.magenta : Color.yellow;
                Gizmos.DrawSphere(Ctx.Path[Ctx.PathIdx].Position, 1f);
            }

            Gizmos.color = new Color(1, 0, 0, 0.15f);
            Gizmos.DrawWireSphere(transform.position, 25f);
        }
#endif
    }

    [System.Serializable]
    public class VehiclePersonality
    {
        public string Name;
        
        [Range(0.5f, 2.0f)]
        public float SpeedMultiplier = 1.0f;
        
        [Range(0.2f, 2.0f)]
        public float MinFollowDistanceMultiplier = 1.0f;
        
        [Range(0.2f, 2.0f)]
        public float SafeReactionTimeMultiplier = 1.0f;
        
        [Range(0.0f, 4.0f)]
        public float OvertakeEagerness = 1.0f;
        
        [Range(0f, 100f)]
        public float RedLightRunChance = 0f;
        
        [Range(0f, 100f)]
        public float YellowLightRunChance = 10f;
        
        [Range(0f, 100f)]
        public float YieldChance = 90f;
        
        [Range(0f, 1f)]
        public float SidewalkSpill = 0f; // 0 = stay inside white lines, larger = can go onto sidewalk slightly

        [Range(0f, 1f)]
        public float LaneJitter = 0f; // Jitter from center of lane (weaving/drunk-like behavior)

        public VehiclePersonality Clone()
        {
            return new VehiclePersonality
            {
                Name = this.Name,
                SpeedMultiplier = this.SpeedMultiplier,
                MinFollowDistanceMultiplier = this.MinFollowDistanceMultiplier,
                SafeReactionTimeMultiplier = this.SafeReactionTimeMultiplier,
                OvertakeEagerness = this.OvertakeEagerness,
                RedLightRunChance = this.RedLightRunChance,
                YellowLightRunChance = this.YellowLightRunChance,
                YieldChance = this.YieldChance,
                SidewalkSpill = this.SidewalkSpill,
                LaneJitter = this.LaneJitter
            };
        }

        public void Jitter(float factor = 0.1f)
        {
            SpeedMultiplier += Random.Range(-factor, factor);
            MinFollowDistanceMultiplier += Random.Range(-factor, factor);
            SafeReactionTimeMultiplier += Random.Range(-factor, factor);
            OvertakeEagerness += Random.Range(-factor * 2f, factor * 2f);
            
            // Keep parameters in reasonable limits
            SpeedMultiplier = Mathf.Clamp(SpeedMultiplier, 0.4f, 2.5f);
            MinFollowDistanceMultiplier = Mathf.Clamp(MinFollowDistanceMultiplier, 0.15f, 2.5f);
            SafeReactionTimeMultiplier = Mathf.Clamp(SafeReactionTimeMultiplier, 0.15f, 2.5f);
            OvertakeEagerness = Mathf.Clamp(OvertakeEagerness, 0.0f, 5.0f);
        }
    }
}
