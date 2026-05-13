using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class MaintainDesiredSpeedRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B10";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 5;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            float diff = context.DesiredSpeed - context.CurrentSpeed;

            if (diff > 0f)
            {
                commands.RequestAcceleration(diff / Mathf.Max(context.DesiredSpeed, 1f));
            }
        }
    }
}
