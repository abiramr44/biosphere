using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Biosphere.Core;

namespace Biosphere.Sim
{
    /// <summary>
    /// The population. Port of life.py's Life class, with one deliberate
    /// architectural change:
    ///
    /// Python stored every trait as a dense (H, W) array and used boolean masks.
    /// That is fine at 64x48 but wasteful at 576x576 -- 331k slots iterated
    /// every step to touch maybe 4k live cells, times 5 genes.
    ///
    /// Here it is a HYBRID: a dense int occupancy grid (Occupancy[i] = entity
    /// index or -1) for O(1) spatial queries, plus tightly packed struct-of-array
    /// entity lists so the step loop only ever touches live entities. Death is a
    /// swap-remove. This is the layout that makes "thousands of entities" cheap.
    /// </summary>
    public sealed class LifeGrid : IDisposable
    {
        public const int Empty = -1;

        private readonly WorldConfig _cfg;
        private readonly WorldGrid _world;
        private Unity.Mathematics.Random _rng;

        public NativeArray<int> Occupancy;          // length N, entity index or Empty

        // --- Entity struct-of-arrays. Index range [0, Count). ---
        public NativeList<int2>   Pos;
        public NativeList<float>  Energy;
        public NativeList<float>  Durability;
        public NativeList<float>  Age;             // sim-hours alive
        public NativeList<Genome> Genes;
        public NativeList<int>    CellId;
        public NativeList<int>    ParentId;
        public NativeList<int>    BirthStep;

        // Scratch reused every step so the hot loop never allocates.
        private NativeList<int> _dying;
        private NativeList<int> _ready;

        /// <summary>Entities born this step (entity indices), for the birth-flash VFX.</summary>
        public NativeList<int> NewbornsThisStep;

        private int _nextId = 1;
        public int StepCount { get; private set; }
        public int Births, Deaths, Seeded, Culled;
        public float HarvestedThisStep;

        public int Count => Pos.Length;
        public int Population => Pos.Length;

        public LifeGrid(WorldConfig cfg, WorldGrid world, uint seed, int capacity = 1 << 16)
        {
            _cfg = cfg; _world = world;
            _rng = new Unity.Mathematics.Random(seed == 0 ? 999u : seed);

            Occupancy = new NativeArray<int>(world.N, Allocator.Persistent);
            for (int i = 0; i < Occupancy.Length; i++) Occupancy[i] = Empty;

            Pos        = new NativeList<int2>(capacity, Allocator.Persistent);
            Energy     = new NativeList<float>(capacity, Allocator.Persistent);
            Durability = new NativeList<float>(capacity, Allocator.Persistent);
            Age        = new NativeList<float>(capacity, Allocator.Persistent);
            Genes      = new NativeList<Genome>(capacity, Allocator.Persistent);
            CellId     = new NativeList<int>(capacity, Allocator.Persistent);
            ParentId   = new NativeList<int>(capacity, Allocator.Persistent);
            BirthStep  = new NativeList<int>(capacity, Allocator.Persistent);

            _dying           = new NativeList<int>(1024, Allocator.Persistent);
            _ready           = new NativeList<int>(1024, Allocator.Persistent);
            NewbornsThisStep = new NativeList<int>(1024, Allocator.Persistent);

            SeedRandom(cfg.DefaultSeedCount, cfg.SeedMinSpacing);
        }

        // ---------------- Spawning ----------------
        private int SpawnAt(int x, int y, in Genome g, int parentId)
        {
            int tile = y * _world.W + x;
            int idx = Pos.Length;

            Pos.Add(new int2(x, y));
            Energy.Add(_cfg.ChildStartEnergy);
            Durability.Add(_cfg.InitDurability);
            Age.Add(0f);
            Genes.Add(g);
            CellId.Add(_nextId++);
            ParentId.Add(parentId);
            BirthStep.Add(StepCount);

            Occupancy[tile] = idx;
            return idx;
        }

        /// <summary>Swap-remove. The entity that was last takes this slot, so
        /// its occupancy pointer has to be rewritten. O(1) death.</summary>
        private void RemoveAt(int idx)
        {
            int2 p = Pos[idx];
            Occupancy[p.y * _world.W + p.x] = Empty;

            int last = Pos.Length - 1;
            if (idx != last)
            {
                Pos[idx] = Pos[last];
                Energy[idx] = Energy[last];
                Durability[idx] = Durability[last];
                Age[idx] = Age[last];
                Genes[idx] = Genes[last];
                CellId[idx] = CellId[last];
                ParentId[idx] = ParentId[last];
                BirthStep[idx] = BirthStep[last];

                int2 moved = Pos[idx];
                Occupancy[moved.y * _world.W + moved.x] = idx;
            }

            Pos.RemoveAt(last);
            Energy.RemoveAt(last);
            Durability.RemoveAt(last);
            Age.RemoveAt(last);
            Genes.RemoveAt(last);
            CellId.RemoveAt(last);
            ParentId.RemoveAt(last);
            BirthStep.RemoveAt(last);
        }

