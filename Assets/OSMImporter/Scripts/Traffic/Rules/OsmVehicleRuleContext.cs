using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class OsmVehicleRuleContext
    {
        public VehicleContext Ctx { get; }
        public VehicleAgent Agent { get; }
        public float DeltaTime { get; set; }

        public float CurrentSpeed => Ctx.CurrentSpeed;
        public float DesiredSpeed { get => Ctx.DesiredSpeed; set => Ctx.DesiredSpeed = value; }
        public float MaxSpeedLimit => Ctx.MaxSpeedLimit;
        public float AheadDistance => Ctx.AheadDistance;
        public VehicleAgent AheadVehicle => Ctx.AheadVehicle;
        public bool IsWaitingAtRedLight => Ctx.IsWaitingAtRedLight;
        public float RedLightStopDist => Ctx.RedLightStopDist;
        public float OvertakeOffset => Ctx.OvertakeOffset;
        public float CurrentMaxOffset => Ctx.CurrentMaxOffset;
        public float MinFollowDistance => Ctx.MinFollowDistance;
        public float SafeReactionTime => Ctx.SafeReactionTime;
        public float BaseSpeed => Ctx.BaseSpeed;
        public float VehicleWidth => Ctx.VehicleWidth;
        public bool Braking { get => Ctx.Braking; set => Ctx.Braking = value; }
        public bool EmergencyBraking { get => Ctx.EmergencyBraking; set => Ctx.EmergencyBraking = value; }

        public OsmVehicleRuleContext(VehicleAgent agent)
        {
            Agent = agent;
            Ctx = agent.Ctx;
        }
    }
}
