using UnityEngine;

namespace Biosphere.Core
{
    /// <summary>
    /// Single source of truth for world scale and simulation constants.
    /// Ported 1:1 from the Python prototype (environment.py / life.py) so the
    /// two implementations can be cross-validated against each other.
    ///
    /// This is a ScriptableObject rather than a static class specifically so
    /// world size is data, not code: the renderer, sim, and camera all read
    /// GridW/GridH from here, which is what makes 256x256 -> 576x576 a single
    /// inspector change instead of a refactor.
    /// </summary>
    [CreateAssetMenu(menuName = "Biosphere/World Config", fileName = "WorldConfig")]
    public class WorldConfig : ScriptableObject
    {
        [Header("Grid")]
        [Tooltip("Tiles across. Tested targets: 256, 384, 512, 576.")]
        public int GridW = 256;
        public int GridH = 256;

        [Tooltip("Terrain texels per tile. Keep at 1: the terrain is a texture " +
                 "where one texel IS one tile. Detail comes from sprite layers on top.")]
        public int TerrainTexelsPerTile = 1;

        [Tooltip("Screen pixels per world unit at zoom 1. One tile = one world unit.")]
        public int PixelsPerUnit = 16;

        [Header("Terrain thresholds")]
        public float WaterLevel = 0.34f;
        public float MountainLevel = 0.80f;
        public float BeachBand = 0.05f;

        [Header("Terrain generation")]
        public float ElevationBlurSigma = 5.0f;
        public int ElevationBlurIterations = 4;
        public float FertilityThreshold = 0.76f;
        public float RockinessThreshold = 0.68f;

        [Header("Time")]
        [Tooltip("Simulated seconds per real second at 1x speed. 60 => a full " +
                 "24h day passes in 24 real minutes.")]
        public float SimSecondsPerRealSecond = 60f;

        [Header("Life")]
        public float MaxEnergy = 1.0f;
        public float ReproEnergyCost = 0.45f;
        public float ChildStartEnergy = 0.25f;
        public float InitDurability = 1.0f;
        public int DefaultSeedCount = 8;
        public int SeedMinSpacing = 4;

        [Header("Budgets")]
        [Tooltip("Hard cap on simultaneously live particles. Memory is " +
                 "preallocated to this size at startup and never grows.")]
        public int MaxParticles = 32768;

        [Tooltip("Hard cap on instanced sprites submitted per layer per frame.")]
        public int MaxSpritesPerLayer = 65536;

        public int TileCount => GridW * GridH;

        /// <summary>World-space rect of the map, origin at bottom-left tile corner.</summary>
        public Rect WorldBounds => new Rect(0f, 0f, GridW, GridH);

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < GridW && y < GridH;

        public int Index(int x, int y) => y * GridW + x;
    }

    /// <summary>
    /// Render layer ordering. Everything in the game draws through the
    /// instanced sprite batcher, so "sorting layer" is really "which depth
    /// slice does this instance write to". Values are the *base* depth; a
    /// per-instance Y-sort offset is added inside the layer band.
    ///
    /// Bands are spaced 1000 apart so a 576-tall map's Y-sort (0..575) can
    /// never bleed into the neighbouring band.
    /// </summary>
    public static class RenderLayer
    {
        public const int TerrainBase   = 0;      // the terrain texture quad
        public const int TerrainDecal  = 1000;   // roads, farmland, scorch marks, borders
        public const int GroundDecor   = 2000;   // rocks, bushes, floor items -- Y-sorted
        public const int Structures    = 3000;   // buildings, walls, bridges -- Y-sorted
        public const int Actors        = 4000;   // living cells / creatures  -- Y-sorted
        public const int Airborne      = 5000;   // birds, thrown objects, projectiles
        public const int Particles     = 6000;   // fire, explosions, magic
        public const int WeatherFx     = 7000;   // rain streaks, cloud shadow overlay
        public const int UiWorldSpace  = 8000;   // selection ring, tooltips anchored in world

        public const int BandSize = 1000;
    }
}
