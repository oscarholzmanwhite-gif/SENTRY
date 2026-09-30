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
