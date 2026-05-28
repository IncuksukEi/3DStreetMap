using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class FullStopWhenTooCloseRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B5";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 10;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle != null && context.AheadDistance <= 0.5f)
            {
                commands.RequestHardStop();
            }
        }
    }
}
