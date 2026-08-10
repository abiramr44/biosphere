using Unity.Mathematics;

namespace Biosphere.Sim
{
    /// <summary>
    /// The heritable trait set. Kept as a fixed 5-field struct rather than a
    /// dictionary-of-arrays (as in the Python prototype) because Burst needs a
    /// blittable, fixed-layout type -- and because a 20-byte struct per cell
    /// lets the whole genome be one NativeArray&lt;Genome&gt; with good cache
    /// locality during the step loop.
    ///
    /// To add a trait: add the field here, add its default/bound/init-std to
    /// GeneTable, bump GeneTable.Count, and extend the indexer switch. Every
    /// consumer (UI charts, colour overlay, CSV logger) drives off GeneTable,
    /// so nothing else needs touching.
    /// </summary>
    public struct Genome
    {
        public float HarvestRate;
        public float MetabolismRate;
        public float ReproThreshold;
        public float MutationRate;
        public float DurabilityLoss;

        public float this[int gene]
        {
            get => gene switch
            {
                0 => HarvestRate,
                1 => MetabolismRate,
                2 => ReproThreshold,
                3 => MutationRate,
                _ => DurabilityLoss
            };
            set
            {
                switch (gene)
                {
                    case 0: HarvestRate = value; break;
                    case 1: MetabolismRate = value; break;
                    case 2: ReproThreshold = value; break;
                    case 3: MutationRate = value; break;
                    default: DurabilityLoss = value; break;
                }
            }
        }
    }

    public static class GeneTable
    {
        public const int Count = 5;

        public const int HarvestRate    = 0;
        public const int MetabolismRate = 1;
        public const int ReproThreshold = 2;
        public const int MutationRate   = 3;
        public const int DurabilityLoss = 4;

        public static readonly string[] Names =
        {
            "harvest_rate", "metabolism_rate", "repro_threshold",
            "mutation_rate", "durability_loss"
        };

        public static readonly string[] DisplayNames =
        {
            "Harvest", "Metabolism", "Repro threshold", "Mutation rate", "Durability loss"
        };

        /// <summary>Population starting mean for each trait.</summary>
        public static readonly float[] Default = { 0.06f, 0.012f, 0.75f, 0.02f, 0.14f };

        /// <summary>Hard clamp per trait. Prevents mutation drifting into
        /// degenerate values (harvest 0, repro threshold above MaxEnergy, etc.).
        /// Mutation step size is also derived from this span, so widening a
        /// bound widens that trait's mutational reach proportionally.</summary>
        public static readonly float[] Min = { 0.01f, 0.002f, 0.30f, 0.001f, 0.02f };
        public static readonly float[] Max = { 0.20f, 0.060f, 0.95f, 0.150f, 0.50f };

        /// <summary>Spread of the *initial* population, so selection has
        /// variance to act on from generation zero.</summary>
        public static readonly float[] InitStd = { 0.015f, 0.003f, 0.08f, 0.01f, 0.05f };

        public static float Span(int g) => Max[g] - Min[g];

        /// <summary>Normalise a trait value to [0,1] against its bounds --
        /// used by the trait-distribution charts and the colour overlay so
        /// traits with wildly different scales are comparable.</summary>
        public static float Normalize(int g, float v) =>
            math.saturate((v - Min[g]) / math.max(1e-8f, Span(g)));

        public static Genome SampleInitial(ref Unity.Mathematics.Random rng)
        {
            Genome g = default;
            for (int i = 0; i < Count; i++)
                g[i] = math.clamp(NextGaussian(ref rng, Default[i], InitStd[i]), Min[i], Max[i]);
            return g;
        }

        /// <summary>
        /// Child = parent + Gaussian noise per trait, with step size scaled by
        /// the PARENT'S OWN MutationRate gene. Mutation rate is therefore itself
        /// heritable and under selection -- evolvable evolvability.
        /// </summary>
        public static Genome Mutate(in Genome parent, ref Unity.Mathematics.Random rng)
        {
            float rate = parent.MutationRate;
            Genome child = default;
            for (int i = 0; i < Count; i++)
            {
                float stepStd = rate * Span(i);
                child[i] = math.clamp(NextGaussian(ref rng, parent[i], stepStd), Min[i], Max[i]);
            }
            return child;
        }

        /// <summary>Box-Muller. Unity.Mathematics.Random has no normal sampler.</summary>
        public static float NextGaussian(ref Unity.Mathematics.Random rng, float mean, float std)
        {
            if (std <= 0f) return mean;
            float u1 = math.max(1e-7f, rng.NextFloat());
            float u2 = rng.NextFloat();
            float z = math.sqrt(-2f * math.log(u1)) * math.cos(2f * math.PI * u2);
            return mean + z * std;
        }
    }
}
