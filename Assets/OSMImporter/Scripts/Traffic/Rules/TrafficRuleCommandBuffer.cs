using UnityEngine;

namespace OSMImporter.Traffic.Rules
{
    public class TrafficRuleCommandBuffer
    {
        float? _hardStop;
        float _maxSpeedLimit = float.MaxValue;
        float _targetSpeed = -1f;
        float _brakeForce;
        float _acceleration;

        float? _lateralOffset;
        bool _denyLaneChange;

        bool _denySpawn;
        bool _denyTransition;

        // ── Speed commands ──

        public void RequestHardStop() => _hardStop = 0f;

        public void SetMaxSpeed(float speed)
        {
            if (speed < _maxSpeedLimit)
                _maxSpeedLimit = speed;
        }

        public void SetTargetSpeed(float speed)
        {
            if (speed < 0f) return;

            if (_targetSpeed < 0f || speed < _targetSpeed)
                _targetSpeed = speed;
        }

        public void RequestBrake(float force01)
        {
            force01 = Mathf.Clamp01(force01);
            if (force01 > _brakeForce)
                _brakeForce = force01;
        }

        public void RequestAcceleration(float accel) => _acceleration += accel;

        // ── Lateral commands ──

        public void SetLateralOffset(float offset)
        {
            if (!_lateralOffset.HasValue)
                _lateralOffset = offset;
        }

        public void DenyLaneChange() => _denyLaneChange = true;

        // ── Spawn / Transition (SUMO) ──

        public void DenySpawn() => _denySpawn = true;

        public void DenyTransition() => _denyTransition = true;

        // ── Resolution getters ──

        public bool ShouldHardStop => _hardStop.HasValue;

        public float ResolvedMaxSpeed => _maxSpeedLimit;

        public float ResolvedTargetSpeed => _targetSpeed;

        public float ResolvedBrakeForce => _brakeForce;

        public float ResolvedAcceleration => _brakeForce > 0f ? 0f : _acceleration;

        public float? ResolvedLateralOffset => _lateralOffset;

        public bool IsLaneChangeDenied => _denyLaneChange;

        public bool IsSpawnDenied => _denySpawn;

        public bool IsTransitionDenied => _denyTransition;

        // ── Lifecycle ──

        public void Clear()
        {
            _hardStop = null;
            _maxSpeedLimit = float.MaxValue;
            _targetSpeed = -1f;
            _brakeForce = 0f;
            _acceleration = 0f;
            _lateralOffset = null;
            _denyLaneChange = false;
            _denySpawn = false;
            _denyTransition = false;
        }
    }
}
