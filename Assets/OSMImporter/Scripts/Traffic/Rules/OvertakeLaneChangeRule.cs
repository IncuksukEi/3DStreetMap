using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class OvertakeLaneChangeRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B16";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 6;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.Ctx.IsOvertaking)
                return;
        }
    }
}
