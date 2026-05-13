using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class YieldIntersectionRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B20";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 7;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle != null
                && context.AheadVehicle.Ctx.CurrentSpeed < 0.5f
                && context.AheadDistance < context.MinFollowDistance * 3f)
            {
                commands.RequestBrake(0.5f);
            }
        }
    }
}
