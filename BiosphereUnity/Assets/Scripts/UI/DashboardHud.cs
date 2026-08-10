using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Biosphere.Core;
using Biosphere.Sim;

namespace Biosphere.UI
{
    /// <summary>
    /// The WorldBox-style HUD: toolbar, cell inspector, and live trait charts.
    ///
    /// This is deliberately IMGUI (OnGUI). It is the fastest way to get a
    /// research dashboard running with zero prefab wiring, and it is trivially
    /// portable. It is NOT what you should ship -- IMGUI allocates per frame and
    /// re-lays-out every repaint. When the layout stabilises, port this to UI
    /// Toolkit (UIDocument + USS); the data-access calls below translate
    /// one-for-one.
    ///
    /// Charts are drawn straight into a small Texture2D rather than a plotting
    /// library, which keeps them on the pixel grid and matches the art style.
    /// </summary>
    public class DashboardHud : MonoBehaviour
    {
        public GameBootstrap Game;

        [Header("Layout")]
        public int PanelWidth = 300;
        public int ChartHeight = 90;
        public int HistoryLength = 512;
        public int HistogramBins = 24;

        [Header("Style")]
        public Color PanelBg = new Color(0.055f, 0.06f, 0.09f, 0.94f);
        public Color Accent = new Color(1f, 0.45f, 0.85f);
        public Color Grid = new Color(1f, 1f, 1f, 0.10f);

        private readonly List<float[]> _history = new();   // one ring per gene
        private readonly List<float> _popHistory = new();
        private int _historyCursor;
        private float _sampleTimer;

        private Texture2D _traitChart, _histChart, _px;
        private GUIStyle _label, _header, _panel;
        private NativeArray<int> _bins;

        private static readonly string[] SpeedLabels = { "x1", "x2", "x4", "x8", "x16", "x32", "x64" };

        private void Awake()
        {
            for (int g = 0; g < GeneTable.Count; g++) _history.Add(new float[HistoryLength]);
            _bins = new NativeArray<int>(HistogramBins, Allocator.Persistent);

            _traitChart = NewChartTex(PanelWidth - 20, ChartHeight);
            _histChart = NewChartTex(PanelWidth - 20, ChartHeight);
            _px = new Texture2D(1, 1) { filterMode = FilterMode.Point };
            _px.SetPixel(0, 0, Color.white);
            _px.Apply();
        }

        private static Texture2D NewChartTex(int w, int h) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

        private void Update()
        {
            if (Game?.Life == null || Game.Paused) return;

            // Sample the charts at a fixed rate, independent of sim speed, so
            // the x-axis stays readable when running at x64.
            _sampleTimer += Time.deltaTime;
            if (_sampleTimer < 0.25f) return;
            _sampleTimer = 0f;

            for (int g = 0; g < GeneTable.Count; g++)
            {
                Game.Life.GenomeStats(g, out float mean, out _, out _, out _);
                _history[g][_historyCursor] = GeneTable.Normalize(g, mean);
            }
            _popHistory.Add(Game.Life.Population);
            if (_popHistory.Count > HistoryLength) _popHistory.RemoveAt(0);

            _historyCursor = (_historyCursor + 1) % HistoryLength;
            RedrawTraitChart();
            RedrawHistogram();
        }

        // ---------------- Charts ----------------
        private void RedrawTraitChart()
        {
            var px = _traitChart.GetPixels32();
            Fill(px, new Color32(14, 16, 26, 255));
            DrawGrid(px, _traitChart.width, _traitChart.height, 4);

            for (int g = 0; g < GeneTable.Count; g++)
            {
                Color32 c = GeneColor(g);
                float[] h = _history[g];
                int prevY = -1;
                for (int x = 0; x < _traitChart.width; x++)
                {
                    int sample = (_historyCursor + 1 + (x * HistoryLength / _traitChart.width))
                                 % HistoryLength;
                    int y = (int)(math.saturate(h[sample]) * (_traitChart.height - 1));
                    if (prevY >= 0) VLine(px, _traitChart.width, _traitChart.height,
                                          x, prevY, y, c);
                    else Plot(px, _traitChart.width, _traitChart.height, x, y, c);
                    prevY = y;
                }
            }
            _traitChart.SetPixels32(px);
            _traitChart.Apply(false, false);
        }

