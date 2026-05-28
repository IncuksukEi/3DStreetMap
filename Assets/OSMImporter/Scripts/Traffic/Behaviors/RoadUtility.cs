namespace OSMImporter.Traffic
{
    /// <summary>
    /// Static helpers cho road type: offset, speed multiplier, max width.
    /// Dùng chung cho tất cả behavior modules.
    /// </summary>
    public static class RoadUtility
    {
        public const float SidewalkRatio = 0.15f; // 15% of road base width is sidewalk on each side

        public static float GetRoadBaseWidth(string roadType)
        {
            if (string.IsNullOrEmpty(roadType)) return 4.0f;
            switch (roadType.ToLower())
            {
                case "motorway":      return 12f;
                case "trunk":         return 10f;
                case "primary":       return 8f;
                case "secondary":     return 7f;
                case "tertiary":      return 6f;
                case "residential":   return 5f;
                case "service":       return 3f;
                case "footway":       return 2f;
                case "pedestrian":    return 3f;
                case "path":          return 1.5f;
                case "cycleway":      return 2f;
                case "living_street": return 4f;
                case "unclassified":  return 5f;
                default:              return 4.0f;
            }
        }

        public static float GetSidewalkWidth(string roadType)
        {
            return GetRoadBaseWidth(roadType) * SidewalkRatio;
        }

        public static float GetTotalMeshWidth(string roadType)
        {
            float baseW = GetRoadBaseWidth(roadType);
            return baseW + 2f * (baseW * SidewalkRatio);
        }

        public static float GetMaxOffset(string roadType)
        {
            // MaxOffset represents the boundary of the driving carriageway (road base half width)
            return GetRoadBaseWidth(roadType) / 2f;
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
