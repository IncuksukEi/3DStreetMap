using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class SmoothDecelerationRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B12";
        public TrafficRuleStage Stage => TrafficRuleStage.Act;
        public int Priority => 4;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!context.EmergencyBraking && context.CurrentSpeed > context.DesiredSpeed)
            {
                float maxDecel = context.BaseSpeed * 4f * context.DeltaTime;
                float diff = context.CurrentSpeed - context.DesiredSpeed;
                if (diff > maxDecel)
                    commands.SetTargetSpeed(context.CurrentSpeed - maxDecel);
            }
        }
    }
}
