using System;
using System.Globalization;
using UnityEngine;

namespace Sentry
{
    // Per-object alert state. Transitions between these are what generate alerts.
    public enum ThreatState
    {
        Ignored,       // no SOI encounter predicted (or nothing known yet)
        CloseApproach, // comets only: passes within the "close approach" radius but never enters the SOI
        NearPass,      // will pass through the home body's SOI but stays above the threshold altitude
        Impact         // predicted to drop below the threshold altitude (atmosphere top / surface)
    }

    // Everything the mod remembers about one asteroid/comet between scans and across saves.
    // Persisted as a THREAT node inside the SentryScenario node of the save file.
    public class ThreatRecord
    {
        public Guid VesselId;
        public string Name = "";
        public ThreatState State = ThreatState.Ignored;

        // Prediction (only meaningful when State != Ignored; ImpactUT only when State == Impact).
        public double EntryUT;
        public double ImpactUT;
        public double PeriapsisUT;
        public double CapturePeA;
        public double Moid;

        // Only meaningful when State == Impact. IsGroundImpact distinguishes a genuine ground
        // impact (CapturePeA < 0) from an atmosphere graze (0 <= CapturePeA < threshold) - both
        // currently share ThreatState.Impact (owner's explicit choice: a grazer still belongs
        // under the "Impacts" filter, since it has the potential to become a real impact if the
        // player loads it and drag starts applying), but only a genuine ground impact will ever
        // actually happen unattended. GroundImpactUT is the actual ground-crossing time - NaN for
        // a graze, since the orbit never reaches ground level - and is what the stock Alarm Clock
        // now targets instead of the old atmosphere-entry-based ImpactUT (see SentryScenario.
        // ArmAlarm). See SoiIntersection.EncounterResult for the full reasoning.
        public bool IsGroundImpact;
        public double GroundImpactUT = double.NaN;

        // Comet close approach (only meaningful when State == CloseApproach; NaN otherwise):
        // the closest the comet gets to the home body and when, without entering the SOI.
        public double ClosestApproachUT = double.NaN;
        public double ClosestApproachDistance = double.NaN;

        // Cache key: if the vessel's orbit still has this epoch + reference body and we haven't
        // passed ValidUntilUT, the prediction is reused without recomputing.
        public double OrbitEpoch;
        public string ReferenceBody = "";
        public double ValidUntilUT;

        public double FirstSeenUT;
        public double LastEvaluatedUT;
        public double LastChangeUT;

        public string ObjectClass = ""; // A..I
        public bool IsComet;
        public string CometType = "";   // e.g. "short", "long", "interstellar"; empty for asteroids
        public bool AutoTracked;        // true if the mod (not the player) pressed Track on it

        // True while this object is clawed onto (or otherwise merged with) a player craft - see
        // SentryScenario.OnPartCouple/HandleCaptured. When set, VesselId is the SURVIVING
        // merged vessel's id, which may not be the asteroid's original one. The record keeps being
        // evaluated exactly as before, but the routine player-facing
        // alerts are suppressed, since the player is flying the thing.
        public bool Captured;

        // Stock Alarm Clock integration. AlarmId is 0 when no alarm is currently
        // armed for this record. ImminentAlertFired guards the one-time "Impact imminent"
        // alert and is reset whenever the record leaves the Impact state.
        public uint AlarmId;
        public bool ImminentAlertFired;

        // Guards the one-time "stop warp" alert that fires when this object first becomes an
        // impact threat (see SentryScenario.ApplyResult). Unlike ImminentAlertFired, this is a
        // lifetime latch for the record - it is set once, the first time State ever becomes
        // Impact, and never reset (not even by DisarmAlarm), so a prediction that flickers
        // Impact -> NearPass -> Impact again as the orbit refines doesn't force warp down a
        // second time for the same object. The stock Alarm Clock's own approach-ramp (ArmAlarm)
        // is what protects the actual impact moment; this flag only governs the one "hey, a new
        // threat showed up" interrupt.
        public bool DiscoveryWarpStopped;

