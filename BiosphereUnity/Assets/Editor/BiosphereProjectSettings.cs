using UnityEditor;
using UnityEngine;

namespace Biosphere.EditorTools
{
    /// <summary>
    /// Applies the pixel-art project settings from ARCHITECTURE.md §3.2 in code,
    /// so they can't be mis-clicked. Safe to run repeatedly.
    ///
    /// Menu: Biosphere -> 0. Apply Pixel-Art Project Settings
    ///
    /// Note: switching colour space triggers a full asset reimport. On a project
    /// this small that is a few seconds.
    /// </summary>
    public static class BiosphereProjectSettings
    {
        [MenuItem("Biosphere/0. Apply Pixel-Art Project Settings", priority = -10)]
        public static void Apply()
        {
            // Linear colour space: the shaders do their day/night tinting with
            // straight multiplies, which only behave predictably in linear.
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
                PlayerSettings.colorSpace = ColorSpace.Linear;

            // Apply to EVERY quality level, not just the active one -- the build
            // can select a different level than the editor is previewing.
            int original = QualitySettings.GetQualityLevel();
            string[] levels = QualitySettings.names;
            for (int i = 0; i < levels.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                // Only settings that are stable across 2022.3 -> Unity 6 are set
                // here. softParticles / billboardsFaceCameraPosition /
                // realtimeReflectionProbes were deliberately dropped: they are
                // deprecated or removed in Unity 6 and irrelevant to a 2D game.
                QualitySettings.antiAliasing = 0;                                  // MSAA smears pixel edges
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                QualitySettings.vSyncCount = 1;                                    // no tearing while panning
                QualitySettings.globalTextureMipmapLimit = 0;                      // full-res textures
                QualitySettings.shadows = ShadowQuality.Disable;                   // top-down 2D
            }
            QualitySettings.SetQualityLevel(original, true);

            // Burst is what makes the simulation cheap. It is on by default, but
            // it gets switched off during debugging and left off.
            EditorPrefs.SetBool("BurstCompilation", true);

            AssetDatabase.SaveAssets();

            Debug.Log("[Biosphere] project settings applied: Linear colour space, " +
                      "MSAA off, aniso off, mips full-res, shadows off, vSync on " +
                      $"(across {levels.Length} quality level(s)).\n" +
                      "If Unity asks to reimport assets for the colour space change, say yes.");
        }

        /// <summary>
        /// Fails loudly if the project is on a Scriptable Render Pipeline. The
        /// shaders here are built-in-pipeline CG (UnityCG.cginc,
        /// UnityObjectToClipPos) and will render magenta under URP/HDRP.
        /// </summary>
        [MenuItem("Biosphere/Check Render Pipeline", priority = 21)]
        public static void CheckPipeline()
        {
            var rp = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            if (rp == null)
            {
                Debug.Log("[Biosphere] Built-in Render Pipeline detected. Correct.");
                return;
            }
            Debug.LogError(
                $"[Biosphere] This project is using an SRP ({rp.GetType().Name}). " +
                "The Biosphere shaders are written for the Built-in Render Pipeline " +
                "and will render magenta. Either create the project from the '2D " +
                "(Built-In Render Pipeline)' template, or clear Graphics Settings -> " +
                "Scriptable Render Pipeline Settings.");
        }
    }
}
