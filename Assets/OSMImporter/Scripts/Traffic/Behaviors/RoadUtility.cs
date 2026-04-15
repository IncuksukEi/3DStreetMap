namespace OSMImporter.Traffic
{
    /// <summary>
    /// Static helpers cho road type: offset, speed multiplier, max width.
    /// Dùng chung cho tất cả behavior modules.
    /// </summary>
    public static class RoadUtility
    {
        public static float GetMaxOffset(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 2.0f;
            switch (roadType)
            {
                case "motorway":      return 4.5f;
                case "trunk":         return 3.5f;
                case "primary":       return 3.0f;
                case "secondary":     return 2.5f;
                case "tertiary":      return 2.0f;
                case "residential":   return 1.5f;
                case "living_street": return 1.0f;
                default:              return 2.0f;
            }
        }

        public static float GetLaneOffset(string roadType)
        {
            float maxOffset = GetMaxOffset(roadType);
            bool isLanedRoad = maxOffset > 2.0f;
            float minOffset = isLanedRoad ? 1.0f : 0.2f;

            if (maxOffset <= 1.5f) return minOffset;

            float laneWidth = isLanedRoad ? 1.5f : 1.2f;
            int numLanes = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.FloorToInt((maxOffset - minOffset) / laneWidth) + 1);
            int pickedLane = UnityEngine.Random.Range(0, numLanes);

            // Nhiễu nhẹ ±0.2m để xe không đi trùng rãnh
            float jitter = UnityEngine.Random.Range(-0.2f, 0.2f);
            float offset = minOffset + pickedLane * laneWidth + jitter;
            return UnityEngine.Mathf.Clamp(offset, minOffset, maxOffset);
        }

        public static float GetRoadTypeSpeedMultiplier(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 1f;
            switch (roadType)
            {
                case "motorway":      return 1.5f;
                case "trunk":         return 1.25f;
                case "primary":       return 1f;
                case "secondary":     return 0.8f;
                case "tertiary":      return 0.7f;
                case "residential":   return 0.5f;
                case "living_street": return 0.35f;
                default:              return 1f;
            }
        }
    }
}
