using OSMImporter.Traffic.NativeSumo.Graph;

namespace OSMImporter.Traffic.Rules
{
    public class NativeSumoRuleContext
    {
        public SVehicle Vehicle { get; }
        public SLane CurrentLane { get; }
        public SNetwork Network { get; }
        public float StepDt { get; set; }

        public float CurrentSpeed => Vehicle.currentSpeed;
        public float MaxSpeed => Vehicle.maxSpeed;
        public float LaneMaxSpeed => CurrentLane?.maxSpeed ?? float.MaxValue;
        public float PositionOnLane => Vehicle.positionOnLane;
        public float LaneLength => CurrentLane?.length ?? 0f;
        public SVehicle FrontVehicle => FindFrontVehicle(CurrentLane);
        public float DistanceToFrontVehicle
        {
            get
            {
                var front = FrontVehicle;
                if (front == null) return float.MaxValue;
                return (front.positionOnLane - front.length) - Vehicle.positionOnLane;
            }
        }
        public float RelativeSpeedFront => FrontVehicle == null ? 0f : CurrentSpeed - FrontVehicle.currentSpeed;

        public NativeSumoRuleContext(SVehicle vehicle, SNetwork network)
        {
            Vehicle = vehicle;
            CurrentLane = vehicle.currentLane;
            Network = network;
        }

        private SVehicle FindFrontVehicle(SLane lane)
        {
            if (lane == null || lane.vehicles == null) return null;

            SVehicle front = null;
            float bestGap = float.MaxValue;

            for (int i = 0; i < lane.vehicles.Count; i++)
            {
                var other = lane.vehicles[i];
                if (other == null || other == Vehicle || other.positionOnLane <= Vehicle.positionOnLane)
                    continue;

                float gap = (other.positionOnLane - other.length) - Vehicle.positionOnLane;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    front = other;
                }
            }

            return front;
        }
    }
}
