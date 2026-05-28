using System.Collections;
using UnityEngine;

namespace OSMImporter
{
    /// <summary>
    /// Pivot-based camera controller.
    ///   _pivot    = ground point being looked at
    ///   _yaw      = rotation around Y
    ///   _pitch    = elevation angle (degrees)
    ///   _distance = camera distance from pivot
    ///
    /// WASD        — pan pivot in camera's CURRENT XZ facing direction
    /// Scroll      — zoom
    /// Middle-drag — pan pivot (hold middle button and drag)
    /// Right-drag  — orbit (pitch + yaw)
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class MapCameraController : MonoBehaviour
    {
        [Header("Movement")]
        public float PanSpeed         = 60f;
        public float ZoomSpeed        = 0.12f;
        public float MinDistance      = 5f;
        public float MaxDistance      = 800f;
        public float SmoothTime       = 0.15f;

        [Header("Orbit (right-mouse drag)")]
        public float OrbitSensitivity = 0.35f;
        public float MinPitch         = 8f;
        public float MaxPitch         = 90f;

        [Header("Fly-to")]
        public float FlyDuration      = 0.8f;

        // ── source-of-truth state ─────────────────────────────────────────────
        private Vector3 _pivot;
        private float   _yaw;
        private float   _pitch;
        private float   _distance;

        // smooth-damp
        private Vector3 _pivotVel;
        private float   _distVel;

        // orbit drag
        private Vector2 _orbitPrev;

        // middle-mouse pan drag
        private Vector3 _panPrev;       // world pos under mouse at button-down
        private bool    _panActive;

        private bool      _flying;
        private Coroutine _flyRoutine;
        private Camera    _cam;

        // ─────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            _cam   = GetComponent<Camera>();
            _yaw   = transform.eulerAngles.y;
            _pitch = Mathf.Clamp(transform.eulerAngles.x, MinPitch, MaxPitch);

            float sinP = Mathf.Sin(_pitch * Mathf.Deg2Rad);
            _distance  = sinP > 0.01f
                ? Mathf.Clamp(transform.position.y / sinP, MinDistance, MaxDistance)
                : 150f;

            _pivot   = transform.position - CalcOffset(_yaw, _pitch, _distance);
            _pivot.y = 0f;
        }

        private void Update()
        {
            if (_flying) return;
            HandleOrbit();
            HandleMiddlePan();
            HandleKeyPan();
            HandleScroll();
            ApplyTransform();
        }

        // ── Right-mouse: rotate view direction only (camera stays in place) ──
        private void HandleOrbit()
        {
            if (Input.GetMouseButtonDown(1)) _orbitPrev = Input.mousePosition;
            if (!Input.GetMouseButton(1))    return;

            Vector2 delta = (Vector2)Input.mousePosition - _orbitPrev;
            _orbitPrev    = Input.mousePosition;

            _yaw   += delta.x * OrbitSensitivity;
            _pitch -= delta.y * OrbitSensitivity;
            _pitch  = Mathf.Clamp(_pitch, MinPitch, MaxPitch);

            // Keep camera position fixed — recompute pivot from current position
            // so the camera rotates in-place rather than orbiting a distant point.
            _pivot   = transform.position - CalcOffset(_yaw, _pitch, _distance);
            _pivot.y = 0f;
        }

        // ── Middle-mouse pan ──────────────────────────────────────────────────
        private void HandleMiddlePan()
        {
            if (Input.GetMouseButtonDown(2))
            {
                _panPrev   = HitGround();
                _panActive = true;
            }
            if (Input.GetMouseButtonUp(2)) _panActive = false;
            if (!_panActive) return;

            Vector3 cur = HitGround();
            // Move pivot by the delta so ground "sticks" to cursor
            Vector3 delta = _panPrev - cur;
            delta.y  = 0f;
            _pivot  += delta;
            _pivot.y = 0f;
            // re-sample so it feels 1:1 next frame
            _panPrev = HitGround();
        }

        // ── WASD pan — always in camera's current facing direction ───────────
        private void HandleKeyPan()
        {
            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");

            // Space = zoom out (increase height), Left Ctrl = zoom in
            if (Input.GetKey(KeyCode.Space))
                _distance = Mathf.Clamp(_distance * (1f + 2f * Time.deltaTime), MinDistance, MaxDistance);
            if (Input.GetKey(KeyCode.LeftControl))
                _distance = Mathf.Clamp(_distance * (1f - 2f * Time.deltaTime), MinDistance, MaxDistance);

            if (Mathf.Abs(h) < 0.01f && Mathf.Abs(v) < 0.01f) return;

            // Derive right/forward from the actual camera transform so they
            // always match what the player sees, regardless of pitch/yaw.
            Vector3 right = transform.right;   right.y = 0; right.Normalize();
            Vector3 fwd   = transform.forward; fwd.y   = 0; fwd.Normalize();

            float speed = PanSpeed * Mathf.Max(1f, _distance / 60f);
            _pivot += (right * h + fwd * v) * speed * Time.deltaTime;
            _pivot.y = 0f;
        }

        // ── Scroll zoom ───────────────────────────────────────────────────────
        private void HandleScroll()
        {
            float s = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(s) < 0.001f) return;
            _distance *= 1f - s * ZoomSpeed * 10f;
            _distance  = Mathf.Clamp(_distance, MinDistance, MaxDistance);
        }

        // ── Apply ─────────────────────────────────────────────────────────────
        private void ApplyTransform()
        {
            Vector3 goal = _pivot + CalcOffset(_yaw, _pitch, _distance);
            transform.position = Vector3.SmoothDamp(
                transform.position, goal, ref _pivotVel, SmoothTime);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // ── Fly-to API ────────────────────────────────────────────────────────
        public void FlyTo(Vector3 worldPoint, float targetHeight = -1f)
        {
            if (_flyRoutine != null) StopCoroutine(_flyRoutine);
            _flyRoutine = StartCoroutine(FlyRoutine(
                new Vector3(worldPoint.x, 0f, worldPoint.z)));
        }

        private IEnumerator FlyRoutine(Vector3 destPivot)
        {
            _flying = true;
            Vector3 start = _pivot;
            float elapsed = 0f;
            while (elapsed < FlyDuration)
            {
                elapsed += Time.deltaTime;
                _pivot   = Vector3.Lerp(start, destPivot, Mathf.SmoothStep(0, 1, elapsed / FlyDuration));
                ApplyTransform();
                yield return null;
            }
            _pivot  = destPivot;
            _flying = false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Offset from pivot to camera in world space (spherical coords).
        private static Vector3 CalcOffset(float yawDeg, float pitchDeg, float dist)
        {
            float p = pitchDeg * Mathf.Deg2Rad;
            float y = yawDeg   * Mathf.Deg2Rad;
            return new Vector3(
                -Mathf.Sin(y) * Mathf.Cos(p),
                 Mathf.Sin(p),
                -Mathf.Cos(y) * Mathf.Cos(p)) * dist;
        }

        // Raycast mouse position onto Y=0 ground plane.
        private Vector3 HitGround()
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Mathf.Abs(ray.direction.y) < 0.0001f) return _pivot;
            float t = -ray.origin.y / ray.direction.y;
            return t > 0 ? ray.origin + ray.direction * t : _pivot;
        }
    }
}
