using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Biosphere.Core;
using Biosphere.Sim;

namespace Biosphere.Render
{
    /// <summary>
    /// One draw call per layer, for any number of sprites in that layer.
    ///
    /// The classic Unity approach -- a GameObject with a SpriteRenderer per
    /// entity -- costs a Transform, a culling entry, a renderer component and a
    /// sorting-group evaluation each. At 20,000 creatures that is the entire
    /// frame budget before any simulation runs.
    ///
    /// Instead: a persistent GraphicsBuffer of SpriteInstance structs, filled by
    /// a Burst job straight from the simulation's NativeArrays, and one
    /// Graphics.DrawProceduralNow / RenderPrimitives call. No GC, no Transforms,
    /// no per-entity managed objects anywhere in the pipeline.
    /// </summary>
    public sealed class InstancedSpriteBatch : IDisposable
    {
        public const int Stride = 40;      // 4*4 + 4*4 + 2*4 bytes -- must match the shader struct

        private readonly Material _material;
        private readonly MaterialPropertyBlock _props;   // per-layer, so layers
                                                         // don't stomp each other's
                                                         // instance buffer on a
                                                         // shared material
        private readonly int _capacity;
        private GraphicsBuffer _buffer;
        private NativeArray<SpriteInstance> _cpu;
        private int _count;
        private Bounds _bounds;

        private static readonly int InstancesId  = Shader.PropertyToID("_Instances");
        private static readonly int SkyTintId    = Shader.PropertyToID("_SkyTint");
        private static readonly int BrightnessId = Shader.PropertyToID("_Brightness");

        public NativeArray<SpriteInstance> Buffer => _cpu;
        public int Count => _count;
        public int Capacity => _capacity;
        public Material Material => _material;

        public InstancedSpriteBatch(Material material, int capacity, Bounds worldBounds)
        {
            _material = material;
            _props = new MaterialPropertyBlock();
            _capacity = capacity;
            _bounds = worldBounds;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, Stride);
            _cpu = new NativeArray<SpriteInstance>(capacity, Allocator.Persistent,
                                                   NativeArrayOptions.UninitializedMemory);
        }

        public void SetCount(int n) => _count = math.min(n, _capacity);

        /// <summary>Upload only the used prefix of the buffer. Uploading 4,000
        /// live instances out of a 65,536 capacity costs 4,000 * 40 bytes, not
        /// 2.6 MB.</summary>
        public void Upload()
        {
            if (_count <= 0) return;
            _buffer.SetData(_cpu, 0, 0, _count);
        }

        public void Draw(float brightness, Color skyTint, Camera cam = null)
        {
            if (_count <= 0) return;
            _props.SetBuffer(InstancesId, _buffer);
            _props.SetFloat(BrightnessId, brightness);
            _props.SetColor(SkyTintId, skyTint);

#if UNITY_2022_2_OR_NEWER
            var rp = new RenderParams(_material)
            {
                worldBounds = _bounds,
                camera = cam,
                matProps = _props,
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off
            };
            // 6 verts per quad, _count instances, no index buffer, no mesh.
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count);
#else
            // Pre-2022.2 path. Same GPU work, older entry point; it takes the
            // property block as a trailing argument instead of via RenderParams.
            _material.SetBuffer(InstancesId, _buffer);
            Graphics.DrawProcedural(_material, _bounds, MeshTopology.Triangles,
                                    6, _count, cam, _props);
#endif
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            if (_cpu.IsCreated) _cpu.Dispose();
        }
    }

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
