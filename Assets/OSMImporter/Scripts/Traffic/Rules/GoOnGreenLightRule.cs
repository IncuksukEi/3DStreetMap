using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class GoOnGreenLightRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B7";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 7;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!context.IsWaitingAtRedLight && context.CurrentSpeed < context.DesiredSpeed)
            {
                commands.RequestAcceleration(
                    (context.DesiredSpeed - context.CurrentSpeed) / Mathf.Max(context.DesiredSpeed, 1f));
            }
        }
    }
}
