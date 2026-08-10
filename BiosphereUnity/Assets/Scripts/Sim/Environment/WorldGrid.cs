using System;
using Unity.Collections;
using Unity.Mathematics;
using Biosphere.Core;
using Biosphere.Util;

namespace Biosphere.Sim
{
    public enum TerrainKind : byte { DeepWater = 0, Water = 1, Beach = 2, Land = 3, Mountain = 4 }
    public enum DecorKind   : byte { None = 0, Tree = 1, Rock = 2 }
    public enum Weather     : byte { Clear = 0, Cloudy = 1, Rainy = 2 }

    /// <summary>
    /// The world state: terrain, decoration, moisture, clouds, clock, weather.
    /// Port of environment.py's Biosphere class.
    ///
    /// Everything is a flat NativeArray indexed y * GridW + x -- struct-of-arrays,
    /// Burst-friendly, and directly addressable by jobs. Nothing here is a
    /// GameObject; a 576x576 world is ~15 float arrays, not 331k scene objects.
    /// </summary>
    public sealed class WorldGrid : IDisposable
    {
        public readonly WorldConfig Cfg;
        public readonly int W, H, N;

        // --- Static per-tile fields (generated once per world) ---
        public NativeArray<float>       Elevation;
        public NativeArray<TerrainKind> Terrain;
        public NativeArray<DecorKind>   Decor;
        public NativeArray<float>       Dither;         // fixed per-pixel grain
        public NativeArray<float>       TerrainDetail;  // broad soft mottling

        // --- Dynamic per-tile fields ---
        public NativeArray<float> Moisture;
        public NativeArray<float> CloudShadow;   // current cloud density [0,1] per tile

        // Cloud source field is wider than the map so it can scroll seamlessly.
        private NativeArray<float> _cloudField;
        private int _cloudFieldW;
        private float _cloudOffset;
        private float _cloudDensityMix = 0.05f;

        // --- Clock ---
        public double SimSeconds = 6 * 3600;   // start at 06:00
        public int DayCount = 1;
        public const double SecondsPerDay = 24 * 3600;

        // --- Weather ---
        public Weather CurrentWeather = Weather.Clear;
        private float _weatherTimer;
        private float _weatherChangeAt;
        private Unity.Mathematics.Random _rng;

        /// <summary>Habitable = land or beach. Cached because Life reads it every step.</summary>
        public NativeArray<bool> Habitable;

        /// <summary>Bumped whenever a tile's terrain/decor changed, so the renderer
        /// knows to repaint. Renderer compares against its own last-seen value.</summary>
        public int TerrainRevision { get; private set; }

        public WorldGrid(WorldConfig cfg, uint seed)
        {
            Cfg = cfg;
            W = cfg.GridW; H = cfg.GridH; N = W * H;
            _rng = new Unity.Mathematics.Random(seed == 0 ? 12345u : seed);

            Elevation     = new NativeArray<float>(N, Allocator.Persistent);
            Terrain       = new NativeArray<TerrainKind>(N, Allocator.Persistent);
            Decor         = new NativeArray<DecorKind>(N, Allocator.Persistent);
            Dither        = new NativeArray<float>(N, Allocator.Persistent);
            TerrainDetail = new NativeArray<float>(N, Allocator.Persistent);
            Moisture      = new NativeArray<float>(N, Allocator.Persistent);
            CloudShadow   = new NativeArray<float>(N, Allocator.Persistent);
            Habitable     = new NativeArray<bool>(N, Allocator.Persistent);

            GenerateTerrain();
            GenerateDecor();
            InitClouds();

            for (int i = 0; i < N; i++)
                Moisture[i] = Terrain[i] <= TerrainKind.Water ? 1f : 0.15f;

            FillNoiseInto(Dither, -1f, 1f);

            var raw = new NativeArray<float>(N, Allocator.TempJob);
            FillNoiseInto(raw, -1f, 1f);
            FieldMath.GaussianBlur(raw, W, H, 1.4f, 2);
            float maxAbs = 1e-8f;
            for (int i = 0; i < N; i++) maxAbs = math.max(maxAbs, math.abs(raw[i]));
            for (int i = 0; i < N; i++) TerrainDetail[i] = raw[i] / maxAbs;
            raw.Dispose();

            _weatherChangeAt = _rng.NextFloat(3f, 7f) * 3600f;
            TerrainRevision = 1;
        }

