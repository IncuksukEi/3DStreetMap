using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class BlockUnsafeLaneChangeRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B18";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 9;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (context.AheadVehicle == context.Ctx.OvertakingTarget) return;

            if (context.Ctx.IsOvertaking && context.AheadDistance < context.MinFollowDistance)
            {
                commands.DenyLaneChange();
                return;
            }

            if (context.AheadVehicle != null)
            {
                float closingSpeed = context.CurrentSpeed - context.AheadVehicle.Ctx.CurrentSpeed;
                if (closingSpeed > 5f)
                    commands.DenyLaneChange();
            }
        }
    }
}
