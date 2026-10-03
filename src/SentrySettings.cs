using System;
using Expansions.Missions;
using KSP.Localization;

namespace Sentry
{
    // A real settings screen for the tunables that were previously hardcoded constants
    // in SentryScenario. Deliberately does NOT include AudioAlertEnabled/StopWarpEnabled/
    // UseAlarmClockEnabled - those already live in SentryScenario's own save-file node
    // (persisted differently from a CustomParameterNode's GAME_PARAMETERS node), and moving them
    // here would silently reset any value already customized on an existing save. 
    //
    // No registration attribute needed: KSP's GameParameters.GenerateParameterTypes() walks every
    // loaded assembly and auto-registers any non-abstract CustomParameterNode subclass it finds
    // (confirmed by decompiling GameParameters). Persistence is automatic too - the base
    // ParameterNode.Load/Save reflects over every public field by name (confirmed: string/int/
    // float/bool/enum are all handled), unlike ScenarioModule which needs hand-written OnLoad/OnSave.
    public class SentrySettings : GameParameters.CustomParameterNode
    {
        public override string Title { get { return "SENTRY"; } }
        public override string Section { get { return "SENTRY"; } }
        public override string DisplaySection { get { return "SENTRY"; } }
        public override int SectionOrder { get { return 0; } }
        public override GameParameters.GameMode GameMode { get { return GameParameters.GameMode.ANY; } }
        public override bool HasPresets { get { return false; } }

        [GameParameters.CustomFloatParameterUI("Comet Approach Radius (x SOI)",
            toolTip = "How close a comet must pass, as a multiple of the home body's SOI radius, to trigger a close-approach alert.",
            minValue = 5f, maxValue = 100f, stepCount = 20, displayFormat = "F0", addTextField = true, unlockedDuringMission = true)]
        public float cometApproachSoiMultiple = 25f;

        [GameParameters.CustomFloatParameterUI("Impact Imminent Lead Time (hours)",
            toolTip = "How far before a predicted impact the final warning fires and the mod starts watching every frame for confirmation.",
            minValue = 0.5f, maxValue = 24f, stepCount = 48, displayFormat = "F1", addTextField = true, unlockedDuringMission = true)]
        public float impactImminentLeadHours = 3f;

        // ---- Impact consequence estimate (ImpactConsequence.cs) -----------------------------------
        // These answer "how much should this hurt, as a player preference" (a difficulty-style
        // knob), so they live here rather than in AdvancedSettings, alongside the sliders above.
        // Reputation is genuinely applied in career mode (SentryScenario.ReportConfirmedImpact);
        // facility destruction is genuinely applied too (demolition queued for the next Space
        // Center visit) - Funds is never touched at all, by design (see ImpactConsequence's own
        // WouldBeFundsDelta comment).

        [GameParameters.CustomFloatParameterUI("Consequence: Damage Coefficient",
            toolTip = "Overall multiplier on the estimated reputation hit from a confirmed impact. 1.0 = the model's baseline.",
            minValue = 0.1f, maxValue = 5f, stepCount = 49, displayFormat = "F1", addTextField = true, unlockedDuringMission = true)]
        public float damageCoefficient = 1f;

        [GameParameters.CustomFloatParameterUI("Consequence: Reputation Scaling",
            toolTip = "Multiplier on the estimated reputation hit from a confirmed impact.",
            minValue = 0.1f, maxValue = 5f, stepCount = 49, displayFormat = "F1", addTextField = true, unlockedDuringMission = true)]
        public float reputationScaling = 1f;

        [GameParameters.CustomParameterUI("Consequence: Facility Destruction",
            toolTip = "Whether a large enough confirmed impact near KSC actually demolishes facilities (queued " +
                      "for the next time you visit the Space Center, since buildings only exist as live objects " +
                      "there) - you repair them yourself afterward, same as any other stock damage. No funds are " +
                      "ever auto-deducted for this.",
            unlockedDuringMission = true)]
        public bool facilityDestructionEnabled = true;