        private void FillNoiseInto(NativeArray<float> dst, float lo, float hi)
        {
            for (int i = 0; i < dst.Length; i++) dst[i] = _rng.NextFloat(lo, hi);
        }

        // ---------------- Terrain generation ----------------
        private void GenerateTerrain()
        {
            var field = new NativeArray<float>(N, Allocator.TempJob);
            FillNoiseInto(field, 0f, 1f);
            FieldMath.GaussianBlur(field, W, H, Cfg.ElevationBlurSigma,
                                   Cfg.ElevationBlurIterations);
            FieldMath.RankNormalize(field, Allocator.Temp);
            field.CopyTo(Elevation);
            field.Dispose();

            ClassifyTerrain();
        }

        /// <summary>
        /// Recompute terrain kind + habitability from elevation. Cheap and
        /// vectorisable -- this is what a terraform brush calls after editing
        /// elevation, either over the whole map or (via ClassifyRegion) a rect.
        /// </summary>
        public void ClassifyTerrain() => ClassifyRegion(0, 0, W, H);

        public void ClassifyRegion(int x0, int y0, int x1, int y1)
        {
            x0 = math.max(0, x0); y0 = math.max(0, y0);
            x1 = math.min(W, x1); y1 = math.min(H, y1);
            float wl = Cfg.WaterLevel, ml = Cfg.MountainLevel, bb = Cfg.BeachBand;

            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * W + x;
                float e = Elevation[i];
                TerrainKind k;
                if (e < wl * 0.45f)          k = TerrainKind.DeepWater;
                else if (e < wl)             k = TerrainKind.Water;
                else if (e < wl + bb)        k = TerrainKind.Beach;
                else if (e > ml)             k = TerrainKind.Mountain;
                else                         k = TerrainKind.Land;
                Terrain[i] = k;
                Habitable[i] = k == TerrainKind.Land || k == TerrainKind.Beach;
            }
            TerrainRevision++;
        }

