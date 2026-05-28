using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class AvoidLaneChangeCollisionRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B2";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 10;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle == null) return;

            float safeDistance = context.MinFollowDistance + context.CurrentSpeed * context.SafeReactionTime;

            if (context.AheadDistance < safeDistance && context.Ctx.IsOvertaking)
            {
                commands.DenyLaneChange();
            }
        }
    }
}
