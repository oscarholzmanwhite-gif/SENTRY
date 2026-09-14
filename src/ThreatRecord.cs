using System;
using System.Globalization;

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

        // Last altitude/surface-relative speed observed for this vessel while it was still
        // findable (see SentryScenario.WatchImminentImpacts), used to tell a genuine
        // high-speed impact apart from the vessel disappearing some other way (recovered,
        // destroyed, or successfully soft-landed) shortly before the predicted moment. NaN until
        // first sampled. Deliberately NOT persisted - these are only meaningful within the current
        // run, transiently, right up until the record itself is discarded.
        public double LastKnownAltitude = double.NaN;
        public double LastKnownSurfaceSpeed = double.NaN;

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
