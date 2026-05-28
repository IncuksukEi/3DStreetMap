using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class ClampAccelerationRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B13";
        public TrafficRuleStage Stage => TrafficRuleStage.Act;
        public int Priority => 6;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            float maxA = context.BaseSpeed * 3f;
            commands.SetMaxSpeed(context.CurrentSpeed + maxA * context.DeltaTime);
        }
    }
}
