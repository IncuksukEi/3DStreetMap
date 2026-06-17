using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    /// <summary>
    /// Traffic rule that checks for nearby crossing pedestrians.
    /// Commands the vehicle to stop or brake proportionally.
    /// </summary>
    public class YieldToPedestrianRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "B21";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 9;
        public bool Enabled { get; set; } = true;

        private const float DETECTION_RADIUS = 15f;
        private const float YIELD_ANGLE = 40f; // field of view angle

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            if (!Enabled) return;

            VehicleContext ctx = context.Ctx;
            Transform t = ctx.Transform;

            Vector3 myPos = t.position;
            Vector3 fwd = t.forward;
            fwd.y = 0;
            fwd.Normalize();

            // Find all potential obstacles within radius
            Collider[] colliders = Physics.OverlapSphere(myPos, DETECTION_RADIUS);
            foreach (var col in colliders)
            {
                PedestrianAgent ped = col.GetComponentInParent<PedestrianAgent>();
                if (ped == null || !ped.IsCrossing) continue;

                Vector3 pedPos = ped.transform.position;
                Vector3 toPed = pedPos - myPos;
                toPed.y = 0; // ignore altitude

                float dist = toPed.magnitude;
                if (dist < 0.01f) continue;

                // Check if pedestrian is in the forward direction
                float angle = Vector3.Angle(fwd, toPed);
                if (angle > YIELD_ANGLE) continue;

                // Calculate projection on path
                float dot = Vector3.Dot(toPed.normalized, fwd);
                float projectedDist = dist * dot;
                float lateralOffset = Mathf.Sqrt(Mathf.Max(0f, dist * dist - projectedDist * projectedDist));

                // If pedestrian is crossing or standing on the vehicle's road lane (within 2.2m lateral offset)
                if (lateralOffset < 2.2f && projectedDist > 0f)
                {
                    if (projectedDist < 3.5f)
                    {
                        // Stop immediately if pedestrian is very close
                        commands.RequestHardStop();
                    }
                    else if (projectedDist < 10f)
                    {
                        // Apply braking force proportionally
                        float brakeForce = (10f - projectedDist) / 6.5f;
                        commands.RequestBrake(Mathf.Clamp01(brakeForce));
                    }
                }
            }
        }
    }
}
