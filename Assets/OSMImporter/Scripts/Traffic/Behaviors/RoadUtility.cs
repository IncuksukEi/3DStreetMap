namespace OSMImporter.Traffic
{
    /// <summary>
    /// Static helpers cho road type: offset, speed multiplier, max width.
    /// Dùng chung cho tất cả behavior modules.
    /// </summary>
    public static class RoadUtility
    {
        public const float SidewalkRatio = 0.15f; // 15% of road base width is sidewalk on each side

        // Hệ số nhân chiều rộng đường — khớp với giá trị dùng khi Import OSM
        // Được gán từ WaypointGraph.RoadWidthMultiplier trong TrafficSpawner.Start()
        public static float WidthMultiplier = 1f;

        public static float GetRoadBaseWidth(string roadType)
        {
            float raw;
            if (string.IsNullOrEmpty(roadType)) raw = 4.0f;
            else switch (roadType.ToLower())
            {
                case "motorway":      raw = 12f; break;
                case "trunk":         raw = 10f; break;
                case "primary":       raw = 8f;  break;
                case "secondary":     raw = 7f;  break;
                case "tertiary":      raw = 6f;  break;
                case "residential":   raw = 5f;  break;
                case "service":       raw = 3f;  break;
                case "footway":       raw = 2f;  break;
                case "pedestrian":    raw = 3f;  break;
                case "path":          raw = 1.5f; break;
                case "cycleway":      raw = 2f;  break;
                case "living_street": raw = 4f;  break;
                case "unclassified":  raw = 5f;  break;
                default:              raw = 4.0f; break;
            }
            return raw * WidthMultiplier;
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
            // MaxOffset = nửa chiều rộng lòng đường (đã tính multiplier)
            return GetRoadBaseWidth(roadType) / 2f;
        }

        public static float GetLaneOffset(string roadType, VehicleMeshBuilder.VehicleType vehicleType)
        {
            float maxOffset = GetMaxOffset(roadType);
            
            // Chia làn đường ở bên phải tim đường (mỗi làn rộng khoảng 1.5m)
            int numLanes = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.FloorToInt(maxOffset / 1.5f));
            
            // Phân làn thông minh: Ô tô/Bus đi làn trong (trái), Xe máy đi làn ngoài (phải)
            int pickedLane = 0;
            if (vehicleType == VehicleMeshBuilder.VehicleType.Motorbike)
            {
                pickedLane = numLanes - 1; // Xe máy ưu tiên làn ngoài sát vỉa hè
            }
            else
            {
                pickedLane = 0; // Ô tô/Bus ưu tiên làn trong sát tim đường
            }

            // Tính tâm của làn đường được chọn
            float laneWidth = maxOffset / numLanes;
            float offset = (pickedLane + 0.5f) * laneWidth;

            // Nhiễu nhẹ ±0.15m cho tự nhiên, tránh xe đi xếp hàng thẳng tắp
            float jitter = UnityEngine.Random.Range(-0.15f, 0.15f);
            return UnityEngine.Mathf.Clamp(offset + jitter, 0.1f, maxOffset - 0.2f);
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
