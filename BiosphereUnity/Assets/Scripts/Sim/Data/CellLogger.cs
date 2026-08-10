using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using Biosphere.Core;

namespace Biosphere.Sim
{
    /// <summary>
    /// Per-cell, not just population-average, longitudinal logging.
    ///
    /// Ported from data_logger.py's CellLogger with one important change for
    /// scale: at 576x576 with thousands of cells, snapshotting every cell every
    /// interval produces gigabytes. So there are two channels:
    ///
    ///   AGGREGATE  -- every tick: population, births/deaths, per-trait mean/std.
    ///                 Cheap, always on, feeds the live charts.
    ///   PER-CELL   -- every SampleInterval ticks, and optionally only a random
    ///                 subsample of the population. Feeds offline analysis of
    ///                 lineage survival and regional strategy divergence.
    ///
    /// Rows are buffered in memory and flushed to CSV in batches, so logging
    /// never touches the disk during a frame.
    /// </summary>
    public sealed class CellLogger
    {
        public struct AggregateRow
        {
            public int Step;
            public int Day;
            public float Hour;
            public int Population;
            public int Births, Deaths, Seeded, Culled;
            public float AvgEnergy, AvgDurability;
            public float4 TraitMeanA;    // harvest, metabolism, repro, mutation
            public float TraitMeanE;     // durability loss
            public float4 TraitStdA;
            public float TraitStdE;
        }

        public struct CellRow
        {
            public int Step;
            public int Day;
            public int CellId, ParentId;
            public int X, Y;
            public float Energy, Durability, AgeHours;
            public Genome Genes;
            public float LocalSunlight;   // so regional strategy can be correlated
            public byte Terrain;
        }

        public int SampleIntervalSteps = 60;
        [Range(0f, 1f)] public float PerCellSampleFraction = 1f;
        public int MaxBufferedCellRows = 500_000;

        private readonly List<AggregateRow> _aggregate = new();
        private readonly List<CellRow> _cells = new();
        private Unity.Mathematics.Random _rng = new(0xABCDu);
        private int _sinceLastSample;

        public int AggregateCount => _aggregate.Count;
        public int CellRowCount => _cells.Count;
        public IReadOnlyList<AggregateRow> Aggregate => _aggregate;

        /// <summary>Call once per simulation tick. Cheap on non-sample ticks.</summary>
        public void Record(WorldGrid world, LifeGrid life)
        {
            var agg = new AggregateRow
            {
                Step = life.StepCount,
                Day = world.DayCount,
                Hour = world.SimHour,
                Population = life.Population,
                Births = life.Births,
                Deaths = life.Deaths,
                Seeded = life.Seeded,
                Culled = life.Culled,
                AvgEnergy = life.AvgEnergy(),
                AvgDurability = life.AvgDurability()
            };

            var means = new float[GeneTable.Count];
            var stds = new float[GeneTable.Count];
            for (int g = 0; g < GeneTable.Count; g++)
                life.GenomeStats(g, out means[g], out stds[g], out _, out _);

            agg.TraitMeanA = new float4(means[0], means[1], means[2], means[3]);
            agg.TraitMeanE = means[4];
            agg.TraitStdA = new float4(stds[0], stds[1], stds[2], stds[3]);
            agg.TraitStdE = stds[4];
            _aggregate.Add(agg);

            if (++_sinceLastSample < SampleIntervalSteps) return;
            _sinceLastSample = 0;
            SampleCells(world, life);
        }

        private void SampleCells(WorldGrid world, LifeGrid life)
        {
            if (_cells.Count >= MaxBufferedCellRows) return;

            for (int i = 0; i < life.Count; i++)
            {
                if (PerCellSampleFraction < 1f && _rng.NextFloat() > PerCellSampleFraction)
                    continue;

                int2 p = life.Pos[i];
                int tile = p.y * world.W + p.x;
                _cells.Add(new CellRow
                {
                    Step = life.StepCount,
                    Day = world.DayCount,
                    CellId = life.CellId[i],
                    ParentId = life.ParentId[i],
                    X = p.x, Y = p.y,
                    Energy = life.Energy[i],
                    Durability = life.Durability[i],
                    AgeHours = life.Age[i],
                    Genes = life.Genes[i],
                    LocalSunlight = world.LocalSunlight(tile),
                    Terrain = (byte)world.Terrain[tile]
                });
                if (_cells.Count >= MaxBufferedCellRows) return;
            }
        }

        // ---------------- Export ----------------
        public void WriteCellCsv(string path)
        {
            var sb = new StringBuilder(_cells.Count * 96);
            sb.Append("step,day,cell_id,parent_id,x,y,energy,durability,age_hours,local_sunlight,terrain");
            for (int g = 0; g < GeneTable.Count; g++) sb.Append(',').Append(GeneTable.Names[g]);
            sb.Append('\n');

            var inv = CultureInfo.InvariantCulture;
            foreach (var r in _cells)
            {
                sb.Append(r.Step).Append(',').Append(r.Day).Append(',')
                  .Append(r.CellId).Append(',').Append(r.ParentId).Append(',')
                  .Append(r.X).Append(',').Append(r.Y).Append(',')
                  .Append(r.Energy.ToString("0.#####", inv)).Append(',')
                  .Append(r.Durability.ToString("0.#####", inv)).Append(',')
                  .Append(r.AgeHours.ToString("0.###", inv)).Append(',')
                  .Append(r.LocalSunlight.ToString("0.####", inv)).Append(',')
                  .Append(r.Terrain);
                for (int g = 0; g < GeneTable.Count; g++)
                    sb.Append(',').Append(r.Genes[g].ToString("0.######", inv));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }

        public void WriteAggregateCsv(string path)
        {
            var sb = new StringBuilder(_aggregate.Count * 128);
            sb.Append("step,day,hour,population,births,deaths,seeded,culled,avg_energy,avg_durability");
            for (int g = 0; g < GeneTable.Count; g++)
                sb.Append(",mean_").Append(GeneTable.Names[g]).Append(",std_").Append(GeneTable.Names[g]);
            sb.Append('\n');

            var inv = CultureInfo.InvariantCulture;
            foreach (var r in _aggregate)
            {
                sb.Append(r.Step).Append(',').Append(r.Day).Append(',')
                  .Append(r.Hour.ToString("0.##", inv)).Append(',')
                  .Append(r.Population).Append(',').Append(r.Births).Append(',')
                  .Append(r.Deaths).Append(',').Append(r.Seeded).Append(',').Append(r.Culled).Append(',')
                  .Append(r.AvgEnergy.ToString("0.#####", inv)).Append(',')
                  .Append(r.AvgDurability.ToString("0.#####", inv));

                float[] m = { r.TraitMeanA.x, r.TraitMeanA.y, r.TraitMeanA.z, r.TraitMeanA.w, r.TraitMeanE };
                float[] s = { r.TraitStdA.x, r.TraitStdA.y, r.TraitStdA.z, r.TraitStdA.w, r.TraitStdE };
                for (int g = 0; g < GeneTable.Count; g++)
                    sb.Append(',').Append(m[g].ToString("0.######", inv))
                      .Append(',').Append(s[g].ToString("0.######", inv));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }

        public void Clear() { _aggregate.Clear(); _cells.Clear(); }
    }
}
