using System;
using System.IO;
using UnityEngine;

namespace Sentry.UI
{
    // Small per-installation UI preferences: which filters/sort are active, and where the window
    // sits on screen. Deliberately NOT part of SentryScenario's save-file persistence - a
    // player's preferred filters and window position are a property of the player/install, not of
    // any one save, so they should carry over to a new career rather than reset with it. Stored in
    // GameData/SENTRY/PluginData/ui.cfg, the conventional per-mod-install settings location
    // (the game's asset database doesn't scan PluginData, so this can't collide with anything).
    public static class UiPrefs
    {
        public enum SortMode { Time, Class }

        public static bool ShowImpacts = true;
        public static bool ShowFlyBys = false;
        public static bool ShowApproaches = true;
        public static bool ShowAsteroids = true;
        public static bool ShowComets = true;
        public static SortMode Sort = SortMode.Time;

        // NaN means "not yet set" - WindowBase falls back to its own centred default in that case.
        public static Rect WindowRect = new Rect(float.NaN, float.NaN, float.NaN, float.NaN);

        private static bool loaded;

        private static string FilePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/SENTRY/PluginData/ui.cfg"); }
        }

        // Called once, lazily, the first time anything asks for a preference - avoids doing file
        // I/O before the game (and KSPUtil.ApplicationRootPath) is actually ready.
        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                ConfigNode node = ConfigNode.Load(FilePath);
                if (node == null) return; // first run: nothing saved yet, defaults stand

                node.TryGetValue("showImpacts", ref ShowImpacts);
                node.TryGetValue("showFlyBys", ref ShowFlyBys);
                node.TryGetValue("showApproaches", ref ShowApproaches);
                node.TryGetValue("showAsteroids", ref ShowAsteroids);
                node.TryGetValue("showComets", ref ShowComets);

                string sortStr = Sort.ToString();
                node.TryGetValue("sort", ref sortStr);
                SortMode parsedSort;
                if (Enum.TryParse(sortStr, out parsedSort)) Sort = parsedSort;

                node.TryGetValue("windowRect", ref WindowRect);
            }
            catch (Exception e)
            {
                // Never let a corrupt/unreadable prefs file break the mod - just fall back to
                // defaults and keep going.
                Debug.LogWarning("[SENTRY] Could not load UI prefs from " + FilePath + ": " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                ConfigNode node = new ConfigNode("SENTRY_UI");
                node.AddValue("showImpacts", ShowImpacts);
                node.AddValue("showFlyBys", ShowFlyBys);
                node.AddValue("showApproaches", ShowApproaches);
                node.AddValue("showAsteroids", ShowAsteroids);
                node.AddValue("showComets", ShowComets);
                node.AddValue("sort", Sort.ToString());
                node.AddValue("windowRect", WindowRect);

                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                node.Save(FilePath, "SENTRY UI preferences - per-install, not per-save. Safe to delete.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SENTRY] Could not save UI prefs to " + FilePath + ": " + e.Message);
            }
        }
    }
}