        /// <summary>
        /// Place `count` cells with a minimum spacing, so a small starting
        /// population reads as individually placed beings rather than a clump.
        /// Greedy rejection sampling with a bounded attempt budget -- unlike the
        /// Python version this does not permute the entire habitable tile list,
        /// which matters when that list is 300k entries long.
        /// </summary>
        public int SeedRandom(int count, int minSpacing)
        {
            int placed = 0;
            int attempts = 0;
            int maxAttempts = count * 400;
            var chosen = new NativeList<int2>(count, Allocator.Temp);
            float minSq = minSpacing * minSpacing;

            while (placed < count && attempts++ < maxAttempts)
            {
                int x = _rng.NextInt(0, _world.W);
                int y = _rng.NextInt(0, _world.H);
                int tile = y * _world.W + x;
                if (!_world.Habitable[tile] || Occupancy[tile] != Empty) continue;

                bool tooClose = false;
                for (int c = 0; c < chosen.Length; c++)
                {
                    int2 d = chosen[c] - new int2(x, y);
                    if (d.x * d.x + d.y * d.y < minSq) { tooClose = true; break; }
                }
                if (tooClose) continue;

                SpawnAt(x, y, GeneTable.SampleInitial(ref _rng), 0);
                chosen.Add(new int2(x, y));
                placed++;
            }
            chosen.Dispose();
            Seeded += placed;
            return placed;
        }

        public bool SeedAt(int x, int y)
        {
            if (!_cfg.InBounds(x, y)) return false;
            int tile = y * _world.W + x;
            if (!_world.Habitable[tile] || Occupancy[tile] != Empty) return false;
            SpawnAt(x, y, GeneTable.SampleInitial(ref _rng), 0);
            Seeded++;
            return true;
        }

        public bool KillAt(int x, int y)
        {
            if (!_cfg.InBounds(x, y)) return false;
            int idx = Occupancy[y * _world.W + x];
            if (idx == Empty) return false;
            RemoveAt(idx);
            Culled++;
            return true;
        }

        /// <summary>Localised disaster. Each live cell in radius has killProb of
        /// dying. Iterates the affected tile rect, not the whole map.</summary>
        public int StrikeArea(int cx, int cy, int radius, float killProb)
        {
            int x0 = math.max(0, cx - radius), x1 = math.min(_world.W, cx + radius + 1);
            int y0 = math.max(0, cy - radius), y1 = math.min(_world.H, cy + radius + 1);
            float r2 = radius * radius;

            var hits = new NativeList<int>(64, Allocator.Temp);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 > r2) continue;
                int idx = Occupancy[y * _world.W + x];
                if (idx == Empty) continue;
                if (_rng.NextFloat() < killProb) hits.Add(idx);
            }

            // Descending order so swap-remove never invalidates a pending index.
            var arr = hits.AsArray();
            arr.Sort();
            for (int i = arr.Length - 1; i >= 0; i--) RemoveAt(arr[i]);

