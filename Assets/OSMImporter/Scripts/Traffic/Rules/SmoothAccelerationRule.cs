using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class SmoothAccelerationRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B11";
        public TrafficRuleStage Stage => TrafficRuleStage.Act;
        public int Priority => 4;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.CurrentSpeed < context.DesiredSpeed)
            {
                float maxAccel = context.BaseSpeed * 2f * context.DeltaTime;
                float diff = context.DesiredSpeed - context.CurrentSpeed;
                if (diff > maxAccel)
                    commands.SetTargetSpeed(context.CurrentSpeed + maxAccel);
            }
        }
    }
}
