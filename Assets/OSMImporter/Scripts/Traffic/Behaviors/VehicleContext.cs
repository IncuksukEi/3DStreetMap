using System.Collections.Generic;
using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Shared mutable state giữa tất cả behavior modules.
    /// VehicleAgent sở hữu instance duy nhất, các module đọc/ghi trực tiếp.
    /// </summary>
    public class VehicleContext
    {
        // ── Identity ──
        public VehicleAgent Agent;
        public Transform Transform;
        public VehicleMeshBuilder.VehicleType VehicleType;
        public float VehicleWidth;
        public float BaseSpeed;
        public float RuntimeSpeedScale;
        public LayerMask VehicleLayer;

        // ── Navigation ──
        public WaypointGraph Graph;
        public List<Waypoint> Path = new List<Waypoint>();
        public int PathIdx;
        public List<VehicleAgent.PathPoint> ExactPath = new List<VehicleAgent.PathPoint>();
        public int ExactPathIdx;
        public long StartNodeId;
        public long DestNodeId;
        public bool Rerouting;
        public bool DestroyOnArrival;

        // ── Speed / Driving ──
        public float CurrentSpeed;
        public float DesiredSpeed;
        public bool Braking;
        public bool EmergencyBraking;
        public bool IsWaitingAtRedLight;
        public float ReversingTimer;
        public float RedLightStopDist;
        public float RedLightWaitTimer;
        public long LastSeenLightId;
        public bool DecidedToViolateLight;

        // ── Scan results ──
        public VehicleAgent AheadVehicle;
        public float AheadDistance;
        public float AheadConfidence;

        // ── Overtake ──
        public float OvertakeOffset;
        public float TargetOvertakeOffset;
        public bool IsOvertaking;
        public float OvertakeCooldown;
        public VehicleAgent OvertakingTarget;
        public float OvertakeSide;
        public float LaneOffset;

        // ── Deadlock ──
        public float StuckTimer;
        public int DeadlockLevel;
        public bool IsStuck;
        public bool IsColliding;

        // ── Congestion ──
        public float CongestionCheckTimer;
        public float CongestionReportTimer;

        // ── Parameters (từ VehicleAgent Inspector) ──
        public float RotationSpeed;
        public float StopDistance;
        public float MaxSpeedLimit;
        public float MinFollowDistance;
        public float SafeReactionTime;
        public float EmergencyBrakePwr;
        public float Patience;

        public bool IsMoto => VehicleType == VehicleMeshBuilder.VehicleType.Motorbike;

        /// <summary>
        /// Road type tại waypoint hiện tại (hoặc rỗng nếu hết path).
        /// </summary>
        public string CurrentRoadType =>
            PathIdx < Path.Count ? Path[PathIdx].RoadType : "";

        /// <summary>
        /// Max offset tại waypoint hiện tại, trừ đi một nửa chiều rộng xe để xe không tràn ra ngoài vỉa hè.
        /// </summary>
        public float CurrentMaxOffset
        {
            get
            {
                float baseMax = RoadUtility.GetMaxOffset(CurrentRoadType);
                float spill = (Agent != null && Agent.Personality != null) ? Agent.Personality.SidewalkSpill : 0f;
                // Giới hạn max offset lùi vào một nửa thân xe để giữ xe hoàn toàn trong vạch trắng (lòng đường).
                // Các tính cách "đi ẩu" (Reckless/DeliveryMoto) có spill lớn sẽ được phép đè vạch/lên vỉa hè một chút.
                return Mathf.Max(0.2f, baseMax - (VehicleWidth * 0.5f + 0.1f) + spill);
            }
        }
    }
}
