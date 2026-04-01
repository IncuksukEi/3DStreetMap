using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Builds simple, highly-visible procedural vehicle meshes.
    /// Vehicles are sized to be clearly visible from a top-down camera
    /// at typical OSM map scale (1 unit ≈ 1 metre).
    /// </summary>
    public static class VehicleMeshBuilder
    {
        public enum VehicleType { Car, Motorbike, Bus }

        // ── Public factory ────────────────────────────────────────────────────

        public static GameObject Build(VehicleType type, Color bodyColor)
        {
            VehicleParams p = GetParams(type);
            GameObject root = new GameObject($"Vehicle_{type}");

            BuildBody(root.transform, p, bodyColor);
            BuildWheels(root.transform, p);
            BuildLights(root.transform, p, bodyColor);

            return root;
        }

        // ── Body ──────────────────────────────────────────────────────────────

        private static void BuildBody(Transform parent, VehicleParams p, Color bodyColor)
        {
            // Lower chassis
            CreateBox(parent, "Chassis",
                new Vector3(0, p.ChassisH * 0.5f, 0),
                new Vector3(p.W, p.ChassisH, p.L),
                bodyColor);

            // Cabin / roof (slightly lighter/darker)
            if (p.CabinH > 0f)
            {
                Color cabinColor = Color.Lerp(bodyColor, Color.white, 0.15f);
                CreateBox(parent, "Cabin",
                    new Vector3(0, p.ChassisH + p.CabinH * 0.5f, p.CabinZ),
                    new Vector3(p.W * 0.9f, p.CabinH, p.CabinL),
                    cabinColor);
            }
        }

        // ── Wheels ────────────────────────────────────────────────────────────

        private static void BuildWheels(Transform parent, VehicleParams p)
        {
            float wr = p.WheelR;
            float wt = p.WheelT;
            // Wheels sit at height = WheelR (bottom touches ground at Y=0)
            float wy = wr;
            // Offset outward from body edge by half wheel thickness
            float wx = p.W * 0.5f + wt * 0.5f;

            foreach (float z in new[] { p.L * 0.33f, -p.L * 0.33f })
            {
                CreateCylinder(parent, "WheelL", new Vector3(-wx, wy, z), wr, wt, new Color(0.08f, 0.08f, 0.08f));
                CreateCylinder(parent, "WheelR", new Vector3( wx, wy, z), wr, wt, new Color(0.08f, 0.08f, 0.08f));
            }
        }

        // ── Lights ────────────────────────────────────────────────────────────

        private static void BuildLights(Transform parent, VehicleParams p, Color bodyColor)
        {
            float fz = p.L * 0.5f + 0.05f;
            float bz = -p.L * 0.5f - 0.05f;
            float ly = p.ChassisH * 0.6f;
            float lx = p.W * 0.28f;
            float sr = p.WheelR * 0.55f;

            // Headlights — bright white-yellow
            CreateSphere(parent, "HeadL", new Vector3(-lx, ly, fz), sr, new Color(1.0f, 0.97f, 0.7f), emissive: true);
            CreateSphere(parent, "HeadR", new Vector3( lx, ly, fz), sr, new Color(1.0f, 0.97f, 0.7f), emissive: true);

            // Taillights — vivid red
            CreateSphere(parent, "TailL", new Vector3(-lx, ly, bz), sr * 0.8f, new Color(0.95f, 0.05f, 0.05f), emissive: true);
            CreateSphere(parent, "TailR", new Vector3( lx, ly, bz), sr * 0.8f, new Color(0.95f, 0.05f, 0.05f), emissive: true);

            // Top beacon visible from above (matches body color but brighter)
            Color beacon = Color.Lerp(bodyColor, Color.white, 0.4f);
            CreateBox(parent, "TopBeacon",
                new Vector3(0, p.ChassisH + (p.CabinH > 0 ? p.CabinH : 0) + 0.2f, 0),
                new Vector3(p.W * 0.5f, 0.15f, p.L * 0.5f),
                beacon);
        }

        // ── Primitive helpers ─────────────────────────────────────────────────

        private static GameObject CreateBox(Transform parent, string name,
            Vector3 localPos, Vector3 size, Color color, bool emissive = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale    = size;
            ApplyColor(go, color, emissive);
            RemoveCollider(go);
            return go;
        }

        private static void CreateCylinder(Transform parent, string name,
            Vector3 localPos, float radius, float thickness, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition    = localPos;
            // Rotate so the cylinder length-axis (local Y) points along world X (the axle).
            // Unity Cylinder at scale 1: radius=0.5 in XZ, height=1 in Y.
            // After (0,0,90): local Y → world X.
            // Scale mapping: X=radius (visible circle size), Y=thickness (sticking out), Z=radius.
            go.transform.localEulerAngles = new Vector3(0, 0, 90);
            go.transform.localScale       = new Vector3(radius, thickness, radius);
            ApplyColor(go, color);
            RemoveCollider(go);
        }

        private static void CreateSphere(Transform parent, string name,
            Vector3 localPos, float radius, Color color, bool emissive = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale    = Vector3.one * radius * 2f;
            ApplyColor(go, color, emissive);
            RemoveCollider(go);
        }

        private static void ApplyColor(GameObject go, Color color, bool emissive = false)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;

            Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard");
            var mat = new Material(sh);

            bool isUrp = sh.name.Contains("Universal");
            if (isUrp) mat.SetColor("_BaseColor", color); else mat.color = color;

            if (emissive)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", color * 2f);
            }
            mr.sharedMaterial = mat;
        }

        private static void RemoveCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col) Object.Destroy(col);
        }

        // ── Vehicle parameters ────────────────────────────────────────────────

        private struct VehicleParams
        {
            // body
            public float L, W, ChassisH;
            // cabin
            public float CabinH, CabinL, CabinZ;
            // wheels
            public float WheelR, WheelT;
        }

        private static VehicleParams GetParams(VehicleType t)
        {
            // Sizes in Unity world units (≈ metres at scale 1)
            // Made 2-3× bigger than real life so they're visible from a top-down camera at ~150m
            switch (t)
            {
                case VehicleType.Bus:
                    return new VehicleParams
                    {
                        L = 2.5f, W = 0.62f, ChassisH = 0.37f,
                        CabinH = 0.45f, CabinL = 2.25f, CabinZ = 0f,
                        WheelR = 0.15f,  WheelT = 0.1f
                    };
                case VehicleType.Motorbike:
                    return new VehicleParams
                    {
                        L = 0.55f,  W = 0.2f, ChassisH = 0.15f,
                        CabinH = 0.2f, CabinL = 0.2f, CabinZ = 0.075f,
                        WheelR = 0.1f,  WheelT = 0.05f
                    };
                default: // Car
                    return new VehicleParams
                    {
                        L = 1.1f,  W = 0.45f, ChassisH = 0.2f,
                        CabinH = 0.2f, CabinL = 0.55f, CabinZ = 0.05f,
                        WheelR = 0.11f, WheelT = 0.075f
                    };
            }
        }
    }
}
