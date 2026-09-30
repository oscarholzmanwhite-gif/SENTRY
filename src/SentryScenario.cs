using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Sentry.UI;
using KSP.Localization;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Sentry
{
    // The persistent heart of the mod. Runs in every scene where the game itself runs
    // the asteroid spawner (space centre, tracking station, flight), keeps one ThreatRecord per
    // discovered object, rescans every few in-game hours, and raises alerts on state changes.
    //
    // The [KSPScenario] attribute tells KSP to create this object for every save (new or existing)
    // in the listed scenes and to call OnLoad/OnSave with our own ConfigNode inside the .sfs.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.FLIGHT)]
    public class SentryScenario : ScenarioModule
    {
        public static SentryScenario Instance { get; private set; }

        // Tunables - the ones a player might reasonably want to change live in
        // SentrySettings (GameParameters). Engineering-only constants - not really
        // player preferences, but occasionally worth hand-tuning without a rebuild - live in
        // AdvancedSettings instead (a plain per-install config file; see that class).
        private const double ScanIntervalSeconds = 3.0 * 3600.0;    // 3 in-game hours
        // Kill switch for all auto-track/auto-untrack behaviour below (see UpdateTracking). Not
        // exposed as a player setting - just a code-level escape hatch.
        private const bool AutoTrackEnabled = true;
        // Fallbacks used only if SentrySettings.Instance is somehow null (e.g. no game
        // loaded yet) - match the settings screen's own field defaults.
        private const double DefaultCometApproachSoiMultiple = 25.0;
        private const double DefaultImpactImminentLeadSeconds = 3.0 * 3600.0;

        // Comets never get aimed at the home body the way asteroids do (verified by decompiling
        // CometOrbitType.CalculateOrbit - it builds a random Sun-centred ellipse, not a flyby of any
        // particular body), so a comet SOI encounter is rare. A wider "will pass within N x R_SOI"
        // alert catches the ones worth knowing about for capture missions or just spectacle, without
        // needing a real encounter. Read live, never cached.
        private static double CometApproachSoiMultiple
        {
            get
            {
                SentrySettings s = SentrySettings.Instance;
                return s != null ? (double)s.cometApproachSoiMultiple : DefaultCometApproachSoiMultiple;
            }
        }

        // How long before the predicted impact UT we switch a record from the normal 3-hour scan
        // cadence to per-frame watching (see WatchImminentImpacts) for both the "Impact imminent"
        // reminder and the "confirmed by disappearance" alert. Doesn't need to precisely lead the
        // stock alarm's own warp ramp-down (that runs on its own frame-by-frame schedule regardless
        // of our scan cadence) - this just needs to be comfortably wider than one scan interval so
        // a record is already under fast watch well before the moment it matters. Read live, 
        // never cached.
        private static double ImpactImminentLeadSeconds
        {
            get
            {
                SentrySettings s = SentrySettings.Instance;
                return s != null ? s.impactImminentLeadHours * 3600.0 : DefaultImpactImminentLeadSeconds;
            }
        }

        private readonly Dictionary<Guid, ThreatRecord> records = new Dictionary<Guid, ThreatRecord>();
        private double lastScanUT = double.NegativeInfinity;
        private float lastScanRealTime = float.NegativeInfinity;
        private bool scanRunning;
        private bool scanRequested;
        private bool verboseRequested;
        private float sceneStartRealTime;
        private bool audioAlertEnabled = true;
        private bool stopWarpEnabled = true;
        // Creates a stock Alarm Clock entry for each impactor so KSP's own warp
        // deceleration protects the actual impact moment, not just the moment it's first
        // predicted (see AlarmClockIntegration). Default on, matching the other two toggles.
        private bool useAlarmClockEnabled = true;

        // The worst facility-damage tier owed but not yet applied - set in ReportConfirmedImpact's
        // confirmed branch, cleared once TryApplyPendingFacilityDamage actually demolishes
        // buildings. DestructibleBuilding instances only exist as live GameObjects in the
        // SPACECENTER scene (they're 3D scene props) - a confirmed impact almost always fires from
        // Flight/TrackingStation instead, where there's nothing live to call .Demolish() on yet.
        // Rather than poke ScenarioDestructibles' internal persisted ConfigNode format directly
        // (unverified without a live repro, and risks corrupting its damage-tracking state if
        // guessed wrong), this queues the WORST tier seen (Math.Max, since a building can only be
        // demolished once - a second, smaller catastrophe before the first is ever applied
        // shouldn't downgrade what's owed) and applies it the next time this scenario is actually
        // running in SPACECENTER with buildings registered. Purely a display/flavor consequence -
        // there is no urgency to applying it before the player can even see KSC again.
        private ConsequenceReport.FacilityTier pendingFacilityTier = ConsequenceReport.FacilityTier.None;

        public IEnumerable<ThreatRecord> Records { get { return records.Values; } }
        public double LastScanUT { get { return lastScanUT; } }
        public bool ScanRunning { get { return scanRunning; } }
        public bool AudioAlertEnabled { get { return audioAlertEnabled; } set { audioAlertEnabled = value; } }
        public bool StopWarpEnabled { get { return stopWarpEnabled; } set { stopWarpEnabled = value; } }
        public bool UseAlarmClockEnabled
        {
            get { return useAlarmClockEnabled; }
            set
            {
                if (useAlarmClockEnabled == value) return;
                useAlarmClockEnabled = value;
                SyncAlarmsToEnabledState();
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------

        public override void OnAwake()
        {
            Instance = this;
            sceneStartRealTime = Time.realtimeSinceStartup;
            AdvancedSettings.EnsureLoaded();
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onAsteroidSpawned.Add(OnAsteroidSpawned);
            GameEvents.onCometSpawned.Add(OnCometSpawned);
        }

        private void OnDestroy()
        {
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onAsteroidSpawned.Remove(OnAsteroidSpawned);
            GameEvents.onCometSpawned.Remove(OnCometSpawned);
            if (Instance == this) Instance = null;
        }

        // A brand-new untracked rock from the stock spawner (DiscoverableObjectsUtil.SpawnAsteroid,
        // decompile-confirmed to fire this the moment the ProtoVessel is created). Requesting a scan
        // right away - rather than waiting for the next ScanIntervalSeconds (3 game hours) tick -
        // means it gets a ThreatRecord and a real prediction almost immediately instead of only on
        // the next periodic scan or a manual rescan.
        private void OnAsteroidSpawned(Vessel v)
        {
            RequestScan(false);
        }

        // Fires for both a fresh stock-spawned comet AND each fragment of a comet that just broke
        // apart under atmospheric stress (DiscoverableObjectsUtil.SpawnComet, decompile-confirmed -
        // CometManager.SpawnCometFragment calls the same method once per fragment). Same immediate-
        // scan reasoning as OnAsteroidSpawned above - the owner reported a fragmenting comet's
        // pieces going completely unnoticed unless the player happened to hit the manual rescan
        // button in the few seconds before they, too, were destroyed.
        //
        // Also detects the fragment case specifically, by name: CometManager.SpawnCometFragment
        // (decompiled) names each piece "<parent GetDisplayName()>-A", "-B", "-C", ... and
        // Vessel.GetDisplayName() is just Localizer.Format(vesselName) - a no-op substitution for a
        // plain (non-tag) string, so this is effectively matching against vesselName itself. Per the
        // decompiled ordering in ModuleComet/Part.explode(), the parent's part.explode() -> Die()
        // already runs BEFORE SpawnCometFragments spawns any fragment - so by the time this fires for
        // the first fragment, the parent vessel is already gone, but its ThreatRecord is (almost
        // certainly) still in `records`, since our own Update()/WatchImminentImpacts hasn't
        // necessarily run yet this frame. Flagging it here, synchronously, beats that race reliably.
        private void OnCometSpawned(Vessel v)
        {
            RequestScan(false);
            if (v == null || string.IsNullOrEmpty(v.vesselName)) return;

            string name = v.vesselName;
            if (name.Length < 3 || name[name.Length - 2] != '-') return;
            char suffix = name[name.Length - 1];
            if (suffix < 'A' || suffix > 'Z') return;
            string parentName = name.Substring(0, name.Length - 2);

            foreach (ThreatRecord candidate in records.Values)
            {
                if (candidate.IsComet && candidate.Name == parentName)
                {
                    candidate.FragmentedNotImpacted = true;
                    break;
                }
            }
        }

        // ---- capture (clawed asteroids) ---------------------------------------------------------

        // Fires when two vessels merge - docking, or (the case that matters here) an Advanced
        // Grabbing Unit clawing onto an asteroid or comet.
        //
        // Timing is the whole reason this is hooked rather than inferred from a later scan:
        // Part.Couple fires this event FIRST, while both vessels are still
        // intact, and only then destroys `from`'s vessel and moves every part onto `to`'s. This is
        // therefore the one moment where the asteroid's own Guid and the surviving vessel's Guid
        // are both readable. onPartCoupleComplete is too late - the asteroid's vessel can already
        // be gone by then, leaving nothing to match a record against.
        //
        // Which side survives is NOT fixed: ModuleGrappleNode branches on `dockerSide` between
        // base.part.Couple(other) (the asteroid's vessel survives) and other.Couple(base.part)
        // (the ship's survives), so both cases genuinely occur. The one invariant from
        // Part.Couple is that `to`'s vessel is always the survivor.
        private void OnPartCouple(GameEvents.FromToAction<Part, Part> action)
        {
            if (action.from == null || action.to == null) return;
            Vessel fromV = action.from.vessel;
            Vessel toV = action.to.vessel;
            if (fromV == null || toV == null || toV.id == Guid.Empty) return;

            ThreatRecord rec;
            if (records.TryGetValue(fromV.id, out rec)) HandleCaptured(rec, fromV.id, toV);
            else if (records.TryGetValue(toV.id, out rec)) HandleCaptured(rec, toV.id, toV);
        }

        // A watched object has been clawed onto a player craft.
        // What does change is the noise, not the bookkeeping: the player is flying the thing, so
        // the routine alerts (predicted / revised / imminent / all-clear) and the forced warp stop
        // are suppressed for it. The stock Alarm Clock entry is deliberately KEPT - it's silent
        // anyway, and it's what stops high warp from skipping clean over the impact moment.
        private void HandleCaptured(ThreatRecord rec, Guid oldId, Vessel survivor)
        {
            bool alreadyKnown = rec.Captured;
            rec.Captured = true;

            // Synchronous, one-shot telemetry sample taken right here, at the exact moment of the
            // grapple - not deferred to the next WatchImminentImpacts frame. A violent enough
            // collision can destroy the merged vessel within the same frame as the couple itself,
            // before Update() ever runs again; without this, that scenario would leave zero
            // telemetry (not even the atmDensity fallback) and the confirmed-impact report would be
            // missed or delayed to the next scan's coarser backstop instead of firing immediately.
            SampleTelemetry(rec, survivor, Planetarium.GetUniversalTime(), FlightGlobals.GetHomeBody());

            if (survivor.id != oldId)
            {
                // The ship survived, so the asteroid's own Guid is about to be destroyed. Move the
                // record onto the surviving craft, or the next scan would prune it - and, worse, a
                // grapple inside the impact-imminent window would be read as the vessel vanishing
                // and reported as a confirmed impact for a rock the player just caught.
                records.Remove(oldId);

                ThreatRecord existing;
                if (records.TryGetValue(survivor.id, out existing) && existing != rec)
                {
                    // The surviving craft already carries a record - e.g. a second rock clawed onto
                    // the same craft. One vessel can only have one record, so keep whichever is the
                    // more serious threat (ThreatState ascends Ignored < CloseApproach < NearPass <
                    // Impact) and retire the other's alarm so it can't outlive its record.
                    if (rec.State <= existing.State)
                    {
                        DisarmAlarm(rec);
                        existing.Captured = true;
                        return;
                    }
                    DisarmAlarm(existing);
                }

                rec.VesselId = survivor.id;
                records[survivor.id] = rec;
            }

            if (!alreadyKnown)
            {
                AlertLog.Info(string.Format(
                    "{0} has been captured by {1}; still watched and still counts as the same object, but its routine alerts are now suppressed.",
                    Describe(rec), survivor.vesselName));
            }
        }

        // True if this vessel still physically contains an asteroid or comet part. Used to decide
        // when a captured record stops being one: once the rock is released (undocked/decoupled) or
        // its part is destroyed, the host craft is of no further interest, and a released rock
        // becomes a brand-new vessel with a new Guid that gets rediscovered from scratch.
        //
        // Handles loaded and unloaded vessels separately: an unloaded vessel has no live Part
        // objects at all (and no part MODULEs either),
        // but its ProtoPartSnapshots still carry the part name, which is all this needs.
        private static bool HasSpaceObjectPart(Vessel v)
        {
            if (v.loaded && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && p.partInfo != null && IsSpaceObjectPartName(p.partInfo.name)) return true;
                }
                return false;
            }

            if (v.protoVessel != null && v.protoVessel.protoPartSnapshots != null)
            {
                for (int i = 0; i < v.protoVessel.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot s = v.protoVessel.protoPartSnapshots[i];
                    if (s != null && IsSpaceObjectPartName(s.partName)) return true;
                }
            }
            return false;
        }

        // Part names, not part modules: unloaded objects have no modules to read.
        // PotatoRoid = asteroid, PotatoComet = comet.
        private static bool IsSpaceObjectPartName(string name)
        {
            return name == "PotatoRoid" || name == "PotatoComet";
        }

        // Finds the actual asteroid/comet Part within a loaded vessel, by name rather than by
        // index - v.parts[0] is only safe to assume for a loose space object, never for a captured
        // (clawed) one, where the merged vessel's part order depends on which side of the couple
        // survived (see HandleCaptured/OnPartCouple) and can just as easily put the player's own
        // command pod at index 0. Used by real-mass capture (BeginCaptureRealMass/
        // PollRealMassCapture and WatchImminentImpacts' "already loaded" branch) so a clawed
        // asteroid's mass reading is never accidentally the grappling craft's instead - the owner's
        // own decision on captured objects ("it's the same asteroid after all, only maybe a bit
        // heavier") depends on this being right.
        private static Part FindSpaceObjectPart(Vessel v)
        {
            if (v == null || v.parts == null) return null;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p != null && p.partInfo != null && IsSpaceObjectPartName(p.partInfo.name)) return p;
            }
            return null;
        }

        public override void OnLoad(ConfigNode node)
        {
            records.Clear();
            node.TryGetValue("lastScanUT", ref lastScanUT);
            node.TryGetValue("audioAlertEnabled", ref audioAlertEnabled);
            node.TryGetValue("stopWarpEnabled", ref stopWarpEnabled);
            node.TryGetValue("useAlarmClockEnabled", ref useAlarmClockEnabled);
            int pendingFacilityTierInt = 0;
            node.TryGetValue("pendingFacilityTier", ref pendingFacilityTierInt);
            pendingFacilityTier = (ConsequenceReport.FacilityTier)pendingFacilityTierInt;
            foreach (ConfigNode child in node.GetNodes("THREAT"))
            {
                ThreatRecord r = ThreatRecord.Load(child);
                if (r != null) records[r.VesselId] = r;
            }
            Debug.Log(string.Format("[SENTRY] Scenario loaded: {0} records, lastScanUT={1:F0}", records.Count, lastScanUT));
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("lastScanUT", lastScanUT);
            node.AddValue("audioAlertEnabled", audioAlertEnabled);
            node.AddValue("stopWarpEnabled", stopWarpEnabled);
            node.AddValue("useAlarmClockEnabled", useAlarmClockEnabled);
            node.AddValue("pendingFacilityTier", (int)pendingFacilityTier);
            foreach (ThreatRecord r in records.Values)
            {
                r.Save(node.AddNode("THREAT"));
            }
        }

        private void Update()
        {
            // Cheap, every-frame, every-scene - see AlarmClockIntegration.SyncWarpSafetyMultiplier's
            // own comment for what this controls (how snappy the stock Alarm Clock's warp ramp-down
            // feels) and why it has to run every frame rather than once.
            AlarmClockIntegration.SyncWarpSafetyMultiplier();

            // Only ever anything to do here in the Space Center scene, where DestructibleBuilding
            // GameObjects actually exist - see pendingFacilityTier's own comment. Runs every frame
            // while pending rather than once, since a scene can take a moment after load for every
            // building's own OnAwake/RegisterInstance to finish.
            if (pendingFacilityTier != ConsequenceReport.FacilityTier.None
                && HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                TryApplyPendingFacilityDamage();
            }

            // NOTE: deliberately not gating on FlightGlobals.ready. FlightGlobals.Vessels (the persistent vessel list, loaded with
            // the save) is what we actually need, and it's populated regardless of scene.
            if (FlightGlobals.Vessels == null) return;
            if (Time.realtimeSinceStartup - sceneStartRealTime < AdvancedSettings.FirstScanDelayRealSeconds) return;

            double now = Planetarium.GetUniversalTime();

            // Runs every frame, independent of the heavy scan below and its scanRunning/interval
            // gating - see WatchImminentImpacts for why.
            WatchImminentImpacts(now);

            if (scanRunning) return;
            bool intervalElapsed = now - lastScanUT >= ScanIntervalSeconds || now < lastScanUT; // second clause: save loaded from an earlier UT
            bool realTimeGapElapsed = Time.realtimeSinceStartup - lastScanRealTime >= AdvancedSettings.MinScanGapRealSeconds;
            bool due = intervalElapsed && realTimeGapElapsed;
            // A captured rock being flown changes orbit by the second, under the player's own
            // control - see AdvancedSettings.CapturedRescanRealSeconds for why the 3-game-hour
            // interval misses a deflection burn entirely at 1x warp.
            bool capturedDue = !scanRequested && !due
                && Time.realtimeSinceStartup - lastScanRealTime >= AdvancedSettings.CapturedRescanRealSeconds
                && AnyCapturedVesselLoaded();
            if (scanRequested || due || capturedDue)
            {
                bool verbose = verboseRequested;
                scanRequested = false;
                verboseRequested = false;
                lastScanRealTime = Time.realtimeSinceStartup;
                StartCoroutine(ScanRoutine(verbose, quietSummary: capturedDue));
            }
        }

        private bool AnyCapturedVesselLoaded()
        {
            foreach (ThreatRecord rec in records.Values)
            {
                if (!rec.Captured) continue;
                Vessel v = FlightGlobals.FindVessel(rec.VesselId);
                if (v != null && v.loaded) return true;
            }
            return false;
        }

        // The full scan (ScanRoutine) only runs every ScanIntervalSeconds of GAME time (3 hours),
        // which is fine for orbit recomputation but far too coarse for the tail end of an impact:
        // once the stock Alarm Clock has ramped warp down to 1x for the event (see ArmAlarm), an
        // impactor's vessel can disappear and the mod wouldn't notice - and the player wouldn't
        // hear about it - until up to another 3 in-game (and, since warp is now low, REAL) hours
        // pass. This runs every frame instead for the handful of records actually close to their
        // predicted impact, so both the "Impact imminent" reminder and the "confirmed by
        // disappearance" alert land close to the real moment rather than on the next scan's
        // schedule. Cheap: the early-exit below means it's a no-op for every record until it's
        // genuinely close to impact, which is normally zero or one record at a time.
        private void WatchImminentImpacts(double now)
        {
            if (records.Count == 0) return;
            List<Guid> confirmedGone = null;
            CelestialBody home = FlightGlobals.GetHomeBody();

            foreach (ThreatRecord rec in records.Values)
            {
                // A captured (clawed) object is being actively piloted, so its fate is decided by
                // the player's own flying, not by SoiIntersection.Predict's periodic (~3 game hour)
                // re-classification - which itself assumes a stable, unperturbed Keplerian orbit,
                // not remotely true once real thrust or atmospheric drag is acting on a loaded
                // vessel. A player can dive a captured asteroid into the atmosphere and destroy it
                // within minutes, far faster than the next scan could ever reclassify rec.State as
                // Impact - so a captured record is watched unconditionally here, regardless of its
                // (possibly stale, possibly still NearPass/Ignored from before the dive) last-known
                // ThreatState, purely so a sudden destructive reentry the periodic scan never had a
                // chance to see coming still gets caught and reported the instant it happens, not
                // missed entirely or left to HandleDisappearance's much slower per-scan backstop.
                if (!rec.Captured)
                {
                    if (rec.State != ThreatState.Impact) continue;

                    // A genuine predicted ground impact is watched once its fast-watch window opens,
                    // anchored on GroundImpactUT (the real final event - see ArmAlarm). A grazer
                    // (IsGroundImpact false) has no such window at all under the naive drag-free
                    // unloaded model - it's never predicted to hit anything - but once the player loads
                    // it, real drag/heating applies, and it can explode well before reaching its
                    // predicted, drag-free periapsis.
                    // That disappearance-within-the-atmosphere is real evidence the naive prediction
                    // couldn't see coming, so a grazer observed loaded even once gets watched from then
                    // on (GrazeWatchActive latches and never turns off, so the frame it later
                    // disappears - possibly no longer "loaded" by then - still reaches the confirmed-
                    // impact check below instead of short-circuiting here just because it unloaded/
                    // vanished).
                    bool groundImpactWindowOpen = rec.IsGroundImpact && !double.IsNaN(rec.GroundImpactUT)
                        && now >= rec.GroundImpactUT - ImpactImminentLeadSeconds;

                    if (!groundImpactWindowOpen && !rec.GrazeWatchActive)
                    {
                        Vessel candidate = FlightGlobals.FindVessel(rec.VesselId);
                        if (candidate != null && candidate.loaded) rec.GrazeWatchActive = true;
                        else continue;
                    }
                }

                Vessel v = FlightGlobals.FindVessel(rec.VesselId);

                // A captured (clawed) object's own space-object part can be destroyed (e.g.
                // overheating on reentry) without destroying the whole merged vessel at all:
                // Part.Die() (decompiled) only calls vessel.Die() when the dying part is the
                // vessel's ROOT part. ModuleGrappleNode's couple can land root on EITHER side
                // (see HandleCaptured) - in the branch where the ship's own vessel/part survived
                // as root, the asteroid/comet part is just a non-root attachment, so losing it
                // only detaches that part; the ship (same Guid) sails on untouched.
                // FlightGlobals.FindVessel(rec.VesselId) then keeps finding a perfectly valid
                // vessel forever, so this record would never be reported as gone at all - a real,
                // silent miss (owner's report: "asteroid overheats while being controlled and
                // disappears, SENTRY doesn't catch it"). HasSpaceObjectPart (already used by
                // ScanRoutine's own much slower per-scan version of this same check) catches both
                // this case and a deliberate release/undock (which spins the rock off as a
                // brand-new vessel with its own Guid, rediscovered fresh next scan) - collapsing
                // either into "gone" here is safe because ReportConfirmedImpact's own evidence
                // checks (telemetry / WasInAtmosphere / HasLanded) already correctly read a benign
                // release as "uncertain," never a false confirmed impact.
                if (rec.Captured && v != null && !HasSpaceObjectPart(v)) v = null;

                // A landed/splashed object has already resolved - whatever happened (a hard
                // landing, a player-executed soft landing/redirect), it's sitting still, not
                // still plunging toward the surface. Exempt it immediately rather than waiting
                // for the next full scan 
                if (v != null && v.LandedOrSplashed)
                {
                    HandleLanded(rec, v, now, home);
                    continue;
                }

                // Suppressed for a captured (clawed) object - the player is flying it straight at
                // the ground, deliberately or not, and doesn't need telling. The stock alarm still
                // ramps warp down so the moment itself stays observable, and the disappearance
                // report below still fires: that's the one that matters, since it's where the
                // impact is actually recorded. Also suppressed for a grazer - nothing is actually
                // "imminent" under the naive prediction; if it's genuinely about to explode, the
                // confirmed-impact report below is what matters and fires the instant it happens.
                if (!rec.ImminentAlertFired && !rec.Captured && rec.IsGroundImpact)
                {
                    rec.ImminentAlertFired = true;
                    // stopWarpEligible: false - the stock Alarm Clock (armed in ApplyResult) is what
                    // actually halts warp for the real event; this is just the player-facing "why"
                    // reminder. Only the original discovery alert below forces warp on its own.
                    AlertLog.Alert(Localizer.Format("#SENTRY_title_impactImminent"),
                        Localizer.Format("#SENTRY_msg_impactImminent", Describe(rec), home != null ? home.name : "the home body"),
                        severe: true, stopWarpEligible: false);
                }

                if (v != null)
                {
                    // One-shot, at the start of the fast-watch window: force-load the vessel to
                    // trigger ModuleAsteroid/ModuleComet.OnStart's real procedural mass generation.
                    // Begins the load here; PollRealMassCapture (called every frame below while
                    // pending) waits for Part.started before reading the real mass and unloading -
                    // see that method for why a single synchronous Load-then-read doesn't work.
                    if (!rec.RealMassAttempted)
                    {
                        rec.RealMassAttempted = true;
                        Part alreadyLoadedPart = v.loaded ? FindSpaceObjectPart(v) : null;
                        if (alreadyLoadedPart != null)
                        {
                            // Already loaded for some other reason (a grazer the player is flying
                            // near, or a genuine impact - captured or not - the player happened to
                            // be watching) - Part.Start() has necessarily already run by now, so the
                            // real mass is available immediately with no force-load/poll dance
                            // needed at all. Found by part name (FindSpaceObjectPart), not
                            // v.parts[0] - a captured object's merged vessel can put the player's
                            // own craft's part at index 0 instead.
                            rec.RealMassKg = alreadyLoadedPart.mass * 1000.0;
                            Debug.Log(string.Format("[SENTRY] Captured real mass for {0}: {1:F1} kg (vessel already loaded)",
                                Describe(rec), rec.RealMassKg));
                        }
                        else
                        {
                            BeginCaptureRealMass(rec, v);
                        }
                    }
                    else if (rec.RealMassCapturePending)
                    {
                        PollRealMassCapture(rec, v);
                    }

                    SampleTelemetry(rec, v, now, home);

                    continue;
                }

                ReportConfirmedImpact(rec, home);
                DisarmAlarm(rec);
                if (confirmedGone == null) confirmedGone = new List<Guid>();
                confirmedGone.Add(rec.VesselId);
            }

            // Removed after the loop, not during: records is a Dictionary and we were iterating
            // its Values.
            if (confirmedGone != null)
            {
                foreach (Guid id in confirmedGone) records.Remove(id);
            }
        }

        // Caches the last state actually observed for a still-findable vessel - the instant it
        // disappears we can no longer query it, so this is the only evidence available to tell a
        // real high-speed impact apart from the vessel being recovered, destroyed, or successfully
        // soft-landed. Extracted from WatchImminentImpacts' per-frame loop so HandleCaptured can
        // also call it synchronously, once, at the exact moment of a claw grapple - a captured
        // vessel can in rare cases be destroyed (e.g. a violent enough collision) within the same
        // frame as the couple itself, before WatchImminentImpacts ever gets a chance to run even
        // once, which would otherwise leave zero telemetry (not even the atmDensity fallback) for
        // ReportConfirmedImpact to work with and the confirmed-impact report would be missed or
        // delayed to the next scan's much coarser backstop.
        //
        // Deliberately NOT Vessel.altitude/Vessel.srfSpeed: both are only recomputed inside a block
        // gated on FlightGlobals.ready, which (per the phase-3 lesson already in this file) is
        // false in every scene this mod actually needs to work in except active FLIGHT on the
        // object itself.
        //
        // Also NOT Orbit.GetVel(): it returns velocity relative to FlightGlobals.ActiveVessel's own
        // main body's frame, not relative to this orbit's own referenceBody - only correct when
        // called on the active vessel's own orbit (where those two bodies happen to be the same),
        // garbage for any other orbit, including every case that matters here (an unpiloted
        // asteroid; no active vessel at all in Space Center/Tracking Station). Also not Orbit.pos/
        // Orbit.vel or getRFrmVelOrbit (which reads Orbit.pos internally) - both are cached fields,
        // not guaranteed fresh for "now" on an unloaded on-rails object between orbit-driver
        // updates, which is exactly the kind of staleness this project's own SOI-intersection code
        // already avoids by never trusting cached orbit state.
        //
        // So: reconstructed by hand from getRelativePositionAtUT(now)/getOrbitalVelocityAtUT(now) -
        // the same UT-explicit, metre-accurate source SoiIntersection.cs already trusts - combined
        // with CelestialBody.angularVelocity, mirroring exactly what getRFrmVelOrbit computes
        // (Cross(angularVelocity, pos.xzy)) but from a guaranteed-fresh position instead of a
        // possibly-stale cached one. Only sampled once the object's orbit has actually transitioned
        // to home as its reference body (i.e. really is Kerbin-relative by now) - before that,
        // position/velocity would be relative to the Sun instead, which would be self-consistent
        // but meaningless as an "altitude"/"surface speed".
        private static void SampleTelemetry(ThreatRecord rec, Vessel v, double now, CelestialBody home)
        {
            if (home != null && v.orbit != null && v.orbit.referenceBody == home)
            {
                Vector3d relPos = v.orbit.getRelativePositionAtUT(now);
                Vector3d relVel = v.orbit.getOrbitalVelocityAtUT(now);
                rec.LastKnownAltitude = relPos.magnitude - home.Radius;
                Vector3d rotFrameVel = Vector3d.Cross(home.angularVelocity, relPos.xzy);
                rec.LastKnownSurfaceSpeed = (relVel.xzy - rotFrameVel).magnitude;

                // Same relPos/relVel, cached for ImpactConsequence's final report - see
                // ThreatRecord.LastKnownRelPos/RelVel/SampleUT.
                rec.LastKnownRelPos = relPos;
                rec.LastKnownRelVel = relVel;
                rec.LastKnownSampleUT = now;
            }

            // Independent fallback evidence, deliberately NOT gated on the same
            // v.orbit.referenceBody == home condition as the block above (or on that block
            // succeeding at all) - Vessel.atmDensity is set every physics tick by this vessel's own
            // FlightIntegrator (confirmed by decompiling it), for any loaded vessel, independent of
            // SENTRY's own orbit math. A destructive reentry can apparently sometimes outrun our
            // own per-frame sampling above (e.g. capturing an asteroid already mid-descent,
            // destroyed before a single successful LastKnownAltitude reading) - this is what lets
            // ReportConfirmedImpact still treat that as a confirmed impact instead of "no recent
            // sample" uncertainty.
            if (v.atmDensity > 0.0) rec.WasInAtmosphere = true;
        }

        private static double ThresholdAltitude(CelestialBody home)
        {
            return home != null && home.atmosphere ? home.atmosphereDepth : 0.0;
        }

        // Force-loads an unloaded vessel so its part's PartModules will (eventually) run OnStart() -
        // which is where ModuleAsteroid/ModuleComet actually generate the object's real procedural
        // mesh and compute its exact mass (asteroidMass = paGenerated.volume * density, confirmed by
        // decompiling both modules). Vessel.Load()/Unload() are the same pair the game itself uses
        // whenever a vessel enters/leaves physics range (Unload() rebuilds a fresh ProtoVessel
        // snapshot before tearing the loaded parts down, confirmed by decompiling it), so this is a
        // well-trodden, safe operation - not something invented for this feature. Deliberately a
        // no-op if the vessel is already loaded (never force-unloads something that has a real
        // reason to be loaded, e.g. the active vessel or something the player is otherwise near).
        //
        // Does NOT read the mass here. An earlier version did - v.Load() then immediately
        // v.parts[0].mass in the same call - and always read exactly 150000.0 kg, class A/C/D
        // alike (150 t, KSP.log-confirmed): Part.Start() is decompiled to `private IEnumerator
        // Start()`, a Unity coroutine, and ModulesOnStart() (which triggers the real mass
        // generation) runs partway through it, not synchronously when Load() returns. Reading
        // Part.mass immediately after Load() reads the still-un-started prefab default every time.
        // PollRealMassCapture (called every frame this stays pending) waits for Part.started before
        // reading the real value.
        private static void BeginCaptureRealMass(ThreatRecord rec, Vessel v)
        {
            if (v.loaded) return;
            try
            {
                v.Load();
                rec.RealMassCapturePending = true;
                rec.RealMassCaptureDeadlineRealtime = Time.realtimeSinceStartup + AdvancedSettings.RealMassCaptureTimeoutSeconds;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SENTRY] Failed to force-load " + Describe(rec) + " for real mass: " + e.Message);
                if (v.loaded) v.Unload();
            }
        }

        // Polls every frame (from WatchImminentImpacts) while a real-mass capture is in flight.
        // Reads the real mass once it's actually available, or gives up and unloads anyway once
        // RealMassCaptureTimeoutSeconds elapses, falling back to ImpactConsequence's nominal-class
        // estimate (RealMassKg stays NaN).
        //
        // Ready signal is "has Part.mass moved off this part's own static prefab default", NOT
        // Part.started - found after the owner reported the timeout warning firing
        // repeatedly for a captured comet. Decompiling Part.Start()'s coroutine settled it:
        // ModulesOnStart() (which is where ModuleAsteroid/ModuleComet actually write the real
        // generated mass, synchronously - asteroidMass/cometMass = volume * density) runs early,
        // but started=true isn't set until several `yield return null`s and a full rigidbody/
        // CreateAttachJoint pass LATER in that same coroutine - work that has nothing to do with
        // mass and, for a merged (captured/clawed) multi-part vessel specifically, plausibly takes
        // longer or gets redone around the couple's own joint rebuild. Part.started was a
        // plausible-sounding name for "has this part's mass been generated," not what it actually
        // gates on - the same trap this project has hit before with Orbit.GetVel()/
        // FlightGlobals.ready (see Toolchain notes). Checking the mass field directly is immune to
        // however long that unrelated physics setup takes.
        private static void PollRealMassCapture(ThreatRecord rec, Vessel v)
        {
            if (v == null || !v.loaded)
            {
                // Vanished or was unloaded by something else while we waited - nothing to finish.
                rec.RealMassCapturePending = false;
                return;
            }

            // Found by part name (FindSpaceObjectPart), not v.parts[0] - a captured object's merged
            // vessel can put the player's own craft's part at index 0 instead (see that method).
            Part spaceObjectPart = FindSpaceObjectPart(v);
            bool massGenerated = spaceObjectPart != null && spaceObjectPart.partInfo != null
                && spaceObjectPart.partInfo.partPrefab != null
                && spaceObjectPart.mass != spaceObjectPart.partInfo.partPrefab.mass;
            bool massReady = spaceObjectPart != null && (massGenerated || spaceObjectPart.started);
            bool timedOut = Time.realtimeSinceStartup >= rec.RealMassCaptureDeadlineRealtime;
            if (!massReady && !timedOut) return; // keep waiting, still within budget

            try
            {
                if (massReady)
                {
                    // Part.mass is in tonnes (KSP's universal mass unit) - convert to kg, same as
                    // ImpactConsequence's own nominal-mass calculation.
                    rec.RealMassKg = spaceObjectPart.mass * 1000.0;
                    Debug.Log(string.Format("[SENTRY] Captured real mass for {0}: {1:F1} kg (forced load, mass generated)",
                        Describe(rec), rec.RealMassKg));
                }
                else
                {
                    Debug.LogWarning("[SENTRY] Real mass capture timed out for " + Describe(rec) +
                        " - part never finished starting; falling back to the nominal-class estimate.");
                }
            }
            finally
            {
                rec.RealMassCapturePending = false;
                if (v.loaded) v.Unload();
            }
        }

        // Classifies a disappearing impactor using the last altitude/surface-speed we actually
        // observed it at (see WatchImminentImpacts): near/below the impact threshold altitude AND
        // still moving fast relative to the surface (>= AdvancedSettings.ImpactSurfaceSpeedCutoffMs,
        // default ~ the speed of sound - well above any controlled touchdown) reads as a genuine
        // destructive impact. Anything else - recovered from a safe altitude, or still near the
        // surface but slow (a successful soft landing/redirect) - is reported without the "it has
        // hit" framing, since the last known state doesn't support that conclusion. Refines the
        // older "any disappearance near the predicted UT = impact" heuristic, which could
        // false-positive on exactly this kind of player intervention.
        private void ReportConfirmedImpact(ThreatRecord rec, CelestialBody home)
        {
            string homeName = home != null ? home.name : "the home body";

            // Set by OnCometSpawned the moment a fragment bearing this comet's name+suffix pattern
            // is spawned - which, per the decompiled Part.explode()/Die() ordering, means THIS
            // comet's own destruction already happened as a side effect of breaking apart, not a
            // genuine ground impact. Reporting it as a confirmed impact here too would double-count
            // the same physical event: once for "the comet we were tracking vanished," and again for
            // whatever each fragment - now its own independently-tracked record - eventually does.
            if (rec.FragmentedNotImpacted)
            {
                AlertLog.Notice(Localizer.Format("#SENTRY_title_cometFragmented"),
                    Localizer.Format("#SENTRY_msg_cometFragmented", Describe(rec), homeName));
                return;
            }

            double threshold = ThresholdAltitude(home);
            // Small slack above the threshold: the last sample was taken up to one frame before
            // the actual deletion, so it can read a touch high even for a genuine impact.
            bool nearSurface = !double.IsNaN(rec.LastKnownAltitude)
                && rec.LastKnownAltitude <= threshold + Math.Max(threshold * 0.05, 100.0);
            bool fastEnough = !double.IsNaN(rec.LastKnownSurfaceSpeed)
                && rec.LastKnownSurfaceSpeed >= AdvancedSettings.ImpactSurfaceSpeedCutoffMs;
            bool confirmedByTelemetry = nearSurface && fastEnough;

            // Fallback for when WatchImminentImpacts' own orbit-based sampling never got a single
            // successful reading before the vessel disappeared (owner's report: "no recent
            // altitude sample" firing even when the asteroid demonstrably blew up while loaded -
            // a genuinely fast destructive reentry, e.g. capturing something already mid-descent,
            // can apparently sometimes outrun that per-frame sampling entirely). WasInAtmosphere is
            // set independently, from Vessel.atmDensity (the game's own per-vessel aero
            // simulation), so it doesn't share whatever's causing the orbit-based reading to miss.
            // !rec.HasLanded is the "soft landing" exclusion the owner explicitly asked for - a
            // vessel that safely touched down at any point is never treated as an impact here, even
            // under this fallback.
            bool confirmedByFallback = !confirmedByTelemetry && !rec.HasLanded && rec.WasInAtmosphere;

            if (confirmedByTelemetry || confirmedByFallback)
            {
                string message = confirmedByTelemetry
                    ? Localizer.Format("#SENTRY_msg_impactConfirmed",
                        Describe(rec), homeName, rec.ImpactUT.ToString("F0"), rec.LastKnownAltitude.ToString("F0"), rec.LastKnownSurfaceSpeed.ToString("F0"))
                    : Localizer.Format("#SENTRY_msg_impactConfirmedFallback", Describe(rec), homeName);

                // Folds the consequence estimate into the alert that already exists - only in the
                // confirmed branch, never for an "uncertain" outcome, since we don't actually know
                // an impact happened there. Reputation is now genuinely applied here - Funds/DestructibleBuilding are still never touched, the
                // Prefers the precise last-known telemetry (TryGetActualState); if that's exactly
                // what's missing (the fallback branch), falls back to the last predicted state
                // instead of skipping the report entirely - an approximation, but still a real
                // energy/burst/reputation analysis rather than nothing.
                bool haveState = ImpactConsequence.TryGetActualState(rec, out ImpactState finalState);
                if (!haveState) haveState = ImpactConsequence.TryGetPredictedState(rec, home, out finalState);

                if (haveState)
                {
                    ConsequenceReport report = ImpactConsequence.Compute(rec, finalState, home);
                    if (report.Valid)
                    {
                        string surfaceDesc = report.IsOcean
                            ? Localizer.Format("#SENTRY_frag_ocean")
                            : Localizer.Format("#SENTRY_frag_land");
                        message += "\n" + Localizer.Format("#SENTRY_frag_consequenceReport",
                            report.EnergyKtTnt.ToString("F1"),
                            DescribeBurst(report),
                            surfaceDesc,
                            report.LatitudeDeg.ToString("F1"),
                            report.LongitudeDeg.ToString("F1"),
                            (report.LeadTimeSeconds / 86400.0).ToString("F1"),
                            Math.Abs(report.WouldBeReputationDelta).ToString("F0"));
                        if (report.FacilityWouldBeDamaged)
                        {
                            // No funds figure quoted here (see the facility-application block
                            // below) - the actual repair cost is whatever stock's own
                            // DestructibleBuilding.RepairCost says, not our own estimate.
                            message += " " + Localizer.Format("#SENTRY_frag_facilityDamage");
                        }

                        bool isCareer = HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;

                        if (isCareer && Reputation.Instance != null)
                        {
                            // This is NOT a softer,
                            // SENTRY-specific limit above that - a single bad enough chain of
                            // impacts (e.g. several comet fragments each scoring their own impact)
                            // CAN and should be able to take a player from max reputation straight
                            // to -1000. The reason this still needs code at all, rather than just
                            // calling AddReputation directly and trusting stock to clamp it: stock's
                            // own AddReputation (decompiled: addReputation_granular) has NO clamp of
                            // its own - only a diminishing-returns curve per increment, which
                            // throttles how much a SINGLE large call can move rep but does nothing
                            // to stop a SEQUENCE of separate calls from drifting past -1000
                            // altogether. Pre-capping the REQUESTED delta to the remaining budget
                            // above -1000, before ever calling AddReputation, reproduces
                            // SetReputation's own floor without SetReputation's other problem (it
                            // re-clamps the SET value to [-1000,1000], which is exactly why an
                            // earlier attempt at a LOWER custom floor via SetReputation didn't work -
                            // moot now that the floor is -1000, the same value SetReputation itself
                            // would clamp to, but AddReputation's delta is still what actually needs
                            // capping here).
                            float floor = -Reputation.RepRange;
                            float remainingBudget = Reputation.CurrentRep - floor; // how far above -1000 we are right now
                            float delta = (float)report.WouldBeReputationDelta;
                            if (remainingBudget <= 0f) delta = 0f; // already at/below the floor - fully absorbed
                            else if (-delta > remainingBudget) delta = -remainingBudget;

                            if (delta != 0f)
                            {
                                Reputation.Instance.AddReputation(delta, TransactionReasons.None);
                            }
                            Debug.Log(string.Format("[SENTRY] Applied reputation penalty {0:F1} (requested {1:F1}) for {2}. New total: {3:F1}",
                                delta, report.WouldBeReputationDelta, Describe(rec), Reputation.CurrentRep));
                        }

                        // Facility application - gated on career mode only, independent of
                        // Reputation.Instance (this shouldn't silently no-op just because
                        // reputation happened to be unavailable). No automatic Funding deduction here at all - buildings are
                        // left genuinely destroyed (queued for demolition, see
                        // pendingFacilityTier's own comment, since DestructibleBuilding doesn't
                        // exist as a live GameObject outside the Space Center scene) for the
                        // player to repair themselves via the stock Space Center repair UI, on
                        // their own timeline. This deliberately lets a player defer or skip
                        // repairing a nonessential facility (Spaceplane Hangar, Administration)
                        // rather than being charged a lump sum they didn't choose, and reads more
                        // dramatically than a funds line in a screen message ever could -
                        // ImpactConsequence.Compute still computes WouldBeFundsDelta (harmless,
                        // unused, same "computed fact nothing reads" pattern already accepted for
                        // BypassesFloor) but nothing applies or reports it anymore.
                        if (isCareer && report.FacilityWouldBeDamaged)
                        {
                            if (report.FacilityDamageTier > pendingFacilityTier)
                            {
                                pendingFacilityTier = report.FacilityDamageTier;
                            }
                            Debug.Log(string.Format("[SENTRY] Facility damage from {0}: tier {1}. Demolition queued for next Space Center visit - repair left to the player.",
                                Describe(rec), report.FacilityDamageTier));
                        }
                    }
                }

                AlertLog.Alert(Localizer.Format("#SENTRY_title_impact"), message,
                    severe: true, stopWarpEligible: false);
            }
            else
            {
                string lastKnown = (double.IsNaN(rec.LastKnownAltitude) || double.IsNaN(rec.LastKnownSurfaceSpeed))
                    ? Localizer.Format("#SENTRY_frag_noSample")
                    : Localizer.Format("#SENTRY_frag_lastKnownState", rec.LastKnownAltitude.ToString("F0"), rec.LastKnownSurfaceSpeed.ToString("F0"));
                AlertLog.Alert(Localizer.Format("#SENTRY_title_impactUncertain"),
                    Localizer.Format("#SENTRY_msg_impactUncertain", Describe(rec), homeName, lastKnown),
                    severe: false, stopWarpEligible: false);
            }
        }

        // A landed/splashed object has already resolved, successfully or not - it isn't still
        // approaching anything, so it's no longer an active impact threat. Called from both
        // ScanRoutine (the normal per-scan path) and WatchImminentImpacts (so the exemption takes
        // effect immediately rather than waiting up to a full scan interval), for exactly the same
        // reason both check it: the object's "orbit" is continuously re-anchored to the rotating
        // surface while landed, so re-running impact prediction on it is meaningless and (worse)
        // was flickering into spurious fresh "Impact predicted" alerts.
        private void HandleLanded(ThreatRecord rec, Vessel v, double now, CelestialBody homeBody)
        {
            if (rec == null) return;
            // Unconditional, even if we're about to early-return below (already resolved) - the
            // "soft landing" fact itself needs to stick regardless of whether this specific call
            // does anything else, see ReportConfirmedImpact's WasInAtmosphere fallback.
            rec.HasLanded = true;
            if (rec.State == ThreatState.Ignored) return; // never was a threat, or already resolved

            ThreatState oldState = rec.State;
            if (oldState == ThreatState.Impact) DisarmAlarm(rec);
            rec.State = ThreatState.Ignored;
            rec.LastChangeUT = now;

            if (oldState == ThreatState.Impact && rec.Captured)
            {
                // Same reasoning as every other captured-object transition in ApplyResult: the
                // player is flying this thing, so routine chatter is logged only, not surfaced as
                // a Notice/Alert.
                AlertLog.Info(string.Format("{0} (captured) has come to rest; no longer an active threat.", Describe(rec)));
            }
            else if (oldState == ThreatState.Impact)
            {
                AlertLog.Notice(Localizer.Format("#SENTRY_title_allClear"),
                    Localizer.Format("#SENTRY_msg_allClearLanded", Describe(rec), homeBody != null ? homeBody.name : "the home body"));
            }
            else
            {
                AlertLog.Info(string.Format("{0} has landed/splashed down; no longer an active threat.", Describe(rec)));
            }

            // Deliberately NOT calling UpdateTracking here: releasing an auto-track hold on a
            // landed object could let it expire and quietly vanish, which would be a worse outcome
            // than the alert-spam bug this method fixes - the player may still want to visit or
            // recover it. Scope of this fix is "stop re-alerting," not "stop tracking."
        }

        // Debug hook (F11 in OrbitScanner): run a scan now, with per-object logging.
        public void RequestScan(bool verbose)
        {
            scanRequested = true;
            verboseRequested |= verbose;
        }

        // ---- scanning --------------------------------------------------------------------------

        // quietSummary: skip the "Scan end" line unless something was pruned - set for the
        // once-a-second captured-rock rescans, which would otherwise log a line every second the
        // player spends flying it (that record is always recomputed, since its epoch keeps moving).
        private IEnumerator ScanRoutine(bool verbose, bool quietSummary = false)
        {
            scanRunning = true;
            Stopwatch total = Stopwatch.StartNew();
            CelestialBody homeBody = FlightGlobals.GetHomeBody();
            double now = Planetarium.GetUniversalTime();

            if (homeBody == null || FlightGlobals.Vessels == null)
            {
                scanRunning = false;
                yield break;
            }

            if (verbose)
            {
                Debug.Log(string.Format("[SENTRY] ---- Scan start ({0}) home={1} R_SOI={2:F0}m threshold={3:F0}m UT={4:F1} records={5} ----",
                    verbose ? "verbose" : "scheduled", homeBody.name, homeBody.sphereOfInfluence,
                    homeBody.atmosphere ? homeBody.atmosphereDepth : 0.0, now, records.Count));
            }

            // Snapshot the list: vessels can appear/vanish between the frames we yield across.
            List<Vessel> candidates = new List<Vessel>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.DiscoveryInfo == null) continue;

                // Captured (clawed) objects first - see OnPartCouple/HandleCaptured. A clawed
                // asteroid stops being a VesselType.SpaceObject, so the plain type filter below
                // would silently drop it and the record would be pruned, taking its phase-6
                // consequence with it. It's the same asteroid, so it keeps being evaluated.
                ThreatRecord known;
                if (records.TryGetValue(v.id, out known) && known.Captured)
                {
                    // ...but only for as long as the host craft really still holds the rock.
                    if (HasSpaceObjectPart(v))
                    {
                        candidates.Add(v);
                        continue;
                    }
                    // Released, or the asteroid part is gone. Clear the flag and fall through: if
                    // this vessel is itself a space object again (the branch where the asteroid's
                    // own vessel survived the grapple and the ship has now undocked from it) it
                    // resumes normal handling below; otherwise it drops out of `seen` and is
                    // pruned, which is right - a released rock is a brand-new vessel with a new
                    // Guid and gets rediscovered from scratch.
                    known.Captured = false;
                }

                if (v.vesselType != VesselType.SpaceObject) continue;
                if (v.DiscoveryInfo.Level == DiscoveryLevels.None) continue; // not yet discovered: the player couldn't know about it
                candidates.Add(v);
            }

            HashSet<Guid> seen = new HashSet<Guid>();
            int cacheHits = 0, computed = 0;
            double computeMs = 0.0;

            foreach (Vessel v in candidates)
            {
                if (v == null) continue; // destroyed while we were yielding (Unity's overloaded null check)
                Orbit o = v.orbit;
                if (o == null || o.referenceBody == null) continue;
                seen.Add(v.id);

                ThreatRecord rec;
                records.TryGetValue(v.id, out rec);

                // A landed/splashed object has already resolved - see HandleLanded. Skip prediction
                // entirely rather than let SoiIntersection.Predict run on a "landed" orbit, which KSP
                // continuously re-anchors to the rotating surface (a new epoch practically every
                // frame) - that was causing spurious repeat "Impact predicted"-style alerts for
                // asteroids that had already safely come to rest
                if (v.LandedOrSplashed)
                {
                    HandleLanded(rec, v, now, homeBody);
                    continue;
                }

                // A verbose (F11) scan always recomputes, even on what would otherwise be a cache
                // hit - it exists specifically to validate the algorithm (the fast-vs-brute MOID
                // comparison and the impact-timing cross-check in LogVerbose only mean anything on
                // a fresh computation), so a scan that ran moments earlier must not hide them.
                bool cacheHit = !verbose
                    && rec != null
                    && rec.OrbitEpoch == o.epoch
                    && rec.ReferenceBody == o.referenceBody.name
                    && now < rec.ValidUntilUT;

                if (cacheHit)
                {
                    cacheHits++;
                    if (verbose)
                    {
                        Debug.Log(string.Format("[SENTRY]   {0}: cache hit ({1}, valid until UT {2:F0}, epoch {3:F1})",
                            v.vesselName, rec.State, rec.ValidUntilUT, rec.OrbitEpoch));
                    }
                    continue;
                }

                Stopwatch sw = Stopwatch.StartNew();
                EncounterResult result = SoiIntersection.Predict(o, homeBody, now);

                // Comets never get aimed at the home body the way asteroids do, so a no-encounter
                // verdict is the common case for them - worth checking whether they still pass
                // close enough to be worth knowing about (capture missions, sightseeing).
                string cometTypeIgnored;
                bool isCometObj = IsComet(v, out cometTypeIgnored);
                double approachDist = double.NaN, approachUT = double.NaN;
                if (isCometObj && !result.EncounterFound)
                {
                    approachDist = SoiIntersection.ClosestApproach(o, homeBody, now,
                        CometApproachSoiMultiple * homeBody.sphereOfInfluence, out approachUT);
                }
                sw.Stop();
                computed++;
                computeMs += sw.Elapsed.TotalMilliseconds;

                if (verbose) LogVerbose(v, result, now, homeBody, sw.Elapsed.TotalMilliseconds, isCometObj, approachDist, approachUT);

                ApplyResult(v, rec, result, now, homeBody, approachDist, approachUT);

                // One full prediction per frame keeps the per-frame cost bounded; cache hits are
                // free so they don't yield.
                yield return null;
            }

            // Prune records whose vessel no longer exists - but interpret the disappearance first.
            List<Guid> gone = new List<Guid>();
            foreach (KeyValuePair<Guid, ThreatRecord> kv in records)
            {
                if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            }
            foreach (Guid id in gone)
            {
                HandleDisappearance(records[id], now, homeBody);
                records.Remove(id);
            }

            lastScanUT = now;
            scanRunning = false;
            total.Stop();
            // Skip the summary line when nothing happened (pure cache hits) - otherwise this fires
            // every frame at high time warp and floods KSP.log for no information.
            if (verbose || (computed > 0 && !quietSummary) || gone.Count > 0)
            {
                Debug.Log(string.Format("[SENTRY] ---- Scan end: {0} objects, {1} cache hits, {2} computed ({3:F1} ms compute, {4:F0} ms wall incl. frame yields), {5} records pruned ----",
                    candidates.Count, cacheHits, computed, computeMs, total.Elapsed.TotalMilliseconds, gone.Count));
            }
        }

        // ---- state machine ---------------------------------------------------------------------

        private void ApplyResult(Vessel v, ThreatRecord rec, EncounterResult result, double now, CelestialBody homeBody,
            double approachDist, double approachUT)
        {
            bool isNew = rec == null;
            if (isNew)
            {
                rec = new ThreatRecord { VesselId = v.id, FirstSeenUT = now, State = ThreatState.Ignored };
                records[v.id] = rec;
            }

            ThreatState oldState = rec.State;
            double oldImpactUT = rec.ImpactUT;
            double oldEpoch = rec.OrbitEpoch; // captured before it's overwritten below - see CheckDeflectionBonus

            ThreatState newState = ThreatState.Ignored;
            if (result.EncounterFound) newState = result.IsImpact ? ThreatState.Impact : ThreatState.NearPass;
            else if (!double.IsNaN(approachDist)) newState = ThreatState.CloseApproach;

            // Deflection-bonus dwell-time bookkeeping (mechanical, not part of the eligibility
            // decision itself - see CheckDeflectionBonus below). Every FRESH entry into Impact
            // restarts the clock; any exit clears it, so a later re-entry starts a brand new stint
            // rather than reusing however long some earlier, unrelated stint had already run.
            if (newState == ThreatState.Impact && oldState != ThreatState.Impact)
            {
                rec.ImpactStateEnteredUT = now;
            }
            else if (newState != ThreatState.Impact)
            {
                rec.ImpactStateEnteredUT = double.NaN;
            }

            // Refresh everything we know.
            rec.Name = v.vesselName;
            rec.State = newState;
            rec.EntryUT = result.EntryUT;
            rec.ImpactUT = result.ImpactUT;
            rec.PeriapsisUT = result.PeriapsisUT;
            rec.CapturePeA = result.CapturePeA;
            rec.Moid = result.MoidDistance;
            rec.IsGroundImpact = result.IsGroundImpact;
            rec.GroundImpactUT = result.GroundImpactUT;
            rec.ClosestApproachDistance = approachDist;
            rec.ClosestApproachUT = approachUT;
            rec.OrbitEpoch = v.orbit.epoch;
            rec.ReferenceBody = v.orbit.referenceBody.name;
            rec.LastEvaluatedUT = now;
            rec.ObjectClass = v.DiscoveryInfo.objectSize.ToString();
            string cometType;
            if (IsComet(v, out cometType))
            {
                rec.IsComet = true;
                rec.CometType = cometType;
            }
            else if (!rec.Captured)
            {
                // Only ever clear this for a loose object. IsComet reads the CometVessel *vessel*
                // module, and a vessel module doesn't survive two vessels merging - so a clawed
                // comet whose record moved onto the player's craft would otherwise silently
                // demote itself to "asteroid" and be described wrongly from then on.
                rec.IsComet = false;
                rec.CometType = "";
            }

            // How long this verdict stays valid without recomputing (assuming the orbit itself
            // doesn't change, which the epoch check covers separately):
            //  - an encounter is valid until it happens; the SOI transition will change the
            //    vessel's reference body and epoch anyway, forcing a fresh look;
            //  - a close approach is valid until it happens, same reasoning;
            //  - a miss is valid for a good fraction of the time horizon we searched.
            if (result.EncounterFound && !result.AlreadyInsideSoi)
            {
                rec.ValidUntilUT = result.EntryUT;
            }
            else if (result.EncounterFound)
            {
                // Already inside the SOI: re-check every scan; it's cheap (no MOID/time stepping)
                // and the orbit can change at any moment (loading, periapsis in atmosphere, ...).
                rec.ValidUntilUT = now;
            }
            else if (newState == ThreatState.CloseApproach)
            {
                rec.ValidUntilUT = approachUT;
            }
            else
            {
                rec.ValidUntilUT = now + AdvancedSettings.MissRevalidateFraction * SoiIntersection.HorizonSeconds(v.orbit, homeBody);
            }

            string label = Describe(rec);

            // Keeps the stock Alarm Clock entry in sync with the current graze-vs-ground-impact
            // classification, independent of whether ThreatState itself just changed - a periapsis
            // refinement can flip IsGroundImpact while staying in ThreatState.Impact the whole
            // time (both share that one state - see ThreatRecord.IsGroundImpact), and that flip
            // needs the same arm/disarm response a full state transition would get. Runs on every
            // call, not just on transition, and is idempotent either way (ArmAlarm/DisarmAlarm are
            // both safe to call repeatedly). Only a genuine ground impact (CapturePeA < 0) ever
            // gets an alarm - an atmosphere graze can't actually happen unattended (no drag is
            // simulated on an unloaded object, so it will pass through and come back out), so
            // there's nothing there for a forced warp-stop to protect.
            if (newState == ThreatState.Impact || oldState == ThreatState.Impact)
            {
                if (newState == ThreatState.Impact && rec.IsGroundImpact)
                {
                    if (rec.AlarmId == 0 || !AlarmClockIntegration.IsArmed(rec.AlarmId))
                    {
                        ArmAlarm(rec, v, homeBody); // applies to captured objects too - see below
                    }
                    else
                    {
                        AlarmClockIntegration.UpdateAlarmUT(rec.AlarmId, rec.GroundImpactUT);
                    }
                }
                else
                {
                    DisarmAlarm(rec);
                }
            }

            // Deflection bonus: pays out (and posts its own alert) when this exit from Impact is a
            // genuine, once-only, player-relevant redirect - see the method's own TODO for the full
            // guard contract. Independent of the rec.Captured branch just below: a captured object's
            // deflection is the MOST legitimate case (the player is unambiguously flying it) and
            // should still pay, even though its ROUTINE chatter stays suppressed either way.
            bool deflectionPaid = CheckDeflectionBonus(rec, oldState, newState, oldEpoch, result, v, now, homeBody, label);

            if (newState != oldState)
            {
                rec.LastChangeUT = now;

                // A captured object is one the player is personally flying - they already know far
                // more about it than an alert could tell them, and a forced warp stop mid-burn
                // would be actively hostile. Suppress the routine chatter; the record itself is
                // untouched, so it still counts for everything that matters.
                if (rec.Captured)
                {
                    AlertLog.Info(string.Format("{0} (captured, attached to {1}): {2} -> {3}; alerts suppressed.",
                        label, v.vesselName, oldState, newState));
                }
                else switch (newState)
                    {
                        case ThreatState.Impact:
                            // stopWarpEligible only the very first time this record ever becomes an
                            // impact threat - giving the player a chance to plan a redirect mission the
                            // moment a NEW threat shows up. DiscoveryWarpStopped is a lifetime latch
                            // (never reset), so if a later orbit refinement knocks this same object out
                            // of Impact and back in again, it does NOT force warp down a second time -
                            // that used to happen since the old code re-armed on every re-entry
                            // into Impact, not just the first. Every other severe event for this record
                            // (revision, imminent, confirmed) already leaves warp alone - the stock Alarm
                            // Clock (armed just below) reliably handles the actual terminal cut on its
                            // own schedule, so repeating our own forced stop would just be naggy.
                            bool isNewThreat = !rec.DiscoveryWarpStopped;
                            rec.DiscoveryWarpStopped = true;
                            AlertLog.Alert(Localizer.Format("#SENTRY_title_impactPredicted"),
                                Localizer.Format("#SENTRY_msg_impactPredicted",
                                    label, homeBody.name, When(rec.ImpactUT, now, homeBody), rec.CapturePeA.ToString("F0"), result.ThresholdAltitude.ToString("F0"), When(rec.EntryUT, now, homeBody)),
                                severe: true, stopWarpEligible: isNewThreat);
                            break; // ArmAlarm already ran above - it applies to captured objects too
                        case ThreatState.NearPass:
                            if (oldState == ThreatState.Impact && deflectionPaid)
                            {
                                // CheckDeflectionBonus already posted its own alert for this exit -
                                // nothing more to say here.
                            }
                            else if (oldState == ThreatState.Impact)
                                AlertLog.Notice(Localizer.Format("#SENTRY_title_allClear"),
                                    Localizer.Format("#SENTRY_msg_allClearToNearPass", label, homeBody.name, rec.CapturePeA.ToString("F0"), When(rec.PeriapsisUT, now, homeBody)));
                            else if (rec.IsComet)
                                // Comets rarely enter the SOI at all (their orbits aren't aimed at the
                                // home body like asteroids' are) - when one does, it's closer than any
                                // "close approach" and worth a screen notice, but not another inbox
                                // entry (see AlertLog.Notice) - orbit refinement can flicker this in
                                // and out repeatedly.
                                AlertLog.Notice(Localizer.Format("#SENTRY_title_cometApproachingSoi"),
                                    Localizer.Format("#SENTRY_msg_cometApproachingSoi", label, homeBody.name, When(rec.EntryUT, now, homeBody), rec.CapturePeA.ToString("F0")));
                            else
                                AlertLog.Info(string.Format("{0} will pass through {1}'s SOI: entry {2}, periapsis {3:F0} m.",
                                    label, homeBody.name, When(rec.EntryUT, now, homeBody), rec.CapturePeA));
                            break;
                        case ThreatState.CloseApproach:
                            if (oldState == ThreatState.Impact && deflectionPaid)
                            {
                                // CheckDeflectionBonus already posted its own alert for this exit -
                                // nothing more to say here.
                            }
                            else if (oldState == ThreatState.Impact || oldState == ThreatState.NearPass)
                                AlertLog.Notice(Localizer.Format("#SENTRY_title_allClear"),
                                    Localizer.Format("#SENTRY_msg_allClearToCloseApproach", label, homeBody.name, rec.ClosestApproachDistance.ToString("F0"), When(rec.ClosestApproachUT, now, homeBody)));
                            else
                                AlertLog.Notice(Localizer.Format("#SENTRY_title_cometApproach"),
                                    Localizer.Format("#SENTRY_msg_cometApproach", label, rec.ClosestApproachDistance.ToString("F0"), homeBody.name, When(rec.ClosestApproachUT, now, homeBody)));
                            break;
                        case ThreatState.Ignored:
                            if (oldState == ThreatState.Impact && deflectionPaid)
                            {
                                // CheckDeflectionBonus already posted its own alert for this exit -
                                // nothing more to say here.
                            }
                            else if (oldState == ThreatState.Impact)
                                AlertLog.Notice(Localizer.Format("#SENTRY_title_allClear"),
                                    Localizer.Format("#SENTRY_msg_allClearToIgnored", label, homeBody.name, result.Note));
                            else if (!isNew)
                                AlertLog.Info(string.Format("{0} is no longer predicted to enter {1}'s SOI ({2}).", label, homeBody.name, result.Note));
                            else
                                Debug.Log(string.Format("[SENTRY] New object {0}: no threat ({1}).", label, result.Note));
                            break;
                    }
            }
            else if (newState == ThreatState.Impact && Math.Abs(rec.ImpactUT - oldImpactUT) > AdvancedSettings.ImpactShiftAlertSeconds)
            {
                rec.LastChangeUT = now;
                if (!rec.Captured)
                {
                    AlertLog.Alert(Localizer.Format("#SENTRY_title_impactRevised"),
                        Localizer.Format("#SENTRY_msg_impactRevised", label, When(rec.ImpactUT, now, homeBody), oldImpactUT.ToString("F0"), (rec.ImpactUT - oldImpactUT).ToString("+0;-0")),
                        severe: true, stopWarpEligible: false);
                }
                // No AlarmClockIntegration call needed here anymore - the unified alarm-sync block
                // above already re-synced a ground impact's alarm to the current GroundImpactUT (or
                // armed/disarmed it, if the graze/ground classification itself changed) earlier in
                // this same call, unconditionally, before this revision check ever runs.
            }

            // The "Impact imminent" reminder and the "confirmed by disappearance" alert are both
            // handled by WatchImminentImpacts (Update()) now, not here - see that method for why
            // this per-scan cadence is too coarse for the tail end of an impact.

            // Never for a captured object: it's a player craft now, and StartTrackingObject/
            // StopTrackingObject write to its DiscoveryInfo. Tracking is a concept for loose space
            // objects that can expire - a craft the player is flying can't, and has no business
            // having its discovery level rewritten by this mod.
            if (!rec.Captured) UpdateTracking(rec, v);
        }

        // Deflection bonus - pays a positive reputation reward the first (and only the first) time
        // a record's genuine, sustained Impact verdict resolves to a genuinely safe non-impact
        // verdict. Called from ApplyResult right after rec's fields have already been refreshed for
        // newState/result (rec.CapturePeA, rec.OrbitEpoch, etc. are the NEW values) but before the
        // switch that would otherwise post a plain "All clear" notice for the same transition -
        // return true to suppress that notice (this method posts its own alert instead) or false to
        // let it through unchanged (nothing paid, nothing to announce).
        //
        // Per CLAUDE.md's "Deflection bonus and its exploit guard" design doc, and the owner's own
        // 2026-09-27 simplification of it (no proximity/player-caused check needed - a rare
        // gravity-assist "free" bonus is an acceptable cost, but a deflection must NEVER pay out
        // more than once for the same object):
        //
        //   1. This must be a genuine exit from Impact, not a routine re-evaluation of something
        //      that was never really Impact to begin with: oldState == ThreatState.Impact AND
        //      newState != ThreatState.Impact.
        //   2. The orbit must have actually changed (oldEpoch, captured in ApplyResult before it
        //      overwrote rec.OrbitEpoch, vs. v.orbit.epoch now) - ScanRoutine's own cache-hit check
        //      (rec.OrbitEpoch == o.epoch) means ApplyResult CAN still run again with an unchanged
        //      epoch purely because rec.ValidUntilUT expired (e.g. the "already inside SOI" branch
        //      revalidates every single scan) - an epoch-unchanged exit from Impact is our own math
        //      re-converging on a better answer, not a real deflection, and must not pay.
        //   3. Minimum dwell: rec.ImpactStateEnteredUT (already maintained for you in ApplyResult -
        //      set on every FRESH entry into Impact, cleared to NaN on every exit) must show this
        //      stint lasted at least AdvancedSettings.DeflectionMinDwellSeconds before this exit.
        //      Guards against a marginal object flickering in and out of Impact during ordinary
        //      orbit refinement triggering (or re-triggering, once epoch happens to tick over) a
        //      payout for a "deflection" that was never a sustained, real threat.
        //   4. Post-deflection clearance margin: the NEW periapsis must clear the impact threshold
        //      by a real margin, not just barely - rec.CapturePeA (already the new value) vs.
        //      result.ThresholdAltitude + AdvancedSettings.DeflectionMinPeriapsisMarginM. A rock
        //      nudged from 69 km to 71 km is not deflected.
        //   5. Once-only: rec.HasPaidDeflection must be false, and must be set true the moment this
        //      pays out - this is the core anti-farm guard (bonus once, penalty always).
        //
        // If all five hold: compute a nominal "would-be" energy via
        // ImpactConsequence.TryEstimateNominalEnergyKt(rec) (the object's own class/real mass at a
        // fixed nominal impact speed - there's no live trajectory left to measure a precise speed
        // from once it's been redirected), convert to a bonus via
        // ImpactConsequence.InterpolateDeflectionBonus(energyKt), scale by
        // SentrySettings.Instance.damageCoefficient (the same "how big/dramatic overall" master
        // dial the penalty side uses - deflection deliberately does NOT use reputationScaling, which
        // is documented as specifically a PENALTY-side knob), and apply it in career mode via
        // Reputation.Instance.AddReputation - capped so the requested delta never pushes
        // Reputation.CurrentRep past +Reputation.RepRange (+1000), mirroring EXACTLY the existing
        // floor-clamp pattern in ReportConfirmedImpact (just the opposite direction - a ceiling, not
        // a floor). Post #SENTRY_title_deflectionSuccess/#SENTRY_msg_deflectionSuccess as a real
        // AlertLog.Alert for a loose object, or AlertLog.Info only if rec.Captured (matching the
        // established precedent elsewhere in this method: a captured object's own pilot already
        // knows they just redirected it - no fanfare needed, just log it and still pay the reward).
        private bool CheckDeflectionBonus(ThreatRecord rec, ThreatState oldState, ThreatState newState,
            double oldEpoch, EncounterResult result, Vessel v, double now, CelestialBody homeBody, string label)
        {
            // TODO(human): split guard 4 out of the "moment of exit" check using
            // rec.DeflectionAwaitingClearance (see its comment in ThreatRecord). A genuine exit that
            // passes guards 1/2/3/5 but not 4 should set the flag instead of giving up; a later call
            // (NearPass -> NearPass etc.) with the flag set pays out once guard 4 passes; and
            // re-entering Impact should clear it.
            // I'm just gonna nest `if`s. There's probably a more efficient structure, but see note on C#.
            // Check 1
            string homeName = homeBody != null ? homeBody.name : "the home body";
            if (oldState == ThreatState.Impact && newState != ThreatState.Impact)
            {
                // Check 2
                if (oldEpoch != v.orbit.epoch)
                {
                    // Check 3
                    if (now - rec.ImpactStateEnteredUT > AdvancedSettings.DeflectionMinDwellSeconds)
                    {
                        // Check 4
                        // A rock nudged from 69km to 71km is technically deflected, though. And it's not clear that a rock with a pe of 69km is a threat that needs deflecting, either. This might need more thought.
                        if (rec.CapturePeA > result.ThresholdAltitude + AdvancedSettings.DeflectionMinPeriapsisMarginM)
                        {
                            // Check 5
                            double bonus = 0;
                            if (!rec.HasPaidDeflection)
                            {
                                if (ImpactConsequence.TryEstimateNominalEnergyKt(rec, out double energyKt))
                                {
                                    bonus = ImpactConsequence.InterpolateDeflectionBonus(energyKt);
                                    bonus *= SentrySettings.Instance.damageCoefficient;
                                    // I guess C# only evaluates the second thing in an && after the first is true. I could've used that above.
                                    bool isCareer = HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
                                    // Not sure if the if is in the right spot
                                    if (isCareer && Reputation.Instance != null)
                                    {
                                        // The instructions said to EXACTLY follow the pattern from ReportConfirmedImpact
                                        float ceiling = Reputation.RepRange;
                                        float remainingBudget = ceiling - Reputation.CurrentRep;
                                        // Not sure how this could happen
                                        if (remainingBudget <= 0f)
                                        {
                                            bonus = 0;
                                        }
                                        // Don't account for diminishing returns
                                        else if (bonus > remainingBudget)
                                        {
                                            bonus = remainingBudget;
                                        }
                                        if (bonus != 0)
                                        {
                                            // Last minute cast to `float`
                                            Reputation.Instance.AddReputation((float)bonus, TransactionReasons.None);
                                        }
                                    }
                                    rec.HasPaidDeflection = true;
                                    // I feel like someone piloting a vessel who redirects an asteroid might still want the screen notification with how much rep they earned since there are very few other occasions when a redirect might occur. I might have something wrong, though. Please push back if you disagree.
                                    // I probably messed the `AlertLog.Alert` syntax up
                                    AlertLog.Alert(Localizer.Format("#SENTRY_title_deflectionSuccess"), Localizer.Format("#SENTRY_msg_deflectionSuccess", label, homeName, bonus.ToString("F0")), severe: false, stopWarpEligible: false);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        private void HandleDisappearance(ThreatRecord rec, double now, CelestialBody homeBody)
        {
            // Defensive cleanup: the stock alarm's own deleteWhenDone should already have removed
            // it by firing, but the vessel can disappear slightly before the alarm's own UT.
            DisarmAlarm(rec);

            // The game silently deletes unloaded vessels that fall into an atmosphere, with no
            // event to hook. So "an impactor vanished at about the predicted time" is our
            // corroboration that the impact happened - refined via ReportConfirmedImpact using
            // whatever last-known altitude/speed sample is available (usually none here, since
            // this backstop path is for records that disappeared before ever entering
            // WatchImminentImpacts' fast-watch window - it degrades gracefully to "uncertain" in
            // that case, which is honest: we don't have the evidence either way). Same story for
            // ImpactConsequence's report fragment inside ReportConfirmedImpact: no last-known state
            // vectors means no report, an accepted gap rather than a bug - the fast-watch window
            // covers the overwhelming majority of real confirmations.
            //
            // Not gated on IsGroundImpact/GroundImpactUT here - a grazer that disappears is exactly
            // as reportable as a genuine ground impact, since ReportConfirmedImpact's own
            // altitude/speed evidence check (not this one) is what actually decides "within the
            // atmosphere = impact, use the airburst analysis" vs. "no evidence = uncertain," per
            // the owner's own rule. A grazer that vanishes with no cached telemetry at all (never
            // loaded, so WatchImminentImpacts never got a look at it) has no evidence either way
            // and correctly falls into that method's "uncertain" branch on its own.
            //
            // Also admits rec.Captured regardless of State, as a defensive backstop mirroring
            // WatchImminentImpacts' own unconditional-for-captured-objects watch - a captured
            // object's ThreatState can be stale (still NearPass/Ignored from before the player
            // dove it into the atmosphere) since the periodic scan's Keplerian-orbit prediction
            // doesn't track active piloting. WatchImminentImpacts should normally catch this first
            // (same frame it happens - see that method for why "immediately" matters here), this is
            // only for the rare case it never got a chance to (e.g. destroyed before ever being
            // iterated by Update()).
            if (rec.State == ThreatState.Impact || rec.Captured)
            {
                ReportConfirmedImpact(rec, homeBody);
            }
            else
            {
                AlertLog.Info(string.Format("{0} is gone (expired, recovered or removed); state was {1}. Record dropped.",
                    Describe(rec), rec.State));
            }
        }

        // ---- auto-track / auto-untrack -----------------------------------------------------------

        // Decides whether the mod currently wants this record's vessel tracked, and tracks or
        // untracks it via the exact same stock Tracking Station calls the player's own Track/
        // Untrack buttons use - indistinguishable from a manual click either way.
        //   - Comets are always wanted: they're rare (the stock spawner caps at ~1 at a time), so losing one to untracked-expiry
        //     before it ever shows a close approach would defeat the comet-approach alert feature.
        //     Once tracked, a comet is never auto-untracked.
        //   - An asteroid is wanted while on an impact course, and ALSO while it's a fly-by IF the
        //     player has the "Fly-bys" filter on (UiPrefs.ShowFlyBys) - if they've opted into
        //     watching fly-bys in the window, they shouldn't lose one to expiry either. With the
        //     filter off (the default), fly-bys are left alone entirely, matching the original
        //     phase-3 behaviour.
        //   - We only ever untrack a record WE tracked (rec.AutoTracked) - an object the player
        //     tracked themselves is never touched, whether by our own choice or the game's own
        //     "already tracked" check. This is a deliberate loosening of the original "never
        //     untrack" rule: that rule existed to respect the player's own tracking choices, not
        //     to guard against the warp-clip-through-Kerbin bug (now fixed separately by the
        //     stock Alarm Clock integration - see ArmAlarm/DisarmAlarm below), so it's safe to
        //     release our own holds once they're no longer doing anything useful (e.g. an
        //     asteroid that was on an impact course leaves it via a gravity assist).
        private void UpdateTracking(ThreatRecord rec, Vessel v)
        {
            if (!AutoTrackEnabled || v == null || v.DiscoveryInfo == null) return;
            UiPrefs.EnsureLoaded(); // guards against this running before the window has ever opened

            bool wantTracked = rec.IsComet
                || rec.State == ThreatState.Impact
                || (rec.State == ThreatState.NearPass && UiPrefs.ShowFlyBys);
            bool isTracked = v.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.StateVectors);

            if (wantTracked && !isTracked)
            {
                KSP.UI.Screens.SpaceTracking.StartTrackingObject(v);
                rec.AutoTracked = true;
                AlertLog.Info(string.Format("{0} has been added to tracked objects so it cannot expire.", Describe(rec)));
            }
            else if (!wantTracked && isTracked && rec.AutoTracked)
            {
                KSP.UI.Screens.SpaceTracking.StopTrackingObject(v);
                rec.AutoTracked = false;
                AlertLog.Info(string.Format("{0} is no longer an active threat or shown fly-by; removed from tracked objects.", Describe(rec)));
            }
        }

        // Called immediately when the player toggles the "Fly-bys" filter in the window, so
        // already-tracked fly-bys are released (or newly-shown ones picked up) right away instead
        // of waiting for their state to naturally change on some future scan - a cached NearPass
        // record can go a long time between recomputes (see ValidUntilUT in ApplyResult).
        public void SyncFlyByTracking()
        {
            foreach (ThreatRecord rec in records.Values)
            {
                if (rec.State != ThreatState.NearPass || rec.IsComet) continue;
                Vessel v = FlightGlobals.FindVessel(rec.VesselId);
                UpdateTracking(rec, v);
            }
        }

        // ---- stock Alarm Clock integration -------------------------------------------

        // Idempotent: safe to call even if disabled or already armed. Also self-heals a record
        // whose AlarmId points at an alarm the player deleted by hand via the stock Alarm Clock UI
        // - re-checking AlarmExists (not just "AlarmId != 0") means it stays protected instead of
        // being left with a permanently stale, non-functional id for the rest of its time in
        // Impact state.
        private void ArmAlarm(ThreatRecord rec, Vessel v, CelestialBody homeBody)
        {
            if (!useAlarmClockEnabled || AlarmClockIntegration.IsArmed(rec.AlarmId)) return;
            // Targets GroundImpactUT (the actual ground-crossing moment), not the old atmosphere-
            // entry ImpactUT - drag isn't simulated on an unloaded object, so the previous alarm
            // fired at atmosphere entry and left the whole remaining descent to coast by at 1x warp
            // with nothing else forcing a stop. Only ever called for a genuine ground impact (see
            // the caller in ApplyResult) - GroundImpactUT is NaN for an atmosphere graze, which
            // can't actually happen unattended anyway.
            string title = string.Format("{0} impact", rec.Name);
            string description = string.Format("SENTRY: {0} predicted to impact {1}.", Describe(rec), homeBody.name);
            rec.AlarmId = AlarmClockIntegration.CreateAlarm(v, title, description, rec.GroundImpactUT);
        }

        // Idempotent: safe to call even if no alarm is currently armed.
        private void DisarmAlarm(ThreatRecord rec)
        {
            if (rec.AlarmId == 0) return;
            AlarmClockIntegration.RemoveAlarm(rec.AlarmId);
            rec.AlarmId = 0;
            rec.ImminentAlertFired = false;
        }

        // Called when the player toggles "Use Stock Alarm Clock" in the window, so the change
        // takes effect immediately rather than waiting on the next scan or state transition.
        private void SyncAlarmsToEnabledState()
        {
            if (useAlarmClockEnabled)
            {
                CelestialBody homeBody = FlightGlobals.GetHomeBody();
                if (homeBody == null) return;
                foreach (ThreatRecord rec in records.Values)
                {
                    // Only a genuine ground impact ever gets an alarm - see ArmAlarm's own comment.
                    if (rec.State != ThreatState.Impact || !rec.IsGroundImpact || rec.AlarmId != 0) continue;
                    Vessel v = FlightGlobals.FindVessel(rec.VesselId);
                    if (v == null) continue;
                    ArmAlarm(rec, v, homeBody);
                }
            }
            else
            {
                foreach (ThreatRecord rec in records.Values) DisarmAlarm(rec);
            }
        }

        // ---- facility destruction (KSC building demolition) -------------------------------------

        // Non-persisted (reset every scene load in OnAwake - see there) - tracks how long
        // ScenarioDestructibles.facilityToDestructibles' entry count has held steady, so
        // TryApplyPendingFacilityDamage doesn't act on a registry that's still mid-populating.
        private int facilityCountLastSeen = -1;
        private float facilityCountStableSinceRealtime = -1f;

        // Applies whatever facility-damage tier is owed (see pendingFacilityTier's own comment) by
        // demolishing live DestructibleBuilding instances - only ever meaningful from the Space
        // Center scene, where ScenarioDestructibles.facilityToDestructibles holds real, registered
        // buildings.        
        // `RegisterInstance()` is called from `Start()`/`OnEnable()`, not `Awake()`, and each
        // building's own `OnDisable()`/`OnEnable()` pair (`needsResetOnReEnable`) re-registers on
        // re-enable - consistent with KSC buildings coming in and out of camera/LOD range
        // dynamically, not all registering in one guaranteed batch the moment the scene loads.
        // Acting on the very first non-empty read caught only whatever happened to already be in
        // view that frame, then unconditionally cleared the pending flag regardless of how many
        // were actually found - which silently gave up early on a normal in-session scene switch
        // (fast, little settle time) far more often than on a full restart (slower to reach that
        // first check, so more buildings had already registered by then) - explaining exactly the
        // asymmetry reported. Fix: wait for the registered count to hold steady for
        // AdvancedSettings.FacilityRegistrationSettleSeconds before acting at all.
        //
        // "Single"/"Several"/"Levelled" map to 1 / AdvancedSettings.FacilitySeveralBuildingCount /
        // every registered facility - deliberately not targeted at the actual impact site
        // specifically (that would need an absolute world-position conversion this project has
        // consistently avoided elsewhere for floating-origin reasons - see the Toolchain notes) -
        // an accepted simplification for a feature meant to be exceedingly rare in the first place
        // (per the design doc, "practically impossible" from a random impact - this only
        // realistically fires from a deliberate player-steered catastrophe). Since only whatever
        // happens to be registered after settling gets considered, "Levelled" still isn't a
        // guarantee of literally every KSC building either - the same already-accepted trade-off,
        // just no longer dependent on exactly which frame this method first got lucky enough to run on.
        private void TryApplyPendingFacilityDamage()
        {
            int currentCount = ScenarioDestructibles.facilityToDestructibles != null
                ? ScenarioDestructibles.facilityToDestructibles.Count : 0;

            if (currentCount != facilityCountLastSeen)
            {
                facilityCountLastSeen = currentCount;
                facilityCountStableSinceRealtime = Time.realtimeSinceStartup;
                return; // still changing (or just started) - keep waiting
            }

            if (currentCount == 0) return; // nothing registered at all yet - keep waiting

            if (Time.realtimeSinceStartup - facilityCountStableSinceRealtime
                < AdvancedSettings.FacilityRegistrationSettleSeconds)
            {
                return; // count is non-zero but hasn't held steady long enough yet - keep waiting
            }

            int wantCount = pendingFacilityTier == ConsequenceReport.FacilityTier.Levelled
                ? int.MaxValue
                : (pendingFacilityTier == ConsequenceReport.FacilityTier.Several
                    ? AdvancedSettings.FacilitySeveralBuildingCount
                    : 1);

            int demolished = 0;
            foreach (KeyValuePair<string, List<ScenarioDestructibles.ProtoDestructible>> facility in ScenarioDestructibles.facilityToDestructibles)
            {
                if (demolished >= wantCount) break;

                // A facility with nothing left intact (already destroyed by an earlier event)
                // doesn't count toward wantCount - move on to a fresh one instead.
                bool anyIntactHere = false;
                foreach (ScenarioDestructibles.ProtoDestructible proto in facility.Value)
                {
                    foreach (DestructibleBuilding building in proto.dBuildingRefs)
                    {
                        if (building != null && building.IsIntact && !building.IsDestroyed)
                        {
                            building.Demolish();
                            anyIntactHere = true;
                        }
                    }
                }
                if (anyIntactHere) demolished++;
            }

            AlertLog.Info(string.Format("Applied pending facility damage (tier {0}): {1} facilit{2} demolished.",
                pendingFacilityTier, demolished, demolished == 1 ? "y" : "ies"));
            pendingFacilityTier = ConsequenceReport.FacilityTier.None;
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static bool IsComet(Vessel v, out string cometType)
        {
            cometType = "";
            if (v.vesselModules == null) return false;
            for (int i = 0; i < v.vesselModules.Count; i++)
            {
                CometVessel cv = v.vesselModules[i] as CometVessel;
                if (cv == null) continue;
                cometType = cv.typeName ?? "";
                return true;
            }
            return false;
        }

        private static string Describe(ThreatRecord rec)
        {
            string kind = rec.IsComet ? (string.IsNullOrEmpty(rec.CometType) ? "comet" : rec.CometType + " comet") : "asteroid";
            return string.Format("{0} (class {1} {2})", rec.Name, rec.ObjectClass, kind);
        }

        // Short plain-English rendering of a ConsequenceReport's burst classification, used inside
        // the localized #SENTRY_frag_consequenceReport fragment (as a single pre-formatted arg,
        // same convention as every other numeric value passed to Localizer.Format elsewhere in this
        // file).
        private static string DescribeBurst(ConsequenceReport report)
        {
            switch (report.Class)
            {
                case ConsequenceReport.Classification.Airburst:
                    return Localizer.Format("#SENTRY_frag_burstAirburst", report.BurstAltitudeM.ToString("F0"));
                case ConsequenceReport.Classification.GroundBurst:
                    return Localizer.Format("#SENTRY_frag_burstGroundBurst", report.BurstAltitudeM.ToString("F0"));
                default:
                    return Localizer.Format("#SENTRY_frag_burstCrater");
            }
        }

        // "UT 4173923 (in 179.4 days)" using the home body's own solar day so planet packs read right.
        private static string When(double ut, double now, CelestialBody homeBody)
        {
            if (double.IsNaN(ut)) return "UT n/a";
            double day = homeBody.solarDayLength > 0.0 ? homeBody.solarDayLength : 21600.0;
            return string.Format("UT {0:F0} (in {1:F1} {2} days)", ut, (ut - now) / day, homeBody.name);
        }

        private static void LogVerbose(Vessel v, EncounterResult result, double now, CelestialBody homeBody, double ms,
            bool isCometObj, double approachDist, double approachUT)
        {
            Orbit o = v.orbit;
            string tracked = v.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.StateVectors) ? "tracked" : "untracked";
            Debug.Log(string.Format("[SENTRY]   {0} ({1}, {2}): ref={3} ecc={4:F4} PeR={5:F0}m ApR={6:F0}m epoch={7:F1} [{8:F2} ms]",
                v.vesselName, v.DiscoveryInfo.objectSize, tracked, o.referenceBody.name, o.eccentricity, o.PeR,
                o.eccentricity >= 1.0 ? double.PositiveInfinity : o.ApR, o.epoch, ms));

            // Cross-check the new two-stage MOID against the phase 2 brute force (only makes
            // sense when the object orbits the same parent as the home body).
            if (o.referenceBody == homeBody.orbit.referenceBody)
            {
                Stopwatch sw = Stopwatch.StartNew();
                double brute = SoiIntersection.BruteForceMoidForValidation(o, homeBody.orbit);
                sw.Stop();
                Debug.Log(string.Format("[SENTRY]       MOID fast={0:F0}m brute={1:F0}m (fast - brute = {2:F0}m; brute took {3:F1} ms)",
                    result.MoidDistance, brute, result.MoidDistance - brute, sw.Elapsed.TotalMilliseconds));
            }

            // Comet close-approach cross-check: brute-force sample a window around the fast
            // result's claimed closest-approach UT and compare. Only meaningful when the fast path
            // actually found something to check.
            if (isCometObj && !double.IsNaN(approachDist))
            {
                const double windowSeconds = 2.0 * 86400.0;
                double bruteApproachUT;
                Stopwatch sw2 = Stopwatch.StartNew();
                double bruteApproach = SoiIntersection.BruteForceClosestApproachForValidation(
                    o, homeBody.orbit, approachUT - windowSeconds, approachUT + windowSeconds, 600.0, out bruteApproachUT);
                sw2.Stop();
                Debug.Log(string.Format("[SENTRY]       Close approach fast={0:F0}m@UT{1:F0} brute={2:F0}m@UT{3:F0} (fast-brute={4:F0}m; brute took {5:F1} ms)",
                    approachDist, approachUT, bruteApproach, bruteApproachUT, approachDist - bruteApproach, sw2.Elapsed.TotalMilliseconds));
            }

            if (!result.EncounterFound)
            {
                string gap = double.IsNaN(result.ClosestSampledGap) ? "n/a" : result.ClosestSampledGap.ToString("F0") + "m";
                string approach = double.IsNaN(approachDist) ? "" : string.Format(" closeApproach={0:F0}m@UT{1:F0}", approachDist, approachUT);
                Debug.Log(string.Format("[SENTRY]       -> no encounter ({0}) closestSampledGap={1}{2}", result.Note, gap, approach));
                return;
            }

            string verdict = result.IsImpact ? "*** IMPACT ***" : "safe pass";
            Debug.Log(string.Format("[SENTRY]       -> {0}: capturePeA={1:F1}m (threshold={2:F1}m) entryUT={3:F1} periapsisUT={4:F1} impactUT={5:F1} [{6}]",
                verdict, result.CapturePeA, result.ThresholdAltitude, result.EntryUT, result.PeriapsisUT, result.ImpactUT, result.Note));
            if (!double.IsNaN(result.GameTimeToPe))
            {
                Debug.Log(string.Format("[SENTRY]       timing check: ours entry->Pe = {0:F1}s, game's timeToPe = {1:F1}s (diff {2:F1}s)",
                    result.PeriapsisUT - result.EntryUT, result.GameTimeToPe, (result.PeriapsisUT - result.EntryUT) - result.GameTimeToPe));
            }
        }
    }

    // All player-facing notifications funnel through here. The
    // scenario's state-machine code above only ever calls these three methods.
    public static class AlertLog
    {
        // Stock KSP's own alarm-clock klaxon (GameData/Squad/Alarms/Sounds/Klaxon.wav) - reused
        // as-is since it ships with the base game; no separate audio asset to source or license.
        private const string AlertClipPath = "Squad/Alarms/Sounds/Klaxon";
        private const float ScreenMessageDurationSeconds = 8f;

        private static AudioClip alertClip;
        private static bool alertClipLoadAttempted;

        // Routine, non-actionable notes: log only. Used for "will pass safely",
        // "no longer predicted", auto-track notices, etc. - things worth a KSP.log line but not
        // worth interrupting the player for.
        public static void Info(string message)
        {
            Debug.Log("[SENTRY] INFO: " + message);
        }

        // Screen-message-only notices: routine near-pass/close-approach entries and "all clear"
        // transitions. These can flicker in and out repeatedly as each scan refines an orbit -
        // posting them to the persistent MessageSystem inbox flooded it. They're still logged and flash
        // briefly on screen, and stay visible live in the AlertWindow's list; the permanent inbox is
        // otherwise reserved for the events that go through Alert: new impact, revised impact time, 
        // impact imminent, and the disappearance outcome (either "it has hit" or the ambiguous "status
        //  uncertain" case - see ReportConfirmedImpact - which is deliberately non-severe but still 
        // inbox-worthy, since a vessel vanishing near its predicted impact is always worth 
        // knowing about either way).
        public static void Notice(string title, string message)
        {
            Debug.Log("[SENTRY] NOTICE: " + message);
            ScreenMessages.PostScreenMessage(message, ScreenMessageDurationSeconds, ScreenMessageStyle.UPPER_CENTER);
        }

        // Actionable state changes that belong in the persistent inbox: new impact, revised impact
        // time, impact confirmed (or not) by disappearance, impact imminent. `severe` gates the
        // message colour/icon and whether a sound plays. `stopWarpEligible` is separate and
        // narrower: only the very first "new impact discovered" alert should ever force warp down - 
        // every later severe event for the same record leaves warp alone, since the stock Alarm 
        // Clock (armed once a record enters Impact state) reliably handles the actual terminal 
        // warp cut on its own schedule regardless of our alerts.
        public static void Alert(string title, string message, bool severe, bool stopWarpEligible)
        {
            Debug.Log("[SENTRY] ALERT: " + message);

            ScreenMessages.PostScreenMessage(message, ScreenMessageDurationSeconds, ScreenMessageStyle.UPPER_CENTER);

            if (MessageSystem.Instance != null)
            {
                MessageSystemButton.MessageButtonColor color = severe
                    ? MessageSystemButton.MessageButtonColor.RED
                    : MessageSystemButton.MessageButtonColor.GREEN;
                MessageSystemButton.ButtonIcons icon = severe
                    ? MessageSystemButton.ButtonIcons.ALERT
                    : MessageSystemButton.ButtonIcons.MESSAGE;
                MessageSystem.Instance.AddMessage(new MessageSystem.Message(title, message, color, icon), true);
            }

            SentryScenario scenario = SentryScenario.Instance;
            if (scenario == null) return;

            if (severe && stopWarpEligible && scenario.StopWarpEnabled)
            {
                // Route through the stock Alarm Clock's own trigger path (a throwaway alarm due
                // "now") rather than calling TimeWarp.SetRate ourselves - a direct call wasn't
                // reliably stopping warp; the stock trigger path also calls TimeWarp.fetch.CancelAutoWarp()
                //  first, which our direct call was missing. See AlarmClockIntegration.StopWarpNow.
                if (scenario.UseAlarmClockEnabled)
                {
                    AlarmClockIntegration.StopWarpNow(null, "SENTRY", "Stopping warp: " + title);
                }
                else if (TimeWarp.fetch != null)
                {
                    // Best-effort fallback if the player disabled the stock Alarm Clock integration
                    // entirely - still cancel auto-warp first, same as the trusted path does.
                    TimeWarp.fetch.CancelAutoWarp();
                    TimeWarp.SetRate(0, true, false);
                }
            }
            if (severe && scenario.AudioAlertEnabled)
            {
                PlayAlertSound();
            }
        }

        private static void PlayAlertSound()
        {
            if (!alertClipLoadAttempted)
            {
                alertClipLoadAttempted = true;
                alertClip = GameDatabase.Instance.GetAudioClip(AlertClipPath);
                if (alertClip == null)
                {
                    Debug.LogWarning("[SENTRY] Could not load alert sound '" + AlertClipPath + "' - audio alert disabled for this session.");
                }
            }
            if (alertClip == null) return;

            // A short-lived, non-spatial (2D) audio source rather than AudioSource.PlayClipAtPoint:
            // KSP's floating-origin coordinate system means a 3D-positioned clip can end up far
            // from the camera and be inaudible. spatialBlend = 0 makes it always audible.
            GameObject go = new GameObject("SentryAlertSound");
            AudioSource src = go.AddComponent<AudioSource>();
            src.clip = alertClip;
            src.spatialBlend = 0f;
            src.volume = 0.8f;
            src.Play();
            UnityEngine.Object.Destroy(go, alertClip.length + 0.25f);
        }
    }
}
