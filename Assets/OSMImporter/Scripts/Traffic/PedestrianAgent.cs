using UnityEngine;

namespace OSMImporter.Traffic
{
    /// <summary>
    /// Represents a pedestrian crossing the road.
    /// Moves between two points and loops back and forth.
    /// </summary>
    public class PedestrianAgent : MonoBehaviour
    {
        public float Speed = 1.5f;
        public Vector3 StartPos;
        public Vector3 EndPos;
        
        private float _progress = 0f;
        private bool _isCrossing = false;

        public void StartCrossing(Vector3 start, Vector3 end, float speed)
        {
            StartPos = start;
            EndPos = end;
            Speed = speed;
            transform.position = start;
            _progress = 0f;
            _isCrossing = true;
        }

        private void Update()
        {
            if (!_isCrossing) return;

            float distance = Vector3.Distance(StartPos, EndPos);
            if (distance < 0.01f)
            {
                _isCrossing = false;
                return;
            }

            // Move pedestrian forward
            _progress += (Speed * Time.deltaTime) / distance;
            transform.position = Vector3.Lerp(StartPos, EndPos, Mathf.Clamp01(_progress));

            // Face target direction
            Vector3 moveDir = (EndPos - StartPos).normalized;
            if (moveDir.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(moveDir);
            }

            // Loop back and forth
            if (_progress >= 1f)
            {
                Vector3 temp = StartPos;
                StartPos = EndPos;
                EndPos = temp;
                _progress = 0f;
            }
        }

        public bool IsCrossing => _isCrossing;

        /// <summary>
        /// Factory to build a simple, high-visibility 3D pedestrian mesh procedurally.
        /// </summary>
        public static GameObject CreatePedestrianObject(Color bodyColor)
        {
            GameObject root = new GameObject("Pedestrian");

            // Lower body / torso (Cylinder)
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.75f, 0f);
            body.transform.localScale = new Vector3(0.3f, 0.6f, 0.3f);
            ApplyColor(body, bodyColor);
            DestroyCollider(body);

            // Head (Sphere)
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.45f, 0f);
            head.transform.localScale = new Vector3(0.32f, 0.32f, 0.32f);
            ApplyColor(head, Color.Lerp(bodyColor, Color.white, 0.4f));
            DestroyCollider(head);

            // Capsule collider on root for Raycast/OverlapSphere detection
            CapsuleCollider col = root.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 0.8f, 0f);
            col.radius = 0.25f;
            col.height = 1.6f;

            // Kinematic Rigidbody so trigger detection works
            Rigidbody rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            root.AddComponent<PedestrianAgent>();

            return root;
        }

        private static void ApplyColor(GameObject go, Color color)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;

            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh);
            if (sh.name.Contains("Universal"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            mr.sharedMaterial = mat;
        }

        private static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
        }
    }
}
