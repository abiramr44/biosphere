using Unity.Mathematics;
using UnityEngine;
using Biosphere.Core;

namespace Biosphere.CameraRig
{
    /// <summary>
    /// Strict top-down orthographic pixel camera.
    ///
    /// Three rules make pixel art stay crisp, and all three are enforced here:
    ///
    /// 1. ORTHOGRAPHIC SIZE IS DERIVED, NEVER TYPED. orthographicSize =
    ///    screenHeight / (2 * PixelsPerUnit * zoom). Setting it by hand is the
    ///    single most common cause of shimmering pixel art -- it puts a
    ///    fractional number of screen pixels on each texel.
    ///
    /// 2. ZOOM SNAPS TO CLEAN STEPS. Only integer (and, optionally, 1/2 and 1/4)
    ///    scale factors are allowed. A 1.37x zoom means some texels get 1 screen
    ///    pixel and their neighbours get 2 -- that is the "wobbly pixels" look.
    ///
    /// 3. CAMERA POSITION SNAPS TO THE TEXEL GRID. Even at a clean zoom, a camera
    ///    at x = 12.3718 puts every texel edge on a subpixel boundary. We snap
    ///    the rendered position to 1/PixelsPerUnit while keeping the true
    ///    position in a separate float, so panning still feels smooth.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class PixelCameraController : MonoBehaviour
    {
        [SerializeField] private WorldConfig cfg;

        [Header("Zoom")]
        [Tooltip("Allowed zoom factors. Integers keep texels square; 0.5/0.25 " +
                 "are included so a 576x576 map can be viewed whole.")]
        [SerializeField] private float[] zoomSteps = { 0.25f, 0.5f, 1f, 2f, 3f, 4f, 6f, 8f, 12f, 16f };
        [SerializeField] private int zoomIndex = 2;

        [Header("Pan")]
        [SerializeField] private float keyboardPanSpeed = 40f;   // tiles/sec at zoom 1
        [SerializeField] private float edgeMargin = 4f;          // tiles of overscroll allowed

        private Camera _cam;
        private float2 _truePos;      // unsnapped, for smooth motion
        private bool _dragging;
        private Vector3 _dragOriginWorld;

        public float Zoom => zoomSteps[zoomIndex];
        public Camera Cam => _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.orthographic = true;                    // strict bird's-eye ortho
            _cam.transform.rotation = Quaternion.identity;
            _cam.nearClipPlane = -100f;
            _cam.farClipPlane = 100f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.04f, 0.05f, 0.09f);
            _cam.allowMSAA = false;                      // MSAA on pixel art = mush
            _cam.allowHDR = false;
            _cam.useOcclusionCulling = false;

            _truePos = new float2(cfg.GridW * 0.5f, cfg.GridH * 0.5f);
            ApplyZoom();
        }

        private void LateUpdate()
        {
            HandleKeyboardPan();
            HandleMouseDrag();
            HandleScrollZoom();
            ClampToWorld();
            ApplySnappedPosition();
        }

        private void HandleKeyboardPan()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            if (h == 0f && v == 0f) return;
            // Divide by zoom so panning feels the same speed on screen at any zoom.
            _truePos += new float2(h, v) * (keyboardPanSpeed / Zoom) * Time.unscaledDeltaTime;
        }

        private void HandleMouseDrag()
        {
            // Middle mouse drags the map. Left mouse is reserved for tools.
            if (Input.GetMouseButtonDown(2))
            {
                _dragging = true;
                _dragOriginWorld = _cam.ScreenToWorldPoint(Input.mousePosition);
            }
            if (Input.GetMouseButtonUp(2)) _dragging = false;

            if (_dragging)
            {
                Vector3 now = _cam.ScreenToWorldPoint(Input.mousePosition);
                Vector3 delta = _dragOriginWorld - now;
                _truePos += new float2(delta.x, delta.y);
            }
        }

        private void HandleScrollZoom()
        {
            float scroll = Input.mouseScrollDelta.y;
            if (math.abs(scroll) < 0.01f) return;

            // Zoom toward the cursor: remember the world point under the mouse,
            // change zoom, then shift the camera so that point stays put.
            Vector3 before = _cam.ScreenToWorldPoint(Input.mousePosition);

            zoomIndex = math.clamp(zoomIndex + (scroll > 0 ? 1 : -1), 0, zoomSteps.Length - 1);
            ApplyZoom();
            ApplySnappedPosition();     // ortho size changed; refresh before re-projecting

            Vector3 after = _cam.ScreenToWorldPoint(Input.mousePosition);
            _truePos += new float2(before.x - after.x, before.y - after.y);
        }

        private void ApplyZoom()
        {
            // THE formula. Screen height in pixels / (2 * PPU * zoom).
            _cam.orthographicSize = Screen.height / (2f * cfg.PixelsPerUnit * Zoom);
        }

        private void ClampToWorld()
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * _cam.aspect;

            float minX = math.min(cfg.GridW * 0.5f, halfW - edgeMargin);
            float maxX = math.max(cfg.GridW * 0.5f, cfg.GridW - halfW + edgeMargin);
            float minY = math.min(cfg.GridH * 0.5f, halfH - edgeMargin);
            float maxY = math.max(cfg.GridH * 0.5f, cfg.GridH - halfH + edgeMargin);

            _truePos.x = math.clamp(_truePos.x, minX, maxX);
            _truePos.y = math.clamp(_truePos.y, minY, maxY);
        }

        private void ApplySnappedPosition()
        {
            float texel = 1f / cfg.PixelsPerUnit;
            float snappedX = math.round(_truePos.x / texel) * texel;
            float snappedY = math.round(_truePos.y / texel) * texel;
            transform.position = new Vector3(snappedX, snappedY, -50f);
        }

        /// <summary>Screen point -> tile coordinate. Returns false if outside the
        /// map. This is the one place screen->world conversion should happen.</summary>
        public bool ScreenToTile(Vector3 screenPos, out int tx, out int ty)
        {
            Vector3 w = _cam.ScreenToWorldPoint(screenPos);
            tx = (int)math.floor(w.x);
            ty = (int)math.floor(w.y);
            return cfg.InBounds(tx, ty);
        }

        public void ResetView()
        {
            _truePos = new float2(cfg.GridW * 0.5f, cfg.GridH * 0.5f);
            zoomIndex = 2;
            ApplyZoom();
        }

        public void FocusOn(int tx, int ty)
        {
            _truePos = new float2(tx + 0.5f, ty + 0.5f);
        }

        private void OnRectTransformDimensionsChange() => ApplyZoom();
    }
}
