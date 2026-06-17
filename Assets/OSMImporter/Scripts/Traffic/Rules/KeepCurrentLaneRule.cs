using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class KeepCurrentLaneRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B15";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 6;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!context.Ctx.IsOvertaking && !context.Ctx.IsStuck)
                commands.SetLateralOffset(context.Ctx.LaneOffset);
        }
    }
}
