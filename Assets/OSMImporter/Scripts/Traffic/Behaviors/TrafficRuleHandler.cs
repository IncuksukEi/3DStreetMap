using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// ② TrafficRuleHandler — Ưu tiên CAO
    /// CheckTrafficLight + CheckYielding + CheckIntersectionClearance (Don't block the box).
    /// </summary>
    public class TrafficRuleHandler
    {
        private const float INTERSECTION_YIELD_RANGE = 10f;

        private readonly VehicleContext _ctx;

        public TrafficRuleHandler(VehicleContext ctx) => _ctx = ctx;

        public void Execute()
        {
            CheckTrafficLight();
            CheckYielding();
            CheckIntersectionClearance();
        }

        // ══════════════════════════════════════════════════════════════════
        // TRAFFIC LIGHT — dừng đèn đỏ (Physics-based trigger detection)
        // ══════════════════════════════════════════════════════════════════

        private void CheckTrafficLight()
        {
            _ctx.RedLightStopDist = -1f;
            _ctx.IsWaitingAtRedLight = false;

            Transform t = _ctx.Transform;
            float scanDist = Mathf.Clamp(_ctx.CurrentSpeed * 2.5f, 10f, 15f);
            Vector3 scanCenter = t.position + t.forward * (scanDist * 0.5f);
            float scanRadius = scanDist * 0.55f;

            Collider[] hits = Physics.OverlapSphere(scanCenter, scanRadius, TrafficLightManager.TrafficLightMask);
            if (hits.Length == 0)
            {
                _ctx.RedLightWaitTimer = 0f;
                return;
            }

            TrafficLightTrigger bestTrigger = null;
            float bestDist = float.MaxValue;

            foreach (var col in hits)
            {
                TrafficLightTrigger trigger = col.GetComponent<TrafficLightTrigger>();
                if (trigger == null || trigger.IsGreen) continue;

                float dot = Vector3.Dot(t.forward, trigger.ApproachDir);
                if (dot < 0.4f) continue;

                Vector3 toTrigger = col.transform.position - t.position;
                toTrigger.y = 0f;
                float dotBehind = Vector3.Dot(t.forward, toTrigger.normalized);
                if (dotBehind < -0.1f) continue;

                float dist = toTrigger.magnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestTrigger = trigger;
                }
            }

            if (bestTrigger == null)
            {
                _ctx.RedLightWaitTimer = 0f;
                return;
            }

            _ctx.RedLightStopDist = bestDist;
            _ctx.IsWaitingAtRedLight = true;

            // Timeout 60s anti-deadlock
            _ctx.RedLightWaitTimer += Time.deltaTime;
            if (_ctx.RedLightWaitTimer > 60f)
            {
                _ctx.IsWaitingAtRedLight = false;
                _ctx.RedLightStopDist = -1f;
                _ctx.RedLightWaitTimer = 0f;
                return;
            }

            if (bestDist < 2.0f)
            {
                _ctx.Braking = true;
            }
            else
            {
                float decel = 3f;
                float maxSafeSpeed = Mathf.Sqrt(2f * decel * bestDist);
                _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, maxSafeSpeed);

                // Đèn vàng + còn xa → cho phép đi qua
                if (bestTrigger.IsYellow && bestDist > 8f && _ctx.CurrentSpeed > _ctx.BaseSpeed * 0.6f)
                {
                    _ctx.IsWaitingAtRedLight = false;
                    _ctx.RedLightStopDist = -1f;
                    _ctx.RedLightWaitTimer = 0f;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // YIELDING — nhường đường tại ngã tư không đèn
        // ══════════════════════════════════════════════════════════════════

        private void CheckYielding()
        {
            if (_ctx.PathIdx >= _ctx.Path.Count) return;

            Waypoint nextWp = _ctx.Path[_ctx.PathIdx];
            if (nextWp.ConnectedWaypointIds.Count <= 2 || nextWp.IsTrafficLight) return;

            Transform t = _ctx.Transform;
            float myDist = Vector3.Distance(t.position, nextWp.Position);
            if (myDist > INTERSECTION_YIELD_RANGE) return;

            // Anti-deadlock sớm
            if (_ctx.StuckTimer > _ctx.Patience * 0.5f) return;

            float maxYieldConfidence = 0f;
            Collider[] nearby = Physics.OverlapSphere(nextWp.Position, INTERSECTION_YIELD_RANGE, _ctx.VehicleLayer);

            foreach (var col in nearby)
            {
                VehicleAgent other = col.GetComponentInParent<VehicleAgent>();
                if (other == null || other == _ctx.Agent || other.Ctx == null) continue;

                Vector3 toIntersection = nextWp.Position - other.transform.position;
                if (Vector3.Dot(other.transform.forward, toIntersection) < 0) continue;

                float otherDist = Vector3.Distance(other.transform.position, nextWp.Position);
                if (other.Ctx.CurrentSpeed < 0.3f) continue;

                Vector3 toOther = other.transform.position - t.position;
                bool isComingFromRight = Vector3.Cross(t.forward, toOther).y > 0;

                bool shouldYield = false;
                if (otherDist < myDist - 2f)
                    shouldYield = true;
                else if (Mathf.Abs(otherDist - myDist) <= 2f && isComingFromRight)
                    shouldYield = true;

                if (shouldYield)
                {
                    float conf = _ctx.Agent.Sensor.CalcConfidence(other, Vector3.Distance(t.position, other.transform.position));
                    conf *= 0.8f;
                    maxYieldConfidence = Mathf.Max(maxYieldConfidence, conf);
                }
            }

            if (maxYieldConfidence > 0.2f)
            {
                if (maxYieldConfidence > 0.7f)
                {
                    _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, Mathf.Max(0.5f, (myDist - 2.5f) * 2f));
                }
                else if (maxYieldConfidence > 0.4f)
                {
                    float factor = Mathf.InverseLerp(0.4f, 0.7f, maxYieldConfidence);
                    _ctx.DesiredSpeed = Mathf.Min(_ctx.DesiredSpeed, Mathf.Lerp(_ctx.DesiredSpeed, 2.5f, factor));
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // DON'T BLOCK THE BOX — chặn kẹt cứng ngã tư
        // ══════════════════════════════════════════════════════════════════

        private void CheckIntersectionClearance()
        {
            if (_ctx.PathIdx >= _ctx.Path.Count || _ctx.Braking || _ctx.DesiredSpeed < 0.1f) return;

            Waypoint nextWp = _ctx.Path[_ctx.PathIdx];
            if (nextWp.ConnectedWaypointIds.Count > 2 || nextWp.IsTrafficLight)
            {
                float myDistToIntersection = Vector3.Distance(_ctx.Transform.position, nextWp.Position);
                if (myDistToIntersection < 6f && myDistToIntersection > 1.5f)
                {
                    if (_ctx.AheadVehicle != null && _ctx.AheadVehicle.Ctx.CurrentSpeed < 0.5f)
                    {
                        if (_ctx.AheadDistance < myDistToIntersection + 2.5f)
                        {
                            _ctx.Braking = true;
                            _ctx.DesiredSpeed = 0f;
                        }
                    }
                }
            }
        }
    }
}
