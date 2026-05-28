using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class PrepareStopYellowRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B8";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 8;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            // Stub: requires YellowLightWarning field on VehicleContext
        }
    }
}