        // Deflection bonus bookkeeping (see SentryScenario.ApplyResult's deflection-check hook,
        // still under construction as of this writing - see CLAUDE.md). HasPaidDeflection is a
        // lifetime latch: once true, this object can never pay the bonus again, no matter how many
        // more times it flickers into and out of Impact state - the design doc's explicit anti-farm
        // guard (bonus once, penalty always, asymmetric on purpose). ImpactStateEnteredUT is NOT a
        // lifetime latch like DiscoveryWarpStopped - it's meant to be reset to NaN on every exit
        // from Impact and re-set to the current UT on every FRESH entry, so the minimum-dwell guard
        // restarts for each new stint rather than being satisfied once and then never checked again.
        public bool HasPaidDeflection;
        public double ImpactStateEnteredUT = double.NaN;

        // Set when a record makes a genuine exit from Impact (epoch changed, dwell met, not already
        // paid) but its new periapsis hasn't yet cleared DeflectionMinPeriapsisMarginM. Needed
        // because a continuous burn is caught by the ~1 s captured-rock rescan the instant
        // periapsis crosses the threshold - i.e. with essentially zero margin - and every later
        // scan is NearPass -> NearPass, so a check made only at the moment of exit could never
        // pass. Persisted, so a deflection finished across a save/load still counts.
        public bool DeflectionAwaitingClearance;

        // Last altitude/surface-relative speed observed for this vessel while it was still
        // findable (see SentryScenario.WatchImminentImpacts), used to tell a genuine
        // high-speed impact apart from the vessel disappearing some other way (recovered,
        // destroyed, or successfully soft-landed) shortly before the predicted moment. NaN until
        // first sampled. Deliberately NOT persisted - these are only meaningful within the current
        // run, transiently, right up until the record itself is discarded.
        public double LastKnownAltitude = double.NaN;
        public double LastKnownSurfaceSpeed = double.NaN;

        // Same transience contract as the pair above: the home-body-relative state vectors (orbit-
        // math frame, unswizzled) and the UT they were sampled at, cached every frame alongside
        // LastKnownAltitude/LastKnownSurfaceSpeed so ImpactConsequence can compute a real energy/
        // location report at the moment of confirmed impact instead of just a threshold check.
        // Deliberately NOT persisted - and deliberately not used for anything but that one-shot
        // report, never cached/reused as a "would-be" value ahead of time.
        public Vector3d LastKnownRelPos = new Vector3d(double.NaN, double.NaN, double.NaN);
        public Vector3d LastKnownRelVel = new Vector3d(double.NaN, double.NaN, double.NaN);
        public double LastKnownSampleUT = double.NaN;

        // The real, per-instance generated mass (kg), captured once by briefly force-loading the
        // vessel at the start of the impact-watch window (SentryScenario.TryCaptureRealMass) -
        // triggers ModuleAsteroid/ModuleComet.OnStart's own procedural generation, the same real
        // number the game itself would use, sidestepping ImpactConsequence's nominal-sphere
        // approximation entirely. NaN until captured (or if capture failed); RealMassAttempted
        // guards it to a one-shot attempt per record so a failure doesn't retry every frame. Neither
        // is persisted - same transience contract as every other LastKnown* field above.
        public double RealMassKg = double.NaN;
        public bool RealMassAttempted;

        // Latches true the first time a grazer (IsGroundImpact false) is ever observed loaded, and
        // never turns off again - see SentryScenario.WatchImminentImpacts. A grazer has no
        // predicted crash window at all under the naive drag-free unloaded model (it's never
        // predicted to hit anything), but once the player loads it, real drag/heating applies and
        // it can explode well before its predicted, drag-free periapsis - genuine evidence of an
        // atmospheric event the model couldn't see coming. This flag is what keeps
        // WatchImminentImpacts watching it (caching last-known telemetry) from that point on, even
        // through the frame it later disappears and is no longer "loaded" to check. Not persisted -
        // same transience contract as every other flag on this record; harmless to lose across a
        // save/load, since it just means re-detecting on the next load if it's still loaded then.
        public bool GrazeWatchActive;