        private void RedrawHistogram()
        {
            int gene = Game.ColorGene >= 0 ? Game.ColorGene : 0;
            Game.Life.GenomeHistogram(gene, _bins);

            int peak = 1;
            for (int b = 0; b < _bins.Length; b++) peak = math.max(peak, _bins[b]);

            var px = _histChart.GetPixels32();
            Fill(px, new Color32(14, 16, 26, 255));

            int w = _histChart.width, h = _histChart.height;
            int barW = math.max(1, w / _bins.Length);
            Color32 c = GeneColor(gene);

            for (int b = 0; b < _bins.Length; b++)
            {
                int barH = (int)((float)_bins[b] / peak * (h - 2));
                for (int x = b * barW; x < math.min(w, (b + 1) * barW - 1); x++)
                for (int y = 0; y < barH; y++)
                    px[y * w + x] = c;
            }
            _histChart.SetPixels32(px);
            _histChart.Apply(false, false);
        }

        private static Color32 GeneColor(int g) => g switch
        {
            0 => new Color32(120, 230, 120, 255),   // harvest      - green
            1 => new Color32(255, 130, 90, 255),    // metabolism   - orange
            2 => new Color32(120, 180, 255, 255),   // repro thresh - blue
            3 => new Color32(230, 130, 255, 255),   // mutation     - violet
            _ => new Color32(255, 220, 110, 255)    // durability   - amber
        };

        private static void Fill(Color32[] px, Color32 c)
        { for (int i = 0; i < px.Length; i++) px[i] = c; }

        private void DrawGrid(Color32[] px, int w, int h, int divisions)
        {
            Color32 g = new Color32(38, 42, 58, 255);
            for (int d = 1; d < divisions; d++)
            {
                int y = d * h / divisions;
                for (int x = 0; x < w; x++) px[y * w + x] = g;
            }
        }

        private static void Plot(Color32[] px, int w, int h, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            px[y * w + x] = c;
        }

        private static void VLine(Color32[] px, int w, int h, int x, int y0, int y1, Color32 c)
        {
            int a = math.min(y0, y1), b = math.max(y0, y1);
            for (int y = a; y <= b; y++) Plot(px, w, h, x, y, c);
        }

        // ---------------- IMGUI ----------------
        private void OnGUI()
        {
            if (Game?.Life == null) return;
            EnsureStyles();

            float x = Screen.width - PanelWidth - 10;
            var panelRect = new Rect(x, 10, PanelWidth, Screen.height - 20);

            // Tell the game not to treat clicks on this panel as world clicks.
            // GUI coordinates are top-left origin; Input.mousePosition is
            // bottom-left, hence the Y flip.
            var guiMouse = new Vector2(Input.mousePosition.x,
                                       Screen.height - Input.mousePosition.y);
            Game.PointerOverUi = panelRect.Contains(guiMouse);

            GUI.Box(panelRect, GUIContent.none, _panel);

            GUILayout.BeginArea(new Rect(x + 10, 20, PanelWidth - 20, Screen.height - 40));

            GUILayout.Label(Game.World.StatusString(), _header);
            GUILayout.Label($"Population {Game.Life.Population}   " +
                            $"births {Game.Life.Births}   deaths {Game.Life.Deaths}", _label);

            DrawToolbar();
            GUILayout.Space(6);

            GUILayout.Label("Trait means over time (bounds-normalised)", _label);
            GUILayout.Label(_traitChart, GUILayout.Width(_traitChart.width),
                            GUILayout.Height(_traitChart.height));
            DrawLegend();

            GUILayout.Space(6);
            string gname = Game.ColorGene >= 0
                ? GeneTable.DisplayNames[Game.ColorGene]
                : GeneTable.DisplayNames[0];
            GUILayout.Label($"Distribution: {gname}", _label);
            GUILayout.Label(_histChart, GUILayout.Width(_histChart.width),
                            GUILayout.Height(_histChart.height));

            GUILayout.Space(8);
            DrawInspector();

            GUILayout.EndArea();
        }

