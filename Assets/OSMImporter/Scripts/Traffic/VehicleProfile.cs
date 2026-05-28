using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// VehicleProfile — Physical specifications, clearances, and capabilities of different vehicle types.
    /// </summary>
    public class VehicleProfile
    {
        public VehicleMeshBuilder.VehicleType VehicleType;

        public float Length;
        public float Width;
        public float ComfortWidth;
        public float MinSideClearance;

        public float MaxSpeed;
        public float Accel;
        public float Decel;
        public float EmergencyDecel;

        public float TurnRadius;
        public float LateralAgility;

        public bool CanLaneSplit;
        public bool CanFilterToFront;
        public bool CanUseShoulder;
        public bool CanWrongWayShortDistance;

        public float Dominance; 
        public float ReverseReluctance;

        public static VehicleProfile CreateDefault(VehicleMeshBuilder.VehicleType type)
        {
            var p = new VehicleProfile { VehicleType = type };
            switch (type)
            {
                case VehicleMeshBuilder.VehicleType.Motorbike:
                    p.Length = 0.55f;
                    p.Width = 0.2f;
                    p.ComfortWidth = 0.45f;
                    p.MinSideClearance = 0.08f;
                    p.MaxSpeed = 12f;
                    p.Accel = 8f;
                    p.Decel = 6f;
                    p.EmergencyDecel = 12f;
                    p.TurnRadius = 1f;
                    p.LateralAgility = 10f;
                    p.CanLaneSplit = true;
                    p.CanFilterToFront = true;
                    p.CanUseShoulder = true;
                    p.CanWrongWayShortDistance = true;
                    p.Dominance = 0.1f;
                    p.ReverseReluctance = 0.05f; // Motorbikes don't reverse, they walk/scoot easily
                    break;

                case VehicleMeshBuilder.VehicleType.Bus:
                    p.Length = 2.5f;
                    p.Width = 0.625f;
                    p.ComfortWidth = 1.0f;
                    p.MinSideClearance = 0.35f;
                    p.MaxSpeed = 6f;
                    p.Accel = 2f;
                    p.Decel = 3f;
                    p.EmergencyDecel = 6f;
                    p.TurnRadius = 6f;
                    p.LateralAgility = 2f;
                    p.CanLaneSplit = false;
                    p.CanFilterToFront = false;
                    p.CanUseShoulder = false;
                    p.CanWrongWayShortDistance = false;
                    p.Dominance = 0.9f;
                    p.ReverseReluctance = 0.95f; // Extremely reluctant to reverse
                    break;

                default: // Car
                    p.Length = 1.1f;
                    p.Width = 0.45f;
                    p.ComfortWidth = 0.75f;
                    p.MinSideClearance = 0.2f;
                    p.MaxSpeed = 9f;
                    p.Accel = 5f;
                    p.Decel = 4f;
                    p.EmergencyDecel = 8f;
                    p.TurnRadius = 3f;
                    p.LateralAgility = 5f;
                    p.CanLaneSplit = false;
                    p.CanFilterToFront = false;
                    p.CanUseShoulder = false;
                    p.CanWrongWayShortDistance = false;
                    p.Dominance = 0.5f;
                    p.ReverseReluctance = 0.75f; // Reluctant to reverse
                    break;
            }
            return p;
        }
    }
}
