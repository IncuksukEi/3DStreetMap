using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class StableHeadingRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B14";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 5;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!context.Ctx.IsOvertaking)
                commands.DenyLaneChange();
        }
    }
}
