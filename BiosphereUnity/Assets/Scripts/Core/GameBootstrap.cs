using Unity.Collections;
using Unity.Jobs;          // Schedule() on IJobParallelFor is an extension method
                           // in IJobParallelForExtensions -- without this using,
                           // job.Schedule(...) fails with CS1061.
using Unity.Mathematics;
using UnityEngine;
using Biosphere.CameraRig;
using Biosphere.Particles;
using Biosphere.Render;
using Biosphere.Sim;

namespace Biosphere.Core
{
    public enum ClickTool { Inspect, Seed, Kill, StormStrike, RaiseLand, LowerLand }

    /// <summary>
    /// The composition root. Owns the world, the population, the renderers and
    /// the logger, and drives them in a fixed order every frame:
    ///
    ///   1. simulate (CPU, Burst)     -- the expensive part, and deliberately so
    ///   2. build instance buffers    -- Burst jobs writing straight to upload arrays
    ///   3. upload + draw             -- a handful of draw calls total
    ///
    /// Nothing in this pipeline scales with entity count on the GPU side. Adding
    /// 10,000 more creatures adds CPU sim time and 400 KB of buffer upload; it
    /// adds zero draw calls.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        [Header("Config")]
        public WorldConfig Config;
        public uint Seed = 0;

        [Header("Scene references")]
        public TerrainRenderer Terrain;
        public SpriteLayerRenderer Sprites;
        public PixelParticleSystem Particles;
        // Named PixelCamera, not CameraRig: a field named CameraRig would shadow
        // the Biosphere.CameraRig namespace inside this class.
        public PixelCameraController PixelCamera;

        [Header("Atlas tile indices")]
        [Tooltip("Index into the sprite atlas for each art asset.")]
        public int AtlasCell = 0;
        public int AtlasTree = 1;
        public int AtlasRock = 2;

        [Header("Runtime controls")]
        public bool Paused;
        public int SpeedIndex = 0;                       // into SpeedSteps
        public static readonly float[] SpeedSteps = { 1f, 2f, 4f, 8f, 16f, 32f, 64f };
        public ClickTool Tool = ClickTool.Inspect;
        public int ColorGene = -1;                       // -1 = colour by energy

        [Header("Storm strike")]
        public int StormRadius = 3;
        public float StormKillProb = 0.7f;

        public WorldGrid World { get; private set; }
        public LifeGrid Life { get; private set; }
        public CellLogger Logger { get; private set; }

        /// <summary>Stable CellId of the inspected cell, not an entity index --
        /// indices shift on every swap-remove, IDs never do.</summary>
        public int SelectedCellId { get; private set; }

        /// <summary>Set by DashboardHud each frame. Stops world clicks from
        /// firing when the cursor is over the HUD panel.</summary>
        public bool PointerOverUi;

        private int _decorCount;
        private int _decorRevision = -1;
        private bool _renderersReady;

        public float Speed => SpeedSteps[math.clamp(SpeedIndex, 0, SpeedSteps.Length - 1)];

        private void Start() => NewWorld(Seed);

        public void NewWorld(uint seed)
        {
            World?.Dispose();
            Life?.Dispose();

            uint s = seed != 0 ? seed : (uint)UnityEngine.Random.Range(1, int.MaxValue);
            World = new WorldGrid(Config, s);
            Life = new LifeGrid(Config, World, s ^ 0x5EEDu);
            Logger = new CellLogger();
            SelectedCellId = 0;
            _decorRevision = -1;

            // Renderers own GPU buffers and materials -- initialise them exactly
            // once. Re-initialising on every New World would leak a material and
            // a set of GraphicsBuffers each time.
            if (!_renderersReady)
            {
                Sprites.Initialize(Config);
                Particles.Initialize(Config);
                _renderersReady = true;
            }
            Particles.Clear();
            Terrain.Initialize(Config, World);
            PixelCamera.ResetView();
        }

        private void Update()
        {
            HandleInput();

            if (!Paused)
            {
                World.Step(Time.deltaTime, Speed);
                Life.Step(Time.deltaTime, Speed);
                Logger.Record(World, Life);
                SpawnBirthSparkles();
            }

            BuildAndDraw();
        }

        // ---------------- Rendering ----------------
        private void BuildAndDraw()
        {
            World.SkyTint(out float brightness, out float3 tint);
            var skyTint = new Color(tint.x, tint.y, tint.z, 1f);

            BuildDecorLayer();
            BuildActorLayer();

            Sprites.DrawAll(brightness, skyTint);
        }

