using UnityEngine;
using Biosphere.Core;

namespace Biosphere.Render
{
    /// <summary>
    /// Owns one batch per render layer and drives all of them each frame.
    /// Add a layer here and it participates in sorting automatically -- the
    /// depth encoding in the shader does the rest.
    /// </summary>
    public class SpriteLayerRenderer : MonoBehaviour
    {
        [SerializeField] private Shader spriteShader;
        [SerializeField] private Texture2D spriteAtlas;
        [SerializeField] private int atlasCols = 16;
        [SerializeField] private int atlasRows = 16;

        [Tooltip("Sprite pixel canvas size. Micro-scale target is 24-28 px; " +
                 "with PixelsPerUnit=16 that means a creature is ~1.5-1.75 tiles " +
                 "tall, which reads as 'small figure standing on a tile'.")]
        [SerializeField] private int spritePixelSize = 24;

        private WorldConfig _cfg;
        private Material _mat;
        private readonly System.Collections.Generic.Dictionary<int, InstancedSpriteBatch> _batches = new();

        public float SpriteWorldSize => (float)spritePixelSize / _cfg.PixelsPerUnit;

        public void Initialize(WorldConfig cfg)
        {
            _cfg = cfg;
            _mat = new Material(spriteShader != null
                ? spriteShader
                : Shader.Find("Biosphere/PixelSpriteInstanced"));
            _mat.mainTexture = spriteAtlas;
            _mat.SetFloat("_AtlasCols", atlasCols);
            _mat.SetFloat("_AtlasRows", atlasRows);
            _mat.SetFloat("_Cutoff", 0.5f);
            // Depth scale must be small enough that the largest depth value
            // (UiWorldSpace band + map height) stays well inside the ortho
            // near/far range, and large enough that adjacent Y rows separate.
            _mat.SetFloat("_DepthScale", 1e-5f);
            _mat.enableInstancing = true;

            if (spriteAtlas != null)
            {
                spriteAtlas.filterMode = FilterMode.Point;   // crisp at every zoom
                spriteAtlas.wrapMode = TextureWrapMode.Clamp;
            }
        }

        public InstancedSpriteBatch GetBatch(int layer)
        {
            if (_batches.TryGetValue(layer, out var b)) return b;
            var bounds = new Bounds(
                new Vector3(_cfg.GridW * 0.5f, _cfg.GridH * 0.5f, 0f),
                new Vector3(_cfg.GridW + 8, _cfg.GridH + 8, 100f));
            b = new InstancedSpriteBatch(_mat, _cfg.MaxSpritesPerLayer, bounds);
            _batches[layer] = b;
            return b;
        }

        public void DrawAll(float brightness, Color skyTint)
        {
            foreach (var kv in _batches)
            {
                kv.Value.Upload();
                kv.Value.Draw(brightness, skyTint);
            }
        }

        private void OnDestroy()
        {
            foreach (var kv in _batches) kv.Value.Dispose();
            _batches.Clear();
            if (_mat != null) Destroy(_mat);
        }
    }
}
