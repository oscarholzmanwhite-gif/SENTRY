using System;
using System.IO;
using UnityEngine;

namespace Sentry
{
    // Engineering/tuning constants that most players will never want to touch, but that are
    // still occasionally worth hand-tuning without a rebuild - kept out of the in-game
    // GameParameters settings screen so that screen doesn't fill
    // up with slider widgets for numbers that aren't really player preferences. Per-install, like
    // UiPrefs (not per-save, like SentrySettings): these are properties of this install's
    // tuning philosophy, not of any one career. Stored in
    // GameData/SENTRY/PluginData/advanced.cfg - hand-read via ConfigNode.Load, not
    // GameDatabase, so PluginData is safe here.
    //
    // Loaded once at launch, written out with defaults on first run so there's something to open
    // and edit. Not designed for live-reload - a relaunch picks up a hand-edited change, which is
    // fine for values that aren't meant to be tweaked mid-session.
    public static class AdvancedSettings
    {
        // Re-alert if a predicted impact's UT shifts by more than this while still classified as
        // Impact (SentryScenario.ApplyResult).
        public static double ImpactShiftAlertSeconds = 3600.0;

        // A "miss" verdict is re-checked after this fraction of the search horizon has elapsed
        // (SentryScenario.ApplyResult / SoiIntersection.HorizonSeconds).
        public static double MissRevalidateFraction = 0.5;

        // Real-time floor between scan attempts, alongside the game-time interval - stops the
        // 3-in-game-hour scan gate firing every frame at high time warp (SentryScenario.Update).
        // Lowering this much reintroduces the log-spam bug that floor was added to fix.
        public static float MinScanGapRealSeconds = 2.0f;

        // How long to wait after a scene loads before the first scan, so the scene finishes
        // loading first (SentryScenario.Update).
        public static float FirstScanDelayRealSeconds = 3.0f;

        // Minimum surface-relative speed (m/s) a disappearing impactor must have last been seen
        // travelling at, near the impact threshold altitude, to be logged as a genuine destructive
        // impact rather than "disappeared under other circumstances" - see
        // SentryScenario.ReportConfirmedImpact. Default is roughly the speed of sound at
        // sea level, well above any controlled touchdown.
        public static double ImpactSurfaceSpeedCutoffMs = 340.0;

        // Asteroid Abundance preset raw values (SentrySettings.ApplyAbundance) - the
        // numbers behind the Low/Normal/High choice on the in-game settings screen. Edit these if
        // the built-in presets don't feel right; the dropdown itself still lives in GameParameters.
        // Normal matches ScenarioDiscoverableObjects' own stock defaults.
        public static int AbundanceLowSpawnOddsAgainst = 4;
        public static int AbundanceLowSpawnGroupMinLimit = 1;
        public static int AbundanceLowSpawnGroupMaxLimit = 5;
        public static int AbundanceNormalSpawnOddsAgainst = 2;
        public static int AbundanceNormalSpawnGroupMinLimit = 3;
        public static int AbundanceNormalSpawnGroupMaxLimit = 10;
        public static int AbundanceHighSpawnOddsAgainst = 1;
        public static int AbundanceHighSpawnGroupMinLimit = 5;
        public static int AbundanceHighSpawnGroupMaxLimit = 15;

        private static bool loaded;

        private static string FilePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/SENTRY/PluginData/advanced.cfg"); }
        }

        // Called once, lazily, from SentryScenario.OnAwake - avoids doing file I/O before
        // the game (and KSPUtil.ApplicationRootPath) is actually ready. Idempotent.
        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                ConfigNode node = ConfigNode.Load(FilePath);
                if (node == null)
                {
                    Save(); // first run: write the defaults out so there's a file to hand-edit
                    return;
                }

                node.TryGetValue("impactShiftAlertSeconds", ref ImpactShiftAlertSeconds);
                node.TryGetValue("missRevalidateFraction", ref MissRevalidateFraction);
                node.TryGetValue("minScanGapRealSeconds", ref MinScanGapRealSeconds);
                node.TryGetValue("firstScanDelayRealSeconds", ref FirstScanDelayRealSeconds);
                node.TryGetValue("impactSurfaceSpeedCutoffMs", ref ImpactSurfaceSpeedCutoffMs);
                node.TryGetValue("abundanceLowSpawnOddsAgainst", ref AbundanceLowSpawnOddsAgainst);
                node.TryGetValue("abundanceLowSpawnGroupMinLimit", ref AbundanceLowSpawnGroupMinLimit);
                node.TryGetValue("abundanceLowSpawnGroupMaxLimit", ref AbundanceLowSpawnGroupMaxLimit);
                node.TryGetValue("abundanceNormalSpawnOddsAgainst", ref AbundanceNormalSpawnOddsAgainst);
                node.TryGetValue("abundanceNormalSpawnGroupMinLimit", ref AbundanceNormalSpawnGroupMinLimit);
                node.TryGetValue("abundanceNormalSpawnGroupMaxLimit", ref AbundanceNormalSpawnGroupMaxLimit);
                node.TryGetValue("abundanceHighSpawnOddsAgainst", ref AbundanceHighSpawnOddsAgainst);
                node.TryGetValue("abundanceHighSpawnGroupMinLimit", ref AbundanceHighSpawnGroupMinLimit);
                node.TryGetValue("abundanceHighSpawnGroupMaxLimit", ref AbundanceHighSpawnGroupMaxLimit);
            }
            catch (Exception e)
            {
                // Never let a corrupt/unreadable file break the mod - just fall back to defaults
                // and keep going.
                Debug.LogWarning("[SENTRY] Could not load advanced settings from " + FilePath + ": " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                ConfigNode node = new ConfigNode("SENTRY_ADVANCED");
                node.AddValue("impactShiftAlertSeconds", ImpactShiftAlertSeconds);
                node.AddValue("missRevalidateFraction", MissRevalidateFraction);
                node.AddValue("minScanGapRealSeconds", MinScanGapRealSeconds);
                node.AddValue("firstScanDelayRealSeconds", FirstScanDelayRealSeconds);
                node.AddValue("impactSurfaceSpeedCutoffMs", ImpactSurfaceSpeedCutoffMs);
                node.AddValue("abundanceLowSpawnOddsAgainst", AbundanceLowSpawnOddsAgainst);
                node.AddValue("abundanceLowSpawnGroupMinLimit", AbundanceLowSpawnGroupMinLimit);
                node.AddValue("abundanceLowSpawnGroupMaxLimit", AbundanceLowSpawnGroupMaxLimit);
                node.AddValue("abundanceNormalSpawnOddsAgainst", AbundanceNormalSpawnOddsAgainst);
                node.AddValue("abundanceNormalSpawnGroupMinLimit", AbundanceNormalSpawnGroupMinLimit);
                node.AddValue("abundanceNormalSpawnGroupMaxLimit", AbundanceNormalSpawnGroupMaxLimit);
                node.AddValue("abundanceHighSpawnOddsAgainst", AbundanceHighSpawnOddsAgainst);
                node.AddValue("abundanceHighSpawnGroupMinLimit", AbundanceHighSpawnGroupMinLimit);
                node.AddValue("abundanceHighSpawnGroupMaxLimit", AbundanceHighSpawnGroupMaxLimit);

                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                node.Save(FilePath,
                    "SENTRY advanced tuning - per-install, rarely needs touching." +
                    "Safe to delete (defaults will be used and this file rewritten). Changes need a relaunch to take effect.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SENTRY] Could not save advanced settings to " + FilePath + ": " + e.Message);
            }
        }
    }
}
