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
            shown = UiPrefs.Sort == UiPrefs.SortMode.Class
                ? shown.OrderByDescending(r => r.ClassIndex).ThenBy(TimeToWatch).ToList()
                : shown.OrderBy(TimeToWatch).ToList();

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
                    DrawRow(r, now, day, homeBody);
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
            new GUIContent("Class", "Sort by object size class, biggest first (ties broken by time)")
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

            if (stopWarpIcon == null || audioIcon == null || alarmClockIcon == null || xOverlayIcon == null)
            {
                Debug.LogWarning("[SENTRY] Could not load one or more toggle icons from " + IconBasePath + " - toggle buttons will show blank.");
            }
        }

        private void DrawRow(ThreatRecord r, double now, double day, CelestialBody homeBody)
        {
            GUIStyle rowStyle = r.State == ThreatState.Impact ? impactRowStyle
                : r.State == ThreatState.CloseApproach ? approachRowStyle
                : nearPassRowStyle;
            GUILayout.BeginHorizontal(rowStyle);

            GUILayout.Label(string.IsNullOrEmpty(r.ObjectClass) ? "?" : r.ObjectClass, classStyle, GUILayout.Width(22f));

            string kind = r.IsComet ? "comet" : "asteroid";
            string bodyName = homeBody != null ? homeBody.name : "home";
            string eventText;
            switch (r.State)
            {
                case ThreatState.Impact:
                    eventText = string.Format("Impact in {0:F1} d, periapsis {1:F0} km",
                        (r.ImpactUT - now) / day, r.CapturePeA / 1000.0);
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

        private static void FocusVessel(Guid id)
        {
            Vessel v = FlightGlobals.FindVessel(id);
            if (v == null || v.mapObject == null) return;
            if (!MapView.MapIsEnabled) MapView.EnterMapView();
            PlanetariumCamera.fetch.SetTarget(v.mapObject);
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
        }
    }
}