        // Latches true the instant this vessel is ever observed with nonzero Vessel.atmDensity
        // while loaded (see SentryScenario.WatchImminentImpacts) - a direct readout from the
        // game's own per-vessel FlightIntegrator, set independently of SENTRY's own orbit-based
        // altitude sampling (which occasionally fails to get even one successful reading before a
        // very fast destructive reentry - e.g. capturing an asteroid already mid-descent). Used as
        // fallback evidence in ReportConfirmedImpact: if the precise LastKnownAltitude/
        // LastKnownSurfaceSpeed telemetry never got sampled at all, "was definitely in the
        // atmosphere at some point and never landed" is still strong grounds to treat a
        // disappearance as a genuine impact rather than call it uncertain. Not persisted - same
        // transience contract as every other flag here.
        public bool WasInAtmosphere;

        // Set by HandleLanded the moment this vessel is ever observed LandedOrSplashed - the "soft
        // landing" exclusion for the WasInAtmosphere fallback above: a vessel that safely touched
        // down is never treated as a confirmed impact later, even if it's destroyed for some
        // unrelated reason afterward. HandleLanded already intercepts a currently-landed vessel
        // before it can "disappear" in the first place; this flag covers the case where landing
        // happened at some earlier point and only the later, unrelated loss reaches
        // ReportConfirmedImpact. Not persisted - same transience contract as every other flag here.
        public bool HasLanded;

        // Set the instant a newly-spawned comet vessel's name matches the "<this comet's Name>-X"
        // fragment-naming pattern CometManager.SpawnCometFragment uses (see
        // SentryScenario.OnCometSpawned) - i.e. this comet has (per the decompiled
        // Part.explode()/Die() ordering, already) broken apart under atmospheric stress rather than
        // genuinely hit the ground. ReportConfirmedImpact checks this first: fragmenting isn't
        // crashing, so this comet's own disappearance is reported as "broke apart," not scored as a
        // confirmed impact - each fragment is a separate, newly-tracked object that gets its own
        // independent evaluation instead. Not persisted - same transience contract as every other
        // flag on this record; if a save is loaded mid-fragmentation (vanishingly unlikely - the
        // whole sequence happens within one frame) the ordinary confirmed-impact path just applies
        // as it would for any other comet.
        public bool FragmentedNotImpacted;

        // Set while a force-load is in flight, waiting for the part's Part.Start() coroutine to
        // actually reach ModuleAsteroid/ModuleComet.OnStart() before Part.mass reflects the real
        // generated value (see SentryScenario.PollRealMassCapture - this replaced an earlier,
        // broken version that read Part.mass in the same synchronous call as Load(), which always
        // read the part's un-started prefab default). RealMassCaptureDeadlineRealtime is a
        // Time.realtimeSinceStartup timestamp, not persisted for the same reason as everything
        // else on this pair of fields - meaningless across a save/load or scene change.
        public bool RealMassCapturePending;
        public float RealMassCaptureDeadlineRealtime;

        // 0 for class A, 1 for B, ... ; -1 if unknown. Used for "biggest first" sorting.
        public int ClassIndex
        {
            get
            {
                if (string.IsNullOrEmpty(ObjectClass)) return -1;
                char c = char.ToUpperInvariant(ObjectClass[0]);
                return (c >= 'A' && c <= 'Z') ? c - 'A' : -1;
            }
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("vesselId", VesselId.ToString());
            node.AddValue("name", Name);
            node.AddValue("state", State.ToString());
            node.AddValue("entryUT", Fmt(EntryUT));
            node.AddValue("impactUT", Fmt(ImpactUT));
            node.AddValue("periapsisUT", Fmt(PeriapsisUT));
            node.AddValue("capturePeA", Fmt(CapturePeA));
            node.AddValue("moid", Fmt(Moid));
            node.AddValue("isGroundImpact", IsGroundImpact);
            node.AddValue("groundImpactUT", Fmt(GroundImpactUT));
            node.AddValue("closestApproachUT", Fmt(ClosestApproachUT));
            node.AddValue("closestApproachDistance", Fmt(ClosestApproachDistance));
            node.AddValue("orbitEpoch", Fmt(OrbitEpoch));
            node.AddValue("referenceBody", ReferenceBody);
            node.AddValue("validUntilUT", Fmt(ValidUntilUT));
            node.AddValue("firstSeenUT", Fmt(FirstSeenUT));
            node.AddValue("lastEvaluatedUT", Fmt(LastEvaluatedUT));
            node.AddValue("lastChangeUT", Fmt(LastChangeUT));
            node.AddValue("objectClass", ObjectClass);
            node.AddValue("isComet", IsComet);
            node.AddValue("cometType", CometType);
            node.AddValue("autoTracked", AutoTracked);
            node.AddValue("captured", Captured);
            node.AddValue("alarmId", AlarmId);
            node.AddValue("imminentAlertFired", ImminentAlertFired);
            node.AddValue("discoveryWarpStopped", DiscoveryWarpStopped);
            node.AddValue("hasPaidDeflection", HasPaidDeflection);
            node.AddValue("impactStateEnteredUT", Fmt(ImpactStateEnteredUT));
            node.AddValue("deflectionAwaitingClearance", DeflectionAwaitingClearance);
        }

