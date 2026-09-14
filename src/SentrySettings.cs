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

        public enum AbundancePreset { Low, Normal, High }

        [GameParameters.CustomParameterUI("Asteroid Abundance",
            toolTip = "How many untracked asteroids/comets the stock spawner keeps in flight at once. " +
                      "Only affects the stock spawner - has no effect if Kopernicus or Custom Asteroids is managing spawns.",
            unlockedDuringMission = true)]
        public AbundancePreset abundance = AbundancePreset.Normal;

        [GameParameters.CustomFloatParameterUI("Comet Approach Radius (x SOI)",
            toolTip = "How close a comet must pass, as a multiple of the home body's SOI radius, to trigger a close-approach alert.",
            minValue = 5f, maxValue = 100f, stepCount = 20, displayFormat = "F0", addTextField = true, unlockedDuringMission = true)]
        public float cometApproachSoiMultiple = 25f;

        [GameParameters.CustomFloatParameterUI("Impact Imminent Lead Time (hours)",
            toolTip = "How far before a predicted impact the final warning fires and the mod starts watching every frame for confirmation.",
            minValue = 0.5f, maxValue = 24f, stepCount = 48, displayFormat = "F1", addTextField = true, unlockedDuringMission = true)]
        public float impactImminentLeadHours = 3f;

        public static SentrySettings Instance
        {
            get
            {
                return HighLogic.CurrentGame != null
                    ? HighLogic.CurrentGame.Parameters.CustomParams<SentrySettings>()
                    : null;
            }
        }

        // Stock-spawner-only knob -
        // a no-op if the stock scenario isn't loaded, which is fine under Kopernicus/Custom
        // Asteroids (they have their own equivalent limits, untouched by this mod by design).
        public static void ApplyAbundance()
        {
            ScenarioDiscoverableObjects sdo = ScenarioDiscoverableObjects.Instance;
            SentrySettings s = Instance;
            if (sdo == null || s == null) return;

            // Raw numbers behind each tier live in AdvancedSettings (a hand-editable per-install
            // config file), not hardcoded here - the owner asked not to bury "arbitrary but
            // reasonable" tuning constants where only a rebuild can change them.
            AdvancedSettings.EnsureLoaded();
            switch (s.abundance)
            {
                case AbundancePreset.Low:
                    sdo.spawnOddsAgainst = AdvancedSettings.AbundanceLowSpawnOddsAgainst;
                    sdo.spawnGroupMinLimit = AdvancedSettings.AbundanceLowSpawnGroupMinLimit;
                    sdo.spawnGroupMaxLimit = AdvancedSettings.AbundanceLowSpawnGroupMaxLimit;
                    break;
                case AbundancePreset.High:
                    sdo.spawnOddsAgainst = AdvancedSettings.AbundanceHighSpawnOddsAgainst;
                    sdo.spawnGroupMinLimit = AdvancedSettings.AbundanceHighSpawnGroupMinLimit;
                    sdo.spawnGroupMaxLimit = AdvancedSettings.AbundanceHighSpawnGroupMaxLimit;
                    break;
                default: // Normal
                    sdo.spawnOddsAgainst = AdvancedSettings.AbundanceNormalSpawnOddsAgainst;
                    sdo.spawnGroupMinLimit = AdvancedSettings.AbundanceNormalSpawnGroupMinLimit;
                    sdo.spawnGroupMaxLimit = AdvancedSettings.AbundanceNormalSpawnGroupMaxLimit;
                    break;
            }
        }
    }
}
