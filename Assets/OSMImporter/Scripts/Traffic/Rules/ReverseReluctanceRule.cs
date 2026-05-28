using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    /// <summary>
    /// ReverseReluctanceRule — Implements realistic reluctance to reverse in Vietnamese street traffic.
    /// Denies reversing when vehicles are behind, inside intersections, on main roads, or when the profile/driver profile forbids it.
    /// </summary>
    public class ReverseReluctanceRule : ITrafficRule<OsmVehicleRuleContext>
    {
        public string RuleId => "ReverseReluctance";
        public TrafficRuleStage Stage => TrafficRuleStage.Constrain;
        public int Priority => 80;
        public bool Enabled { get; set; } = true;

        public void Execute(OsmVehicleRuleContext context, TrafficRuleCommandBuffer commands)
        {
            var ctx = context.Agent.Ctx;
            if (ctx == null) return;

            // Only relevant when a reversing timer is active or deadlock level wants to reverse
            if (ctx.ReversingTimer <= 0f && ctx.DeadlockLevel != 1) return;

            float need = 0f;
            if (ctx.StuckTimer > 3f) need += 0.5f;
            if (ctx.StuckTimer > 6f) need += 1.0f;

            float penalty = 0f;

            // Roads and intersection checks
            if (ctx.CurrentRoadType == "motorway" || ctx.CurrentRoadType == "primary")
            {
                penalty += 1.2f;
            }

            if (ctx.PathIdx < ctx.Path.Count && ctx.Path[ctx.PathIdx].ConnectedWaypointIds.Count > 2)
            {
                penalty += 1.0f; // Inside intersection
            }

            // Check if vehicles are behind (close)
            if (HasVehicleBehind(ctx))
            {
                penalty += 1.5f;
            }

            // Profile and personality reluctance
            if (ctx.Profile != null)
            {
                penalty += ctx.Profile.ReverseReluctance * 0.8f;
            }
            if (ctx.Driver != null)
            {
                penalty += ctx.Driver.ReverseReluctance * 0.5f;
            }

            float score = need - penalty;
            bool allowed = score > -0.2f; // allowed threshold

            if (!allowed)
            {
                // Force disable reversing
                ctx.ReversingTimer = 0f;
            }
        }

        private bool HasVehicleBehind(VehicleContext ctx)
        {
            Transform t = ctx.Transform;
            if (t == null) return false;

            float length = 1.1f;
            if (ctx.VehicleType == VehicleMeshBuilder.VehicleType.Bus) length = 2.5f;
            else if (ctx.VehicleType == VehicleMeshBuilder.VehicleType.Motorbike) length = 0.55f;

            float checkDist = length * 1.5f;
            Vector3 origin = t.position + Vector3.up * 0.5f;
            Vector3 dir = -t.forward;

            float halfW = Mathf.Clamp(ctx.VehicleWidth * 0.45f, 0.3f, 1.0f);
            Vector3 halfExtents = new Vector3(halfW, 0.4f, 0.15f);
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);

            RaycastHit[] hits = Physics.BoxCastAll(origin, halfExtents, dir, rot, checkDist, ctx.VehicleLayer);
            for (int i = 0; i < hits.Length; i++)
            {
                var other = hits[i].collider.GetComponentInParent<VehicleAgent>();
                if (other != null && other != ctx.Agent)
                    return true;
            }
            return false;
        }
    }
}