            int n = arr.Length;
            hits.Dispose();
            Culled += n;
            return n;
        }

        // ---------------- Simulation step ----------------
        public void Step(float realDt, float speedMultiplier)
        {
            float dtHours = (realDt * _cfg.SimSecondsPerRealSecond * speedMultiplier) / 3600f;
            StepCount++;
            NewbornsThisStep.Clear();
            if (Count == 0) return;

            float sun = _world.SunlightIntensity();

            // --- Phase 1: metabolism, parallel over entities (Burst) ---
            var metabolism = new MetabolismJob
            {
                Pos = Pos.AsArray(),
                Energy = Energy.AsArray(),
                Age = Age.AsArray(),
                Genes = Genes.AsArray(),
                CloudShadow = _world.CloudShadow,
                Sun = sun,
                DtHours = dtHours,
                MaxEnergy = _cfg.MaxEnergy,
                GridW = _world.W
            };
            metabolism.Schedule(Count, 128).Complete();

            // --- Phase 2: collect deaths, then compact (must be serial) ---
            _dying.Clear();
            for (int i = 0; i < Count; i++)
                if (Energy[i] <= 0f || Durability[i] <= 0f) _dying.Add(i);

            if (_dying.Length > 0)
            {
                var d = _dying.AsArray();
                d.Sort();
                for (int i = d.Length - 1; i >= 0; i--) RemoveAt(d[i]);
                Deaths += d.Length;
            }

            // --- Phase 3: reproduction (serial: mutates occupancy + spawns) ---
            _ready.Clear();
            for (int i = 0; i < Count; i++)
            {
                Genome g = Genes[i];
                if (Energy[i] >= g.ReproThreshold && Durability[i] > g.DurabilityLoss)
                    _ready.Add(i);
            }

            // Shuffle so no positional bias in who gets the last free neighbour tile.
            for (int i = _ready.Length - 1; i > 0; i--)
            {
                int j = _rng.NextInt(0, i + 1);
                (_ready[i], _ready[j]) = (_ready[j], _ready[i]);
            }

            for (int r = 0; r < _ready.Length; r++)
            {
                int parent = _ready[r];
                if (parent >= Count) continue;          // was swap-moved out of range
                int2 p = Pos[parent];
                int free = FindFreeNeighbour(p.x, p.y);
                if (free < 0) continue;

                int fx = free % _world.W, fy = free / _world.W;
                Genome childGenome = GeneTable.Mutate(Genes[parent], ref _rng);
                int childIdx = SpawnAt(fx, fy, childGenome, CellId[parent]);

                Energy[parent] -= _cfg.ReproEnergyCost;
                Durability[parent] -= Genes[parent].DurabilityLoss;
                Births++;
                NewbornsThisStep.Add(childIdx);
            }

            HarvestedThisStep = 0f;
        }

        private static readonly int2[] NeighbourOffsets =
        {
            new int2(-1,-1), new int2(0,-1), new int2(1,-1),
            new int2(-1, 0),                 new int2(1, 0),
            new int2(-1, 1), new int2(0, 1), new int2(1, 1)
        };

        private int FindFreeNeighbour(int x, int y)
        {
            int start = _rng.NextInt(0, 8);
            for (int k = 0; k < 8; k++)
            {
                int2 o = NeighbourOffsets[(start + k) & 7];
                int nx = x + o.x, ny = y + o.y;
                if (!_cfg.InBounds(nx, ny)) continue;
                int tile = ny * _world.W + nx;
                if (!_world.Habitable[tile] || Occupancy[tile] != Empty) continue;
                return tile;
            }
            return -1;
        }

        // ---------------- Queries ----------------
        public int EntityAtTile(int x, int y)
        {
            if (!_cfg.InBounds(x, y)) return Empty;
            return Occupancy[y * _world.W + x];
        }

        /// <summary>Find an entity by its stable CellId (lineage lookups).
        /// Linear scan; only called by UI, never per-frame per-entity.</summary>
        public int IndexOfCellId(int id)
        {
            if (id <= 0) return Empty;
            for (int i = 0; i < Count; i++) if (CellId[i] == id) return i;
            return Empty;
        }

        public void GenomeStats(int gene, out float mean, out float std, out float min, out float max)
        {
            mean = std = 0f; min = float.MaxValue; max = float.MinValue;
            if (Count == 0) { min = max = 0f; return; }
            double sum = 0, sumSq = 0;
            for (int i = 0; i < Count; i++)
            {
                float v = Genes[i][gene];
                sum += v; sumSq += (double)v * v;
                min = math.min(min, v); max = math.max(max, v);
            }
            mean = (float)(sum / Count);
            std = (float)math.sqrt(math.max(0.0, sumSq / Count - (double)mean * mean));
        }

        /// <summary>Histogram of one trait across the population, bucketed over
        /// that trait's bounds. Feeds the live distribution chart.</summary>
        public void GenomeHistogram(int gene, NativeArray<int> bins)
        {
            for (int b = 0; b < bins.Length; b++) bins[b] = 0;
            for (int i = 0; i < Count; i++)
            {
                float t = GeneTable.Normalize(gene, Genes[i][gene]);
                int b = math.clamp((int)(t * bins.Length), 0, bins.Length - 1);
                bins[b]++;
            }
        }

        public float AvgEnergy()
        {
            if (Count == 0) return 0f;
            double s = 0; for (int i = 0; i < Count; i++) s += Energy[i];
            return (float)(s / Count);
        }

        public float AvgDurability()
        {
            if (Count == 0) return 0f;
            double s = 0; for (int i = 0; i < Count; i++) s += Durability[i];
            return (float)(s / Count);
        }

        public void Dispose()
        {
            if (Occupancy.IsCreated) Occupancy.Dispose();
            if (Pos.IsCreated) Pos.Dispose();
            if (Energy.IsCreated) Energy.Dispose();
            if (Durability.IsCreated) Durability.Dispose();
            if (Age.IsCreated) Age.Dispose();
            if (Genes.IsCreated) Genes.Dispose();
            if (CellId.IsCreated) CellId.Dispose();
            if (ParentId.IsCreated) ParentId.Dispose();
            if (BirthStep.IsCreated) BirthStep.Dispose();
            if (_dying.IsCreated) _dying.Dispose();
            if (_ready.IsCreated) _ready.Dispose();
            if (NewbornsThisStep.IsCreated) NewbornsThisStep.Dispose();
        }
    }
}
