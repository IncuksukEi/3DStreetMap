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

        // Rule pipeline (safety constraints that sit on top of behavior modules)
        private TrafficRulePipeline<OsmVehicleRuleContext> _rulePipeline;
        private OsmVehicleRuleContext _ruleCtx;

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
            if (!TryGetComponent<Rigidbody>(out var rb))
                rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;

            int lyr = LayerMaskToLayer(VehicleLayer);
            if (lyr > 0) gameObject.layer = lyr;

            if (Graph == null) Graph = FindFirstObjectByType<WaypointGraph>();
            if (Graph == null || Graph.Waypoints.Count == 0) { enabled = false; return; }

            Waypoint nearest = Graph.FindNearest(transform.position);
            if (nearest == null) { enabled = false; return; }

            // ── Khởi tạo Context ──
            bool isMoto = VehicleType == VehicleMeshBuilder.VehicleType.Motorbike;
            float laneOffset = RoadUtility.GetLaneOffset(nearest.RoadType) + (isMoto ? 0.4f : 0f);

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
                StartNodeId        = nearest.OSMNodeId,
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
            };

            transform.position = WithY(nearest.Position);

            // ── Khởi tạo Behavior Modules ──
            Sensor            = new ObstacleSensor(Ctx);
            SpeedCtrl         = new SpeedController(Ctx);
            TrafficRules      = new TrafficRuleHandler(Ctx);
            OvertakeCtrl      = new OvertakeController(Ctx);
            DeadlockResolver  = new DeadlockResolver(Ctx);
            Mover             = new VehicleMover(Ctx);
            CongestionTracker = new CongestionTracker(Ctx);
            Navigator         = new PathNavigator(Ctx);

            // ── Khởi tạo Rule Pipeline ──
            _ruleCtx = new OsmVehicleRuleContext(this);
            _rulePipeline = new TrafficRulePipeline<OsmVehicleRuleContext>();
            TrafficRuleRegistry<OsmVehicleRuleContext>.Instance.PopulateDefaults(_rulePipeline);
            RegisterDefaultOsmRules();

            // ── Chọn điểm đích đầu tiên ──
            Navigator.PickNewDestination();
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
            if (commands.ShouldHardStop)
            {
                Ctx.DesiredSpeed = 0f;
                Ctx.CurrentSpeed = 0f;
                Ctx.EmergencyBraking = true;
            }

            if (commands.ResolvedTargetSpeed >= 0f)
            {
                Ctx.DesiredSpeed = Mathf.Min(Ctx.DesiredSpeed, commands.ResolvedTargetSpeed);
            }

            if (commands.ResolvedMaxSpeed < float.MaxValue)
            {
                Ctx.DesiredSpeed = Mathf.Min(Ctx.DesiredSpeed, commands.ResolvedMaxSpeed);
            }

            if (commands.ResolvedBrakeForce > 0f)
            {
                Ctx.DesiredSpeed = Mathf.Min(Ctx.DesiredSpeed,
                    Ctx.CurrentSpeed * (1f - commands.ResolvedBrakeForce));
            }

            if (commands.IsLaneChangeDenied && Ctx.IsOvertaking)
            {
                Ctx.IsOvertaking = false;
                Ctx.OvertakingTarget = null;
                Ctx.OvertakeSide = 0f;
                Ctx.TargetOvertakeOffset = Ctx.LaneOffset;
                Ctx.OvertakeCooldown = 1.0f;
            }

            if (commands.ResolvedLateralOffset.HasValue)
            {
                float maxOff = Ctx.CurrentMaxOffset;
                Ctx.TargetOvertakeOffset = Mathf.Clamp(commands.ResolvedLateralOffset.Value, -maxOff, maxOff);
            }
        }

        private void RegisterDefaultOsmRules()
        {
            _rulePipeline.AddRule(new AvoidFrontCollisionRule());
            _rulePipeline.AddRule(new AvoidLaneChangeCollisionRule());
            _rulePipeline.AddRule(new MaintainSafeDistanceRule());
            _rulePipeline.AddRule(new MaxSpeedLimitRule());
            _rulePipeline.AddRule(new FullStopWhenTooCloseRule());
            _rulePipeline.AddRule(new StopAtRedLightRule());
            _rulePipeline.AddRule(new GoOnGreenLightRule());
            _rulePipeline.AddRule(new PrepareStopYellowRule());
            _rulePipeline.AddRule(new DoNotRunRedLightRule());
            _rulePipeline.AddRule(new MaintainDesiredSpeedRule());
            _rulePipeline.AddRule(new SmoothAccelerationRule());
            _rulePipeline.AddRule(new SmoothDecelerationRule());
            _rulePipeline.AddRule(new ClampAccelerationRule());
            _rulePipeline.AddRule(new StableHeadingRule());
            _rulePipeline.AddRule(new KeepCurrentLaneRule());
            _rulePipeline.AddRule(new OvertakeLaneChangeRule());
            _rulePipeline.AddRule(new PrepareTurnLaneChangeRule());
            _rulePipeline.AddRule(new BlockUnsafeLaneChangeRule());
            _rulePipeline.AddRule(new BlockedIntersectionRule());
            _rulePipeline.AddRule(new YieldIntersectionRule());
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
}
