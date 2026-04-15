using UnityEngine;
using OSMImporter.Geo;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// Convert tọa độ SUMO (x_meters, y_meters) ↔ Unity (x, 0, z).
    /// 
    /// SUMO dùng Cartesian projection từ .net.xml (gốc = góc trái-dưới network).
    /// Unity dùng MercatorProjection từ originLat/originLon (tâm bounding box OSM).
    /// 
    /// Cả hai đều output meters → chỉ cần align offset gốc.
    /// </summary>
    public class SumoCoordinateMapper
    {
        // Offset gốc network SUMO (đọc từ .net.xml <location> tag)
        private Vector2 _sumoNetOffset;

        // Offset gốc Unity (tính từ origin lat/lon của map)
        private Vector3 _unityOriginOffset;

        // Origin lat/lon (center of OSM bounding box)
        private double _originLat;
        private double _originLon;

        /// <summary>
        /// Khởi tạo mapper từ OSM bounds.
        /// </summary>
        /// <param name="originLat">Vĩ độ gốc Unity (center of map)</param>
        /// <param name="originLon">Kinh độ gốc Unity (center of map)</param>
        /// <param name="sumoNetOffsetX">netOffset x từ SUMO .net.xml</param>
        /// <param name="sumoNetOffsetY">netOffset y từ SUMO .net.xml</param>
        public SumoCoordinateMapper(double originLat, double originLon,
                                     float sumoNetOffsetX = 0f, float sumoNetOffsetY = 0f)
        {
            _originLat = originLat;
            _originLon = originLon;
            _sumoNetOffset = new Vector2(sumoNetOffsetX, sumoNetOffsetY);

            // Unity gốc tọa độ = MercatorProjection(originLat, originLon) = (0, 0, 0)
            _unityOriginOffset = Vector3.zero;
        }

        /// <summary>
        /// Cập nhật offset từ SUMO .net.xml <location netOffset="x,y"/>
        /// Gọi sau khi parse network file.
        /// </summary>
        public void SetSumoNetOffset(float x, float y)
        {
            _sumoNetOffset = new Vector2(x, y);
        }

        /// <summary>
        /// Convert SUMO position (meters) → Unity world position.
        /// 
        /// SUMO coords: x=East, y=North (từ gốc network + netOffset)
        /// Unity coords: x=East, z=North, y=Up=0
        /// 
        /// Cả hai dùng Mercator → meters, chỉ khác gốc tọa độ.
        /// </summary>
        public Vector3 SumoToUnity(float sumoX, float sumoY)
        {
            // SUMO position đã bao gồm netOffset
            // Unity position = 0 tại originLat/originLon
            // → trừ netOffset để về gốc OSM, rồi map sang Unity
            float unityX = sumoX - _sumoNetOffset.x;
            float unityZ = sumoY - _sumoNetOffset.y;

            return new Vector3(unityX, 0f, unityZ);
        }

        /// <summary>
        /// Convert SUMO position (Vector2) → Unity.
        /// </summary>
        public Vector3 SumoToUnity(Vector2 sumoPos)
            => SumoToUnity(sumoPos.x, sumoPos.y);

        /// <summary>
        /// Convert Unity world position → SUMO position (inverseo).
        /// </summary>
        public Vector2 UnityToSumo(Vector3 unityPos)
        {
            float sumoX = unityPos.x + _sumoNetOffset.x;
            float sumoY = unityPos.z + _sumoNetOffset.y;
            return new Vector2(sumoX, sumoY);
        }

        /// <summary>
        /// SUMO angle → Unity Y rotation.
        /// SUMO: 0=North, clockwise (degrees).
        /// Unity: Quaternion.Euler(0, yAngle, 0) — 0=+Z(North), clockwise.
        /// → Convention trùng nhau.
        /// </summary>
        public static float SumoAngleToUnityYRotation(float sumoAngle)
        {
            return sumoAngle;
        }

        /// <summary>
        /// Tạo Unity Quaternion từ SUMO angle.
        /// </summary>
        public static Quaternion SumoAngleToUnityRotation(float sumoAngle)
        {
            return Quaternion.Euler(0f, sumoAngle, 0f);
        }

        /// <summary>
        /// Calibrate tự động: dùng 1 waypoint đã biết để tính offset chính xác.
        /// Gọi khi khởi tạo với 1 xe SUMO đang ở vị trí OSM node đã biết.
        /// </summary>
        public void CalibrateWithKnownPoint(Vector2 sumoPos, Vector3 unityPos)
        {
            _sumoNetOffset = new Vector2(
                sumoPos.x - unityPos.x,
                sumoPos.y - unityPos.z
            );
            Debug.Log($"[SumoCoordinateMapper] Calibrated offset: ({_sumoNetOffset.x:F2}, {_sumoNetOffset.y:F2})");
        }
    }
}