        /// <summary>
        /// Static decoration (trees, rocks) is rebuilt ONLY when the terrain
        /// revision changes. On a normal frame this whole function is a single
        /// integer comparison -- the instance buffer from the last rebuild is
        /// still on the GPU and still valid.
        /// </summary>
        private void BuildDecorLayer()
        {
            var batch = Sprites.GetBatch(RenderLayer.GroundDecor);

            if (_decorRevision != World.TerrainRevision)
            {
                _decorRevision = World.TerrainRevision;
                float size = Sprites.SpriteWorldSize;
                int n = 0;
                var dst = batch.Buffer;

                for (int y = 0; y < World.H && n < dst.Length; y++)
                for (int x = 0; x < World.W && n < dst.Length; x++)
                {
                    var kind = World.Decor[y * World.W + x];
                    if (kind == DecorKind.None) continue;

                    dst[n++] = new SpriteInstance
                    {
                        PosSize = new float4(x + 0.5f, y + 0.5f + size * 0.25f, size, size),
                        Color = new float4(1f, 1f, 1f, 1f),
                        AtlasDepth = new float2(
                            kind == DecorKind.Tree ? AtlasTree : AtlasRock,
                            RenderLayer.GroundDecor + (World.H - y))
                    };
                }
                _decorCount = n;
                batch.SetCount(n);
                batch.Upload();
            }
            else
            {
                batch.SetCount(_decorCount);
            }
        }

        private void BuildActorLayer()
        {
            var batch = Sprites.GetBatch(RenderLayer.Actors);
            int n = math.min(Life.Count, batch.Capacity);
            if (n == 0) { batch.SetCount(0); return; }

            var job = new BuildCellInstancesJob
            {
                Pos = Life.Pos.AsArray(),
                Energy = Life.Energy.AsArray(),
                Durability = Life.Durability.AsArray(),
                Genes = Life.Genes.AsArray(),
                Out = batch.Buffer,
                ColorGene = ColorGene,
                GeneMin = ColorGene >= 0 ? GeneTable.Min[ColorGene] : 0f,
                GeneSpan = ColorGene >= 0 ? GeneTable.Span(ColorGene) : 1f,
                MaxEnergy = Config.MaxEnergy,
                SpriteSize = Sprites.SpriteWorldSize,
                LayerDepth = RenderLayer.Actors,
                GridH = World.H,
                AtlasTile = AtlasCell
            };
            job.Schedule(n, 256).Complete();
            batch.SetCount(n);
        }

        private void SpawnBirthSparkles()
        {
            // Cap the VFX, not the births: a saturation event can produce
            // thousands of births in one tick and the sparkles are cosmetic.
            int emitted = 0;
            for (int i = 0; i < Life.NewbornsThisStep.Length && emitted < 32; i++)
            {
                int idx = Life.NewbornsThisStep[i];
                if (idx >= Life.Count) continue;
                int2 p = Life.Pos[idx];
                Particles.Emit(FxPreset.Magic, new float2(p.x + 0.5f, p.y + 0.5f), count: 4, scale: 0.6f);
                emitted++;
            }
        }

        // ---------------- Input ----------------
        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.Space)) Paused = !Paused;
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                SpeedIndex = math.min(SpeedIndex + 1, SpeedSteps.Length - 1);
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                SpeedIndex = math.max(SpeedIndex - 1, 0);
            if (Input.GetKeyDown(KeyCode.N)) NewWorld(0);
            if (Input.GetKeyDown(KeyCode.C)) ColorGene = ColorGene >= GeneTable.Count - 1 ? -1 : ColorGene + 1;

            if (!Input.GetMouseButtonDown(0)) return;
            // The HUD is IMGUI, which has no EventSystem, so it can't be asked
            // "is the pointer over UI?". DashboardHud sets this flag from its own
            // panel rect instead. (Using EventSystem here would also force this
            // assembly to reference UnityEngine.UI for no benefit.)
            if (PointerOverUi) return;
            if (!PixelCamera.ScreenToTile(Input.mousePosition, out int tx, out int ty)) return;

            ApplyTool(tx, ty);
        }

        public void ApplyTool(int tx, int ty)
        {
            switch (Tool)
            {
                case ClickTool.Inspect:
                {
                    int idx = Life.EntityAtTile(tx, ty);
                    SelectedCellId = idx == LifeGrid.Empty ? 0 : Life.CellId[idx];
                    break;
                }
                case ClickTool.Seed:
                    if (Life.SeedAt(tx, ty))
                        Particles.Emit(FxPreset.Magic, new float2(tx + 0.5f, ty + 0.5f), 16);
                    break;

                case ClickTool.Kill:
                    if (Life.KillAt(tx, ty))
                        Particles.Emit(FxPreset.Smoke, new float2(tx + 0.5f, ty + 0.5f), 10);
                    break;

                case ClickTool.StormStrike:
                {
                    Life.StrikeArea(tx, ty, StormRadius, StormKillProb);
                    Particles.Emit(FxPreset.Explosion, new float2(tx + 0.5f, ty + 0.5f),
                                   count: 120, scale: StormRadius * 0.4f);
                    break;
                }
                case ClickTool.RaiseLand:
                case ClickTool.LowerLand:
                {
                    float delta = Tool == ClickTool.RaiseLand ? 0.12f : -0.12f;
                    var r = World.ApplyElevationBrush(tx, ty, 4, delta);
                    Terrain.MarkDirty(r);      // only this rect reuploads
                    Particles.Emit(FxPreset.Splash, new float2(tx + 0.5f, ty + 0.5f), 12, 0.8f);
                    break;
                }
            }
        }

        public void SeedBatch(int count) => Life.SeedRandom(count, Config.SeedMinSpacing);

        public void TriggerWeather(Weather w) => World.SetWeather(w, 3f);

        private void OnDestroy()
        {
            World?.Dispose();
            Life?.Dispose();
            
        }
    }
}
