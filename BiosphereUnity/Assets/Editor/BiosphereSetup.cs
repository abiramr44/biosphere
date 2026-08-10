using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Biosphere.CameraRig;
using Biosphere.Core;
using Biosphere.Particles;
using Biosphere.Render;
using Biosphere.UI;

namespace Biosphere.EditorTools
{
    /// <summary>
    /// One-click project setup. Hand-authoring a .unity scene file is a GUID
    /// minefield, so instead the scene is BUILT from code: this creates the
    /// WorldConfig asset, generates a placeholder sprite atlas, spawns and wires
    /// every GameObject, and saves the result.
    ///
    /// Menu: Biosphere -> 1. Setup Project Scene
    /// </summary>
    public static class BiosphereSetup
    {
        private const string SettingsDir = "Assets/Settings";
        private const string ArtDir      = "Assets/Art/Sprites";
        private const string ScenesDir   = "Assets/Scenes";
        private const string ConfigPath  = SettingsDir + "/WorldConfig.asset";
        private const string AtlasPath   = ArtDir + "/placeholder_atlas.png";
        private const string ScenePath   = ScenesDir + "/Biosphere.unity";

        private const int AtlasCols = 16;
        private const int AtlasRows = 16;
        private const int CellPx    = 32;

        [MenuItem("Biosphere/1. Setup Project Scene", priority = 0)]
        public static void Setup()
        {
            EnsureDir(SettingsDir);
            EnsureDir(ArtDir);
            EnsureDir(ScenesDir);

            var cfg = CreateOrLoadConfig();
            var atlas = CreateOrLoadAtlas();
            BuildScene(cfg, atlas);

            EditorUtility.DisplayDialog("Biosphere",
                "Scene built and saved to " + ScenePath +
                "\n\nPress Play.\n\nControls:\n" +
                "  Space   pause\n  + / -   speed\n  N       new world\n" +
                "  C       cycle colour overlay\n  scroll  zoom\n  middle-drag  pan",
                "OK");
        }

        // ---------------- Config asset ----------------
        private static WorldConfig CreateOrLoadConfig()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<WorldConfig>(ConfigPath);
            if (cfg != null) return cfg;

            cfg = ScriptableObject.CreateInstance<WorldConfig>();
            // Start small enough to eyeball, large enough to be a real test.
            cfg.GridW = 256;
            cfg.GridH = 256;
            cfg.PixelsPerUnit = 16;
            AssetDatabase.CreateAsset(cfg, ConfigPath);
            AssetDatabase.SaveAssets();
            return cfg;
        }

        // ---------------- Placeholder art ----------------
        /// <summary>
        /// Generates a stand-in atlas so the project runs before any real art
        /// exists. Cell 0 = creature blob (shaded, matching the Python slime
        /// sprite), cell 1 = tree, cell 2 = rock. Replace this PNG with real art
        /// and everything keeps working -- the atlas index constants on
        /// GameBootstrap are the only contract.
        /// </summary>
        private static Texture2D CreateOrLoadAtlas()
        {
            if (File.Exists(AtlasPath))
                return AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);

