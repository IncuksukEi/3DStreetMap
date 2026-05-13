using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class AvoidFrontCollisionRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B1";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 10;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle == null) return;

            float safeDistance = context.MinFollowDistance + context.CurrentSpeed * context.SafeReactionTime;
            float dangerDistance = safeDistance * 0.375f; // ~3m at typical values
            float dist = context.AheadDistance;

            if (dist < dangerDistance)
            {
                commands.RequestBrake(1.0f);
            }
            else if (dist < safeDistance)
            {
                commands.RequestBrake((safeDistance - dist) / safeDistance);
            }
        }
    }

    public class NativeSumoAvoidFrontCollisionRule : ITrafficRule<NativeSumoRuleContext>
    {
        public string RuleId => "B1.NativeSumo";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 10;
        public bool Enabled { get; set; } = true;

        public void Execute(NativeSumoRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.FrontVehicle == null) return;

            float safeDistance = Mathf.Max(5f, context.Vehicle.length + context.CurrentSpeed);
            float dangerDistance = Mathf.Max(2f, safeDistance * 0.35f);
            float distance = context.DistanceToFrontVehicle;

            if (distance <= 0.5f)
                commands.RequestHardStop();
            else if (distance < dangerDistance)
                commands.RequestBrake(1f);
            else if (distance < safeDistance)
                commands.RequestBrake((safeDistance - distance) / safeDistance);
        }
    }
}
