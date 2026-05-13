using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class DoNotRunRedLightRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B9";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 10;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.IsWaitingAtRedLight && context.RedLightStopDist > 0f)
            {
                commands.RequestBrake(1.0f);
            }
        }
    }
}
