using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class StopAtRedLightRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B6";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 9;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!context.IsWaitingAtRedLight) return;
            if (context.RedLightStopDist <= 0f) return;

            float stopDistance = 10f + context.CurrentSpeed * 0.5f;

            if (context.RedLightStopDist < stopDistance)
            {
                commands.RequestBrake((stopDistance - context.RedLightStopDist) / stopDistance);
            }
        }
    }
}
