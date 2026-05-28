using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class MaxSpeedLimitRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B4";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 8;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            commands.SetMaxSpeed(context.MaxSpeedLimit);

            if (context.CurrentSpeed > context.MaxSpeedLimit)
            {
                commands.RequestBrake((context.CurrentSpeed - context.MaxSpeedLimit) / context.MaxSpeedLimit);
            }
        }
    }

    public class NativeSumoMaxSpeedLimitRule : ITrafficRule<NativeSumoRuleContext>
    {
        public string RuleId => "B4.NativeSumo";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 8;
        public bool Enabled { get; set; } = true;

        public void Execute(NativeSumoRuleContext context, TrafficRuleCommandBuffer commands)
        {
            float laneLimit = context.LaneMaxSpeed > 0f ? context.LaneMaxSpeed : float.MaxValue;
            float maxSpeed = Mathf.Min(context.MaxSpeed, laneLimit);
            commands.SetMaxSpeed(maxSpeed);

            if (context.CurrentSpeed > maxSpeed && maxSpeed > 0f)
                commands.RequestBrake((context.CurrentSpeed - maxSpeed) / maxSpeed);
        }
    }
}