        private void DrawToolbar()
        {
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Game.Paused ? "Resume" : "Pause")) Game.Paused = !Game.Paused;
            if (GUILayout.Button("New world")) Game.NewWorld(0);
            if (GUILayout.Button(SpeedLabels[Game.SpeedIndex]))
                Game.SpeedIndex = (Game.SpeedIndex + 1) % GameBootstrap.SpeedSteps.Length;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+8 cells")) Game.SeedBatch(8);
            if (GUILayout.Button("Rain")) Game.TriggerWeather(Weather.Rainy);
            if (GUILayout.Button("Clear sky")) Game.TriggerWeather(Weather.Clear);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            foreach (ClickTool t in System.Enum.GetValues(typeof(ClickTool)))
            {
                bool on = Game.Tool == t;
                if (GUILayout.Toggle(on, t.ToString(), GUI.skin.button) && !on) Game.Tool = t;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour by:", _label, GUILayout.Width(64));
            if (GUILayout.Button(Game.ColorGene < 0 ? "energy" : GeneTable.DisplayNames[Game.ColorGene]))
                Game.ColorGene = Game.ColorGene >= GeneTable.Count - 1 ? -1 : Game.ColorGene + 1;
            if (GUILayout.Button("Save log"))
            {
                string dir = Application.persistentDataPath;
                Game.Logger.WriteCellCsv(System.IO.Path.Combine(dir, "biosphere_cells.csv"));
                Game.Logger.WriteAggregateCsv(System.IO.Path.Combine(dir, "biosphere_aggregate.csv"));
                Debug.Log($"[Biosphere] logs written to {dir}");
            }
            GUILayout.EndHorizontal();
        }

        private void DrawLegend()
        {
            GUILayout.BeginHorizontal();
            for (int g = 0; g < GeneTable.Count; g++)
            {
                Color32 c = GeneColor(g);
                var prev = GUI.color;
                GUI.color = c;
                GUILayout.Label("■", _label, GUILayout.Width(12));
                GUI.color = prev;
                GUILayout.Label(GeneTable.DisplayNames[g].Substring(0, 4), _label, GUILayout.Width(34));
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Click-to-inspect. Looks the cell up by its stable CellId every frame,
        /// so if the selected cell dies the panel says so instead of silently
        /// following whichever entity got swapped into that slot.
        /// </summary>
        private void DrawInspector()
        {
            if (Game.SelectedCellId == 0)
            {
                GUILayout.Label("Click a cell with the Inspect tool.", _label);
                return;
            }

            int idx = Game.Life.IndexOfCellId(Game.SelectedCellId);
            if (idx == LifeGrid.Empty)
            {
                GUILayout.Label($"Cell #{Game.SelectedCellId} — DEAD", _header);
                return;
            }

            int2 p = Game.Life.Pos[idx];
            GUILayout.Label($"Cell #{Game.Life.CellId[idx]}", _header);
            GUILayout.Label($"parent      #{Game.Life.ParentId[idx]}" +
                            (Game.Life.ParentId[idx] == 0 ? "  (seeded)" : ""), _label);
            GUILayout.Label($"position    ({p.x}, {p.y})", _label);
            GUILayout.Label($"energy      {Game.Life.Energy[idx]:0.000}", _label);
            GUILayout.Label($"durability  {Game.Life.Durability[idx]:0.000}", _label);
            GUILayout.Label($"age         {Game.Life.Age[idx]:0.0} sim-h", _label);

            int tile = p.y * Game.World.W + p.x;
            GUILayout.Label($"local sun   {Game.World.LocalSunlight(tile):0.00}", _label);
            GUILayout.Label($"terrain     {Game.World.Terrain[tile]}", _label);

            GUILayout.Space(4);
            GUILayout.Label("Genome", _header);
            Genome g = Game.Life.Genes[idx];
            for (int i = 0; i < GeneTable.Count; i++)
            {
                float v = g[i];
                float t = GeneTable.Normalize(i, v);
                GUILayout.BeginHorizontal();
                GUILayout.Label(GeneTable.DisplayNames[i], _label, GUILayout.Width(110));
                GUILayout.Label(v.ToString("0.0000"), _label, GUILayout.Width(60));
                // Inline bar showing where this cell sits within the trait's bounds.
                Rect r = GUILayoutUtility.GetRect(80, 10);
                GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(r, _px);
                GUI.color = GeneColor(i);
                GUI.DrawTexture(new Rect(r.x, r.y, r.width * t, r.height), _px);
                GUI.color = Color.white;
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Focus camera")) Game.PixelCamera.FocusOn(p.x, p.y);
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            _header = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _header.normal.textColor = Accent;

            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, PanelBg);
            bg.Apply();
            _panel = new GUIStyle(GUI.skin.box);
            _panel.normal.background = bg;
        }

        private void OnDestroy()
        {
            if (_bins.IsCreated) _bins.Dispose();
        }
    }
}