        /// <summary>
        /// Terraform brush entry point: raise/lower elevation in a radius and
        /// immediately reclassify only the affected rect. This is the "instant
        /// tile update" path -- no full-map rebuild, no texture reupload beyond
        /// the dirty rect (see TerrainRenderer.MarkDirty).
        /// </summary>
        public TileRect ApplyElevationBrush(int cx, int cy, int radius, float delta)
        {
            int x0 = math.max(0, cx - radius), x1 = math.min(W, cx + radius + 1);
            int y0 = math.max(0, cy - radius), y1 = math.min(H, cy + radius + 1);
            float r2 = radius * radius;

            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 > r2) continue;
                float falloff = 1f - math.sqrt(d2 / math.max(1e-5f, r2));
                int i = y * W + x;
                Elevation[i] = math.saturate(Elevation[i] + delta * falloff);
            }
            ClassifyRegion(x0, y0, x1, y1);
            return new TileRect(x0, y0, x1 - x0, y1 - y0);
        }

        private void GenerateDecor()
        {
            var fert = new NativeArray<float>(N, Allocator.TempJob);
            FillNoiseInto(fert, 0f, 1f);
            FieldMath.GaussianBlur(fert, W, H, 2.0f, 2);
            FieldMath.Normalize01(fert);

            var rock = new NativeArray<float>(N, Allocator.TempJob);
            FillNoiseInto(rock, 0f, 1f);
            FieldMath.GaussianBlur(rock, W, H, 1.5f, 2);
            FieldMath.Normalize01(rock);

            for (int i = 0; i < N; i++)
            {
                DecorKind d = DecorKind.None;
                if (Terrain[i] == TerrainKind.Land &&
                    fert[i] > Cfg.FertilityThreshold &&
                    Elevation[i] < Cfg.MountainLevel - 0.05f)
                    d = DecorKind.Tree;
                else if (Terrain[i] == TerrainKind.Mountain && rock[i] > Cfg.RockinessThreshold)
                    d = DecorKind.Rock;
                Decor[i] = d;
            }
            fert.Dispose();
            rock.Dispose();
        }

        private void InitClouds()
        {
            const int pad = 24;
            _cloudFieldW = W + pad;
            _cloudField = new NativeArray<float>(_cloudFieldW * H, Allocator.Persistent);
            for (int i = 0; i < _cloudField.Length; i++) _cloudField[i] = _rng.NextFloat();
            FieldMath.GaussianBlur(_cloudField, _cloudFieldW, H, 3.5f, 3);
            for (int i = 0; i < _cloudField.Length; i++)
                _cloudField[i] = math.saturate((_cloudField[i] - 0.4f) * 2f);
            _cloudOffset = 0f;
            ScrollClouds(0);
        }

        // ---------------- Time / astronomy ----------------
        public float SimHour => (float)((SimSeconds % SecondsPerDay) / 3600.0);

        public float SunlightIntensity()
        {
            float h = SimHour;
            if (h < 6f || h > 18f) return 0f;
            return math.saturate(math.sin(math.PI * (h - 6f) / 12f));
        }

        /// <summary>Returns (brightness, tintRGB) for the current hour -- the
        /// global day/night grade applied to the terrain texture.</summary>
        public void SkyTint(out float brightness, out float3 tint)
        {
            float h = SimHour;
            float3 night = new float3(0.08f, 0.10f, 0.22f);
            float3 dawn  = new float3(0.95f, 0.55f, 0.35f);
            float3 day   = new float3(1.00f, 1.00f, 0.98f);
            float3 dusk  = new float3(0.85f, 0.40f, 0.35f);

            if (h < 5f || h >= 20f)      { brightness = 0.18f; tint = night; }
            else if (h < 7f)             { float t = (h - 5f) / 2f; brightness = FieldMath.Lerp(0.18f, 1f, t); tint = FieldMath.Lerp(night, dawn, t); }
            else if (h < 9f)             { float t = (h - 7f) / 2f; brightness = 1f;                          tint = FieldMath.Lerp(dawn, day, t); }
            else if (h < 17f)            { brightness = 1f; tint = day; }
            else if (h < 19f)            { float t = (h - 17f) / 2f; brightness = FieldMath.Lerp(1f, 0.9f, t); tint = FieldMath.Lerp(day, dusk, t); }
            else                         { float t = (h - 19f);      brightness = FieldMath.Lerp(0.9f, 0.18f, t); tint = FieldMath.Lerp(dusk, night, t); }
        }

        // ---------------- Weather ----------------
        private void UpdateWeather(float simDt)
        {
            _weatherTimer += simDt;
            if (_weatherTimer >= _weatherChangeAt)
            {
                _weatherTimer = 0f;
                _weatherChangeAt = _rng.NextFloat(3f, 7f) * 3600f;
                CurrentWeather = RollWeather(CurrentWeather);
            }

            float target = CurrentWeather switch
            {
                Weather.Clear  => 0.05f,
                Weather.Cloudy => 0.55f,
                _              => 0.85f
            };
            _cloudDensityMix += (target - _cloudDensityMix) * 0.02f;

            _cloudOffset += simDt * 0.0015f;
            ScrollClouds((int)_cloudOffset % _cloudFieldW);
        }

        private Weather RollWeather(Weather from)
        {
            float3 p = from switch
            {
                Weather.Clear  => new float3(0.50f, 0.35f, 0.15f),
                Weather.Cloudy => new float3(0.35f, 0.40f, 0.25f),
                _              => new float3(0.30f, 0.40f, 0.30f)
            };
            float r = _rng.NextFloat();
            if (r < p.x) return Weather.Clear;
            if (r < p.x + p.y) return Weather.Cloudy;
            return Weather.Rainy;
        }

        private void ScrollClouds(int shift)
        {
            for (int y = 0; y < H; y++)
            {
                int rowSrc = y * _cloudFieldW;
                int rowDst = y * W;
                for (int x = 0; x < W; x++)
                {
                    int sx = (x + shift) % _cloudFieldW;
                    CloudShadow[rowDst + x] = _cloudField[rowSrc + sx] * _cloudDensityMix;
                }
            }
        }

        /// <summary>Manual weather override (GUI nature tool). Holds for
        /// holdHours of sim-time before the random cycle resumes.</summary>
        public void SetWeather(Weather w, float holdHours = 3f)
        {
            CurrentWeather = w;
            _weatherTimer = 0f;
            _weatherChangeAt = holdHours * 3600f;
        }

        // ---------------- Moisture ----------------
        private void UpdateMoisture(float simDt)
        {
            float sun = SunlightIntensity();
            float evap = 0.000006f * sun * simDt;
            float rain = CurrentWeather == Weather.Rainy ? 0.00004f * simDt : 0f;
            for (int i = 0; i < N; i++)
            {
                float m = Moisture[i] - evap + rain;
                Moisture[i] = Terrain[i] <= TerrainKind.Water ? 1f : math.saturate(m);
            }
        }

        // ---------------- Step ----------------
        public void Step(float realDt, float speedMultiplier)
        {
            float simDt = realDt * Cfg.SimSecondsPerRealSecond * speedMultiplier;
            SimSeconds += simDt;
            while (SimSeconds >= SecondsPerDay) { SimSeconds -= SecondsPerDay; DayCount++; }
            UpdateWeather(simDt);
            UpdateMoisture(simDt);
        }

        /// <summary>Per-tile effective sunlight, factoring in hour and cloud shadow.
        /// This is what drives the selection pressure Life feels -- a shaded
        /// valley genuinely receives less energy than an open plain.</summary>
        public float LocalSunlight(int i) => SunlightIntensity() * (1f - CloudShadow[i] * 0.6f);

        public string StatusString()
        {
            int hh = (int)SimHour;
            int mm = (int)((SimHour - hh) * 60f);
            return $"Day {DayCount}  |  {hh:00}:{mm:00}  |  {CurrentWeather}  |  sun {SunlightIntensity():0.00}";
        }

        public void Dispose()
        {
            if (Elevation.IsCreated)     Elevation.Dispose();
            if (Terrain.IsCreated)       Terrain.Dispose();
            if (Decor.IsCreated)         Decor.Dispose();
            if (Dither.IsCreated)        Dither.Dispose();
            if (TerrainDetail.IsCreated) TerrainDetail.Dispose();
            if (Moisture.IsCreated)      Moisture.Dispose();
            if (CloudShadow.IsCreated)   CloudShadow.Dispose();
            if (Habitable.IsCreated)     Habitable.Dispose();
            if (_cloudField.IsCreated)   _cloudField.Dispose();
        }
    }

    public struct TileRect
    {
        public int X, Y, W, H;
        public TileRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int XMax => X + W;
        public int YMax => Y + H;
        public bool IsEmpty => W <= 0 || H <= 0;

        public static TileRect Union(TileRect a, TileRect b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            int x0 = math.min(a.X, b.X), y0 = math.min(a.Y, b.Y);
            int x1 = math.max(a.XMax, b.XMax), y1 = math.max(a.YMax, b.YMax);
            return new TileRect(x0, y0, x1 - x0, y1 - y0);
        }
    }
}
