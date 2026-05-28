using UnityEngine;
using OSMImporter.Navigation;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Gắn vào Camera. Click chuột trái vào xe để xem:
    /// - Điểm xuất phát (xanh lá)
    /// - Điểm đích (đỏ)
    /// - Tuyến đường đang đi (cyan)
    /// Click vào vùng trống để bỏ chọn.
    /// </summary>
    public class VehicleInspector : MonoBehaviour
    {
        // Xe đang được chọn
        public static VehicleAgent SelectedVehicle { get; private set; }

        // Cached materials cho GL drawing
        private static Material _lineMat;

        private void Update()
        {
            if (Input.GetMouseButtonDown(0))
            {
                Camera mainCam = Camera.main;
                if (mainCam == null) return;

                Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
                {
                    VehicleAgent agent = hit.collider.GetComponentInParent<VehicleAgent>();
                    SelectedVehicle = agent; // null nếu click không trúng xe
                }
                else
                {
                    SelectedVehicle = null;
                }
            }

            // Bỏ chọn nếu xe bị destroy
            if (SelectedVehicle != null && SelectedVehicle.gameObject == null)
                SelectedVehicle = null;
        }

        // ══════════════════════════════════════════════════════════════════════
        // ROUTE RENDERING — Google Maps style (dải đỏ bám mặt đường)
        // ══════════════════════════════════════════════════════════════════════

        // Độ rộng dải đường (mét)
        private const float ROUTE_WIDTH       = 0.6f;
        private const float ROUTE_BORDER_WIDTH = 0.9f;  // Viền ngoài đậm hơn
        private const float ROUTE_Y_OFFSET    = 0.05f;  // Sát mặt đường (road mesh Y≈0)

        private void OnEnable()
        {
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        private void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
        {
            if (SelectedVehicle == null) return;

            // Bỏ qua nếu là camera ánh sáng/baking để tránh lỗi
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

            EnsureLineMaterial();

            // URP yêu cầu phải set ma trận hiển thị bằng tay một cách rõ ràng
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;

            var path = SelectedVehicle.GetExactPath();
            if (path == null) 
            {
                GL.PopMatrix();
                return;
            }
            
            int pathIdx = SelectedVehicle.GetExactPathIndex();
            if (path.Count < 2) 
            {
                GL.PopMatrix();
                return;
            }

            Vector3 startPos = SelectedVehicle.GetStartPosition();
            startPos.y += ROUTE_Y_OFFSET;
            Vector3 destPos  = SelectedVehicle.GetDestinationPosition();
            destPos.y += ROUTE_Y_OFFSET;

            // ── 1. Dải đường ĐÃ ĐI (xám mờ, bám mặt đường) ──
            Color pastBorder = new Color(0.35f, 0.35f, 0.35f, 0.5f);
            Color pastFill   = new Color(0.5f, 0.5f, 0.5f, 0.35f);
            DrawRouteStrip(path, 0, Mathf.Min(pathIdx + 1, path.Count), pastBorder, pastFill, ROUTE_BORDER_WIDTH, ROUTE_WIDTH);

            // ── 2. Dải đường PHÍA TRƯỚC (đỏ Google Maps, bám mặt đường) ──
            // Viền ngoài tối hơn
            Color borderColor = new Color(0.6f, 0f, 0f, 0.85f);
            // Lõi sáng đỏ
            float pulse = 0.75f + Mathf.Sin(Time.time * 3f) * 0.1f; // Nhấp nháy nhẹ
            Color fillColor   = new Color(0.9f, 0.15f, 0.15f, pulse);
            DrawRouteStrip(path, Mathf.Max(0, pathIdx), path.Count, borderColor, fillColor, ROUTE_BORDER_WIDTH, ROUTE_WIDTH);

            if (pathIdx < path.Count)
            {
                Vector3 vehPos = SelectedVehicle.transform.position;
                vehPos.y += ROUTE_Y_OFFSET;
                Vector3 nextWp = path[pathIdx].Position;
                nextWp.y += ROUTE_Y_OFFSET;
                DrawSingleSegmentStrip(vehPos, nextWp, fillColor, ROUTE_WIDTH * 0.8f);
            }

            // ── 4. Marker điểm xuất phát (xanh lá) ──
            DrawMapPin(startPos, new Color(0.2f, 0.85f, 0.3f, 0.95f));

            // ── 5. Marker điểm đích (đỏ) ──
            DrawMapPin(destPos, new Color(0.95f, 0.2f, 0.2f, 0.95f));

            // ── 6. Marker vị trí xe (xanh dương) ──
            DrawVehicleDot(SelectedVehicle.transform.position, new Color(0.2f, 0.5f, 1f, 0.9f));

            GL.PopMatrix();
        }

        /// <summary>
        /// Vẽ dải đường dày trên mặt đường bằng GL.QUADS (2 pass: viền + lõi).
        /// </summary>
        private void DrawRouteStrip(System.Collections.Generic.List<VehicleAgent.PathPoint> path, int fromIdx, int toIdx, Color borderColor, Color fillColor, float borderW, float fillW)
        {
            if (toIdx - fromIdx < 2) return;

            // Pass 1: Viền ngoài (đậm hơn, rộng hơn)
            _lineMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(borderColor);
            for (int i = fromIdx; i < toIdx - 1; i++)
            {
                Vector3 a = path[i].Position;      a.y += ROUTE_Y_OFFSET;
                Vector3 b = path[i + 1].Position;  b.y += ROUTE_Y_OFFSET;
                EmitQuadSegment(a, b, borderW * 0.5f);
            }
            GL.End();

            // Pass 2: Lõi bên trong (màu chính)
            _lineMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(fillColor);
            for (int i = fromIdx; i < toIdx - 1; i++)
            {
                Vector3 a = path[i].Position;      a.y += ROUTE_Y_OFFSET + 0.01f;
                Vector3 b = path[i + 1].Position;  b.y += ROUTE_Y_OFFSET + 0.01f;
                EmitQuadSegment(a, b, fillW * 0.5f);
            }
            GL.End();

            // Pass 3: Vẽ tròn tại mỗi joint để che khe trống góc cua
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(fillColor);
            for (int i = fromIdx; i < toIdx; i++)
            {
                Vector3 p = path[i].Position; p.y += ROUTE_Y_OFFSET + 0.01f;
                EmitCircle(p, fillW * 0.5f, 8);
            }
            GL.End();
        }

        /// <summary>
        /// Emit 1 quad segment (hình chữ nhật nằm ngang dọc theo đoạn a→b).
        /// </summary>
        private void EmitQuadSegment(Vector3 a, Vector3 b, float halfW)
        {
            Vector3 dir = b - a;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return;
            dir.Normalize();

            // Perpendicular trên mặt phẳng XZ
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x) * halfW;

            Vector3 p0 = a - perp;
            Vector3 p1 = a + perp;
            Vector3 p2 = b + perp;
            Vector3 p3 = b - perp;

            GL.Vertex3(p0.x, p0.y, p0.z);
            GL.Vertex3(p1.x, p1.y, p1.z);
            GL.Vertex3(p2.x, p2.y, p2.z);
            GL.Vertex3(p3.x, p3.y, p3.z);
        }

        /// <summary>
        /// Vẽ hình tròn flat bằng triangle fan (dùng cho joint).
        /// </summary>
        private void EmitCircle(Vector3 center, float radius, int segments)
        {
            float step = 360f / segments;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * step * Mathf.Deg2Rad;
                float a1 = (i + 1) * step * Mathf.Deg2Rad;

                GL.Vertex3(center.x, center.y, center.z);
                GL.Vertex3(center.x + Mathf.Cos(a0) * radius, center.y, center.z + Mathf.Sin(a0) * radius);
                GL.Vertex3(center.x + Mathf.Cos(a1) * radius, center.y, center.z + Mathf.Sin(a1) * radius);
            }
        }

        /// <summary>
        /// Vẽ 1 đoạn đường nối (xe → waypoint tiếp theo).
        /// </summary>
        private void DrawSingleSegmentStrip(Vector3 a, Vector3 b, Color color, float width)
        {
            _lineMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(color);
            EmitQuadSegment(a, b, width * 0.5f);
            GL.End();
        }

        /// <summary>
        /// Marker kiểu Google Maps pin — hình tròn + cột.
        /// </summary>
        private void DrawMapPin(Vector3 pos, Color color)
        {
            _lineMat.SetPass(0);
            float pinHeight = 10f;
            float pinRadius = 2.0f;

            // Cột thẳng đứng (dải mỏng)
            GL.Begin(GL.QUADS);
            Color poleColor = new Color(color.r * 0.7f, color.g * 0.7f, color.b * 0.7f, color.a);
            GL.Color(poleColor);
            float hw = 0.2f;
            Vector3 bot = pos + Vector3.up * 0.1f;
            Vector3 top = pos + Vector3.up * pinHeight;
            GL.Vertex3(bot.x - hw, bot.y, bot.z);
            GL.Vertex3(bot.x + hw, bot.y, bot.z);
            GL.Vertex3(top.x + hw, top.y, top.z);
            GL.Vertex3(top.x - hw, top.y, top.z);
            // Trục Z
            GL.Vertex3(bot.x, bot.y, bot.z - hw);
            GL.Vertex3(bot.x, bot.y, bot.z + hw);
            GL.Vertex3(top.x, top.y, top.z + hw);
            GL.Vertex3(top.x, top.y, top.z - hw);
            GL.End();

            // Đầu pin (hình tròn nằm ngang trên đỉnh)
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            EmitCircle(top, pinRadius, 12);
            GL.End();

            // Viền tròn tối hơn
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, color.a));
            EmitCircle(top + Vector3.down * 0.05f, pinRadius + 0.4f, 12);
            GL.End();

            // Vòng tròn dưới chân (shadow/glow)
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(color.r, color.g, color.b, 0.25f));
            EmitCircle(pos + Vector3.up * 0.1f, 3f, 12);
            GL.End();
        }

        /// <summary>
        /// Chấm tròn nhỏ đánh dấu vị trí xe hiện tại.
        /// </summary>
        private void DrawVehicleDot(Vector3 pos, Color color)
        {
            _lineMat.SetPass(0);

            // Vòng ngoài glow
            float pulse = 3.0f + Mathf.Sin(Time.time * 4f) * 0.5f;
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(color.r, color.g, color.b, 0.25f));
            EmitCircle(pos + Vector3.up * 0.2f, pulse, 16);
            GL.End();

            // Chấm lõi
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            EmitCircle(pos + Vector3.up * 0.25f, 1.5f, 12);
            GL.End();

            // Viền trắng
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(Color.white);
            EmitCircle(pos + Vector3.up * 0.22f, 1.8f, 12);
            GL.End();

            // Chấm lõi lại đè lên viền
            _lineMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            EmitCircle(pos + Vector3.up * 0.26f, 1.4f, 12);
            GL.End();
        }

        // UI overlay hiện thông tin xe
        private void OnGUI()
        {
            if (SelectedVehicle == null) return;

            float panelW = 320f, panelH = 200f;
            float x = Screen.width - panelW - 15f;
            float y = 15f;

            // Nền panel bán trong suốt
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(new Rect(x, y, panelW, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan }
            };
            GUIStyle infoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white }
            };
            GUIStyle smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            float cy = y + 8f;
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 24), $"🚗 {SelectedVehicle.name}", titleStyle);
            cy += 26f;

            string vType = SelectedVehicle.VehicleType.ToString();
            float speed = SelectedVehicle.GetCurrentSpeed();
            string pName = SelectedVehicle.Personality != null ? SelectedVehicle.Personality.Name : "Bình thường";
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"Loại: {vType}  |  Tính cách: {pName}", infoStyle);
            cy += 22f;
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"Tốc độ hiện tại: {speed:F1} m/s", infoStyle);
            cy += 22f;

            Vector3 startP = SelectedVehicle.GetStartPosition();
            Vector3 destP = SelectedVehicle.GetDestinationPosition();
            float distToDest = Vector3.Distance(SelectedVehicle.transform.position, destP);

            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"📍 Xuất phát: ({startP.x:F0}, {startP.z:F0})", infoStyle);
            cy += 20f;
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"🏁 Đích:        ({destP.x:F0}, {destP.z:F0})", infoStyle);
            cy += 20f;
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"📏 Còn lại: {distToDest:F0}m", infoStyle);
            cy += 22f;

            var path = SelectedVehicle.GetCurrentPath();
            int idx = SelectedVehicle.GetCurrentPathIndex();
            int total = path != null ? path.Count : 0;
            string state = SelectedVehicle.IsStuck ? "⚠ Kẹt xe" :
                           speed < 0.3f ? "⏸ Dừng" : "▶ Đang chạy";
            GUI.Label(new Rect(x + 10, cy, panelW - 20, 20), $"Waypoint: {idx}/{total}  |  {state}", infoStyle);
            cy += 22f;

            // Nút bỏ chọn
            GUI.color = new Color(1f, 0.3f, 0.3f, 0.9f);
            if (GUI.Button(new Rect(x + panelW - 80, y + panelH - 30, 70, 22), "✕ Đóng"))
                SelectedVehicle = null;
            GUI.color = Color.white;
        }

        private static void EnsureLineMaterial()
        {
            if (_lineMat == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                _lineMat = new Material(shader);
                _lineMat.hideFlags = HideFlags.HideAndDontSave;
                _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lineMat.SetInt("_ZWrite", 0);
            }
            // LessEqual: Vẽ đúng chiều sâu không gian, không đè xuyên qua các khối nhà
            _lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
        }
    }
}
