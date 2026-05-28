using System.Globalization;
using System.Xml;
using UnityEngine;
using OSMImporter.Geo;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// Unified coordinate mapper: SUMO (x,y mét) ↔ Unity (x,0,z).
    ///
    /// Flow: SUMO (x,y) → nội suy ra lat/lon từ boundaries → MercatorProjection → Unity
    /// Reverse: Unity → lat/lon → nội suy ra SUMO (x,y)
    ///
    /// Dùng chung cho cả TraCI pipeline lẫn Native SUMO.
    /// </summary>
    public class SumoToUnityMapper
    {
        // SUMO location boundaries
        private double origMinLon, origMinLat, origMaxLon, origMaxLat;
        private double convMinX, convMinY, convMaxX, convMaxY;

        // OSM origin (center of map trong Unity)
        private double osmCenterLat, osmCenterLon;
        private float scale;

        private bool _initialized;
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Khởi tạo mapper từ file .net.xml + OSM center
        /// </summary>
        public SumoToUnityMapper(string netXmlPath, double centerLat, double centerLon, float mapScale = 1f)
        {
            osmCenterLat = centerLat;
            osmCenterLon = centerLon;
            scale = mapScale;

            if (!string.IsNullOrEmpty(netXmlPath))
            {
                ParseNetXml(netXmlPath);
            }
        }

        /// <summary>
        /// Khởi tạo mapper chỉ từ OSM center (chưa có boundaries → cần gọi ParseNetXml sau)
        /// </summary>
        public SumoToUnityMapper(double centerLat, double centerLon, float mapScale = 1f)
        {
            osmCenterLat = centerLat;
            osmCenterLon = centerLon;
            scale = mapScale;
        }

        /// <summary>
        /// Khởi tạo mapper trực tiếp từ boundaries (không cần đọc file)
        /// </summary>
        public SumoToUnityMapper(
            double origMinLon, double origMinLat, double origMaxLon, double origMaxLat,
            double convMinX, double convMinY, double convMaxX, double convMaxY,
            double centerLat, double centerLon, float mapScale = 1f)
        {
            this.origMinLon = origMinLon; this.origMinLat = origMinLat;
            this.origMaxLon = origMaxLon; this.origMaxLat = origMaxLat;
            this.convMinX = convMinX; this.convMinY = convMinY;
            this.convMaxX = convMaxX; this.convMaxY = convMaxY;
            this.osmCenterLat = centerLat;
            this.osmCenterLon = centerLon;
            this.scale = mapScale;
            _initialized = true;
        }

        // ══════════════════════════════════════════════════════════════════
        // PARSE .net.xml
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Đọc <location> tag từ .net.xml để lấy origBoundary và convBoundary.
        /// Có thể gọi sau constructor nếu chưa có path lúc khởi tạo.
        /// </summary>
        public bool ParseNetXml(string netXmlPath)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(netXmlPath);
                XmlNode loc = doc.SelectSingleNode("//location");

                if (loc == null)
                {
                    Debug.LogError("[SumoToUnityMapper] Không tìm thấy <location> trong .net.xml");
                    return false;
                }

                // origBoundary="minLon,minLat,maxLon,maxLat"
                string[] orig = loc.Attributes["origBoundary"].Value.Split(',');
                origMinLon = double.Parse(orig[0], CultureInfo.InvariantCulture);
                origMinLat = double.Parse(orig[1], CultureInfo.InvariantCulture);
                origMaxLon = double.Parse(orig[2], CultureInfo.InvariantCulture);
                origMaxLat = double.Parse(orig[3], CultureInfo.InvariantCulture);

                // convBoundary="minX,minY,maxX,maxY"
                string[] conv = loc.Attributes["convBoundary"].Value.Split(',');
                convMinX = double.Parse(conv[0], CultureInfo.InvariantCulture);
                convMinY = double.Parse(conv[1], CultureInfo.InvariantCulture);
                convMaxX = double.Parse(conv[2], CultureInfo.InvariantCulture);
                convMaxY = double.Parse(conv[3], CultureInfo.InvariantCulture);

                _initialized = true;

                Debug.Log($"[SumoToUnityMapper] Parsed boundaries:" +
                    $"\n  origBoundary: ({origMinLon:F6},{origMinLat:F6}) → ({origMaxLon:F6},{origMaxLat:F6})" +
                    $"\n  convBoundary: ({convMinX:F2},{convMinY:F2}) → ({convMaxX:F2},{convMaxY:F2})");

                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SumoToUnityMapper] Parse failed: {e.Message}");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // SUMO → UNITY
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Chuyển tọa độ SUMO (x, y) sang Unity Vector3.
        /// SUMO x,y → nội suy ra lat/lon → MercatorProjection → Unity
        /// </summary>
        public Vector3 SumoToUnity(float sumoX, float sumoY)
        {
            if (!_initialized)
            {
                // Fallback: dùng trực tiếp như offset (ít chính xác)
                return new Vector3(sumoX, 0f, sumoY);
            }

            // Nội suy tuyến tính: SUMO conv coords → orig lat/lon
            double convWidth = convMaxX - convMinX;
            double convHeight = convMaxY - convMinY;
            if (convWidth < 0.001) convWidth = 0.001;
            if (convHeight < 0.001) convHeight = 0.001;

            double tx = (sumoX - convMinX) / convWidth;
            double ty = (sumoY - convMinY) / convHeight;

            double lon = origMinLon + tx * (origMaxLon - origMinLon);
            double lat = origMinLat + ty * (origMaxLat - origMinLat);

            return MercatorProjection.LatLonToUnityCorrected(lat, lon, osmCenterLat, osmCenterLon, scale);
        }

        /// <summary>
        /// Overload nhận Vector2 (từ TraCIClient.VehicleState.Position)
        /// </summary>
        public Vector3 SumoToUnity(Vector2 sumoPos)
            => SumoToUnity(sumoPos.x, sumoPos.y);

        /// <summary>
        /// Overload nhận Vector3 (SUMO z trong Unity = SUMO y)
        /// </summary>
        public Vector3 SumoToUnity(Vector3 sumoPoint)
            => SumoToUnity(sumoPoint.x, sumoPoint.z);

        // ══════════════════════════════════════════════════════════════════
        // UNITY → SUMO (reverse)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Chuyển Unity world position → SUMO (x, y).
        /// Unity → lat/lon (inverse Mercator) → nội suy về SUMO conv coords
        /// </summary>
        public Vector2 UnityToSumo(Vector3 unityPos)
        {
            if (!_initialized) return new Vector2(unityPos.x, unityPos.z);

            // Inverse Mercator: Unity → lat/lon
            double originLatRad = osmCenterLat * Mathf.Deg2Rad;
            double cosLat = System.Math.Cos(originLatRad);
            const double EarthRadius = 6378137.0;

            double x = unityPos.x / scale;
            double z = unityPos.z / scale;

            double lon = osmCenterLon + (x / (EarthRadius * cosLat)) * Mathf.Rad2Deg;

            double yOrigin = EarthRadius * System.Math.Log(
                System.Math.Tan(System.Math.PI / 4.0 + originLatRad / 2.0));
            double yWorld = yOrigin + z;
            double lat = (2.0 * System.Math.Atan(System.Math.Exp(yWorld / EarthRadius))
                         - System.Math.PI / 2.0) * Mathf.Rad2Deg;

            // lat/lon → nội suy ngược về SUMO conv coords
            double tx = (lon - origMinLon) / (origMaxLon - origMinLon);
            double ty = (lat - origMinLat) / (origMaxLat - origMinLat);

            float sumoX = (float)(convMinX + tx * (convMaxX - convMinX));
            float sumoY = (float)(convMinY + ty * (convMaxY - convMinY));

            return new Vector2(sumoX, sumoY);
        }

        // ══════════════════════════════════════════════════════════════════
        // ANGLE HELPERS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// SUMO angle → Unity Y rotation.
        /// SUMO: 0=North, clockwise (degrees).
        /// Unity: Quaternion.Euler(0, yAngle, 0) — 0=+Z(North), clockwise.
        /// Convention trùng nhau.
        /// </summary>
        public static float SumoAngleToUnityYRotation(float sumoAngle) => sumoAngle;

        /// <summary>
        /// Tạo Unity Quaternion từ SUMO angle.
        /// </summary>
        public static Quaternion SumoAngleToUnityRotation(float sumoAngle)
            => Quaternion.Euler(0f, sumoAngle, 0f);

        // ══════════════════════════════════════════════════════════════════
        // CALIBRATION
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Auto-calibrate: dùng 1 known SUMO point + Unity point để validate/adjust.
        /// Tính offset error và log ra để developer kiểm tra.
        /// </summary>
        public Vector3 GetCalibrationError(Vector2 sumoPos, Vector3 expectedUnityPos)
        {
            Vector3 computed = SumoToUnity(sumoPos);
            Vector3 error = computed - expectedUnityPos;
            Debug.Log($"[SumoToUnityMapper] Calibration error: ({error.x:F2}, {error.z:F2})m " +
                $"at SUMO({sumoPos.x:F2},{sumoPos.y:F2})");
            return error;
        }
    }
}
