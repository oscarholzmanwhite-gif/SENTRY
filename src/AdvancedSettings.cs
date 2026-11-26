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

        // Nominal impact speed (m/s) used ONLY for ImpactConsequence.TryEstimateNominalEnergyKt
        // (the deflection bonus) - a deflected object's real orbit no longer predicts an impact at
        // all, so there's no live trajectory to measure a precise entry speed from. Matches the
        // design doc's own stated baseline: "Impact speed ~ sqrt(v_inf^2 + v_esc^2) ... ~4 km/s
        // baseline" - the same assumption the original penalty table's own calibration ("30x
        // density assumed, 4 km/s, land impact") was built on, so the deflection bonus table (which
        // reuses those exact same energy anchors) is measuring against a consistent baseline.
        public static double NominalImpactSpeedMs = 4000.0;

        // Real-time rescan cadence (s) while a captured rock's vessel is loaded. The normal scan
        // runs every 3 in-game hours, which at 1x warp (i.e. while the player is flying the rock)
        // is 3 real hours - so a deflection burn's Impact -> NearPass change went unnoticed until
        // the player happened to time-warp. Everything else in such a scan is a cache hit.
        public static float CapturedRescanRealSeconds = 1.0f;

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

        // Pancake factor f_p: how many times its original width the breaking-up debris cloud spreads
        // before it counts as "burst". Collins, Melosh & Marcus (2005), "Earth Impact Effects
        // Program," Meteoritics & Planetary Science 40, 817-840, use 7 (good Tunguska-class fits for
        // 5-10; Collins et al. 2017 fit Chelyabinsk with 5-6). The burst altitude comes from setting
        // the cloud width L(z) = L0 * sqrt(1 + (2H/l)^2 * (exp((z*-z)/2H) - 1)^2) (Collins et al.
        // 2017, "A numerical assessment of simple airblast models of impact airbursts", same
        // journal, eq. A16) equal to f_p * L0: z_b = z* - 2H * ln(1 + (l/2H) * sqrt(f_p^2 - 1)).
        // Replaced an earlier "alpha" constant (sqrt(-ln(0.001)) ~ 2.6 in place of sqrt(48) ~ 6.9)
        // that came from a garbled transcription of the paper and put mid-size bursts several km
        // too high - see "Burst altitude formula corrected". Must be > 1.
        public static double PancakeFactor = 7.0;

        // FALLBACK ONLY (~5,600 m, stock Kerbin's real value, vs Earth's ~8,500 m) - as of this
        // pass, ImpactConsequence.EstimateScaleHeightM derives the real value live from the actual
        // home body's own pressure profile (a two-point log-ratio against home.GetPressure), so a
        // planet pack (RSS, JNSQ, GPP, ...) gets its own body's real atmosphere instead of Kerbin's
        // regardless of what's actually loaded - this constant only kicks in if that derivation
        // can't run (no atmosphere, or degenerate pressure data).
        public static double AtmosphereScaleHeightM = 5600.0;

        // Kerbin's atmosphere is thinner than Earth's, so blast energy couples to the ground more
        // efficiently (lower burst altitudes for a given size/speed) - "physics, not fudge"
        // correction, applied as a flat multiplier on facility-damage radii. This is a Kerbin-vs-
        // Earth COMPARISON, not a derivable fact about an arbitrary body (unlike the scale height
        // above) - ImpactConsequence.FacilityCheck only applies it when the actual home body is
        // named "Kerbin"; any other home body (a planet pack's own world) gets no correction
        // (multiplier 1.0) rather than an unjustified one.
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

        // Mirrors stock's own AlarmClockScenario.warpChangeTimeSafteyMultiplier (decompile-
        // confirmed public instance field, stock default 1.2) - the real-time margin the stock
        // Alarm Clock uses to decide WHEN to start paying attention to an approaching alarm at all
        // (HandleWarpActions gates on currentUT + warpRate * this > alarm.ut). Once it starts, it
        // already self-accelerates: it steps down one warp level per completed transition while
        // there's slack, and falls back to instant (no-fade) single-level drops the moment it's
        // behind schedule - so this margin mostly controls how much of the descent happens as a
        // gradual early fade vs. a snappier late catch-up, not the total number of levels crossed.
        // A LOWER value delays the gradual fade, pushing more of the ramp into the fast catch-up
        // path (feels snappier, closer to the actual event) - which is what SentryScenario.Update
        // applies this for (AlarmClockIntegration.SyncWarpSafetyMultiplier), at the owner's request.
        // IMPORTANT: this is stock's own single scenario-wide field, not per-alarm - lowering it
        // changes the ramp-down feel for EVERY stock alarm in the save (maneuver nodes, contract
        // deadlines, etc.), not just SENTRY's own kill-warp alarms. It's also a real safety margin,
        // not just a feel knob: at extreme warp rates, too low a value risks not leaving enough
        // real time to actually reach 1x before the target UT is skipped over entirely - precisely
        // the "asteroid warps clean through Kerbin" failure mode the whole Alarm Clock integration
        // exists to prevent. Default here (1.0) is a modest ~17% reduction from stock's 1.2; don't
        // push this much lower without testing carefully at your own highest normal warp rate.
        public static double AlarmClockWarpSafetyMultiplier = 1.0;

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
                node.TryGetValue("nominalImpactSpeedMs", ref NominalImpactSpeedMs);
                node.TryGetValue("capturedRescanRealSeconds", ref CapturedRescanRealSeconds);
                node.TryGetValue("rubblePileStrengthPa", ref RubblePileStrengthPa);
                node.TryGetValue("dragCoefficient", ref DragCoefficient);
                node.TryGetValue("pancakeFactor", ref PancakeFactor);
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
                node.TryGetValue("alarmClockWarpSafetyMultiplier", ref AlarmClockWarpSafetyMultiplier);
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
                node.AddValue("nominalImpactSpeedMs", NominalImpactSpeedMs);
                node.AddValue("capturedRescanRealSeconds", CapturedRescanRealSeconds);
                node.AddValue("rubblePileStrengthPa", RubblePileStrengthPa);
                node.AddValue("dragCoefficient", DragCoefficient);
                node.AddValue("pancakeFactor", PancakeFactor);
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
                node.AddValue("realMassCaptureTimeoutSeconds", RealMassCaptureTimeoutSeconds);
                node.AddValue("alarmClockWarpSafetyMultiplier", AlarmClockWarpSafetyMultiplier);

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
