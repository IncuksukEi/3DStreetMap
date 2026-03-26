using UnityEngine;

namespace OSMImporter.Geo
{
    public static class MercatorProjection
    {
        private const double EarthRadius = 6378137.0;

        public static Vector3 LatLonToUnityCorrected(double lat, double lon, double originLat, double originLon, float scale = 1f)
        {
            double originLatRad = originLat * Mathf.Deg2Rad;
            double cosLat = System.Math.Cos(originLatRad);

            double deltaLon = (lon - originLon) * Mathf.Deg2Rad;
            double x = EarthRadius * deltaLon * cosLat;

            double latRad = lat * Mathf.Deg2Rad;
            double originLatRadFull = originLat * Mathf.Deg2Rad;
            double y = EarthRadius * System.Math.Log(System.Math.Tan(System.Math.PI / 4.0 + latRad / 2.0));
            double yOrigin = EarthRadius * System.Math.Log(System.Math.Tan(System.Math.PI / 4.0 + originLatRadFull / 2.0));
            double z = y - yOrigin;

            return new Vector3((float)(x * scale), 0f, (float)(z * scale));
        }
    }
}