        public static ThreatRecord Load(ConfigNode node)
        {
            ThreatRecord r = new ThreatRecord();
            string id = "";
            node.TryGetValue("vesselId", ref id);
            if (!Guid.TryParse(id, out r.VesselId)) return null;

            node.TryGetValue("name", ref r.Name);
            string state = "";
            node.TryGetValue("state", ref state);
            ThreatState parsed;
            if (Enum.TryParse(state, out parsed)) r.State = parsed;

            r.EntryUT = ReadDouble(node, "entryUT");
            r.ImpactUT = ReadDouble(node, "impactUT");
            r.PeriapsisUT = ReadDouble(node, "periapsisUT");
            r.CapturePeA = ReadDouble(node, "capturePeA");
            r.Moid = ReadDouble(node, "moid");
            node.TryGetValue("isGroundImpact", ref r.IsGroundImpact);
            r.GroundImpactUT = ReadDouble(node, "groundImpactUT");
            r.ClosestApproachUT = ReadDouble(node, "closestApproachUT");
            r.ClosestApproachDistance = ReadDouble(node, "closestApproachDistance");
            r.OrbitEpoch = ReadDouble(node, "orbitEpoch");
            node.TryGetValue("referenceBody", ref r.ReferenceBody);
            r.ValidUntilUT = ReadDouble(node, "validUntilUT");
            r.FirstSeenUT = ReadDouble(node, "firstSeenUT");
            r.LastEvaluatedUT = ReadDouble(node, "lastEvaluatedUT");
            r.LastChangeUT = ReadDouble(node, "lastChangeUT");
            node.TryGetValue("objectClass", ref r.ObjectClass);
            node.TryGetValue("isComet", ref r.IsComet);
            node.TryGetValue("cometType", ref r.CometType);
            node.TryGetValue("autoTracked", ref r.AutoTracked);
            node.TryGetValue("captured", ref r.Captured);
            node.TryGetValue("alarmId", ref r.AlarmId);
            node.TryGetValue("imminentAlertFired", ref r.ImminentAlertFired);
            node.TryGetValue("discoveryWarpStopped", ref r.DiscoveryWarpStopped);
            node.TryGetValue("hasPaidDeflection", ref r.HasPaidDeflection);
            r.ImpactStateEnteredUT = ReadDouble(node, "impactStateEnteredUT");
            node.TryGetValue("deflectionAwaitingClearance", ref r.DeflectionAwaitingClearance);
            // Migration: a record already in Impact from a save written before this field existed
            // (or one that never freshly re-entered Impact since) has no stint start, and NaN fails
            // every dwell comparison, silently blocking the deflection bonus forever. FirstSeenUT,
            // not LastChangeUT: the latter is bumped by every >1 h impact-time revision, which a
            // captured craft being flown triggers constantly, so it badly understates the stint.
            if (r.State == ThreatState.Impact && double.IsNaN(r.ImpactStateEnteredUT))
            {
                r.ImpactStateEnteredUT = r.FirstSeenUT;
            }
            return r;
        }

        // Doubles are written/read with the invariant culture and "R" (round-trip) precision so
        // NaN, infinities and full-precision UTs survive a save/load regardless of the OS locale.
        private static string Fmt(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static double ReadDouble(ConfigNode node, string key)
        {
            string s = "";
            if (!node.TryGetValue(key, ref s)) return double.NaN;
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }
    }
}
