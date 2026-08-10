using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Biosphere.Util
{
    /// <summary>
    /// Numpy-equivalent field operations over flat NativeArray&lt;float&gt; grids.
    /// These are the direct ports of environment.py's _gaussian_blur and the
    /// rank-normalisation step in _generate_terrain.
    ///
    /// All of them are separable / O(n) so they stay cheap at 576x576 (331k
    /// tiles) -- worldgen at that size should land in the low tens of ms.
    /// </summary>
    public static class FieldMath
    {
        /// <summary>
        /// Separable Gaussian blur, zero-padded, matching the Python version's
        /// behaviour (kernel size = (sigma*3)|1, N iterations of H then V).
        /// Allocates one scratch buffer and ping-pongs; caller owns src.
        ///
        /// NOTE: `src` MUST be Allocator.TempJob or Allocator.Persistent.
        /// Allocator.Temp arrays cannot be handed to a scheduled job -- Unity's
        /// safety system rejects them. Scratch buffers here are TempJob for the
        /// same reason.
        /// </summary>
        public static void GaussianBlur(NativeArray<float> src, int w, int h,
                                        float sigma, int iterations)
        {
            int size = ((int)(sigma * 3f)) | 1;
            int pad = size / 2;

            var kernel = new NativeArray<float>(size, Allocator.TempJob);
            float sum = 0f;
            for (int i = 0; i < size; i++)
            {
                float ax = i - pad;
                float v = math.exp(-(ax * ax) / (2f * sigma * sigma));
                kernel[i] = v;
                sum += v;
            }
            for (int i = 0; i < size; i++) kernel[i] = kernel[i] / sum;

            var scratch = new NativeArray<float>(src.Length, Allocator.TempJob);

            for (int it = 0; it < iterations; it++)
            {
                new Blur1DJob { Src = src, Dst = scratch, K = kernel, W = w, H = h, Horizontal = true }
                    .Schedule(h, 4).Complete();
                new Blur1DJob { Src = scratch, Dst = src, K = kernel, W = w, H = h, Horizontal = false }
                    .Schedule(h, 4).Complete();
            }

            scratch.Dispose();
            kernel.Dispose();
        }

        [BurstCompile]
        private struct Blur1DJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            [NativeDisableParallelForRestriction] public NativeArray<float> Dst;
            [ReadOnly] public NativeArray<float> K;
            public int W, H;
            public bool Horizontal;

            public void Execute(int row)
            {
                int pad = K.Length / 2;
                if (Horizontal)
                {
                    for (int x = 0; x < W; x++)
                    {
                        float acc = 0f;
                        for (int k = 0; k < K.Length; k++)
                        {
                            int sx = x + k - pad;
                            if (sx < 0 || sx >= W) continue;   // zero padding
                            acc += Src[row * W + sx] * K[k];
                        }
                        Dst[row * W + x] = acc;
                    }
                }
                else
                {
                    for (int x = 0; x < W; x++)
                    {
                        float acc = 0f;
                        for (int k = 0; k < K.Length; k++)
                        {
                            int sy = row + k - pad;
                            if (sy < 0 || sy >= H) continue;
                            acc += Src[sy * W + x] * K[k];
                        }
                        Dst[row * W + x] = acc;
                    }
                }
            }
        }

        /// <summary>
        /// Rank-normalise (histogram equalisation) in place: replaces each value
        /// with its rank / (n-1), giving a uniform [0,1] distribution while
        /// preserving spatial shape. This is what makes WaterLevel = 0.34
        /// reliably produce ~34% water regardless of the blur's output skew.
        ///
        /// Implemented as an index sort (O(n log n)) -- ~30ms at 576x576, and it
        /// only runs once per world generation.
        /// </summary>
        public static void RankNormalize(NativeArray<float> field, Allocator alloc)
        {
            int n = field.Length;
            var idx = new NativeArray<int>(n, alloc);
            for (int i = 0; i < n; i++) idx[i] = i;

            var keys = new NativeArray<float>(n, alloc);
            field.CopyTo(keys);

            // Managed sort on a temp array; worldgen is not a hot path.
            var managedIdx = idx.ToArray();
            var managedKeys = keys.ToArray();
            System.Array.Sort(managedKeys, managedIdx);

            float inv = 1f / math.max(1, n - 1);
            for (int rank = 0; rank < n; rank++)
                field[managedIdx[rank]] = rank * inv;

            keys.Dispose();
            idx.Dispose();
        }

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public static float3 Lerp(float3 a, float3 b, float t) => a + (b - a) * t;

        /// <summary>Fill with uniform noise in [0,1) using a deterministic seed.</summary>
        public static void FillNoise(NativeArray<float> dst, uint seed)
        {
            var rng = new Random(seed == 0 ? 1u : seed);
            for (int i = 0; i < dst.Length; i++) dst[i] = rng.NextFloat();
        }

        public static void Normalize01(NativeArray<float> f)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < f.Length; i++) { lo = math.min(lo, f[i]); hi = math.max(hi, f[i]); }
            float span = math.max(1e-8f, hi - lo);
            for (int i = 0; i < f.Length; i++) f[i] = (f[i] - lo) / span;
        }
    }
}