            int w = AtlasCols * CellPx, h = AtlasRows * CellPx;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);

            DrawBlob(px, w, CellIndexToOrigin(0), new Color(1f, 1f, 1f, 1f));
            DrawTree(px, w, CellIndexToOrigin(1));
            DrawRock(px, w, CellIndexToOrigin(2));

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(AtlasPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceUpdate);
            ApplyPixelArtImportSettings(AtlasPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
        }

        /// <summary>Atlas cell index -> bottom-left pixel of that cell. Must match
        /// the index->UV maths in PixelSpriteInstanced.shader (row 0 at the TOP).</summary>
        private static Vector2Int CellIndexToOrigin(int index)
        {
            int col = index % AtlasCols;
            int row = index / AtlasCols;
            return new Vector2Int(col * CellPx, (AtlasRows - 1 - row) * CellPx);
        }

        private static void Put(Color32[] px, int texW, Vector2Int o, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= CellPx || y >= CellPx) return;
            px[(o.y + y) * texW + (o.x + x)] = c;
        }

        /// <summary>Rounded pseudo-3D blob: soft edge, upper-left highlight,
        /// lower-right shadow rim. Same read as the Python slime sprite.</summary>
        private static void DrawBlob(Color32[] px, int texW, Vector2Int o, Color tint)
        {
            const float cx = 15.5f, cy = 13f, rx = 10f, ry = 8.5f;
            for (int y = 0; y < CellPx; y++)
            for (int x = 0; x < CellPx; x++)
            {
                float dx = (x - cx) / rx, dy = (y - cy) / ry;
                float d = dx * dx + dy * dy;
                if (d > 1f) continue;

                float shade = 1f;
                shade += (-dx - dy) * 0.28f;                  // upper-left light
                if (d > 0.72f) shade *= 0.72f;                // shadow rim
                shade = Mathf.Clamp(shade, 0.45f, 1.45f);

                var c = new Color(tint.r * shade, tint.g * shade, tint.b * shade, 1f);
                Put(px, texW, o, x, y, c);
            }
        }

        private static void DrawTree(Color32[] px, int texW, Vector2Int o)
        {
            var trunk = new Color(0.35f, 0.22f, 0.12f);
            var leafD = new Color(0.10f, 0.38f, 0.14f);
            var leafL = new Color(0.20f, 0.62f, 0.22f);

            for (int y = 2; y < 11; y++)
            for (int x = 14; x < 18; x++) Put(px, texW, o, x, y, trunk);

            for (int y = 9; y < 27; y++)
            for (int x = 0; x < CellPx; x++)
            {
                float dx = (x - 15.5f) / 9f, dy = (y - 18f) / 9f;
                float d = dx * dx + dy * dy;
                if (d > 1f) continue;
                Put(px, texW, o, x, y, d < 0.45f && dx < 0.1f ? leafL : leafD);
            }
        }

        private static void DrawRock(Color32[] px, int texW, Vector2Int o)
        {
            var dark = new Color(0.34f, 0.32f, 0.35f);
            var lite = new Color(0.58f, 0.57f, 0.60f);
            for (int y = 3; y < 18; y++)
            for (int x = 0; x < CellPx; x++)
            {
                float dx = (x - 15.5f) / 10f, dy = (y - 9f) / 7f;
                if (dx * dx + dy * dy > 1f) continue;
                Put(px, texW, o, x, y, (-dx - dy) > 0.25f ? lite : dark);
            }
        }

        /// <summary>The import settings from ARCHITECTURE.md §3.1, applied in code
        /// so they can't be got wrong by hand.</summary>
        private static void ApplyPixelArtImportSettings(string path)
        {
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.Default;   // sampled as a raw atlas
            imp.filterMode = FilterMode.Point;               // no bilinear smear
            imp.mipmapEnabled = false;                       // no mush when zoomed out
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.alphaIsTransparency = true;
            imp.sRGBTexture = true;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
        }

        // ---------------- Scene ----------------
        private static void BuildScene(WorldConfig cfg, Texture2D atlas)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                    NewSceneMode.Single);

            // NewScene(..., NewSceneMode.Single) unloads the previously active
            // scene, and as a side effect can invalidate any ScriptableObject
            // (like cfg) that was loaded via AssetDatabase before the switch --
            // touching it right afterward throws MissingReferenceException.
            // Re-fetching it fresh here guarantees every wiring call below
            // uses a live reference, not a stale one caught mid-teardown.
            cfg = AssetDatabase.LoadAssetAtPath<WorldConfig>(ConfigPath);

            // --- Camera ---
            var camGo = new GameObject("Main Camera", typeof(Camera));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.04f, 0.05f, 0.09f);

            // Deactivate before AddComponent: Unity calls Awake() the moment a
            // component is added to an ACTIVE GameObject, even in the Editor
            // outside Play mode. PixelCameraController.Awake() dereferences
            // cfg immediately -- if it's still unassigned at that point, Awake
            // throws, Unity disables the component, and a disabled camera
            // controller gets baked straight into the saved scene. Wiring cfg
            // while the object is inactive defers Awake() until SetActive(true).
            camGo.SetActive(false);
            var camCtrl = camGo.AddComponent<PixelCameraController>();
            SetPrivate(camCtrl, "cfg", cfg);
            camGo.SetActive(true);

            // --- Terrain quad ---
            var terrainGo = new GameObject("Terrain",
                typeof(MeshFilter), typeof(MeshRenderer), typeof(TerrainRenderer));
            var terrain = terrainGo.GetComponent<TerrainRenderer>();
            SetPrivate(terrain, "terrainShader", Shader.Find("Biosphere/TerrainUnlit"));

            // --- Sprite layers ---
            var spritesGo = new GameObject("Sprites", typeof(SpriteLayerRenderer));
            var sprites = spritesGo.GetComponent<SpriteLayerRenderer>();
            SetPrivate(sprites, "spriteShader", Shader.Find("Biosphere/PixelSpriteInstanced"));
            SetPrivate(sprites, "spriteAtlas", atlas);
            SetPrivate(sprites, "atlasCols", AtlasCols);
            SetPrivate(sprites, "atlasRows", AtlasRows);
            SetPrivate(sprites, "spritePixelSize", 24);

            // --- Particles ---
            var particlesGo = new GameObject("Particles", typeof(PixelParticleSystem));
            var particles = particlesGo.GetComponent<PixelParticleSystem>();
            SetPrivate(particles, "particleShader", Shader.Find("Biosphere/PixelParticle"));

            // --- Game + HUD ---
            var gameGo = new GameObject("Game", typeof(GameBootstrap), typeof(DashboardHud));
            var game = gameGo.GetComponent<GameBootstrap>();
            game.Config = cfg;
            game.Terrain = terrain;
            game.Sprites = sprites;
            game.Particles = particles;
            game.PixelCamera = camCtrl;

            var hud = gameGo.GetComponent<DashboardHud>();
            hud.Game = game;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
        }

        /// <summary>Assign a [SerializeField] private field. Reflection is fine
        /// here -- this runs once, in the editor, at setup time.</summary>
        private static void SetPrivate(Object target, string field, object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null)
            {
                Debug.LogWarning($"[BiosphereSetup] no serialized field '{field}' on {target.GetType().Name}");
                return;
            }
            switch (prop.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = (Object)value; break;
                case SerializedPropertyType.Integer:
                    prop.intValue = (int)value; break;
                case SerializedPropertyType.Float:
                    prop.floatValue = System.Convert.ToSingle(value); break;
                default:
                    Debug.LogWarning($"[BiosphereSetup] unhandled type for '{field}'"); break;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureDir(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        [MenuItem("Biosphere/Regenerate Placeholder Atlas", priority = 20)]
        public static void RegenerateAtlas()
        {
            if (File.Exists(AtlasPath)) File.Delete(AtlasPath);
            AssetDatabase.Refresh();
            CreateOrLoadAtlas();
            Debug.Log("[Biosphere] placeholder atlas regenerated at " + AtlasPath);
        }
    }
}
