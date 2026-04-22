using System.Collections.Generic;
using UnityEngine;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// Quản lý pool xe Unity tương ứng với xe SUMO.
    /// Mỗi frame: spawn/despawn xe theo danh sách SUMO, cập nhật position/rotation mượt.
    /// </summary>
    public class SumoVehicleSync
    {
        // Mapping: SUMO vehicle ID → Unity GameObject
        private readonly Dictionary<string, SumoVehicleInstance> _vehicles = new();
        private readonly SumoToUnityMapper _mapper;
        private readonly Transform _parent;

        // Smoothing
        private readonly float _positionLerpSpeed;
        private readonly float _rotationLerpSpeed;

        public int ActiveVehicleCount => _vehicles.Count;

        public SumoVehicleSync(SumoToUnityMapper mapper, Transform parent,
                                float posLerp = 12f, float rotLerp = 8f)
        {
            _mapper = mapper;
            _parent = parent;
            _positionLerpSpeed = posLerp;
            _rotationLerpSpeed = rotLerp;
        }

        // ══════════════════════════════════════════════════════════════════
        // SYNC — gọi mỗi frame từ SumoBridge
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cập nhật toàn bộ xe từ SUMO states.
        /// Tự động spawn xe mới, despawn xe đã rời, lerp position mượt.
        /// </summary>
        public void Sync(List<TraCIClient.VehicleState> states, float dt)
        {
            var activeIds = new HashSet<string>();

            foreach (var state in states)
            {
                activeIds.Add(state.Id);

                if (!_vehicles.TryGetValue(state.Id, out var instance))
                {
                    // Xe mới xuất hiện trong SUMO → spawn Unity GameObject
                    instance = SpawnVehicle(state);
                    _vehicles[state.Id] = instance;
                }

                // Cập nhật target (lerp sẽ smooth trong frame)
                instance.TargetPosition = _mapper.SumoToUnity(state.Position);
                instance.TargetRotation = SumoToUnityMapper.SumoAngleToUnityRotation(state.Angle);
                instance.Speed = state.Speed;
            }

            // Xoá xe đã rời SUMO simulation
            var toRemove = new List<string>();
            foreach (var kvp in _vehicles)
            {
                if (!activeIds.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            foreach (var id in toRemove)
            {
                DespawnVehicle(id);
            }

            // Lerp tất cả xe đến target position/rotation
            foreach (var kvp in _vehicles)
            {
                var inst = kvp.Value;
                if (inst.GameObject == null) continue;

                Transform t = inst.GameObject.transform;
                t.position = Vector3.Lerp(t.position, inst.TargetPosition, dt * _positionLerpSpeed);
                t.rotation = Quaternion.Slerp(t.rotation, inst.TargetRotation, dt * _rotationLerpSpeed);

                // Spin wheels
                SpinWheels(inst, dt);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SPAWN / DESPAWN
        // ══════════════════════════════════════════════════════════════════

        private SumoVehicleInstance SpawnVehicle(TraCIClient.VehicleState state)
        {
            // Map SUMO vehicle type → Unity VehicleType
            var vType = MapSumoType(state.VehicleType);

            // Random color
            Color color = Random.ColorHSV(0f, 1f, 0.5f, 0.8f, 0.6f, 0.9f);

            // Build mesh
            GameObject go = VehicleMeshBuilder.Build(vType, color);
            go.name = $"SUMO_{state.Id}";

            if (_parent != null)
                go.transform.SetParent(_parent, false);

            // Set initial position (no lerp)
            Vector3 pos = _mapper.SumoToUnity(state.Position);
            go.transform.position = pos;
            go.transform.rotation = SumoToUnityMapper.SumoAngleToUnityRotation(state.Angle);

            // Thu thập wheel transforms
            var wheels = new List<Transform>();
            foreach (Transform child in go.transform)
            {
                if (child.name.StartsWith("Wheel"))
                    wheels.Add(child);
            }

            // Thêm collider để VehicleInspector có thể raycast
            var boxCol = go.AddComponent<BoxCollider>();
            var renderer = go.GetComponentInChildren<MeshRenderer>();
            if (renderer != null)
                boxCol.size = renderer.bounds.size;

            return new SumoVehicleInstance
            {
                GameObject = go,
                SumoId = state.Id,
                VehicleType = vType,
                TargetPosition = pos,
                TargetRotation = go.transform.rotation,
                Speed = state.Speed,
                Wheels = wheels.ToArray()
            };
        }

        private void DespawnVehicle(string sumoId)
        {
            if (_vehicles.TryGetValue(sumoId, out var instance))
            {
                if (instance.GameObject != null)
                    Object.Destroy(instance.GameObject);
                _vehicles.Remove(sumoId);
            }
        }

        /// <summary>Xoá tất cả xe (gọi khi disconnect).</summary>
        public void Clear()
        {
            foreach (var kvp in _vehicles)
            {
                if (kvp.Value.GameObject != null)
                    Object.Destroy(kvp.Value.GameObject);
            }
            _vehicles.Clear();
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════

        private static VehicleMeshBuilder.VehicleType MapSumoType(string sumoType)
        {
            if (string.IsNullOrEmpty(sumoType)) return VehicleMeshBuilder.VehicleType.Car;

            string lower = sumoType.ToLowerInvariant();
            if (lower.Contains("bus") || lower.Contains("coach")) return VehicleMeshBuilder.VehicleType.Bus;
            if (lower.Contains("moto") || lower.Contains("bicycle") || lower.Contains("bike"))
                return VehicleMeshBuilder.VehicleType.Motorbike;
            return VehicleMeshBuilder.VehicleType.Car;
        }

        private void SpinWheels(SumoVehicleInstance inst, float dt)
        {
            if (inst.Wheels == null || inst.Wheels.Length == 0) return;
            float wheelRadius = 0.75f;
            float degPerSec = inst.Speed / wheelRadius * Mathf.Rad2Deg;
            foreach (var w in inst.Wheels)
            {
                if (w != null) w.Rotate(0f, degPerSec * dt, 0f, Space.Self);
            }
        }

        /// <summary>Lấy instance theo SUMO ID (cho Inspector/debug).</summary>
        public SumoVehicleInstance GetVehicle(string sumoId)
        {
            _vehicles.TryGetValue(sumoId, out var inst);
            return inst;
        }

        /// <summary>Lấy tất cả instances.</summary>
        public IReadOnlyDictionary<string, SumoVehicleInstance> GetAll() => _vehicles;
    }

    /// <summary>
    /// State 1 xe SUMO trong Unity.
    /// </summary>
    public class SumoVehicleInstance
    {
        public GameObject GameObject;
        public string SumoId;
        public VehicleMeshBuilder.VehicleType VehicleType;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public float Speed;
        public Transform[] Wheels;
    }
}
