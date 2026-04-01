using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OSMImporter
{
    /// <summary>
    /// Sleek side-panel listing all named OSM areas.
    /// Click any row to fly the camera to that area.
    /// Press Tab to show/hide.
    /// </summary>
    public class MapAreaPanel : MonoBehaviour
    {
        [Header("References (auto-found if null)")]
        public MapCameraController CameraController;
        public OSMAreaRegistry     Registry;

        // ── Appearance ────────────────────────────────────────────────────────
        private const float PANEL_W    = 260f;
        private const float ROW_H      = 38f;
        private const float HEADER_H   = 48f;
        private const float SEARCH_H   = 36f;

        private static readonly Color COL_BG       = new Color(0.06f, 0.07f, 0.10f, 0.95f);
        private static readonly Color COL_HEADER    = new Color(0.10f, 0.55f, 0.90f, 1.00f);
        private static readonly Color COL_ROW       = new Color(0.10f, 0.12f, 0.16f, 1.00f);
        private static readonly Color COL_ROW_ALT   = new Color(0.12f, 0.14f, 0.20f, 1.00f);
        private static readonly Color COL_HOVER     = new Color(0.18f, 0.48f, 0.85f, 1.00f);
        private static readonly Color COL_SEARCH_BG = new Color(0.14f, 0.16f, 0.22f, 1.00f);
        private static readonly Color COL_TEXT      = new Color(0.95f, 0.96f, 1.00f, 1.00f);
        private static readonly Color COL_SUBTEXT   = new Color(0.55f, 0.75f, 0.95f, 1.00f);
        private static readonly Color COL_BADGE     = new Color(0.10f, 0.55f, 0.90f, 0.35f);

        // ── private ───────────────────────────────────────────────────────────
        private Canvas    _canvas;
        private GameObject _panel;
        private Transform  _rowContainer;
        private InputField _searchField;
        private string     _filter  = "";
        private bool       _visible = true;

        private readonly List<(GameObject row, OSMAreaRegistry.AreaEntry entry)> _rows
            = new List<(GameObject, OSMAreaRegistry.AreaEntry)>();

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (CameraController == null)
                CameraController = FindFirstObjectByType<MapCameraController>();
            if (Registry == null)
                Registry = FindFirstObjectByType<OSMAreaRegistry>();

            BuildUI();

            if (Registry != null && Registry.Areas.Count > 0)
                PopulateList();
            else
                // If registry hasn't been populated yet, show a message and retry
                StartCoroutine(RetryPopulate());
        }

        private System.Collections.IEnumerator RetryPopulate()
        {
            yield return new WaitForSeconds(0.5f);
            if (Registry != null) PopulateList();
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUI()
        {
            // ── Root Canvas ────────────────────────────────────────────────────
            var canvasGO = new GameObject("OSM_AreaPanel_Canvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution  = new Vector2(1920, 1080);
            scaler.screenMatchMode      = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight   = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists so UI clicks/input work
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var esGO = new GameObject("EventSystem");
                esGO.AddComponent<EventSystem>();
                esGO.AddComponent<StandaloneInputModule>();
            }

            // ── Toggle button (☰) ─────────────────────────────────────────────
            BuildToggleButton(canvasGO.transform);

            // ── Main panel ────────────────────────────────────────────────────
            _panel = BuildPanel(canvasGO.transform);

            // Header
            BuildHeader(_panel.transform);

            // Search
            BuildSearchBox(_panel.transform);

            // Scroll area
            var sv = BuildScrollView(_panel.transform);
            _rowContainer = sv.transform.Find("Viewport/Content");
        }

        private void BuildToggleButton(Transform root)
        {
            var btn = MakeRect(root, "ToggleBtn", COL_HEADER * 0.85f);
            var rt  = btn.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot     = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(44, 44);
            rt.anchoredPosition = new Vector2(8, -8);

            MakeLabel(btn.transform, "☰", 20, COL_TEXT, TextAnchor.MiddleCenter);

            var b = btn.AddComponent<Button>();
            b.targetGraphic = btn.GetComponent<Image>();
            var bc = b.colors;
            bc.normalColor      = COL_HEADER * 0.85f;
            bc.highlightedColor = COL_HEADER;
            bc.pressedColor     = COL_HOVER;
            b.colors = bc;
            b.onClick.AddListener(TogglePanel);
        }

        private GameObject BuildPanel(Transform root)
        {
            var panel = MakeRect(root, "Panel", COL_BG);
            var rt    = panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot     = new Vector2(0, 0.5f);
            rt.offsetMin = new Vector2(60, 8);
            rt.offsetMax = new Vector2(60 + PANEL_W, -8);
            return panel;
        }

        private void BuildHeader(Transform parent)
        {
            var hdr = MakeRect(parent, "Header", COL_HEADER);
            var rt  = hdr.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot     = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(0, -HEADER_H); rt.offsetMax = new Vector2(0, 0);

            MakeLabel(hdr.transform, "🗺  Khu Vực", 15, COL_TEXT, TextAnchor.MiddleCenter);
        }

        private void BuildSearchBox(Transform parent)
        {
            var box = MakeRect(parent, "SearchBox", COL_SEARCH_BG);
            var rt  = box.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot     = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(8, -(HEADER_H + SEARCH_H + 6));
            rt.offsetMax = new Vector2(-8, -(HEADER_H + 6));

            _searchField               = box.AddComponent<InputField>();
            _searchField.targetGraphic = box.GetComponent<Image>();

            var txt = MakeLabel(box.transform, "", 13, COL_TEXT, TextAnchor.MiddleLeft);
            txt.GetComponent<RectTransform>().offsetMin = new Vector2(30, 0);
            _searchField.textComponent = txt;

            var ph = MakeLabel(box.transform, "🔍  Tìm kiếm...", 13, COL_SUBTEXT, TextAnchor.MiddleLeft);
            ph.GetComponent<RectTransform>().offsetMin = new Vector2(10, 0);
            _searchField.placeholder = ph;

            _searchField.onValueChanged.AddListener(v => { _filter = v.ToLowerInvariant(); ApplyFilter(); });
        }

        private GameObject BuildScrollView(Transform parent)
        {
            float top = HEADER_H + SEARCH_H + 14f;

            var sv = new GameObject("ScrollView");
            sv.transform.SetParent(parent, false);
            var rt = sv.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(0, 4);
            rt.offsetMax = new Vector2(0, -top);

            var sr       = sv.AddComponent<ScrollRect>();
            sr.horizontal = false;

            // Viewport
            var vp   = new GameObject("Viewport"); vp.transform.SetParent(sv.transform, false);
            var vpRT = vp.AddComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
            vp.AddComponent<Image>().color = Color.clear;
            var mask = vp.AddComponent<Mask>(); mask.showMaskGraphic = false;
            sr.viewport = vpRT;

            // Content
            var content = new GameObject("Content"); content.transform.SetParent(vp.transform, false);
            var cRT = content.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0, 1); cRT.anchorMax = new Vector2(1, 1);
            cRT.pivot     = new Vector2(0.5f, 1);
            cRT.offsetMin = Vector2.zero; cRT.offsetMax = Vector2.zero;

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing              = 2;
            vlg.padding              = new RectOffset(4, 4, 4, 4);
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight     = true;

            var csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sr.content = cRT;

            return sv;
        }

        // ── List population ───────────────────────────────────────────────────

        private void PopulateList()
        {
            if (Registry == null || Registry.Areas.Count == 0)
            {
                // Show "empty" message
                var empty = MakeRect(_rowContainer, "EmptyMsg", Color.clear);
                var le    = empty.AddComponent<LayoutElement>(); le.preferredHeight = 60f;
                MakeLabel(empty.transform, "Không có khu vực.\nHãy Generate lại với Generate Labels ✅",
                    11, COL_SUBTEXT, TextAnchor.MiddleCenter);
                return;
            }

            int i = 0;
            foreach (var entry in Registry.Areas)
                AddRow(entry, i++);
        }

        private void AddRow(OSMAreaRegistry.AreaEntry entry, int index)
        {
            Color rowBg = (index % 2 == 0) ? COL_ROW : COL_ROW_ALT;
            var row     = MakeRect(_rowContainer, entry.Name, rowBg);
            var le      = row.AddComponent<LayoutElement>(); le.preferredHeight = ROW_H;

            // Row left icon / bullet
            var bullet = MakeRect(row.transform, "Bullet", COL_HEADER);
            var brt    = bullet.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0, 0.15f); brt.anchorMax = new Vector2(0, 0.85f);
            brt.offsetMin = new Vector2(6, 0); brt.offsetMax = new Vector2(10, 0);

            // Main name text
            var nameTxt = MakeLabel(row.transform, entry.Name, 12, COL_TEXT, TextAnchor.MiddleLeft);
            var nRT     = nameTxt.GetComponent<RectTransform>();
            nRT.anchorMin = new Vector2(0, 0); nRT.anchorMax = new Vector2(1, 1);
            nRT.offsetMin = new Vector2(18, 2); nRT.offsetMax = new Vector2(-6, -2);
            nameTxt.verticalOverflow = VerticalWrapMode.Truncate;

            // Sub-text badge (area type)
            if (!string.IsNullOrEmpty(entry.AreaType))
            {
                var badge = MakeRect(row.transform, "Badge", COL_BADGE);
                var bdRT  = badge.GetComponent<RectTransform>();
                bdRT.anchorMin = new Vector2(1, 0.2f); bdRT.anchorMax = new Vector2(1, 0.8f);
                bdRT.pivot     = new Vector2(1, 0.5f);
                bdRT.sizeDelta = new Vector2(60, 0);
                bdRT.anchoredPosition = new Vector2(-6, 0);
                MakeLabel(badge.transform, entry.AreaType, 9, COL_SUBTEXT, TextAnchor.MiddleCenter);
            }

            // Button
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = row.GetComponent<Image>();
            var bc = btn.colors;
            bc.normalColor      = rowBg;
            bc.highlightedColor = COL_HOVER;
            bc.pressedColor     = COL_HEADER;
            bc.colorMultiplier  = 1f;
            btn.colors = bc;

            Vector3 pos = entry.Centroid;
            btn.onClick.AddListener(() => FlyTo(pos, entry.Name));

            _rows.Add((row, entry));
        }

        // ── Camera fly-to ─────────────────────────────────────────────────────

        private void FlyTo(Vector3 centroid, string name)
        {
            if (CameraController != null)
            {
                CameraController.FlyTo(centroid);
            }
            else
            {
                Camera cam = Camera.main;
                if (cam != null)
                    cam.transform.position = new Vector3(centroid.x, cam.transform.position.y, centroid.z);
            }
            Debug.Log($"[OSM Panel] → {name}");
        }

        // ── Filter ────────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            foreach (var (row, entry) in _rows)
            {
                bool show = string.IsNullOrEmpty(_filter) ||
                            entry.Name.ToLowerInvariant().Contains(_filter) ||
                            entry.AreaType.ToLowerInvariant().Contains(_filter);
                row.SetActive(show);
            }
        }

        // ── Toggle ────────────────────────────────────────────────────────────

        public void TogglePanel()
        {
            _visible = !_visible;
            if (_panel) _panel.SetActive(_visible);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab)) TogglePanel();
        }

        // ── UI Helpers ────────────────────────────────────────────────────────

        private static GameObject MakeRect(Transform parent, string name, Color color)
        {
            var go  = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img  = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        private static Text MakeLabel(Transform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Lbl");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.AddComponent<Text>();
            t.text      = text;
            t.fontSize  = size;
            t.color     = color;
            t.alignment = anchor;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }
    }
}
