using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OSMImporter.Editor
{
    /// <summary>
    /// Downloads OSM XML data from Overpass API (full query) hoặc OSM Main API (fallback).
    /// </summary>
    public static class OSMDownloader
    {
        // Force TLS 1.2+ và bypass SSL cert — fix Unity không kết nối được HTTPS
        static OSMDownloader()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.ServerCertificateValidationCallback =
                (object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) => true;
            // Tăng connection limit (default = 2 per host)
            ServicePointManager.DefaultConnectionLimit = 10;
        }

        // Overpass API mirrors
        private static readonly string[] OverpassEndpoints = new[]
        {
            "https://overpass-api.de/api/interpreter",
            "https://lz4.overpass-api.de/api/interpreter",
            "https://z.overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"
        };

        private static int _endpointIndex = 0;
        private const int PerRequestTimeoutMs = 30_000; // 30s mỗi endpoint
        private const int RetryDelayMs = 1500;

        // OSM Main API — giới hạn 50k nodes/request nhưng rất ổn định cho bbox nhỏ
        private const string OsmMainApi = "https://api.openstreetmap.org/api/0.6/map?bbox=";

        /// <summary>
        /// Download OSM data for a bounding box.
        /// Thử Overpass trước, nếu tất cả fail → fallback sang OSM Main API.
        /// </summary>
        public static async Task<string> DownloadAsync(
            double minLat, double minLon, double maxLat, double maxLon,
            Action<string> onProgress, Action<string> onError)
        {
            string tempPath = Path.Combine(
                Application.temporaryCachePath,
                $"osm_download_{DateTime.Now:yyyyMMdd_HHmmss}.osm");

            // ── Phase 1: Thử Overpass API ──
            string overpassResult = await TryOverpass(minLat, minLon, maxLat, maxLon, tempPath, onProgress);
            if (overpassResult != null) return overpassResult;

            // ── Phase 2: Fallback sang OSM Main API ──
            onProgress?.Invoke("Overpass thất bại — đang dùng OSM Main API (fallback)...");
            string osmResult = await TryOsmMainApi(minLat, minLon, maxLat, maxLon, tempPath, onProgress);
            if (osmResult != null) return osmResult;

            onError?.Invoke("Cả Overpass lẫn OSM Main API đều thất bại.\nKiểm tra kết nối mạng và thử lại.");
            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // OVERPASS API
        // ══════════════════════════════════════════════════════════════════

        private static async Task<string> TryOverpass(
            double minLat, double minLon, double maxLat, double maxLon,
            string tempPath, Action<string> onProgress)
        {
            string query = BuildOverpassQuery(minLat, minLon, maxLat, maxLon);

            for (int ep = 0; ep < OverpassEndpoints.Length; ep++)
            {
                string endpoint = OverpassEndpoints[(_endpointIndex + ep) % OverpassEndpoints.Length];
                string host = GetShortName(endpoint);
                string label = $"[{ep + 1}/{OverpassEndpoints.Length}]";

                onProgress?.Invoke($"{label} {host}...");

                try
                {
                    await DownloadWithTimeout(
                        () => CreateOverpassRequest(endpoint, query),
                        tempPath, PerRequestTimeoutMs,
                        msg => onProgress?.Invoke($"{label} {msg}"));

                    var fi = new FileInfo(tempPath);
                    if (fi.Length > 100)
                    {
                        _endpointIndex = (_endpointIndex + ep + 1) % OverpassEndpoints.Length;
                        onProgress?.Invoke($"✔ Overpass OK ({fi.Length / 1024f:F0} KB)");
                        return tempPath;
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"{label} {host}: {TrimError(ex)}");
                }

                if (ep < OverpassEndpoints.Length - 1)
                    await Task.Delay(RetryDelayMs);
            }

            return null;
        }

        private static HttpWebRequest CreateOverpassRequest(string endpoint, string query)
        {
            byte[] postData = Encoding.UTF8.GetBytes("data=" + Uri.EscapeDataString(query));
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = postData.Length;
            request.Timeout = PerRequestTimeoutMs;
            request.ReadWriteTimeout = PerRequestTimeoutMs;
            request.UserAgent = "UnityOSMImporter/1.0";
            request.KeepAlive = false;
            // Ghi body đồng bộ (nhanh, chỉ vài KB)
            using (var s = request.GetRequestStream())
                s.Write(postData, 0, postData.Length);
            return request;
        }

        // ══════════════════════════════════════════════════════════════════
        // OSM MAIN API (fallback — ổn định, không cần Overpass)
        // ══════════════════════════════════════════════════════════════════

        private static async Task<string> TryOsmMainApi(
            double minLat, double minLon, double maxLat, double maxLon,
            string tempPath, Action<string> onProgress)
        {
            // OSM Main API bbox format: minLon,minLat,maxLon,maxLat
            string url = OsmMainApi + FormattableString.Invariant(
                $"{minLon},{minLat},{maxLon},{maxLat}");

            onProgress?.Invoke("OSM Main API: downloading...");

            try
            {
                await DownloadWithTimeout(
                    () =>
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Method = "GET";
                        req.Timeout = 60_000; // OSM Main API thường nhanh
                        req.ReadWriteTimeout = 60_000;
                        req.UserAgent = "UnityOSMImporter/1.0";
                        req.KeepAlive = false;
                        return req;
                    },
                    tempPath, 60_000,
                    msg => onProgress?.Invoke($"OSM API: {msg}"));

                var fi = new FileInfo(tempPath);
                if (fi.Length > 100)
                {
                    onProgress?.Invoke($"✔ OSM Main API OK ({fi.Length / 1024f:F0} KB)");
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"OSM Main API thất bại: {TrimError(ex)}");
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // CORE DOWNLOAD WITH HARD TIMEOUT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Download response vào file với hard timeout (giải quyết GetResponseAsync không timeout).
        /// </summary>
        private static async Task DownloadWithTimeout(
            Func<HttpWebRequest> createRequest, string tempPath, int timeoutMs,
            Action<string> onProgress)
        {
            var downloadTask = Task.Run(() =>
            {
                var request = createRequest();

                using (var response = request.GetResponse())
                using (var respStream = response.GetResponseStream())
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    while ((bytesRead = respStream.Read(buffer, 0, bytesRead = buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, bytesRead);
                        totalRead += bytesRead;
                        onProgress?.Invoke($"Downloaded {totalRead / 1024f:F0} KB...");
                    }
                }
            });

            // Hard timeout — nếu Task.Run không xong trong thời gian → throw
            if (await Task.WhenAny(downloadTask, Task.Delay(timeoutMs)) != downloadTask)
                throw new TimeoutException($"Timeout sau {timeoutMs / 1000}s");

            // Re-throw exception nếu download task bị lỗi
            await downloadTask;
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════

        private static string BuildOverpassQuery(double minLat, double minLon, double maxLat, double maxLon)
        {
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

        private static string GetShortName(string endpoint)
        {
            try { return new Uri(endpoint).Host; }
            catch { return endpoint; }
        }

        private static string TrimError(Exception ex)
        {
            string msg = ex.Message;
            // Cắt ngắn lỗi dài cho gọn UI
            if (msg.Length > 80) msg = msg.Substring(0, 77) + "...";
            return msg;
        }
    }
}
