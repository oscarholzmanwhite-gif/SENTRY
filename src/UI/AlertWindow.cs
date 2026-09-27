using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sentry.UI
{
    // Loosely modeled on [x] Science!'s general idea - a small always-relevant panel of
    // checklist-style rows you can click to jump to the relevant object - but written from
    // scratch: no code or icon assets from that mod are reused, since it is CC BY-NC-SA licensed
    // and copying its actual code would put SENTRY's own files under that license's
    // NonCommercial/ShareAlike terms.
    public class AlertWindow : WindowBase
    {
        private Vector2 scrollPos;

        private GUIStyle impactRowStyle;
        private GUIStyle nearPassRowStyle;
        private GUIStyle approachRowStyle;
        private GUIStyle classStyle;
        private GUIStyle dangerRepStyle;
        private GUIStyle mildRepStyle;
        private GUIStyle rowLabelStyle;
        private GUIStyle headerStyle;
        private GUIStyle countsStyle;
        private bool stylesReady;

        // Icon toggles at the bottom of the window (owner's phase-4b refinement request), replacing
        // the earlier plain-text checkboxes. Loaded lazily via GameDatabase, same pattern as the
        // toolbar icons and the alert klaxon clip elsewhere in this mod.
        private const float ToggleIconSize = 36f;
        // NOT PluginData: KSP's GameDatabase (confirmed by decompiling UrlDir.Create) skips any
        // folder literally named "PluginData" while indexing GameData, so GetTexture() below would
        // always return null for anything stored there. (The toolbar icons stay under PluginData/
        // since ToolbarControl loads those itself, bypassing GameDatabase entirely - unaffected.)
        private const string IconBasePath = "SENTRY/Icons/";
        private Texture2D stopWarpIcon;
        private Texture2D audioIcon;
        private Texture2D alarmClockIcon;
        private Texture2D xOverlayIcon;
        // Optional - unlike the three toggles above, a missing rescan icon isn't logged as a
        // warning, since DrawIconToggleRow falls back to a plain text button ("Rescan") when this
        // is null rather than showing a blank icon. Expected at GameData/SENTRY/Icons/icon_rescan.png
        // - the other four icons in this folder are all 40x40 source PNGs (confirmed by reading
        // their PNG headers), drawn at ToggleIconSize (36) - Unity scales a texture to fit
        // whatever GUILayout.Width/Height it's given, so the source and draw sizes don't have to
        // match, but 40x40 keeps this one consistent with its neighbours. Not PluginData, per the
        // GameDatabase exclusion noted above - drop a PNG there and it's picked up with no code
        // change.
        private Texture2D rescanIcon;
        private bool iconsLoadAttempted;

        public AlertWindow() : base("SENTRY", 440f, 320f)
        {
            // Filters/sort/position are a player preference, not save data - load once and, if a
            // previous session left a window position/size, start there instead of re-centring.
            UiPrefs.EnsureLoaded();
            Rect saved = UiPrefs.WindowRect;
            if (!float.IsNaN(saved.width) && saved.width > 0f && !float.IsNaN(saved.height) && saved.height > 0f)
            {
                WindowRect = saved;
            }
        }

        protected override void OnHidden()
        {
            UiPrefs.WindowRect = WindowRect;
            UiPrefs.Save();
        }

        protected override void OnRectChanged(Rect rect)
        {
            UiPrefs.WindowRect = rect;
            UiPrefs.Save();
        }

        protected override void DrawContents()
        {
            EnsureStyles();
            EnsureIcons();

            SentryScenario scenario = SentryScenario.Instance;

            if (scenario == null)
            {
                GUILayout.Label("SENTRY isn't active in this scene.");
                return;
            }

            CelestialBody homeBody = FlightGlobals.GetHomeBody();
            double now = Planetarium.GetUniversalTime();
            double day = (homeBody != null && homeBody.solarDayLength > 0.0) ? homeBody.solarDayLength : 21600.0;

            DrawHeader(scenario, now, day);
            GUILayout.Space(4f);
            DrawFilterRow(scenario);
            DrawSortRow();
            GUILayout.Space(2f);

            List<ThreatRecord> all = scenario.Records.Where(r => r.State != ThreatState.Ignored).ToList();
            List<ThreatRecord> shown = all.Where(PassesFilter).ToList();

            // Each shown impactor's consequence estimate, computed once per draw and reused for
            // both "Danger" sorting and the row's own display, so an Impact-state row is never
            // computed twice in the same frame. Still fresh every draw - this dictionary itself is
            // rebuilt from scratch on every DrawContents call, nothing here survives past it - see
            // ImpactConsequence's "two independent calls, nothing precomputed ahead of time" design.
            Dictionary<Guid, ConsequenceReport> estimates = new Dictionary<Guid, ConsequenceReport>();
            foreach (ThreatRecord r in shown)
            {
                // Grazers (0 <= CapturePeA < atmosphere threshold, see ThreatRecord.IsGroundImpact)
                // get no estimate at all - showing a fake energy/rep number next to "will graze
                // atmosphere" would contradict the disclaimer DrawRow prints for them.
                if (r.State != ThreatState.Impact || !r.IsGroundImpact) continue;
                if (ImpactConsequence.TryGetPredictedState(r, homeBody, out ImpactState predicted))
                {
                    ConsequenceReport report = ImpactConsequence.Compute(r, predicted, homeBody);
                    if (report.Valid) estimates[r.VesselId] = report;
                }
            }

            if (UiPrefs.Sort == UiPrefs.SortMode.Class)
            {
                shown = shown.OrderByDescending(r => r.ClassIndex).ThenBy(TimeToWatch).ToList();
            }
            else if (UiPrefs.Sort == UiPrefs.SortMode.Danger)
            {
                // Rows with no estimate (fly-bys, comet approaches, or an Impact row the model
                // couldn't compute) sort to the bottom, not the top - they aren't currently a
                // known danger, which is the least dangerous case, not the most.
                shown = shown.OrderByDescending(r => estimates.TryGetValue(r.VesselId, out ConsequenceReport rep)
                        ? Math.Abs(rep.WouldBeReputationDelta) : double.NegativeInfinity)
                    .ThenBy(TimeToWatch).ToList();
            }
            else
            {
                shown = shown.OrderBy(TimeToWatch).ToList();
            }

            if (shown.Count == 0)
            {
                int hidden = all.Count - shown.Count;
                string msg = all.Count == 0
                    ? "No threats right now."
                    : string.Format("Nothing matches the current filters ({0} hidden).", hidden);
                GUILayout.FlexibleSpace();
                GUILayout.Label(msg);
                GUILayout.FlexibleSpace();
            }
            else
            {
                scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
                foreach (ThreatRecord r in shown)
                {
                    ConsequenceReport? estimate = estimates.TryGetValue(r.VesselId, out ConsequenceReport rep)
                        ? (ConsequenceReport?)rep : null;
                    DrawRow(r, now, day, homeBody, estimate);
                }
                GUILayout.EndScrollView();
            }

            // Icon toggles live at the bottom (owner's phase-4b request), not as text checkboxes
            // up top - see DrawIconToggleRow.
            GUILayout.Space(4f);
            DrawIconToggleRow(scenario);
        }

        // What a player opens this window for, first: is anything actually going to hit, and
        // when. Always visible regardless of the filters below - filters only affect the list.
        private void DrawHeader(SentryScenario scenario, double now, double day)
        {
            ThreatRecord nextImpact = scenario.Records
                .Where(r => r.State == ThreatState.Impact)
                .OrderBy(r => r.ImpactUT)
                .FirstOrDefault();

            if (nextImpact != null)
            {
                string text = string.Format("Next impact: {0} (class {1}) in {2:F1} d",
                    nextImpact.Name, nextImpact.ObjectClass, (nextImpact.ImpactUT - now) / day);
                if (GUILayout.Button(new GUIContent(text, "Click to focus this object in map view"), headerStyle))
                {
                    FocusVessel(nextImpact.VesselId);
                }
            }
            else
            {
                GUILayout.Label("No impact currently predicted.", headerStyle);
            }

            int impacts = scenario.Records.Count(r => r.State == ThreatState.Impact);
            int flyBys = scenario.Records.Count(r => r.State == ThreatState.NearPass);
            int approaches = scenario.Records.Count(r => r.State == ThreatState.CloseApproach);
            string lastScan = scenario.LastScanUT > double.NegativeInfinity
                ? string.Format("{0:F1} h ago", (now - scenario.LastScanUT) / 3600.0)
                : "pending";
            GUILayout.Label(string.Format("{0} impact(s), {1} fly-by(s), {2} comet approach(es) - last scan {3}",
                impacts, flyBys, approaches, lastScan), countsStyle);
        }

        private static readonly GUIContent[] SortOptions =
        {
            new GUIContent("Time", "Sort by time until the event"),
            new GUIContent("Class", "Sort by object size class, biggest first (ties broken by time)"),
            new GUIContent("Danger", "Sort by estimated reputation hit, worst first (ties broken by time)")
        };

        private void DrawFilterRow(SentryScenario scenario)
        {
            GUILayout.Label("Show:");
            GUILayout.BeginHorizontal();
            DrawFilterToggle(ref UiPrefs.ShowImpacts, "Impacts", "Objects predicted to enter the atmosphere/surface");
            bool flyBysBefore = UiPrefs.ShowFlyBys;
            DrawFilterToggle(ref UiPrefs.ShowFlyBys, "Fly-bys", "Objects that pass through the SOI but stay above the threshold altitude");
            if (UiPrefs.ShowFlyBys != flyBysBefore && scenario != null)
            {
                // Toggling this also decides whether fly-bys get auto-tracked (see
                // SentryScenario.UpdateTracking) - sync immediately rather than waiting for
                // a future scan, since a cached NearPass record can go a long time before its next
                // recompute.
                scenario.SyncFlyByTracking();
            }
            DrawFilterToggle(ref UiPrefs.ShowApproaches, "Comet approaches", "Comets passing close by without entering the SOI");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawFilterToggle(ref UiPrefs.ShowAsteroids, "Asteroids", "Show asteroids");
            DrawFilterToggle(ref UiPrefs.ShowComets, "Comets", "Show comets");
            GUILayout.EndHorizontal();
        }

        // Checkbox glyph and label are two separate GUILayout controls (rather than one
        // GUILayout.Toggle(bool, string) call) with the checkbox's size pinned explicitly - relying
        // on HighLogic.Skin's own toggle-style width/padding metrics for combined checkbox+text
        // auto-sizing was overlapping adjacent toggles, since that style wasn't designed for
        // GUILayout auto-flow. This sidesteps the skin's own layout assumptions entirely.
        private void DrawFilterToggle(ref bool value, string label, string description)
        {
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            bool before = value;
            string tooltip = description + " (currently " + (before ? "shown" : "hidden") + ")";
            bool after = GUILayout.Toggle(before, new GUIContent(string.Empty, tooltip), GUILayout.Width(18f), GUILayout.Height(18f));
            GUILayout.Space(8f); // breathing room between the checkbox glyph and its label
            GUILayout.Label(new GUIContent(label, tooltip), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.Space(10f);
            if (after != before)
            {
                value = after;
                UiPrefs.Save();
            }
        }

        private void DrawSortRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sort:", GUILayout.ExpandWidth(false));
            int before = (int)UiPrefs.Sort;
            int after = GUILayout.Toolbar(before, SortOptions, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (after != before)
            {
                UiPrefs.Sort = (UiPrefs.SortMode)after;
                UiPrefs.Save();
            }
        }

        private bool PassesFilter(ThreatRecord r)
        {
            bool stateOk = r.State == ThreatState.Impact ? UiPrefs.ShowImpacts
                : r.State == ThreatState.NearPass ? UiPrefs.ShowFlyBys
                : r.State == ThreatState.CloseApproach ? UiPrefs.ShowApproaches
                : true;
            bool kindOk = r.IsComet ? UiPrefs.ShowComets : UiPrefs.ShowAsteroids;
            return stateOk && kindOk;
        }

        // Icon toggle row: a fast-forward icon for "stop warp on impact discovery" (fires once, the
        // first time a course is classified as an impact - the terminal warp cut right before the
        // actual event is the stock Alarm Clock's job, see ArmAlarm/SentryScenario.Alert's
        // stopWarpEligible), a speaker icon for "audio alert", and a clock icon for "use
        // stock Alarm Clock".
        //
        // ALL THREE use the same mute-icon convention: the red X overlay means that feature is
        // OFF. The stop-warp button originally used the opposite reading ("X over fast-forward =
        // warp will be cut short"), which is defensible on its own but was the reverse of its two
        // neighbours - so with all three at their defaults every button showed no X, meaning "on"
        // for two of them and "off" for the third. Consistency beats per-icon cleverness here; don't re-invert one of them.
        //
        private void DrawIconToggleRow(SentryScenario scenario)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool stopWarpOff = !scenario.StopWarpEnabled;
            string warpTooltip = "Stop Warp on Impact Discovery: " + (stopWarpOff ? "Disabled" : "Enabled");
            if (IconToggle.Draw(stopWarpIcon, xOverlayIcon, stopWarpOff, warpTooltip, ToggleIconSize))
            {
                scenario.StopWarpEnabled = !scenario.StopWarpEnabled;
            }

            GUILayout.Space(12f);

            bool audioMuted = !scenario.AudioAlertEnabled;
            string audioTooltip = "Alarm Sound: " + (audioMuted ? "Disabled" : "Enabled");
            if (IconToggle.Draw(audioIcon, xOverlayIcon, audioMuted, audioTooltip, ToggleIconSize))
            {
                scenario.AudioAlertEnabled = !scenario.AudioAlertEnabled;
            }

            GUILayout.Space(12f);

            bool alarmClockOff = !scenario.UseAlarmClockEnabled;
            string alarmClockTooltip = "Use Stock Alarm Clock: " + (alarmClockOff ? "Disabled" : "Enabled");
            if (IconToggle.Draw(alarmClockIcon, xOverlayIcon, alarmClockOff, alarmClockTooltip, ToggleIconSize))
            {
                scenario.UseAlarmClockEnabled = !scenario.UseAlarmClockEnabled;
            }

            GUILayout.Space(12f);

            // A fire-once action, not a toggle - IconToggle's overlay/on-off machinery doesn't
            // apply, so this is a plain icon button. Falls back to a text label if the owner
            // hasn't dropped an icon file in yet (see rescanIcon's own comment for the path).
            const string rescanTooltip = "Rescan now";
            bool rescanClicked = rescanIcon != null
                ? GUILayout.Button(new GUIContent(rescanIcon, rescanTooltip), GUILayout.Width(ToggleIconSize), GUILayout.Height(ToggleIconSize))
                : GUILayout.Button(new GUIContent("Rescan", rescanTooltip));
            if (rescanClicked)
            {
                scenario.RequestScan(verbose: false);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void EnsureIcons()
        {
            if (iconsLoadAttempted) return;
            iconsLoadAttempted = true;

            stopWarpIcon = GameDatabase.Instance.GetTexture(IconBasePath + "toggle_stopwarp", false);
            audioIcon = GameDatabase.Instance.GetTexture(IconBasePath + "toggle_audio", false);
            alarmClockIcon = GameDatabase.Instance.GetTexture(IconBasePath + "toggle_alarmclock", false);
            xOverlayIcon = GameDatabase.Instance.GetTexture(IconBasePath + "toggle_x", false);
            rescanIcon = GameDatabase.Instance.GetTexture(IconBasePath + "icon_rescan", false); // optional, see field comment

            if (stopWarpIcon == null || audioIcon == null || alarmClockIcon == null || xOverlayIcon == null)
            {
                Debug.LogWarning("[SENTRY] Could not load one or more toggle icons from " + IconBasePath + " - toggle buttons will show blank.");
            }
        }

        // Threshold (absolute reputation points) above which the estimated rep hit in a row's
        // class column is colored red rather than the milder default - a plain visual flag for
        // "this one is worth paying attention to", not tied to any actual game-state clamp.
        private const double DangerRepThreshold = 10.0;

        private void DrawRow(ThreatRecord r, double now, double day, CelestialBody homeBody, ConsequenceReport? estimate)
        {
            GUIStyle rowStyle = r.State == ThreatState.Impact ? impactRowStyle
                : r.State == ThreatState.CloseApproach ? approachRowStyle
                : nearPassRowStyle;
            GUILayout.BeginHorizontal(rowStyle);

            // Class letter and its estimated reputation hit sit together in their own narrow
            // column, left of the main text block - owner's ask, so the two numbers that matter
            // most for "how bad is this one" are readable at a glance without opening the row.
            GUILayout.BeginVertical(GUILayout.Width(40f));
            GUILayout.Label(string.IsNullOrEmpty(r.ObjectClass) ? "?" : r.ObjectClass, classStyle);
            if (estimate.HasValue)
            {
                double repHit = Math.Abs(estimate.Value.WouldBeReputationDelta);
                GUIStyle repStyle = repHit >= DangerRepThreshold ? dangerRepStyle : mildRepStyle;
                // Stock KSP has no loadable reputation icon file (its star glyph is baked into a
                // Unity sprite atlas the compiled UI uses, not a loose GameData asset) - using the
                // Unicode star character instead, since IMGUI just renders whatever the active font
                // supports. Unverified whether HighLogic.Skin's font actually has this glyph - if
                // it renders as a blank box in-game, drop back to plain text and revisit with the
                // real stock icon instead.
                GUILayout.Label(string.Format("~{0:F0}★", repHit), repStyle);
            }
            GUILayout.EndVertical();

            string kind = r.IsComet ? "comet" : "asteroid";
            string bodyName = homeBody != null ? homeBody.name : "home";
            string eventText;
            switch (r.State)
            {
                case ThreatState.Impact:
                    // The disclaimer is the one thing that actually distinguishes these two cases
                    // to the player - both share ThreatState.Impact/the "Impacts" filter (owner's
                    // choice: a grazer still has the potential to become a real impact if loaded,
                    // since drag only applies to loaded vessels), but only a ground impact will
                    // ever actually happen unattended (see ThreatRecord.IsGroundImpact).
                    string disposition = r.IsGroundImpact ? "will impact surface" : "will graze atmosphere";
                    eventText = string.Format("Impact in {0:F1} d, periapsis {1:F0} km ({2})",
                        (r.ImpactUT - now) / day, r.CapturePeA / 1000.0, disposition);
                    // Live estimate, computed once per draw by DrawContents (never cached across
                    // draws) - never the same computation as the final report that fires at actual
                    // confirmed impact (see ImpactConsequence.TryGetPredictedState vs
                    // TryGetActualState). The rep number itself now lives in the class column
                    // above; kt stays here since it's still useful context for this specific row.
                    // Never populated for a grazer in the first place (see DrawContents) - showing
                    // a fake energy estimate right next to "will graze atmosphere" would contradict
                    // the disclaimer.
                    if (estimate.HasValue)
                    {
                        eventText += string.Format("\nEst. {0:F1} kt", estimate.Value.EnergyKtTnt);
                    }
                    break;
                case ThreatState.CloseApproach:
                    eventText = string.Format("Approach in {0:F1} d, {1:F1} Mm",
                        (r.ClosestApproachUT - now) / day, r.ClosestApproachDistance / 1e6);
                    break;
                default: // NearPass
                    eventText = string.Format("Fly-by in {0:F1} d, periapsis {1:F0} km",
                        (r.EntryUT - now) / day, r.CapturePeA / 1000.0);
                    break;
            }
            string text = string.Format("{0} ({1})\n{2} at {3}", r.Name, kind, eventText, bodyName);

            if (GUILayout.Button(new GUIContent(text, "Click to focus this object in map view"), rowLabelStyle, GUILayout.ExpandWidth(true)))
            {
                FocusVessel(r.VesselId);
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        private static double TimeToWatch(ThreatRecord r)
        {
            switch (r.State)
            {
                case ThreatState.Impact: return r.ImpactUT;
                case ThreatState.CloseApproach: return r.ClosestApproachUT;
                default: return r.EntryUT;
            }
        }

        // The Guid this button last focused, so a later click on a different row can clean up the
        // previous vessel's rendering state exactly the way selecting a new one does in stock code
        // - see below.
        private static Guid lastFocusedVesselId = Guid.Empty;

        private static void FocusVessel(Guid id)
        {
            Vessel v = FlightGlobals.FindVessel(id);
            if (v == null || v.mapObject == null) return;
            if (!MapView.MapIsEnabled) MapView.EnterMapView();

            // PlanetariumCamera.SetTarget alone only moves the camera - it does NOT reproduce a
            // real map-icon double-click's full trajectory line (SOI-transition/encounter markers).
            // Confirmed by decompiling KSP.UI.Screens.SpaceTracking.SetVessel (the method a real
            // Tracking Station row click runs): that also sets the vessel's own
            // OrbitRenderer.isFocused and, if the object is tracked, attaches a patched-conics
            // solver via Vessel.AttachPatchedConicsSolver() - neither of which SetTarget touches.
            // Replicating just those two calls here (not the rest of SetVessel, which is Tracking-
            // Station-UI-specific: widget list highlighting, button locking) is what actually turns
            // the detailed line on.
            if (lastFocusedVesselId != Guid.Empty && lastFocusedVesselId != id)
            {
                Vessel previous = FlightGlobals.FindVessel(lastFocusedVesselId);
                if (previous != null && previous.orbitRenderer != null)
                {
                    previous.orbitRenderer.isFocused = false;
                    previous.orbitRenderer.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                    previous.DetachPatchedConicsSolver();
                }
            }

            PlanetariumCamera.fetch.SetTarget(v.mapObject);
            if (v.orbitRenderer != null) v.orbitRenderer.isFocused = true;
            if (v.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.StateVectors) && !v.PatchedConicsAttached)
            {
                v.AttachPatchedConicsSolver();
            }
            lastFocusedVesselId = id;
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;

            classStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };

            rowLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperLeft };

            headerStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = true };

            countsStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(9, GUI.skin.label.fontSize - 2) };
            countsStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);

            impactRowStyle = new GUIStyle(GUI.skin.box);
            impactRowStyle.normal.textColor = new Color(1f, 0.55f, 0.45f);

            nearPassRowStyle = new GUIStyle(GUI.skin.box);
            nearPassRowStyle.normal.textColor = new Color(1f, 0.92f, 0.55f);

            approachRowStyle = new GUIStyle(GUI.skin.box);
            approachRowStyle.normal.textColor = new Color(0.55f, 0.8f, 1f);

            dangerRepStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold };
            dangerRepStyle.normal.textColor = new Color(1f, 0.3f, 0.3f);

            mildRepStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };
            mildRepStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }
    }
}
