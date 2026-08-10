using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Biosphere.Sim
{
    /// <summary>
    /// The one genuinely hot per-entity loop: energy income from local sunlight
    /// minus metabolic cost, plus ageing. Burst-compiled and parallel over
    /// entities. Everything else in the step (death compaction, reproduction)
    /// is order-dependent and stays serial on the main thread.
    ///
    /// Note this reads CloudShadow per entity by tile index -- that is the
    /// mechanism by which shaded map regions exert real, spatially-varying
    /// selection pressure without anything being hand-tuned.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct MetabolismJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int2>   Pos;
        [ReadOnly] public NativeArray<Genome> Genes;
        [ReadOnly] public NativeArray<float>  CloudShadow;

        public NativeArray<float> Energy;
        public NativeArray<float> Age;

        public float Sun;
        public float DtHours;
        public float MaxEnergy;
        public int   GridW;

        public void Execute(int i)
        {
            int2 p = Pos[i];
            int tile = p.y * GridW + p.x;

            float localLight = Sun * (1f - CloudShadow[tile] * 0.6f);
            Genome g = Genes[i];

            float gain = g.HarvestRate * DtHours * localLight;
            float cost = g.MetabolismRate * DtHours;

            Energy[i] = math.clamp(Energy[i] + gain - cost, 0f, MaxEnergy);
            Age[i] = Age[i] + DtHours;
        }
    }

    /// <summary>
    /// Builds the GPU instance buffer for living cells. Runs parallel over
    /// entities and writes straight into the array that gets uploaded to a
    /// ComputeBuffer -- no managed intermediate, no per-entity GameObject,
    /// no Transform hierarchy. This is what keeps thousands of actors at one
    /// draw call.
    /// </summary>
    [BurstCompile]
    public struct BuildCellInstancesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int2>   Pos;
        [ReadOnly] public NativeArray<float>  Energy;
        [ReadOnly] public NativeArray<float>  Durability;
        [ReadOnly] public NativeArray<Genome> Genes;

        [WriteOnly] public NativeArray<SpriteInstance> Out;

        public int   ColorGene;      // -1 = colour by energy, else gene index
        public float GeneMin, GeneSpan;
        public float MaxEnergy;
        public float SpriteSize;     // world units
        public float LayerDepth;     // RenderLayer band base
        public int   GridH;
        public int   AtlasTile;      // which cell of the sprite atlas to sample

        public void Execute(int i)
        {
            int2 p = Pos[i];

            float t = ColorGene < 0
                ? math.saturate(Energy[i] / MaxEnergy)
                : math.saturate((Genes[i][ColorGene] - GeneMin) / math.max(1e-8f, GeneSpan));

            // Magenta -> hot pink. Chosen because nothing in the terrain palette
            // (greens/blues/greys/tan) is near this hue, so cells never blend in.
            float3 lo = new float3(0.75f, 0.05f, 0.55f);
            float3 hi = new float3(1.00f, 0.55f, 0.90f);
            float3 col = lo + (hi - lo) * t;

            // Low durability blends toward warning red -- readable at a glance
            // without clicking each cell.
            float warn = math.saturate((0.35f - Durability[i]) / 0.35f) * 0.65f;
            col = col * (1f - warn) + new float3(1.0f, 0.15f, 0.05f) * warn;

            Out[i] = new SpriteInstance
            {
                PosSize = new float4(p.x + 0.5f, p.y + 0.5f, SpriteSize, SpriteSize),
                Color = new float4(col.x, col.y, col.z, 1f),
                // Depth: lower on screen (smaller Y) must draw in FRONT, so
                // depth decreases with Y within the layer band.
                AtlasDepth = new float2(AtlasTile, LayerDepth + (GridH - p.y))
            };
        }
    }

    /// <summary>
    /// One instanced quad. 40 bytes, matches the StructuredBuffer layout in
    /// PixelSpriteInstanced.shader exactly -- if you change one, change both.
    /// </summary>
    public struct SpriteInstance
    {
        public float4 PosSize;      // xy = world centre, zw = width/height in world units
        public float4 Color;        // rgba tint, multiplied against the atlas texel
        public float2 AtlasDepth;   // x = atlas tile index, y = sort depth
    }
}
