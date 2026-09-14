using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Sentry.UI
{
    // Registers the mod with ToolbarControl exactly once. Standard pattern for mods using this
    // library: a tiny addon whose only job is the one-time RegisterMod() call, kept separate from
    // the addon that actually owns a button/window instance per scene.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class RegisterToolbar : MonoBehaviour
    {
        public const string ModId = "SENTRY";
        public const string ModName = "SENTRY";

        private void Start()
        {
            ToolbarControl.RegisterMod(ModId, ModName, true, true, true);
        }
    }

    // Owns the toolbar button and the AlertWindow instance, and draws it. Runs in every scene so
    // the toolbar button (gated to the three relevant scenes via AppScenes) and window keep
    // working across scene changes - SentryScenario itself has no OnGUI, so this is where
    // GUILayout actually happens; it just reads the scenario's data and toggles it live.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class SentryUI : MonoBehaviour
    {
        // MAPVIEW is its own ApplicationLauncher flag, not a sub-state of FLIGHT (confirmed by
        // reflecting the AppScenes enum: SPACECENTER=1, FLIGHT=2, MAPVIEW=4, ... all distinct
        // bits) - without it here, the button vanishes the instant the player enters map view
        // (including via our own row-click "focus in map view" feature) and reappears on
        // leaving it, which reads as the icon randomly disappearing rather than a scene change.
        private const ApplicationLauncher.AppScenes RelevantScenes =
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.TRACKSTATION
            | ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW;

        private static AlertWindow sharedWindow;
        private ToolbarControl toolbarControl;

        private void Start()
        {
            // Scenes outside the mod's three relevant ones (editors, main menu, ...) get neither
            // a toolbar button nor a window - nothing to show there.
            if (!IsRelevantScene()) return;

            if (sharedWindow == null) sharedWindow = new AlertWindow();

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                sharedWindow.Show, sharedWindow.Hide,
                RelevantScenes,
                RegisterToolbar.ModId, "sentryButton",
                "SENTRY/PluginData/Icons/icon-38", "SENTRY/PluginData/Icons/icon-24",
                "SENTRY");
        }

        private void OnGUI()
        {
            if (sharedWindow != null && IsRelevantScene())
            {
                sharedWindow.Draw();
            }
        }

        private void OnDestroy()
        {
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                toolbarControl = null;
            }
        }

        private static bool IsRelevantScene()
        {
            GameScenes scene = HighLogic.LoadedScene;
            return scene == GameScenes.SPACECENTER || scene == GameScenes.TRACKSTATION || scene == GameScenes.FLIGHT;
        }
    }
}
