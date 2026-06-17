using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    /// <summary>
    /// BehaviorArbitrator — Reconciles all competing DrivingDesires generated in the frame
    /// by grouping them by priority levels and blending intents based on driver profiles.
    /// </summary>
    public class BehaviorArbitrator
    {
        private readonly List<DrivingDesire> _desires = new List<DrivingDesire>();

        public void Clear()
        {
            _desires.Clear();
        }

        public void AddDesire(DrivingDesire desire)
        {
            _desires.Add(desire);
        }

        public void Arbitrate(VehicleContext ctx)
        {
            if (ctx == null) return;

            // default targets
            float finalBrakeIntent = 0f;
            float targetSpeed = ctx.BaseSpeed * ctx.RuntimeSpeedScale;
            float targetOffset = ctx.LaneOffset;
            bool shouldHardStop = false;

            // Group desires
            DrivingDesire safetyDesire = DrivingDesire.CreateDefault("None");
            DrivingDesire routeDesire = DrivingDesire.CreateDefault("None");
            DrivingDesire lawDesire = DrivingDesire.CreateDefault("None");
            DrivingDesire opportunisticDesire = DrivingDesire.CreateDefault("None");

            bool hasSafety = false;
            bool hasRoute = false;
            bool hasLaw = false;
            bool hasOpportunistic = false;

            for (int i = 0; i < _desires.Count; i++)
            {
                var d = _desires[i];
                string src = d.Source;

                // 1. Safety & Collision
                if (src == "B1" || src == "B5" || src == "ObstacleSensor" || src == "ProximityBrake" || src == "SafetyOverride" || src == "BrakeForceRule" || d.Risk > 0.8f)
                {
                    if (!hasSafety || d.Urgency > safetyDesire.Urgency)
                    {
                        safetyDesire = d;
                        hasSafety = true;
                    }
                }
                // 2. Traffic Signal & Laws
                else if (src == "B6" || src == "B8" || src == "B9" || src == "TrafficRules" || src == "DoNotRunRedLightRule" || src == "StopAtRedLightRule" || src == "PrepareStopYellowRule" || src == "DoNotRunRedLightRule" || src == "MaxSpeedLimitRule")
                {
                    if (!hasLaw || d.Urgency > lawDesire.Urgency)
                    {
                        lawDesire = d;
                        hasLaw = true;
                    }
                }
                // 3. Navigation & Path following
                else if (src == "B10" || src == "B11" || src == "B12" || src == "PathNavigator" || src == "OvertakeController" || src == "B17" || src == "MaintainDesiredSpeedRule" || src == "PrepareTurnLaneChangeRule" || src == "KeepCurrentLaneRule" || src == "TargetSpeedRule" || src == "LateralOffsetRule")
                {
                    if (!hasRoute || d.Urgency > routeDesire.Urgency)
                    {
                        routeDesire = d;
                        hasRoute = true;
                    }
                }
                // 4. Vietnamese opportunistic gap seeking
                else if (src == "GapExploitation" || src == "MotorbikeController" || src == "B16" || src == "OvertakeLaneChangeRule")
                {
                    if (!hasOpportunistic || d.Urgency > opportunisticDesire.Urgency)
                    {
                        opportunisticDesire = d;
                        hasOpportunistic = true;
                    }
                }
            }

            // Resolve speed and braking
            if (hasSafety && (safetyDesire.BrakeIntent > 0f || safetyDesire.TargetSpeed == 0f))
            {
                // Hard safety override
                shouldHardStop = (safetyDesire.BrakeIntent >= 0.9f || safetyDesire.TargetSpeed == 0f);
                finalBrakeIntent = safetyDesire.BrakeIntent;
                targetSpeed = Mathf.Max(0f, safetyDesire.TargetSpeed);
            }
            else
            {
                float lawWeight = (ctx.Driver != null) ? ctx.Driver.Lawfulness : 0.8f;
                float oppWeight = (ctx.Driver != null) ? ctx.Driver.Opportunism : 0.5f;

                // Base speed from path/desired speed
                if (hasRoute && routeDesire.TargetSpeed >= 0f)
                {
                    targetSpeed = routeDesire.TargetSpeed;
                }

                // Apply traffic light constraints
                if (hasLaw && lawDesire.TargetSpeed >= 0f)
                {
                    float lawSpeed = lawDesire.TargetSpeed;
                    
                    // Aggressive/Opportunistic drivers creep past or violate red lights
                    if (lawSpeed == 0f && lawWeight < 0.3f && oppWeight > 0.6f)
                    {
                        targetSpeed = Mathf.Max(targetSpeed * 0.25f, 1.2f); // Slow creep instead of stop
                    }
                    else
                    {
                        // Normal soft blend based on driver lawfulness
                        targetSpeed = Mathf.Lerp(targetSpeed, lawSpeed, lawWeight);
                        if (lawSpeed == 0f && lawWeight > 0.5f)
                        {
                            finalBrakeIntent = Mathf.Max(finalBrakeIntent, lawDesire.BrakeIntent > 0f ? lawDesire.BrakeIntent : 0.8f);
                        }
                    }
                }

                // Apply gap exploitation desires
                if (hasOpportunistic && opportunisticDesire.TargetSpeed >= 0f)
                {
                    float oppSpeed = opportunisticDesire.TargetSpeed;
                    targetSpeed = Mathf.Lerp(targetSpeed, oppSpeed, oppWeight);
                }
            }

            // Resolve lateral offsets
            if (hasSafety && !float.IsNaN(safetyDesire.TargetLateralOffset))
            {
                targetOffset = safetyDesire.TargetLateralOffset;
            }
            else
            {
                float oppWeight = (ctx.Driver != null) ? ctx.Driver.Opportunism : 0.5f;
                float baseOffset = ctx.LaneOffset;

                if (hasRoute && !float.IsNaN(routeDesire.TargetLateralOffset))
                {
                    baseOffset = routeDesire.TargetLateralOffset;
                }

                if (hasOpportunistic && !float.IsNaN(opportunisticDesire.TargetLateralOffset))
                {
                    // Blend toward weave target based on driver opportunism
                    targetOffset = Mathf.Lerp(baseOffset, opportunisticDesire.TargetLateralOffset, oppWeight);
                }
                else
                {
                    targetOffset = baseOffset;
                }
            }

            // Apply arbitrated outputs to context
            if (shouldHardStop || finalBrakeIntent >= 0.95f)
            {
                ctx.DesiredSpeed = 0f;
                ctx.CurrentSpeed = 0f;
                ctx.EmergencyBraking = true;
                ctx.Braking = true;
            }
            else
            {
                ctx.DesiredSpeed = targetSpeed;
                if (finalBrakeIntent > 0.05f)
                {
                    ctx.Braking = true;
                    ctx.DesiredSpeed = Mathf.Min(ctx.DesiredSpeed, ctx.CurrentSpeed * (1f - finalBrakeIntent));
                }
            }

            // Clamp and commit
            float maxOff = ctx.CurrentMaxOffset;
            ctx.TargetOvertakeOffset = Mathf.Clamp(targetOffset, -maxOff, maxOff);
        }
    }
}
