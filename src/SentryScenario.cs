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
        // See the Update() guard for why this exists alongside the OnAwake call.
        private bool abundanceApplyPending = true;
        private bool audioAlertEnabled = true;
        private bool stopWarpEnabled = true;
        // Creates a stock Alarm Clock entry for each impactor so KSP's own warp
        // deceleration protects the actual impact moment, not just the moment it's first
        // predicted (see AlarmClockIntegration). Default on, matching the other two toggles.
        private bool useAlarmClockEnabled = true;

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
            // Apply the current Abundance preset every time this scenario spins up (SPACECENTER/
            // TRACKSTATION/FLIGHT) - ScenarioDiscoverableObjects.Instance is a fresh object each
            // scene load too, so this can't just be done once.
            SentrySettings.ApplyAbundance();
            GameEvents.OnGameSettingsApplied.Add(OnSettingsApplied);
            GameEvents.onPartCouple.Add(OnPartCouple);
        }

        private void OnDestroy()
        {
            GameEvents.OnGameSettingsApplied.Remove(OnSettingsApplied);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            if (Instance == this) Instance = null;
        }

        // Fires when the player hits Accept in the Esc-menu Settings dialog - re-applies the
        // Abundance preset immediately rather than waiting for a scene reload. The comet-radius/
        // impact-lead settings don't need this: they're read fresh from SentrySettings.Instance
        // every time they're used, never cached.
        private void OnSettingsApplied()
        {
            SentrySettings.ApplyAbundance();
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

        public override void OnLoad(ConfigNode node)
        {
            records.Clear();
            node.TryGetValue("lastScanUT", ref lastScanUT);
            node.TryGetValue("audioAlertEnabled", ref audioAlertEnabled);
            node.TryGetValue("stopWarpEnabled", ref stopWarpEnabled);
            node.TryGetValue("useAlarmClockEnabled", ref useAlarmClockEnabled);
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
            foreach (ThreatRecord r in records.Values)
            {
                r.Save(node.AddNode("THREAT"));
            }
        }

        private void Update()
        {
            // OnAwake's ApplyAbundance() call can silently no-op if ScenarioDiscoverableObjects
            // hasn't run its own OnAwake yet - scenario module instantiation order between this
            // mod and stock (or any other mod) on a brand-new career isn't something we control,
            // and unlike an existing save (where this mod's SentryScenario entry was appended
            // after stock's, so it reliably runs second) a fresh game builds every AddToAllGames
            // scenario's order from assembly-scan order instead. Retrying here is a fully
            // reliable fix rather than a guess: Unity runs every component's Awake() for a scene
            // load before any of their Update()s, so by the time this line first runs,
            // ScenarioDiscoverableObjects.Instance (also set in ITS OnAwake) is guaranteed to
            // exist if it's going to exist at all this scene.
            if (abundanceApplyPending)
            {
                abundanceApplyPending = false;
                SentrySettings.ApplyAbundance();
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
            if (scanRequested || due)
            {
                bool verbose = verboseRequested;
                scanRequested = false;
                verboseRequested = false;
                lastScanRealTime = Time.realtimeSinceStartup;
                StartCoroutine(ScanRoutine(verbose));
            }
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
                if (rec.State != ThreatState.Impact || double.IsNaN(rec.ImpactUT)) continue;
                if (now < rec.ImpactUT - ImpactImminentLeadSeconds) continue;

                Vessel v = FlightGlobals.FindVessel(rec.VesselId);

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
                // impact is actually recorded.
                if (!rec.ImminentAlertFired && !rec.Captured)
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
                    // Cache the last state we actually saw, every frame - the instant it disappears
                    // we can no longer query it, so this is the only evidence available to tell a
                    // real high-speed impact apart from the vessel being recovered, destroyed, or
                    // successfully soft-landed by the player shortly before the predicted moment.
                    //
                    // Deliberately NOT Vessel.altitude/Vessel.srfSpeed: oth are only recomputed inside
                    //  a block gated on FlightGlobals.ready, which (per the phase-3 lesson already in this file)
                    //  is false in every scene this mod actually needs to work in except active FLIGHT
                    //  on the object itself.
                    //
                    // Also NOT Orbit.GetVel() It returns velocity relative to FlightGlobals.ActiveVessel's
                    //  own main body's frame, not relative to this orbit's own referenceBody - only correct
                    // when called on the active vessel's own orbit (where those two bodies happen to
                    // be the same), garbage for any other orbit, including every case that matters
                    // here (an unpiloted asteroid; no active vessel at all in Space Center/Tracking
                    // Station). Also not Orbit.pos/Orbit.vel or getRFrmVelOrbit (which reads Orbit.pos
                    // internally) - both are cached fields, not guaranteed fresh for "now" on an
                    // unloaded on-rails object between orbit-driver updates, which is exactly the
                    // kind of staleness this project's own SOI-intersection code already avoids by
                    // never trusting cached orbit state.
                    //
                    // So: reconstructed by hand from getRelativePositionAtUT(now)/
                    // getOrbitalVelocityAtUT(now) - the same UT-explicit, metre-accurate source
                    // SoiIntersection.cs already trusts - combined with CelestialBody.angularVelocity,
                    // mirroring exactly what getRFrmVelOrbit computes (Cross(angularVelocity,
                    // pos.xzy)) but from a guaranteed-fresh position instead of a possibly-stale
                    // cached one. Only sampled once the object's orbit has actually transitioned to
                    // home as its reference body (i.e. really is Kerbin-relative by now) - before
                    // that, position/velocity would be relative to the Sun instead, which would be
                    // self-consistent but meaningless as an "altitude"/"surface speed".
                    if (home != null && v.orbit != null && v.orbit.referenceBody == home)
                    {
                        Vector3d relPos = v.orbit.getRelativePositionAtUT(now);
                        Vector3d relVel = v.orbit.getOrbitalVelocityAtUT(now);
                        rec.LastKnownAltitude = relPos.magnitude - home.Radius;
                        Vector3d rotFrameVel = Vector3d.Cross(home.angularVelocity, relPos.xzy);
                        rec.LastKnownSurfaceSpeed = (relVel.xzy - rotFrameVel).magnitude;
                    }
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

        private static double ThresholdAltitude(CelestialBody home)
        {
            return home != null && home.atmosphere ? home.atmosphereDepth : 0.0;
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
            double threshold = ThresholdAltitude(home);
            // Small slack above the threshold: the last sample was taken up to one frame before
            // the actual deletion, so it can read a touch high even for a genuine impact.
            bool nearSurface = !double.IsNaN(rec.LastKnownAltitude)
                && rec.LastKnownAltitude <= threshold + Math.Max(threshold * 0.05, 100.0);
            bool fastEnough = !double.IsNaN(rec.LastKnownSurfaceSpeed)
                && rec.LastKnownSurfaceSpeed >= AdvancedSettings.ImpactSurfaceSpeedCutoffMs;

            if (nearSurface && fastEnough)
            {
                AlertLog.Alert(Localizer.Format("#SENTRY_title_impact"),
                    Localizer.Format("#SENTRY_msg_impactConfirmed",
                        Describe(rec), homeName, rec.ImpactUT.ToString("F0"), rec.LastKnownAltitude.ToString("F0"), rec.LastKnownSurfaceSpeed.ToString("F0")),
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
            if (rec == null || rec.State == ThreatState.Ignored) return; // never was a threat, or already resolved

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

        private IEnumerator ScanRoutine(bool verbose)
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
            if (verbose || computed > 0 || gone.Count > 0)
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

            ThreatState newState = ThreatState.Ignored;
            if (result.EncounterFound) newState = result.IsImpact ? ThreatState.Impact : ThreatState.NearPass;
            else if (!double.IsNaN(approachDist)) newState = ThreatState.CloseApproach;

            // Refresh everything we know.
            rec.Name = v.vesselName;
            rec.State = newState;
            rec.EntryUT = result.EntryUT;
            rec.ImpactUT = result.ImpactUT;
            rec.PeriapsisUT = result.PeriapsisUT;
            rec.CapturePeA = result.CapturePeA;
            rec.Moid = result.MoidDistance;
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

            if (newState != oldState)
            {
                rec.LastChangeUT = now;
                // Leaving Impact for any other state means the alarm clock entry (if any) is no
                // longer wanted - do this once here rather than in every case below.
                if (oldState == ThreatState.Impact) DisarmAlarm(rec);

                // Arming the stock alarm happens regardless of whether the object is captured.
                // It's silent (no native popup or sound - see AlarmClockIntegration), and its only
                // real job is ramping warp down so the impact moment can't be skipped clean over at
                // high warp. The phase-6 consequence depends on actually observing that moment, so
                // a clawed asteroid needs this just as much as a loose one does.
                if (newState == ThreatState.Impact) ArmAlarm(rec, v, homeBody);

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
                        if (oldState == ThreatState.Impact)
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
                        if (oldState == ThreatState.Impact || oldState == ThreatState.NearPass)
                            AlertLog.Notice(Localizer.Format("#SENTRY_title_allClear"),
                                Localizer.Format("#SENTRY_msg_allClearToCloseApproach", label, homeBody.name, rec.ClosestApproachDistance.ToString("F0"), When(rec.ClosestApproachUT, now, homeBody)));
                        else
                            AlertLog.Notice(Localizer.Format("#SENTRY_title_cometApproach"),
                                Localizer.Format("#SENTRY_msg_cometApproach", label, rec.ClosestApproachDistance.ToString("F0"), homeBody.name, When(rec.ClosestApproachUT, now, homeBody)));
                        break;
                    case ThreatState.Ignored:
                        if (oldState == ThreatState.Impact)
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
                // Alarm UT is resynced either way: a captured object's impact time shifts constantly
                // while the player manoeuvres it, and the alarm is what keeps the moment observable.
                AlarmClockIntegration.UpdateAlarmUT(rec.AlarmId, rec.ImpactUT);
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
            // that case, which is honest: we don't have the evidence either way).
            if (rec.State == ThreatState.Impact && !double.IsNaN(rec.ImpactUT) && now >= rec.ImpactUT - ScanIntervalSeconds)
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
            string title = string.Format("{0} impact", rec.Name);
            string description = string.Format("SENTRY: {0} predicted to impact {1}.", Describe(rec), homeBody.name);
            rec.AlarmId = AlarmClockIntegration.CreateAlarm(v, title, description, rec.ImpactUT);
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
                    if (rec.State != ThreatState.Impact || rec.AlarmId != 0) continue;
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
