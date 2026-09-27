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

        // ---- Impact consequence model (ImpactConsequence.cs) - v1 "compute and report" only, none
        // of this is ever applied to Reputation/Funds/DestructibleBuilding. These are the "what does
        // the physical model assume" constants; the "how much should this hurt" player-facing knobs
        // (damage coefficient, reputation scaling, etc.) live in SentrySettings instead.

        // Rubble-pile material strength (Pa) - the dynamic pressure q = 1/2 rho v^2 an object can
        // withstand before it starts to break up. Mid of the ~0.1-1 MPa range for rubble piles.
        public static double RubblePileStrengthPa = 5.0e5;

        // Aerodynamic drag coefficient (Cd) used in both the dynamic-pressure descent and the
        // Collins-Melosh-Marcus dispersion length (see ImpactConsequence.BurstAltitude).
        public static double DragCoefficient = 1.3;

        // Threshold constant (alpha) in the pancake-model airburst-altitude closed form: z_b = z* -
        // 2H * ln(1 + (l / 2H) * sqrt(-ln(alpha))). Collins, Melosh & Marcus (2005), "Earth Impact
        // Effects Program," Meteoritics & Planetary Science 40, 817-840 (see also the Appendix of
        // Collins et al.'s 2017 "A numerical assessment of simple airblast models of impact
        // airbursts," same journal, which reproduces the ablative pancake-model equations A11-A18).
        public static double BurstDispersionAlpha = 0.001;

        // Atmosphere scale height (m). Kerbin value (~5,600 m, vs Earth's
        // ~8,500 m) - rarely worth touching, it's a physics fact about this specific body, not a
        // preference.
        public static double AtmosphereScaleHeightM = 5600.0;

        // Kerbin's atmosphere is thinner than Earth's, so blast energy couples to the ground more
        // efficiently (lower burst altitudes for a given size/speed) - "physics, not
        // fudge" correction, applied as a flat multiplier on facility-damage radii.
        public static double AtmosphereThinnessMultiplier = 1.5;

        // Above this burst altitude (m), classify as a harmless high airburst rather than a
        // ground-coupling burst.
        public static double AirburstAltitudeThresholdM = 10000.0;

        // Descent-stepping resolution (m) for the strength-crossing search in BurstAltitude - pure
        // performance/precision tuning, not a physical assumption.
        public static double BurstStepMeters = 100.0;

        // Facility-destruction energy breakpoints (kt TNT) and their un-multiplied damage radii (m)
        // (<15 kt: none, 15-70 kt: ~2 km/single building, 70 kt-1 Mt: ~5
        // km/several buildings, >1 Mt: KSC effectively levelled). AtmosphereThinnessMultiplier is
        // applied on top of these. This is a computed "would this have hit a facility" fact only -
        // v1 never actually damages anything.
        public static double FacilityThresholdLowKt = 15.0;
        public static double FacilityThresholdMidKt = 70.0;
        public static double FacilityThresholdHighKt = 1000.0;
        public static double FacilityRadiusLowM = 2000.0;
        public static double FacilityRadiusMidM = 5000.0;
        public static double FacilityRadiusHighM = 8000.0;

        // How many separate KSC facilities the "several buildings" tier (70 kt-1 Mt) actually
        // demolishes, once SentryScenario gets a chance to (see FacilityDamageTier/
        // TryApplyPendingFacilityDamage) - the "single" tier is always exactly 1, "levelled" is
        // always every registered facility, so this is the only tier that needs a tunable count.
        public static int FacilitySeveralBuildingCount = 4;

        // Real-time (seconds) the count of ScenarioDestructibles.facilityToDestructibles entries
        // must hold steady before SentryScenario.TryApplyPendingFacilityDamage acts on it.
        // DestructibleBuilding registers itself from Start()/OnEnable(), not Awake() -
        // decompile-confirmed each building's own OnDisable()/OnEnable() pair
        // (needsResetOnReEnable) re-registers on re-enable, consistent with KSC buildings coming
        // in and out of camera/LOD range dynamically rather than all registering in one guaranteed
        // batch on scene load. Acting the instant the dictionary is merely non-empty was catching
        // only whatever happened to be in view that first frame, then clearing the pending flag
        // regardless - this settle window lets registration finish before anything is demolished.
        public static float FacilityRegistrationSettleSeconds = 2.0f;

        // Placeholder engineering estimate of what rebuilding a damaged facility would cost, fed
        // into the (unused-in-v1) would-be funds delta. Hand-tune once real numbers matter.
        public static double RebuildCostBaselineFunds = 500000.0;

        // KSC's lat/long on stock Kerbin, for the facility-proximity check. A coarse, once-verified
        // constant rather than a runtime SpaceCenter.Instance lookup, since that singleton is only
        // populated in the Space Center scene and this needs to work from Tracking Station too; also
        // means a planet pack that relocates KSC can just hand-edit this file.
        public static double KscLatitudeDeg = -0.0972;
        public static double KscLongitudeDeg = -74.5766;

        // Shape of the continuous lead-time mitigation curve: mitigation(t) = floor + (1 - floor) *
        // exp(-decay * years^power). Chosen so mitigation(0) = 1.0 (no warning = full penalty),
        // mitigation(1 year) ~= 0.30 (note years^power = 1 at years = 1 regardless of power, so
        // this anchor is independent of the power term), and mitigation(~100 days) ~= 0.90. That
        // third point was added: the owner observed the stock Tracking Station alone
        // routinely gives ~100 days of warning, and a pure exponential in years (power = 1) already
        // discounted that "ordinary" case by ~40% - power > 1 pushes most of the discount later,
        // so routine tracking-station warning barely dents the penalty and only early
        // detection (SENTINEL-driven) earns real mitigation.
        // Never reaches zero even for very long lead times - the impact still happened, so some
        // confidence hit always applies.
        public static double MitigationFloor = 0.25;
        public static double MitigationDecayPerYear = 2.708;
        public static double MitigationPower = 2.3;

        // Real-time budget (seconds) to wait for a force-loaded asteroid/comet's Part.Start()
        // coroutine to actually finish before giving up on capturing its real generated mass and
        // falling back to the nominal-class estimate (SentryScenario.PollRealMassCapture). Found
        // Part.Start() is a Unity coroutine and ModuleAsteroid/ModuleComet.OnStart()'s
        // real mass generation only runs partway through it, not synchronously when Vessel.Load()
        // returns - reading Part.mass immediately after Load() was reading the part's un-started
        // prefab default (150 t) every time. In practice this should resolve within a frame or two;
        // this budget is a generous safety margin, not a tuned value.
        public static float RealMassCaptureTimeoutSeconds = 2.0f;

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
                node.TryGetValue("rubblePileStrengthPa", ref RubblePileStrengthPa);
                node.TryGetValue("dragCoefficient", ref DragCoefficient);
                node.TryGetValue("burstDispersionAlpha", ref BurstDispersionAlpha);
                node.TryGetValue("atmosphereScaleHeightM", ref AtmosphereScaleHeightM);
                node.TryGetValue("atmosphereThinnessMultiplier", ref AtmosphereThinnessMultiplier);
                node.TryGetValue("airburstAltitudeThresholdM", ref AirburstAltitudeThresholdM);
                node.TryGetValue("burstStepMeters", ref BurstStepMeters);
                node.TryGetValue("facilityThresholdLowKt", ref FacilityThresholdLowKt);
                node.TryGetValue("facilityThresholdMidKt", ref FacilityThresholdMidKt);
                node.TryGetValue("facilityThresholdHighKt", ref FacilityThresholdHighKt);
                node.TryGetValue("facilityRadiusLowM", ref FacilityRadiusLowM);
                node.TryGetValue("facilityRadiusMidM", ref FacilityRadiusMidM);
                node.TryGetValue("facilityRadiusHighM", ref FacilityRadiusHighM);
                node.TryGetValue("facilitySeveralBuildingCount", ref FacilitySeveralBuildingCount);
                node.TryGetValue("facilityRegistrationSettleSeconds", ref FacilityRegistrationSettleSeconds);
                node.TryGetValue("rebuildCostBaselineFunds", ref RebuildCostBaselineFunds);
                node.TryGetValue("kscLatitudeDeg", ref KscLatitudeDeg);
                node.TryGetValue("kscLongitudeDeg", ref KscLongitudeDeg);
                node.TryGetValue("mitigationFloor", ref MitigationFloor);
                node.TryGetValue("mitigationDecayPerYear", ref MitigationDecayPerYear);
                node.TryGetValue("mitigationPower", ref MitigationPower);
                node.TryGetValue("realMassCaptureTimeoutSeconds", ref RealMassCaptureTimeoutSeconds);
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
                node.AddValue("rubblePileStrengthPa", RubblePileStrengthPa);
                node.AddValue("dragCoefficient", DragCoefficient);
                node.AddValue("burstDispersionAlpha", BurstDispersionAlpha);
                node.AddValue("atmosphereScaleHeightM", AtmosphereScaleHeightM);
                node.AddValue("atmosphereThinnessMultiplier", AtmosphereThinnessMultiplier);
                node.AddValue("airburstAltitudeThresholdM", AirburstAltitudeThresholdM);
                node.AddValue("burstStepMeters", BurstStepMeters);
                node.AddValue("facilityThresholdLowKt", FacilityThresholdLowKt);
                node.AddValue("facilityThresholdMidKt", FacilityThresholdMidKt);
                node.AddValue("facilityThresholdHighKt", FacilityThresholdHighKt);
                node.AddValue("facilityRadiusLowM", FacilityRadiusLowM);
                node.AddValue("facilityRadiusMidM", FacilityRadiusMidM);
                node.AddValue("facilityRadiusHighM", FacilityRadiusHighM);
                node.AddValue("facilitySeveralBuildingCount", FacilitySeveralBuildingCount);
                node.AddValue("facilityRegistrationSettleSeconds", FacilityRegistrationSettleSeconds);
                node.AddValue("rebuildCostBaselineFunds", RebuildCostBaselineFunds);
                node.AddValue("kscLatitudeDeg", KscLatitudeDeg);
                node.AddValue("kscLongitudeDeg", KscLongitudeDeg);
                node.AddValue("mitigationFloor", MitigationFloor);
                node.AddValue("mitigationDecayPerYear", MitigationDecayPerYear);
                node.AddValue("mitigationPower", MitigationPower);

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