        [GameParameters.CustomParameterUI("Consequence: Reputation Penalties",
            toolTip = "Whether a confirmed impact costs reputation (career only). When off, the impact is " +
                      "still analysed and reported - only the reputation change is skipped.",
            unlockedDuringMission = true)]
        public bool reputationPenaltiesEnabled = true;

        [GameParameters.CustomParameterUI("Consequence: Deflection Bonuses",
            toolTip = "Whether redirecting a natural impactor away from an impact course earns reputation " +
                      "(career only).",
            unlockedDuringMission = true)]
        public bool deflectionBonusesEnabled = true;

        // Null-safe reads for call sites: with no game loaded (or settings somehow missing), behave
        // as the defaults do rather than silently switching consequences off.
        public static bool ReputationPenaltiesEnabled
        {
            get { SentrySettings s = Instance; return s == null || s.reputationPenaltiesEnabled; }
        }

        public static bool DeflectionBonusesEnabled
        {
            get { SentrySettings s = Instance; return s == null || s.deflectionBonusesEnabled; }
        }

        // ---- Density patch status (read-only) -----------------------------------------------------
        // A get-only string property renders as a plain label in the Esc-menu settings dialog, with
        // its getter re-evaluated live on every redraw (confirmed by decompiling
        // DifficultyOptionsMenu: a string member becomes a DialogGUILabel wrapping a reflection
        // getter, and an empty title shows the value alone). autoPersistance = false keeps
        // ParameterNode.Load/Save from touching it - nothing here is ever written to the save.
        //
        // Detection is by effect, not by file: it reads the same live prefab densities the
        // consequence model uses (ImpactConsequence.TryGetDensity) and compares them to stock's
        // 0.03 t/m^3. So it reports what's actually in effect - catching a patch that failed to
        // apply, or someone else's density patch, which a "does the .cfg exist" check would miss.
        public const double StockSpaceObjectDensity = 0.03;

        [GameParameters.CustomStringParameterUI("", lines = 6, autoPersistance = false)]
        public string DensityPatchStatus
        {
            get
            {
                if (!ImpactConsequence.TryGetDensity(false, out double asteroid) ||
                    !ImpactConsequence.TryGetDensity(true, out double comet))
                    return Localizer.Format("#SENTRY_settings_densityUnavailable");
                return DescribeDensity(asteroid / StockSpaceObjectDensity, comet / StockSpaceObjectDensity);
            }
            // Deliberate no-op. DifficultyOptionsMenu silently skips any property that isn't
            // writable (decompiled: `if (!property.CanWrite) continue;`), so a get-only property
            // never appears at all. A label never calls this, and autoPersistance = false keeps
            // ParameterNode.Load from calling it either.
            set { }
        }

        // Turns the two live density multipliers (1.0 = stock) into the status text shown above.
        private static string DescribeDensity(double asteroidMult, double cometMult)
        {
            if (Math.Abs(asteroidMult - 1) < 0.01 && Math.Abs(cometMult - 1) < 0.01)
            {
                return Localizer.Format("#SENTRY_settings_densityStock");
            }
            else if (Math.Abs(asteroidMult - 30) < 0.01 && Math.Abs(cometMult - 30) < 0.01)
            {
                return Localizer.Format("#SENTRY_settings_densityPatched");
            }
            return Localizer.Format("#SENTRY_settings_densityCustom", asteroidMult.ToString("F1"), cometMult.ToString("F1"));
        }

        // No player-facing reputation-floor setting: the only floor is stock's own hard minimum,
        // -Reputation.RepRange (-1000) - not a SENTRY-specific value, so there's nothing here to
        // make tunable. See SentryScenario.ReportConfirmedImpact's reputation-application block.

        public static SentrySettings Instance
        {
            get
            {
                return HighLogic.CurrentGame != null
                    ? HighLogic.CurrentGame.Parameters.CustomParams<SentrySettings>()
                    : null;
            }
        }
    }
}
