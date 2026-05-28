using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class PrepareTurnLaneChangeRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B17";
        public TrafficRuleStage Stage => TrafficRuleStage.Plan;
        public int Priority => 7;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            // Handled by OvertakeController.PrepareTurn - stub for future pipeline migration
        }
    }
}
