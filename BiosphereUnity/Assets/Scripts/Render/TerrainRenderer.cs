using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Biosphere.Core;
using Biosphere.Sim;

namespace Biosphere.Render
{
    /// <summary>
    /// The terrain is ONE Texture2D on ONE quad. Not a Unity Tilemap.
    ///
    /// Why: a Tilemap at 576x576 is 331,776 tile entries with chunked mesh
    /// rebuilds, and every terraform stroke triggers a chunk remesh. A texture
    /// is a flat 331k-texel array where a tile edit is a texel write and the
    /// upload cost is proportional to the DIRTY RECT, not the map. Repainting a
    /// 16x16 brush stroke costs 256 texels. That is what "instant tile updates"
    /// actually requires at this scale.
    ///
    /// One texel = one tile, FilterMode.Point, no mips, no compression. The quad
    /// is scaled to GridW x GridH world units, so 1 world unit = 1 tile = 1 texel,
    /// which keeps the pixel grid exact at any zoom.
    ///
    /// The day/night grade, cloud shadow and rain sparkle are NOT baked into the
    /// texture -- they are shader uniforms and a separate low-res overlay, so a
    /// sunset costs zero texture uploads.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class TerrainRenderer : MonoBehaviour
    {
        [SerializeField] private Shader terrainShader;

        private WorldConfig _cfg;
        private WorldGrid _world;

        private Texture2D _albedo;         // one texel per tile: base terrain colour
        private Texture2D _cloudTex;       // one texel per tile: R = cloud density
        private NativeArray<Color32> _albedoPixels;
        private NativeArray<Color32> _cloudPixels;

        private Material _mat;
        private TileRect _dirty;
        private int _lastTerrainRevision = -1;
        private float _cloudUploadTimer;

        private static readonly int SkyTintId    = Shader.PropertyToID("_SkyTint");
        private static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        private static readonly int CloudTexId   = Shader.PropertyToID("_CloudTex");
        private static readonly int RainId       = Shader.PropertyToID("_Rain");
        private static readonly int TimeSeedId   = Shader.PropertyToID("_TimeSeed");

        public void Initialize(WorldConfig cfg, WorldGrid world)
        {
            _cfg = cfg; _world = world;

            _albedo = NewPointTexture(world.W, world.H, TextureFormat.RGBA32);
            _cloudTex = NewPointTexture(world.W, world.H, TextureFormat.RGBA32);
            _albedoPixels = _albedo.GetRawTextureData<Color32>();
            _cloudPixels = _cloudTex.GetRawTextureData<Color32>();

            var mr = GetComponent<MeshRenderer>();
            _mat = new Material(terrainShader != null ? terrainShader : Shader.Find("Biosphere/TerrainUnlit"));
            _mat.mainTexture = _albedo;
            _mat.SetTexture(CloudTexId, _cloudTex);
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.allowOcclusionWhenDynamic = false;

            GetComponent<MeshFilter>().sharedMesh = QuadMesh.UnitQuad();
            transform.localScale = new Vector3(world.W, world.H, 1f);
            transform.position = new Vector3(world.W * 0.5f, world.H * 0.5f, RenderLayer.TerrainBase);

            MarkDirty(new TileRect(0, 0, world.W, world.H));
            RepaintDirty();
        }

        private static Texture2D NewPointTexture(int w, int h, TextureFormat fmt)
        {
            var t = new Texture2D(w, h, fmt, mipChain: false, linear: false)
            {
                filterMode = FilterMode.Point,      // <- the whole point: no bilinear smear
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            return t;
        }

        /// <summary>Queue a rect for repaint. Multiple calls in one frame are
        /// unioned into a single upload.</summary>
        public void MarkDirty(TileRect r) => _dirty = TileRect.Union(_dirty, r);

        public void MarkTileDirty(int x, int y) => MarkDirty(new TileRect(x, y, 1, 1));

        private void LateUpdate()
        {
            if (_world == null) return;

            // Terraform / biome spread bumps the revision; catch anything that
            // changed tiles without explicitly calling MarkDirty.
            if (_world.TerrainRevision != _lastTerrainRevision)
            {
                _lastTerrainRevision = _world.TerrainRevision;
                if (_dirty.IsEmpty) MarkDirty(new TileRect(0, 0, _world.W, _world.H));
            }

            RepaintDirty();
            UploadCloudsThrottled();
            PushLighting();
        }

        private void RepaintDirty()
        {
            if (_dirty.IsEmpty) return;

            var job = new PaintTerrainJob
            {
                Elevation = _world.Elevation,
                Terrain = _world.Terrain,
                Moisture = _world.Moisture,
                Detail = _world.TerrainDetail,
                Dither = _world.Dither,
                Pixels = _albedoPixels,
                GridW = _world.W,
                X0 = _dirty.X, Y0 = _dirty.Y, X1 = _dirty.XMax, Y1 = _dirty.YMax,
                WaterLevel = _cfg.WaterLevel,
                MountainLevel = _cfg.MountainLevel
            };
            job.Schedule(_dirty.H, 8).Complete();

            // Apply() reuploads the whole texture. For a full-map repaint that is
            // correct; for a small brush stroke, use the partial-upload path.
            if (_dirty.W * _dirty.H > _world.N / 4)
                _albedo.Apply(false, false);
            else
                PartialUpload(_albedo, _albedoPixels, _dirty, _world.W);

            _dirty = default;
        }

        /// <summary>
        /// Upload only the dirty rows. Unity has no rect-upload for Texture2D,
        /// so we use a scratch Texture2D of the rect size + Graphics.CopyTexture,
        /// which is a straight GPU-side blit and costs O(rect), not O(map).
        /// </summary>
        private static Texture2D _scratch;
        private static void PartialUpload(Texture2D dst, NativeArray<Color32> src, TileRect r, int gridW)
        {
            if (_scratch == null || _scratch.width < r.W || _scratch.height < r.H)
            {
                if (_scratch != null) Destroy(_scratch);
                _scratch = new Texture2D(math.max(r.W, 64), math.max(r.H, 64),
                                         TextureFormat.RGBA32, false, false)
                { filterMode = FilterMode.Point };
            }

            var scratchPixels = _scratch.GetRawTextureData<Color32>();
            for (int row = 0; row < r.H; row++)
            {
                int srcStart = (r.Y + row) * gridW + r.X;
                int dstStart = row * _scratch.width;
                NativeArray<Color32>.Copy(src, srcStart, scratchPixels, dstStart, r.W);
            }
            _scratch.Apply(false, false);
            Graphics.CopyTexture(_scratch, 0, 0, 0, 0, r.W, r.H, dst, 0, 0, r.X, r.Y);
        }

        /// <summary>Clouds drift continuously, so they get their own texture on a
        /// fixed ~12 Hz upload budget rather than riding the terrain's dirty rect.
        /// The shader interpolates nothing -- point sampled, same as everything.</summary>
        private void UploadCloudsThrottled()
        {
            _cloudUploadTimer += Time.deltaTime;
            if (_cloudUploadTimer < 1f / 12f) return;
            _cloudUploadTimer = 0f;

            for (int i = 0; i < _world.N; i++)
            {
                byte c = (byte)(math.saturate(_world.CloudShadow[i]) * 255f);
                _cloudPixels[i] = new Color32(c, c, c, 255);
            }
            _cloudTex.Apply(false, false);
        }

        private void PushLighting()
        {
            _world.SkyTint(out float brightness, out float3 tint);
            _mat.SetColor(SkyTintId, new Color(tint.x, tint.y, tint.z, 1f));
            _mat.SetFloat(BrightnessId, brightness);
            _mat.SetFloat(RainId, _world.CurrentWeather == Weather.Rainy ? 1f : 0f);
            _mat.SetFloat(TimeSeedId, Time.time);
        }

        private void OnDestroy()
        {
            if (_albedo != null) Destroy(_albedo);
            if (_cloudTex != null) Destroy(_cloudTex);
            if (_mat != null) Destroy(_mat);
        }
    }

    /// <summary>
    /// Terrain colour ramp, ported from environment.py's render(). Runs Burst,
    /// parallel over rows, and only over the dirty rect. Day/night is deliberately
    /// NOT applied here -- that is a shader uniform.
    /// </summary>
    [BurstCompile]
    public struct PaintTerrainJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float>       Elevation;
        [ReadOnly] public NativeArray<TerrainKind> Terrain;
        [ReadOnly] public NativeArray<float>       Moisture;
        [ReadOnly] public NativeArray<float>       Detail;
        [ReadOnly] public NativeArray<float>       Dither;

        [NativeDisableParallelForRestriction]
        public NativeArray<Color32> Pixels;

        public int GridW;
        public int X0, Y0, X1, Y1;
        public float WaterLevel, MountainLevel;

        public void Execute(int rowOffset)
        {
            int y = Y0 + rowOffset;

            float3 waterShallow = new float3(0.20f, 0.85f, 0.80f);
            float3 waterMid     = new float3(0.10f, 0.48f, 0.78f);
            float3 waterDeep    = new float3(0.03f, 0.14f, 0.42f);
            float3 dry          = new float3(0.78f, 0.68f, 0.22f);
            float3 lush         = new float3(0.12f, 0.72f, 0.20f);
            float3 highland     = new float3(0.30f, 0.55f, 0.22f);
            float3 beach        = new float3(0.96f, 0.85f, 0.50f);
            float3 rockLow      = new float3(0.42f, 0.38f, 0.40f);
            float3 rockHigh     = new float3(0.62f, 0.60f, 0.62f);
            float3 snow         = new float3(0.96f, 0.97f, 1.00f);

            for (int x = X0; x < X1; x++)
            {
                int i = y * GridW + x;
                float e = Elevation[i];
                float3 c;

                switch (Terrain[i])
                {
                    case TerrainKind.DeepWater:
                    case TerrainKind.Water:
                    {
                        float depth = math.pow(math.saturate((WaterLevel - e) / WaterLevel), 0.65f);
                        c = depth < 0.5f
                            ? math.lerp(waterShallow, waterMid, math.saturate(depth / 0.5f))
                            : math.lerp(waterMid, waterDeep, math.saturate((depth - 0.5f) / 0.5f));
                        break;
                    }
                    case TerrainKind.Beach:
                        c = beach;
                        break;
                    case TerrainKind.Mountain:
                    {
                        float t = math.saturate((e - MountainLevel) / (1f - MountainLevel));
                        float3 rock = math.lerp(rockLow, rockHigh, t);
                        c = math.lerp(rock, snow, math.saturate((e - 0.90f) / 0.10f));
                        break;
                    }
                    default:
                    {
                        float3 moist = math.lerp(dry, lush, Moisture[i]);
                        float et = math.saturate((e - WaterLevel) / (MountainLevel - WaterLevel));
                        c = math.lerp(moist, highland, math.pow(et, 1.5f));
                        break;
                    }
                }

                // Broad organic mottling + fine pixel grain. Both are static
                // per-tile fields, so this is deterministic and never shimmers.
                c = math.saturate(c + Detail[i] * 0.05f + Dither[i] * 0.045f);

                Pixels[i] = new Color32(
                    (byte)(c.x * 255f), (byte)(c.y * 255f), (byte)(c.z * 255f), 255);
            }
        }
    }

    public static class QuadMesh
    {
        private static Mesh _quad;

        /// <summary>Unit quad centred on origin, UV 0..1. Shared by the terrain
        /// plane and every instanced sprite draw.</summary>
        public static Mesh UnitQuad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "BiosphereUnitQuad" };
            _quad.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f,  0.5f, 0f),  new Vector3(-0.5f, 0.5f, 0f)
            });
            _quad.SetUVs(0, new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)
            });
            _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _quad.RecalculateBounds();
            _quad.UploadMeshData(false);
            return _quad;
        }
    }
}
