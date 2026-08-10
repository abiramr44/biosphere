using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Biosphere.Core;
using Biosphere.Sim;
using Debug = UnityEngine.Debug;

namespace Biosphere.EditorTools
{
    /// <summary>
    /// Runs the simulation with no rendering, no scene and no Play mode, then
    /// prints trait drift. This is the verification step: it answers "did the
    /// C# port reproduce the evolution the Python prototype produced?" without
    /// needing the graphics pipeline to work first.
    ///
    /// The reference numbers to compare against are in PROJECT_STATUS.md
    /// ("Verified findings"): on the 64x48 / 8-cell configuration over ~45 days,
    /// Python saw harvest_rate +55..113%, metabolism_rate -35..40%, and a
    /// population that saturates every habitable tile in ~35-45 days and then
    /// freezes. If this port is faithful, the SIGNS and rough magnitudes should
    /// match. Exact values will not -- different RNG, different draw order.
    ///
    /// Menu: Biosphere -> 2. Run Headless Evolution Test
    /// </summary>
    public static class HeadlessEvolutionTest
    {
        [MenuItem("Biosphere/2. Run Headless Evolution Test (64x48, 60 days)", priority = 1)]
        public static void RunSmall() => Run(64, 48, 8, 60);

        [MenuItem("Biosphere/3. Run Headless Perf Probe (256x256, 20 days)", priority = 2)]
        public static void RunPerf() => Run(256, 256, 40, 20);

        private static void Run(int w, int h, int seedCount, int days)
        {
            var cfg = ScriptableObject.CreateInstance<WorldConfig>();
            cfg.GridW = w;
            cfg.GridH = h;
            cfg.DefaultSeedCount = seedCount;

            uint seed = (uint)Random.Range(1, int.MaxValue);
            var world = new WorldGrid(cfg, seed);
            var life = new LifeGrid(cfg, world, seed ^ 0x5EEDu);

            // One step = 1 sim-minute. realDt=1s x SimSecondsPerRealSecond=60.
            const float realDt = 1f;
            int stepsPerDay = 24 * 60;
            int totalSteps = days * stepsPerDay;

            var start = Snapshot(life);
            int startPop = life.Population;
            int habitable = CountHabitable(world);

            var sw = Stopwatch.StartNew();
            int saturationDay = -1;
            int lastPop = 0, frozenSince = -1;

            for (int s = 0; s < totalSteps; s++)
            {
                world.Step(realDt, 1f);
                life.Step(realDt, 1f);

                if (life.Population == 0)
                {
                    Debug.LogWarning($"[Biosphere] EXTINCTION at day {s / stepsPerDay}. " +
                                     "That is a result, not a bug -- note the seed: " + seed);
                    break;
                }

                if (s % stepsPerDay == 0)
                {
                    int day = s / stepsPerDay;
                    if (saturationDay < 0 && life.Population >= habitable) saturationDay = day;
                    if (life.Population == lastPop) { if (frozenSince < 0) frozenSince = day; }
                    else frozenSince = -1;
                    lastPop = life.Population;
                }
            }
            sw.Stop();

            var end = Snapshot(life);
            var sb = new StringBuilder();
            sb.AppendLine($"=== Biosphere headless evolution test ===");
            sb.AppendLine($"world {w}x{h}  habitable {habitable}  seed {seed}");
            sb.AppendLine($"{days} sim-days in {sw.ElapsedMilliseconds} ms " +
                          $"({totalSteps} steps, {totalSteps / Mathf.Max(1f, sw.ElapsedMilliseconds / 1000f):0} steps/sec)");
            sb.AppendLine($"population {startPop} -> {life.Population}   " +
                          $"births {life.Births}  deaths {life.Deaths}");
            if (saturationDay >= 0)
                sb.AppendLine($"SATURATED every habitable tile on day {saturationDay}");
            if (frozenSince >= 0)
                sb.AppendLine($"population static since day {frozenSince} " +
                              "(the known saturation freeze -- see PROJECT_STATUS.md)");
            sb.AppendLine();
            sb.AppendLine($"{"trait",-18}{"start",10}{"end",10}{"change",10}");

            for (int g = 0; g < GeneTable.Count; g++)
            {
                float pct = start[g] == 0f ? 0f : (end[g] - start[g]) / start[g] * 100f;
                sb.AppendLine($"{GeneTable.Names[g],-18}{start[g],10:0.00000}{end[g],10:0.00000}{pct,9:+0.0;-0.0}%");
            }

            sb.AppendLine();
            sb.AppendLine("Expected signs (from the Python prototype): harvest_rate UP, " +
                          "metabolism_rate DOWN. Those two are the load-bearing check.");

            Debug.Log(sb.ToString());

            life.Dispose();
            world.Dispose();
            Object.DestroyImmediate(cfg);
        }

        private static float[] Snapshot(LifeGrid life)
        {
            var means = new float[GeneTable.Count];
            for (int g = 0; g < GeneTable.Count; g++)
                life.GenomeStats(g, out means[g], out _, out _, out _);
            return means;
        }

        private static int CountHabitable(WorldGrid w)
        {
            int n = 0;
            for (int i = 0; i < w.N; i++) if (w.Habitable[i]) n++;
            return n;
        }
    }
}
