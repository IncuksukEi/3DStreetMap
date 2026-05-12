using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OSMImporter.Editor
{
    /// <summary>
    /// Tải Microsoft Global Building Footprints cho 1 bounding box.
    /// Data: GeoJSON line-delimited, compressed .csv.gz
    /// Source: https://github.com/microsoft/GlobalMLBuildingFootprints
    /// </summary>
    public static class MSBuildingDownloader
    {
        private const string DATASET_LINKS_URL = 
            "https://minedbuildings.z5.web.core.windows.net/global-buildings/dataset-links.csv";

        // Cache dataset-links.csv tại local
        private static List<DatasetLink> _cachedLinks;

        public class DatasetLink
        {
            public string Region;
            public string QuadKey;
            public string Url;
            public long Size;
        }

        /// <summary>
        /// Tính quadkey từ lat/lon tại zoom level cụ thể (Bing Maps tile system)
        /// </summary>
        public static string LatLonToQuadKey(double lat, double lon, int zoom)
        {
            int x = (int)((lon + 180.0) / 360.0 * (1 << zoom));
            double latRad = lat * Math.PI / 180.0;
            int y = (int)((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << zoom));

            // Clamp
            x = Math.Max(0, Math.Min(x, (1 << zoom) - 1));
            y = Math.Max(0, Math.Min(y, (1 << zoom) - 1));

            var sb = new StringBuilder();
            for (int i = zoom; i > 0; i--)
            {
                char digit = '0';
                int mask = 1 << (i - 1);
                if ((x & mask) != 0) digit++;
                if ((y & mask) != 0) { digit++; digit++; }
                sb.Append(digit);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tìm tất cả quadkey tile (zoom 9) phủ bounding box
        /// </summary>
        public static HashSet<string> GetQuadKeysForBBox(double minLat, double minLon, double maxLat, double maxLon, int zoom = 9)
        {
            var keys = new HashSet<string>();
            // Sample grid points trong bbox để tìm tất cả tile
            double latStep = (maxLat - minLat) / 10.0;
            double lonStep = (maxLon - minLon) / 10.0;
            if (latStep < 0.001) latStep = 0.001;
            if (lonStep < 0.001) lonStep = 0.001;

            for (double lat = minLat; lat <= maxLat; lat += latStep)
                for (double lon = minLon; lon <= maxLon; lon += lonStep)
                    keys.Add(LatLonToQuadKey(lat, lon, zoom));

            // Đảm bảo 4 góc
            keys.Add(LatLonToQuadKey(minLat, minLon, zoom));
            keys.Add(LatLonToQuadKey(minLat, maxLon, zoom));
            keys.Add(LatLonToQuadKey(maxLat, minLon, zoom));
            keys.Add(LatLonToQuadKey(maxLat, maxLon, zoom));

            return keys;
        }

        /// <summary>
        /// Download dataset-links.csv và tìm URLs cho các quadkey
        /// </summary>
        public static async Task<List<DatasetLink>> FindLinksForBBox(
            double minLat, double minLon, double maxLat, double maxLon,
            Action<string> onProgress)
        {
            // Download dataset-links.csv nếu chưa cache
            if (_cachedLinks == null)
            {
                onProgress?.Invoke("Downloading Microsoft Building Footprints index...");
                _cachedLinks = await DownloadDatasetLinks(onProgress);
            }

            // Tính quadkeys cho bbox ở nhiều zoom level (9 là partition level của MS)
            var results = new List<DatasetLink>();
            
            // MS dùng quadkey partition khác nhau, cần match prefix
            foreach (var link in _cachedLinks)
            {
                if (string.IsNullOrEmpty(link.QuadKey)) continue;
                
                // Filter theo region Vietnam trước (performance)
                if (!string.IsNullOrEmpty(link.Region) && 
                    !link.Region.Equals("Vietnam", StringComparison.OrdinalIgnoreCase) &&
                    !link.Region.Equals("VNM", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check quadkey overlap: tile quadkey phải là prefix hoặc match
                for (int zoom = 6; zoom <= 12; zoom++)
                {
                    var bboxKeys = GetQuadKeysForBBox(minLat, minLon, maxLat, maxLon, zoom);
                    foreach (var bk in bboxKeys)
                    {
                        if (link.QuadKey.StartsWith(bk) || bk.StartsWith(link.QuadKey))
                        {
                            if (!results.Contains(link))
                                results.Add(link);
                        }
                    }
                }
            }

            onProgress?.Invoke($"Found {results.Count} Microsoft Building tile(s) covering area");
            return results;
        }

        /// <summary>
        /// Download và parse building footprints từ Microsoft cho 1 bbox, trả về list polygon coordinates
        /// </summary>
        public static async Task<List<BuildingFootprint>> DownloadBuildingsForBBox(
            double minLat, double minLon, double maxLat, double maxLon,
            Action<string> onProgress, Action<string> onError)
        {
            var allBuildings = new List<BuildingFootprint>();

            try
            {
                var links = await FindLinksForBBox(minLat, minLon, maxLat, maxLon, onProgress);
                
                if (links.Count == 0)
                {
                    // Fallback: tải trực tiếp từ quadkey
                    onProgress?.Invoke("No indexed tiles found, trying direct quadkey lookup...");
                    links = GenerateDirectLinks(minLat, minLon, maxLat, maxLon);
                }

                int tileIdx = 0;
                foreach (var link in links)
                {
                    tileIdx++;
                    onProgress?.Invoke($"Downloading MS Buildings tile {tileIdx}/{links.Count}...");

                    try
                    {
                        string geojsonData = await DownloadAndDecompress(link.Url, onProgress);
                        if (string.IsNullOrEmpty(geojsonData)) continue;

                        // Parse line-delimited GeoJSON
                        var buildings = ParseGeoJsonLines(geojsonData, minLat, minLon, maxLat, maxLon);
                        allBuildings.AddRange(buildings);
                        onProgress?.Invoke($"Tile {tileIdx}: {buildings.Count} buildings in area (total: {allBuildings.Count})");
                    }
                    catch (Exception ex)
                    {
                        onProgress?.Invoke($"Tile {tileIdx} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Microsoft Buildings download failed: {ex.Message}");
            }

            return allBuildings;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static async Task<List<DatasetLink>> DownloadDatasetLinks(Action<string> onProgress)
        {
            var links = new List<DatasetLink>();
            
            try
            {
                using (var request = UnityWebRequest.Get(DATASET_LINKS_URL))
                {
                    request.timeout = 60;
                    request.SetRequestHeader("User-Agent", "UnityOSMImporter/1.0");
                    request.certificateHandler = new BypassCertHandler();

                    var op = request.SendWebRequest();
                    while (!op.isDone)
                    {
                        onProgress?.Invoke($"Downloading index... {request.downloadedBytes / 1024f:F0} KB");
                        await Task.Delay(300);
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[MSBuildings] Failed to load dataset-links.csv: {request.error}");
                        return links;
                    }

                    string csv = request.downloadHandler.text;
                    using (var reader = new StringReader(csv))
                    {
                        string header = reader.ReadLine(); // Skip header
                        string line;
                        int count = 0;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var parts = ParseCsvLine(line);
                            if (parts.Length >= 3)
                            {
                                links.Add(new DatasetLink
                                {
                                    Region = parts[0].Trim(),
                                    QuadKey = parts.Length > 1 ? parts[1].Trim() : "",
                                    Url = parts[2].Trim(),
                                    Size = parts.Length > 3 && long.TryParse(parts[3].Trim(), out long s) ? s : 0
                                });
                            }
                            count++;
                            if (count % 5000 == 0)
                                onProgress?.Invoke($"Reading index: {count} entries...");
                        }
                        onProgress?.Invoke($"Index loaded: {links.Count} tiles worldwide");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MSBuildings] Failed to load dataset-links.csv: {ex.Message}");
            }

            return links;
        }

        private static string[] ParseCsvLine(string line)
        {
            // Handle CSV with possible quoted fields
            var result = new List<string>();
            bool inQuote = false;
            var sb = new StringBuilder();
            
            foreach (char c in line)
            {
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == ',' && !inQuote) { result.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        /// <summary>Fallback: sinh direct URL từ quadkey pattern của Microsoft</summary>
        private static List<DatasetLink> GenerateDirectLinks(double minLat, double minLon, double maxLat, double maxLon)
        {
            var links = new List<DatasetLink>();
            var quadkeys = GetQuadKeysForBBox(minLat, minLon, maxLat, maxLon, 9);
            
            foreach (var qk in quadkeys)
            {
                links.Add(new DatasetLink
                {
                    Region = "Vietnam",
                    QuadKey = qk,
                    Url = $"https://minedbuildings.z5.web.core.windows.net/global-buildings/v3/buildings_{qk}.csv.gz"
                });
            }
            return links;
        }

        private static async Task<string> DownloadAndDecompress(string url, Action<string> onProgress)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 120;
                request.SetRequestHeader("User-Agent", "UnityOSMImporter/1.0");
                request.certificateHandler = new BypassCertHandler();

                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    onProgress?.Invoke($"Downloaded {request.downloadedBytes / 1024f:F0} KB...");
                    await Task.Delay(200);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    throw new Exception(request.error);

                byte[] rawData = request.downloadHandler.data;
                if (rawData == null || rawData.Length == 0) return null;

                // Decompress gzip nếu cần
                if (url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    using (var ms = new MemoryStream(rawData))
                    using (var gzip = new GZipStream(ms, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzip, Encoding.UTF8))
                        return await reader.ReadToEndAsync();
                }
                else
                {
                    return Encoding.UTF8.GetString(rawData);
                }
            }
        }

        /// <summary>
        /// Parse line-delimited GeoJSON, filter theo bbox
        /// Format: {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[lon,lat],...]]},"properties":{"height":-1,"confidence":-1}}
        /// </summary>
        private static List<BuildingFootprint> ParseGeoJsonLines(string data, double minLat, double minLon, double maxLat, double maxLon)
        {
            var buildings = new List<BuildingFootprint>();
            
            using (var reader = new StringReader(data))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var building = ParseGeoJsonFeature(line, minLat, minLon, maxLat, maxLon);
                    if (building != null)
                        buildings.Add(building);
                }
            }

            return buildings;
        }

        /// <summary>
        /// Lightweight JSON parser cho GeoJSON Feature (không dùng thư viện ngoài)
        /// </summary>
        private static BuildingFootprint ParseGeoJsonFeature(string json, double minLat, double minLon, double maxLat, double maxLon)
        {
            try
            {
                // Tìm coordinates array
                int coordStart = json.IndexOf("\"coordinates\"", StringComparison.Ordinal);
                if (coordStart < 0) return null;

                // Tìm mở ngoặc sau "coordinates":
                int bracketStart = json.IndexOf('[', coordStart);
                if (bracketStart < 0) return null;

                // Parse height nếu có
                float height = -1f;
                int heightIdx = json.IndexOf("\"height\"", StringComparison.Ordinal);
                if (heightIdx >= 0)
                {
                    int colonIdx = json.IndexOf(':', heightIdx);
                    if (colonIdx >= 0)
                    {
                        int endIdx = json.IndexOfAny(new[] { ',', '}' }, colonIdx + 1);
                        if (endIdx > colonIdx)
                        {
                            string hStr = json.Substring(colonIdx + 1, endIdx - colonIdx - 1).Trim();
                            float.TryParse(hStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out height);
                        }
                    }
                }

                // Parse coordinates — tìm innermost ring [[[lon,lat],[lon,lat],...]]
                // Cần tìm 3 lớp [ rồi parse số
                var coords = new List<double[]>();
                int depth = 0;
                int i = bracketStart;
                
                // Skip tới inner ring (depth 3)
                while (i < json.Length && depth < 3)
                {
                    if (json[i] == '[') depth++;
                    i++;
                }

                // Parse pairs [lon, lat]
                while (i < json.Length)
                {
                    // Tìm cặp số
                    if (json[i] == '[')
                    {
                        i++;
                        int numStart = i;
                        int commaPos = json.IndexOf(',', i);
                        int closeBracket = json.IndexOf(']', i);
                        
                        if (commaPos > 0 && closeBracket > commaPos)
                        {
                            string lonStr = json.Substring(numStart, commaPos - numStart).Trim();
                            string latStr = json.Substring(commaPos + 1, closeBracket - commaPos - 1).Trim();
                            
                            if (double.TryParse(lonStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double lon) &&
                                double.TryParse(latStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double lat))
                            {
                                coords.Add(new[] { lat, lon });
                            }
                            i = closeBracket + 1;
                        }
                        else break;
                    }
                    else if (json[i] == ']')
                    {
                        break; // End of ring
                    }
                    else i++;
                }

                if (coords.Count < 3) return null;

                // Check bbox filter — ít nhất 1 vertex phải nằm trong bbox
                bool inBBox = false;
                foreach (var c in coords)
                {
                    if (c[0] >= minLat && c[0] <= maxLat && c[1] >= minLon && c[1] <= maxLon)
                    {
                        inBBox = true;
                        break;
                    }
                }
                if (!inBBox) return null;

                return new BuildingFootprint
                {
                    Coordinates = coords,
                    Height = height
                };
            }
            catch
            {
                return null;
            }
        }

        // Bypass SSL certificate validation
        private class BypassCertHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }
    }

    public class BuildingFootprint
    {
        public List<double[]> Coordinates; // [lat, lon] pairs
        public float Height; // -1 nếu không có data
    }
}
