using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace OSMImporter.Editor
{
    /// <summary>
    /// Downloads OSM XML data from Overpass API hoặc OSM Main API (fallback).
    /// Dùng UnityWebRequest — native HTTPS, không bị lỗi TLS của Mono HttpWebRequest.
    /// </summary>
    public static class OSMDownloader
    {
        // Overpass API mirrors
        private static readonly string[] OverpassEndpoints = new[]
        {
            "https://overpass-api.de/api/interpreter",
            "https://lz4.overpass-api.de/api/interpreter",
            "https://z.overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"
        };

        private static int _endpointIndex = 0;
        private const int PerRequestTimeoutSec = 30;
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
        // OVERPASS API — POST request
        // ══════════════════════════════════════════════════════════════════

        private static async Task<string> TryOverpass(
            double minLat, double minLon, double maxLat, double maxLon,
            string tempPath, Action<string> onProgress)
        {
            string query = BuildOverpassQuery(minLat, minLon, maxLat, maxLon);
            byte[] postData = Encoding.UTF8.GetBytes("data=" + Uri.EscapeDataString(query));

            for (int ep = 0; ep < OverpassEndpoints.Length; ep++)
            {
                string endpoint = OverpassEndpoints[(_endpointIndex + ep) % OverpassEndpoints.Length];
                string host = GetShortName(endpoint);
                string label = $"[{ep + 1}/{OverpassEndpoints.Length}]";

                onProgress?.Invoke($"{label} {host}...");

                try
                {
                    string result = await PostDownload(endpoint, postData, tempPath, PerRequestTimeoutSec,
                        msg => onProgress?.Invoke($"{label} {msg}"));

                    if (result != null)
                    {
                        _endpointIndex = (_endpointIndex + ep + 1) % OverpassEndpoints.Length;
                        onProgress?.Invoke($"✔ Overpass OK ({new FileInfo(tempPath).Length / 1024f:F0} KB)");
                        return result;
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

        // ══════════════════════════════════════════════════════════════════
        // OSM MAIN API (fallback — GET request)
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
                string result = await GetDownload(url, tempPath, 60,
                    msg => onProgress?.Invoke($"OSM API: {msg}"));

                if (result != null)
                {
                    onProgress?.Invoke($"✔ OSM Main API OK ({new FileInfo(tempPath).Length / 1024f:F0} KB)");
                    return result;
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"OSM Main API thất bại: {TrimError(ex)}");
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // UNITY WEB REQUEST — native HTTPS, không lỗi TLS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>POST request dùng UnityWebRequest.</summary>
        private static async Task<string> PostDownload(
            string url, byte[] postData, string savePath, int timeoutSec,
            Action<string> onProgress)
        {
            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(postData);
                request.uploadHandler.contentType = "application/x-www-form-urlencoded";
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = timeoutSec;
                request.SetRequestHeader("User-Agent", "UnityOSMImporter/1.0");

                // Bỏ qua SSL cert validation (fix cho self-signed cert / Unity Mono issue)
                request.certificateHandler = new BypassCertHandler();

                var op = request.SendWebRequest();

                // Poll cho đến khi xong — giữ trên main thread
                while (!op.isDone)
                {
                    onProgress?.Invoke($"Downloading... {request.downloadedBytes / 1024f:F0} KB");
                    await Task.Delay(200);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    throw new Exception(request.error);

                byte[] data = request.downloadHandler.data;
                if (data == null || data.Length < 100)
                    return null;

                File.WriteAllBytes(savePath, data);
                return savePath;
            }
        }

        /// <summary>GET request dùng UnityWebRequest.</summary>
        private static async Task<string> GetDownload(
            string url, string savePath, int timeoutSec,
            Action<string> onProgress)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSec;
                request.SetRequestHeader("User-Agent", "UnityOSMImporter/1.0");
                request.certificateHandler = new BypassCertHandler();

                var op = request.SendWebRequest();

                while (!op.isDone)
                {
                    onProgress?.Invoke($"Downloading... {request.downloadedBytes / 1024f:F0} KB");
                    await Task.Delay(200);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    throw new Exception(request.error);

                byte[] data = request.downloadHandler.data;
                if (data == null || data.Length < 100)
                    return null;

                File.WriteAllBytes(savePath, data);
                return savePath;
            }
        }

        // Bypass SSL certificate validation — cần cho một số mirror / Mono runtime
        private class BypassCertHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
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
            if (msg.Length > 80) msg = msg.Substring(0, 77) + "...";
            return msg;
        }
    }
}
