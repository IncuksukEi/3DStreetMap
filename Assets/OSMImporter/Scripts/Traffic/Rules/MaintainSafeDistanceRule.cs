using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class MaintainSafeDistanceRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B3";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 9;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle == null) return;

            float safeDistance = context.MinFollowDistance + context.CurrentSpeed * context.SafeReactionTime;

            if (context.AheadDistance < safeDistance)
            {
                float brakeForce = (safeDistance - context.AheadDistance) / safeDistance * 0.7f;
                commands.RequestBrake(brakeForce);
            }
        }
    }
}
