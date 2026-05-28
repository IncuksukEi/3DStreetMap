using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class BlockedIntersectionRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B19";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 8;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle != null
                && context.AheadDistance < context.MinFollowDistance * 2f
                && context.AheadVehicle.Ctx.CurrentSpeed < 0.5f)
            {
                commands.RequestBrake(1.0f);
            }
        }
    }
}
