using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OSMImporter.Editor
{
    /// <summary>
    /// Downloads OSM XML data from the Overpass API on a background thread
    /// and saves it to a temporary file that can then be parsed by OSMParser.
    /// </summary>
    public static class OSMDownloader
    {
        // Official Overpass API instances (round-robin for reliability)
        private static readonly string[] OverpassEndpoints = new[]
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
            "https://lz4.overpass-api.de/api/interpreter"
        };

        private static int _endpointIndex = 0;

        /// <summary>
        /// Download OSM data for a bounding box from Overpass API.
        /// Returns the local file path of the downloaded .osm file, or null on failure.
        /// </summary>
        public static async Task<string> DownloadAsync(
            double minLat, double minLon, double maxLat, double maxLon,
            Action<string> onProgress, Action<string> onError)
        {
            // Build Overpass QL query – fetch nodes, ways (with their nodes), and relevant relations
            string query = BuildOverpassQuery(minLat, minLon, maxLat, maxLon);

            string tempPath = Path.Combine(
                Application.temporaryCachePath,
                $"osm_download_{DateTime.Now:yyyyMMdd_HHmmss}.osm");

            Exception lastException = null;

            for (int attempt = 0; attempt < OverpassEndpoints.Length; attempt++)
            {
                string endpoint = OverpassEndpoints[(_endpointIndex + attempt) % OverpassEndpoints.Length];
                onProgress?.Invoke($"Connecting to {endpoint}...");

                try
                {
                    byte[] postData = Encoding.UTF8.GetBytes("data=" + Uri.EscapeDataString(query));

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                    request.Method = "POST";
                    request.ContentType = "application/x-www-form-urlencoded";
                    request.ContentLength = postData.Length;
                    request.Timeout = 120_000; // 2 minutes
                    request.UserAgent = "UnityOSMImporter/1.0 (Unity Editor plugin)";

                    using (Stream reqStream = await request.GetRequestStreamAsync())
                        await reqStream.WriteAsync(postData, 0, postData.Length);

                    onProgress?.Invoke("Downloading OSM data...");

                    using (WebResponse response = await request.GetResponseAsync())
                    using (Stream respStream = response.GetResponseStream())
                    using (FileStream fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await respStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                            onProgress?.Invoke($"Downloaded {totalRead / 1024f:F0} KB...");
                        }
                    }

                    // Advance endpoint index for next call (load balancing)
                    _endpointIndex = (_endpointIndex + attempt + 1) % OverpassEndpoints.Length;

                    onProgress?.Invoke($"Download complete! Saved to {tempPath}");
                    return tempPath;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    // Retry với endpoint tiếp theo nếu còn
                    if (attempt < OverpassEndpoints.Length - 1)
                        onProgress?.Invoke($"Endpoint {endpoint} failed ({ex.Message}), trying next...");
                }
            }

            onError?.Invoke($"All Overpass API endpoints failed: {lastException?.Message ?? "Unknown error"}. Check your internet connection.");
            return null;
        }

        private static string BuildOverpassQuery(double minLat, double minLon, double maxLat, double maxLon)
        {
            // Compact Overpass QL that fetches:
            //   - All nodes in bbox
            //   - All ways in bbox (+ their nodes via `>;`)
            //   - Relations of type=multipolygon (buildings) and highway/boundary
            string bbox = FormattableString.Invariant($"{minLat},{minLon},{maxLat},{maxLon}");
            return $@"[out:xml][timeout:90][bbox:{bbox}];
(
  node;
  way;
  relation[""type""=""multipolygon""];
);
out body;
>;
out skel qt;";
        }

        /// <summary>
        /// Compute a bounding box from a center point and a radius in meters.
        /// </summary>
        public static (double minLat, double minLon, double maxLat, double maxLon)
            BBoxFromCenter(double centerLat, double centerLon, double radiusMeters)
        {
            const double EarthRadius = 6378137.0;
            double deltaLat = (radiusMeters / EarthRadius) * (180.0 / Math.PI);
            double deltaLon = (radiusMeters / (EarthRadius * Math.Cos(centerLat * Math.PI / 180.0))) * (180.0 / Math.PI);
            return (centerLat - deltaLat, centerLon - deltaLon, centerLat + deltaLat, centerLon + deltaLon);
        }
    }
}
