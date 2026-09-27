using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sentry
{
    // A home-body-relative state vector pair (unswizzled orbit-math frame, matching
    // SoiIntersection's own convention) at a specific UT. Fed into Compute() either from the
    // predicted impact point (UI live estimate) or the actual last-known telemetry (final report).
    public struct ImpactState
    {
        public Vector3d RelPos;
        public Vector3d RelVel;
        public double UT;

        // True for TryGetActualState's real last-known telemetry - it already reflects whatever
        // real atmospheric drag the object experienced before disappearing, so Compute() treats it
        // as ground truth and never adjusts it further. False for TryGetPredictedState's state,
        // which comes from a pure vis-viva (gravity-only, no-drag) orbit propagation - Compute()
        // runs that through IntegrateDragSpeed's own numerical descent to approximate the real
        // braking loss a loaded object would actually show, so the two paths read comparably
        // instead of the predicted path always overstating energy the way an un-decelerated
        // atmosphere-entry speed would.
        public bool IsObserved;
    }

    public struct ConsequenceReport
    {
        public bool Valid;

        public double MassKg;
        public double SpeedMs;
        public double EnergyJ;
        public double EnergyKtTnt;

        public enum Classification { Airburst, GroundBurst, Crater }
        public double BurstAltitudeM; // NaN for Crater (reaches the surface intact)
        public Classification Class;

        public double LatitudeDeg;
        public double LongitudeDeg;

        // home.TerrainAltitude(lat, lon, allowNegative: true) < 0 (and
        // home.ocean true) - see Compute(). Feeds SentrySettings.oceanImpactModifierPercent into
        // the penalty below; also read by SentryScenario to pick the report's land/ocean wording.
        public bool IsOcean;

        public double LeadTimeSeconds; // impact UT minus rec.FirstSeenUT

        public bool FacilityWouldBeDamaged;   // computed fact - see FacilityTier for how much
        public double FacilityDamageRadiusM;
        // How much of KSC this impact's energy band would plausibly damage, per the design doc's
        // own energy-banded table (15-70kt: one facility; 70kt-1Mt: several; >1Mt: everything).
        // None whenever FacilityWouldBeDamaged is false. SentryScenario is the one that actually
        // acts on this (queuing/demolishing DestructibleBuilding instances) - this class only
        // ever reports the fact, per its own "pure calculator" contract.
        public enum FacilityTier { None, Single, Several, Levelled }
        public FacilityTier FacilityDamageTier;

        // Negative. SentryScenario.ReportConfirmedImpact's confirmed branch
        // applies this value for real via Reputation.Instance.AddReputation, in career mode only -
        // unclamped beyond stock's own built-in [-1000,1000] range (the design doc's own two-tier
        // reputation floor is deliberately not implemented). The AlertWindow live-estimate call
        // site (Compute() called from DrawContents/DrawRow) NEVER applies this - it only ever
        // builds a display string, every draw, and must stay that way or a value would be applied
        // once per GUI repaint instead of once per real impact.
        public double WouldBeReputationDelta;
        // Negative or 0. Applied for real via Funding.Instance.AddFunds in
        // career mode, alongside the facility demolition FacilityDamageTier calls for - see
        // SentryScenario.ReportConfirmedImpact. Only ever nonzero when FacilityWouldBeDamaged.
        public double WouldBeFundsDelta;
        public bool BypassesFloor;            // Class F-I catastrophic-tier flag; nothing to clamp against - no floor is implemented
    }

    // Pure, side-effect-free impact consequence calculator: energy, burst altitude/classification,
    // location, and the reputation/funds hit the doc's phase-6 model assesses. Never calls
    // Funds/DestructibleBuilding itself - those stay compute-only. Reputation is the one exception:
    // its caller (SentryScenario.ReportConfirmedImpact) applies WouldBeReputationDelta for real in
    // career mode, see that field's own comment. Fed by two independent call sites in
    // SentryScenario/AlertWindow; this class itself never caches a report, applies anything, or
    // reads/writes a ThreatRecord's persisted fields.
    public static class ImpactConsequence
    {
        private const double JoulesPerKtTnt = 4.184e12;
        private const double SecondsPerYear = 365.25 * 86400.0;

        // Class letter -> catastrophic tier (doc's Class F-I). Reported as a flag only; v1 tracks no
        // cumulative reputation total for a floor to clamp against.
        private const double CatastrophicEnergyKtThreshold = 300.0;

        public static ConsequenceReport Compute(ThreatRecord rec, ImpactState state, CelestialBody home)
        {
            ConsequenceReport report = new ConsequenceReport();
            if (rec == null || home == null) return report;

            double mass;
            if (!double.IsNaN(rec.RealMassKg) && rec.RealMassKg > 0.0)
            {
                // The real, per-instance generated mass, captured once by force-loading the vessel
                // at the start of the impact-watch window (see SentryScenario.TryCaptureRealMass) -
                // exact, not an approximation. Preferred over the nominal-sphere estimate whenever
                // available, for both the live estimate and the final report alike.
                mass = rec.RealMassKg;
            }
            else if (!TryGetMass(rec.ObjectClass, rec.IsComet, out mass))
            {
                return report; // Valid stays false - unrecognized class or prefab/module not found
            }
            report.MassKg = mass;

            Vector3d rotFrameVel = Vector3d.Cross(home.angularVelocity, state.RelPos.xzy);
            double speed = (state.RelVel.xzy - rotFrameVel).magnitude;

            report.LatitudeDeg = home.GetLatitude(state.RelPos.xzy, true);
            report.LongitudeDeg = home.GetLongitude(state.RelPos.xzy, true);

            // CelestialBody.TerrainAltitude(lat, lon, allowNegative: true) - confirmed by
            // decompiling it - returns pqsController.GetSurfaceHeight(dir) - pqsController.radius,
            // i.e. terrain height relative to sea level, negative underwater. This is the exact
            // public stock method the design doc's own research note anticipated needing
            // ("CelestialBody.pqsController.GetSurfaceHeight... or an equivalent") without having
            // to touch PQS directly or guess its return convention. Guarded on home.ocean for
            // airless bodies (no sea level to be under).
            report.IsOcean = home.ocean && home.TerrainAltitude(report.LatitudeDeg, report.LongitudeDeg, true) < 0.0;

            // state.UT, not rec.ImpactUT: for the live estimate (TryGetPredictedState) the two are
            // always identical anyway (that method sets UT = rec.ImpactUT itself), but for the
            // final report (TryGetActualState) state.UT is the real last-known-sample time - the
            // actual moment, not the original predicted atmosphere-entry time, which can be stale
            // (a scan-cadence-old prediction) or simply wrong for a grazer that exploded at an
            // unrelated time under real physics the drag-free prediction never modeled. Preferring
            // rec.ImpactUT here would silently understate or overstate the lead-time-mitigation
            // credit for exactly the confirmed-impact case this field matters most for.
            report.LeadTimeSeconds = state.UT - rec.FirstSeenUT;

            // Still the nominal per-class radius even when RealMassKg overrode the mass above - the
            // real generated mesh's exact radius isn't read (would need more reflection into
            // ModuleAsteroid's private paGenerated field). Feeds the dispersion-length term in
            // BurstAltitude below AND (for the predicted path only) the cross-sectional area used
            // by IntegrateDragSpeed just after it.
            TryGetNominalRadius(rec.ObjectClass, rec.IsComet, out double bodyRadius);
            (double burstAltitude, ConsequenceReport.Classification cls) =
                BurstAltitude(mass, bodyRadius, state, home);
            report.BurstAltitudeM = burstAltitude;
            report.Class = cls;

            // Drag-loss adjustment - PREDICTED path only (state.IsObserved false). The owner's own
            // framing: an asteroid won't unexpectedly accelerate in the atmosphere, so the raw
            // atmosphere-entry speed above is already a valid, conservative estimate on its own -
            // but it's still strictly higher than what a loaded object's real drag would leave it
            // with by the time it reaches the ground/burst point, which is exactly the loaded-vs-
            // unloaded inconsistency being closed here. IntegrateDragSpeed numerically walks the
            // same descent BurstAltitude already classified (a lightweight, Trajectories[mod]-style
            // integration - exact vis-viva for gravity, forward-Euler for the dissipative drag
            // term) down to the burst altitude (or the ground, for a Crater) and returns the speed
            // actually left at that point. TryGetActualState's real telemetry (state.IsObserved
            // true) already IS that answer, directly measured - never run back through a model.
            double finalSpeed = speed;
            if (!state.IsObserved && home.atmosphere)
            {
                double stopAltitude = double.IsNaN(burstAltitude) ? 0.0 : burstAltitude;
                finalSpeed = IntegrateDragSpeed(mass, bodyRadius, stopAltitude, state, home);
            }
            report.SpeedMs = finalSpeed;
            report.EnergyJ = 0.5 * mass * finalSpeed * finalSpeed;
            report.EnergyKtTnt = report.EnergyJ / JoulesPerKtTnt;

            SentrySettings settings = SentrySettings.Instance;
            double damageCoefficient = settings != null ? settings.damageCoefficient : 1.0;
            double reputationScaling = settings != null ? settings.reputationScaling : 1.0;
            double fundsScaling = settings != null ? settings.fundsScaling : 1.0;
            double oceanModifier = settings != null ? settings.oceanImpactModifierPercent / 100.0 : 0.30;

            (bool facilityDamaged, double facilityRadius, ConsequenceReport.FacilityTier facilityTier) =
                FacilityCheck(report.EnergyKtTnt, report.LatitudeDeg, report.LongitudeDeg, settings);
            report.FacilityWouldBeDamaged = facilityDamaged;
            report.FacilityDamageRadiusM = facilityRadius;
            report.FacilityDamageTier = facilityTier;

            double basePenalty = InterpolatePenaltyTable(report.EnergyKtTnt);
            if (report.IsOcean) basePenalty *= oceanModifier;
            double leadYears = Math.Max(0.0, report.LeadTimeSeconds) / SecondsPerYear;
            double mitigation = MitigationFactor(leadYears);
            report.WouldBeReputationDelta = -basePenalty * mitigation * reputationScaling * damageCoefficient;
            report.WouldBeFundsDelta = facilityDamaged
                ? -AdvancedSettings.RebuildCostBaselineFunds * fundsScaling * damageCoefficient
                : 0.0;
            report.BypassesFloor = report.EnergyKtTnt >= CatastrophicEnergyKtThreshold;

            report.Valid = true;
            return report;
        }

        // Final-report path: the real telemetry WatchImminentImpacts cached every frame right up
        // until the vessel disappeared. False if the fast-watch window was never entered (see
        // SentryScenario.HandleDisappearance's backstop path) - degrades gracefully, no report.
        public static bool TryGetActualState(ThreatRecord rec, out ImpactState state)
        {
            state = default(ImpactState);
            if (rec == null || double.IsNaN(rec.LastKnownSampleUT) || double.IsNaN(rec.LastKnownRelPos.x))
            {
                return false;
            }
            state = new ImpactState
            {
                RelPos = rec.LastKnownRelPos,
                RelVel = rec.LastKnownRelVel,
                UT = rec.LastKnownSampleUT,
                IsObserved = true
            };
            return true;
        }

        // Live-estimate path: cheap, uncached, evaluated fresh every AlertWindow draw from the
        // currently predicted impact point - never the same computation as TryGetActualState, and
        // never persisted anywhere.
        //
        // Sampled at ImpactUT (the atmosphere-ENTRY moment, ~70 km up on Kerbin - see
        // ThreatRecord/phase 6c's graze-vs-ground-impact split), NOT GroundImpactUT - this is only
        // ever the STARTING point for Compute()'s own IntegrateDragSpeed descent now, not the final
        // speed used for energy. Sampling the entry point and then integrating drag loss down to
        // the ground/burst altitude is the right split: switching the sample point itself to
        // GroundImpactUT would double-apply gravity's contribution (IntegrateDragSpeed already
        // walks that same descent) without adding anything, since under a pure vis-viva, no-drag
        // model, falling further under gravity alone only INCREASES speed (lower r -> higher v) -
        // moving further from, not closer to, what real drag would leave behind.
        public static bool TryGetPredictedState(ThreatRecord rec, CelestialBody home, out ImpactState state)
        {
            state = default(ImpactState);
            if (rec == null || home == null || double.IsNaN(rec.ImpactUT)) return false;

            Vessel v = FlightGlobals.FindVessel(rec.VesselId);
            if (v == null || v.orbit == null) return false;

            if (v.orbit.referenceBody == home)
            {
                state = new ImpactState
                {
                    RelPos = v.orbit.getRelativePositionAtUT(rec.ImpactUT),
                    RelVel = v.orbit.getOrbitalVelocityAtUT(rec.ImpactUT),
                    UT = rec.ImpactUT
                };
                return true;
            }

            if (double.IsNaN(rec.EntryUT)) return false;
            Orbit captureOrbit = SoiIntersection.ReconstructCaptureOrbit(v.orbit, home, rec.EntryUT);
            state = new ImpactState
            {
                RelPos = captureOrbit.getRelativePositionAtUT(rec.ImpactUT),
                RelVel = captureOrbit.getOrbitalVelocityAtUT(rec.ImpactUT),
                UT = rec.ImpactUT
            };
            return true;
        }

        // ---- Mass: live density x nominal per-class volume --------------------------------------
        // Stock generates each asteroid/comet's actual mesh (and therefore its exact volume)
        // procedurally per-instance from a persisted seed, with +-25% radius variance
        // (ModuleAsteroid.OnStart: radius = paPrefab.radius * Random.Range(0.75, 1.25)) plus
        // further shape irregularity from the mesh generator itself. That per-instance seed only
        // exists on a LOADED part's module, which unloaded asteroids never have (see CLAUDE.md's
        // "Verified facts"), so replicating the exact generated volume for an arbitrary background
        // object isn't practical. Instead this reads the nominal per-class prefab radius (the same
        // "Procedural/PA_<class>" / "Procedural/PC_<class>" asset stock itself loads in
        // ModuleAsteroid.OnStart/ModuleComet.OnStart, confirmed by decompiling both) and treats it
        // as an idealized sphere - a deliberate, documented approximation for an estimate/report
        // feature, not an attempt at exact per-instance fidelity.

        private static readonly Dictionary<string, double> radiusCache = new Dictionary<string, double>();
        private static double? asteroidDensity;
        private static double? cometDensity;

        // NOTE: ModuleAsteroid/ModuleComet.density is in KSP's universal mass unit - tonnes per m^3,
        // the same unit Part.mass/prefabMass always use (confirmed: PotatoRoid.cfg's own dummy
        // "mass = 150" is 150 tonnes, not kg) - so volume * density gives TONNES, not kilograms.
        // Missing the x1000 here was a real bug: every mass (and therefore every energy) came out
        // 1000x too small, silently clamping every impact to the penalty table's floor regardless
        // of class. Found while sanity-checking Class A vs Class D by hand.
        private const double KgPerTonne = 1000.0;

        private static bool TryGetMass(string objectClass, bool isComet, out double massKg)
        {
            massKg = 0.0;
            if (!TryGetNominalRadius(objectClass, isComet, out double radius)) return false;
            if (!TryGetDensity(isComet, out double density)) return false;

            double volume = (4.0 / 3.0) * Math.PI * radius * radius * radius;
            massKg = volume * density * KgPerTonne;
            Debug.Log(string.Format("[SENTRY] ImpactConsequence: nominal mass for class {0} ({1}) = {2:F1} kg (radius {3:F1} m, density {4:F4} t/m^3)",
                objectClass, isComet ? "comet" : "asteroid", massKg, radius, density));
            return massKg > 0.0;
        }

        // Live read of ModuleAsteroid.density / ModuleComet.density off the loaded part prefab -
        // never hardcoded, so an independent MM patch multiplying density (planned separately, not
        // part of this mod) is picked up automatically with zero coordination, per CLAUDE.md's
        // "mass must be read at runtime" rule. Cached once per process lifetime - prefabs don't
        // change after load. Note ModuleComet does NOT derive from ModuleAsteroid (confirmed by
        // decompiling both - each extends PartModule directly with its own separate `density`
        // field), so the two are read independently rather than via a shared base type.
        private static bool TryGetDensity(bool isComet, out double density)
        {
            if (isComet)
            {
                if (cometDensity.HasValue) { density = cometDensity.Value; return true; }
                AvailablePart ap = PartLoader.getPartInfoByName("PotatoComet");
                ModuleComet m = ap != null && ap.partPrefab != null ? ap.partPrefab.FindModuleImplementing<ModuleComet>() : null;
                if (m == null) { density = 0.0; return false; }
                cometDensity = m.density;
                density = cometDensity.Value;
                return true;
            }
            else
            {
                if (asteroidDensity.HasValue) { density = asteroidDensity.Value; return true; }
                AvailablePart ap = PartLoader.getPartInfoByName("PotatoRoid");
                ModuleAsteroid m = ap != null && ap.partPrefab != null ? ap.partPrefab.FindModuleImplementing<ModuleAsteroid>() : null;
                if (m == null) { density = 0.0; return false; }
                asteroidDensity = m.density;
                density = asteroidDensity.Value;
                return true;
            }
        }

        private static bool TryGetNominalRadius(string objectClass, bool isComet, out double radiusM)
        {
            radiusM = 0.0;
            if (string.IsNullOrEmpty(objectClass)) return false;

            string key = (isComet ? "PC_" : "PA_") + objectClass;
            if (radiusCache.TryGetValue(key, out radiusM)) return radiusM > 0.0;

            string path = "Procedural/" + key;
            float r = 0f;
            if (isComet)
            {
                ProceduralComet pc = Resources.Load<ProceduralComet>(path);
                if (pc != null) r = pc.radius;
            }
            else
            {
                ProceduralAsteroid pa = Resources.Load<ProceduralAsteroid>(path);
                if (pa != null) r = pa.radius;
            }
            radiusCache[key] = r;
            radiusM = r;
            return r > 0.0;
        }

        // ---- Burst altitude: dynamic-pressure descent + Collins-Melosh-Marcus dispersion --------
        // Collins, Melosh & Marcus (2005), "Earth Impact Effects Program," Meteoritics & Planetary
        // Science 40, 817-840; closed-form reproduced (with the ablative pancake-model derivation,
        // Eqns A11-A18) in Collins et al. (2017), "A numerical assessment of simple airblast models
        // of impact airbursts," same journal. z* is the altitude where dynamic pressure first
        // exceeds material strength (breakup, ignoring the debris cloud's subsequent spread); the
        // dispersion length l and the alpha threshold then push that down to the altitude of peak
        // energy deposition, z_b - the "burst altitude" actually used for classification here.
        private static (double burstAltitudeM, ConsequenceReport.Classification cls) BurstAltitude(
            double massKg, double bodyRadiusM, ImpactState state, CelestialBody home)
        {
            if (!home.atmosphere)
            {
                return (double.NaN, ConsequenceReport.Classification.Crater);
            }

            double mu = home.gravParameter;
            double r0 = state.RelPos.magnitude;
            double v0 = state.RelVel.magnitude; // inertial orbital speed - vis-viva needs this, not surface speed
            double specificEnergy = 0.5 * v0 * v0 - mu / r0;
            double a = -mu / (2.0 * specificEnergy);

            Vector3d rotFrameVel0 = Vector3d.Cross(home.angularVelocity, state.RelPos.xzy);
            double surfaceSpeed0 = (state.RelVel.xzy - rotFrameVel0).magnitude;

            // Surface-relative speed at another radius, approximated by scaling with the inertial
            // vis-viva speed ratio (the rotational contribution's magnitude/direction changes little
            // over the burst-altitude range compared with the body's own radius) - a deliberate
            // simplification for an estimate feature, not a full vector re-derivation per altitude.
            Func<double, double> surfaceSpeedAtRadius = r =>
            {
                double v = Math.Sqrt(Math.Max(0.0, mu * (2.0 / r - 1.0 / a)));
                return surfaceSpeed0 * (v / Math.Max(1.0, v0));
            };

            double topAltitude = home.atmosphereDepth;
            double step = Math.Max(1.0, AdvancedSettings.BurstStepMeters);
            double strength = AdvancedSettings.RubblePileStrengthPa;

            double zStar = double.NaN;
            for (double h = topAltitude; h >= 0.0; h -= step)
            {
                double r = home.Radius + h;
                double vs = surfaceSpeedAtRadius(r);
                double rho = home.GetDensity(home.GetPressure(h), home.GetTemperature(h));
                double q = 0.5 * rho * vs * vs;
                if (q >= strength)
                {
                    zStar = h;
                    break;
                }
            }

            if (double.IsNaN(zStar))
            {
                // Never crosses the strength threshold before reaching the ground - hits intact.
                return (double.NaN, ConsequenceReport.Classification.Crater);
            }

            double sinTheta = Math.Abs(Vector3d.Dot(state.RelVel, state.RelPos))
                / Math.Max(1.0, state.RelVel.magnitude * state.RelPos.magnitude);
            if (sinTheta < 0.05) sinTheta = 0.05; // guard a near-grazing sample

            double rhoI = massKg / Math.Max(1.0, (4.0 / 3.0) * Math.PI * bodyRadiusM * bodyRadiusM * bodyRadiusM);
            double rho0 = home.GetDensity(home.GetPressure(0.0), home.GetTemperature(0.0));
            double cd = AdvancedSettings.DragCoefficient;
            double h_ = AdvancedSettings.AtmosphereScaleHeightM;

            double l = 2.0 * bodyRadiusM * sinTheta * Math.Sqrt(rhoI / (cd * Math.Max(1e-12, rho0)))
                * Math.Exp(zStar / (2.0 * h_));
            double alpha = AdvancedSettings.BurstDispersionAlpha;
            double zBurst = zStar - 2.0 * h_ * Math.Log(1.0 + (l / (2.0 * h_)) * Math.Sqrt(-Math.Log(alpha)));

            if (zBurst <= 0.0)
            {
                return (double.NaN, ConsequenceReport.Classification.Crater);
            }
            if (zBurst > AdvancedSettings.AirburstAltitudeThresholdM)
            {
                return (zBurst, ConsequenceReport.Classification.Airburst);
            }
            return (zBurst, ConsequenceReport.Classification.GroundBurst);
        }

        // ---- Drag-inclusive descent speed: predicted (unloaded) path only -----------------------
        // BurstAltitude above answers "does it break up, and where" using a deliberately pure
        // vis-viva (no-drag) peak-dynamic-pressure criterion - that's an existing, separately
        // accepted approximation and is left untouched here. This answers a different question:
        // given that classification, how much speed would a REAL object plausibly have left by the
        // time it reaches that altitude, once actual atmospheric drag is accounted for. A
        // lightweight, Trajectories(the mod)-style numerical descent: gravity's contribution is
        // exact (the same vis-viva relation BurstAltitude already uses - conservative and path-
        // independent, so there's nothing to gain from integrating it numerically too), while
        // drag - the only genuinely dissipative, path-dependent term - is integrated step by step
        // with a simple forward-Euler update. Only ever called for the PREDICTED path
        // (state.IsObserved == false, see Compute) - TryGetActualState's real telemetry already IS
        // the answer this approximates, measured directly, and is never run back through this.
        private static double IntegrateDragSpeed(double massKg, double objectRadiusM, double stopAltitudeM,
            ImpactState state, CelestialBody home)
        {
            double mu = home.gravParameter;
            double r0 = state.RelPos.magnitude;
            double v0 = state.RelVel.magnitude;
            double specificEnergy = 0.5 * v0 * v0 - mu / r0;
            double a = -mu / (2.0 * specificEnergy);

            Vector3d rotFrameVel0 = Vector3d.Cross(home.angularVelocity, state.RelPos.xzy);
            double surfaceSpeed0 = (state.RelVel.xzy - rotFrameVel0).magnitude;

            // Same vis-viva-ratio approximation BurstAltitude uses for its own "vs" - the exact,
            // conservative (gravity-only) surface speed at any radius along this same orbit.
            Func<double, double> vacuumSurfaceSpeedAtRadius = r =>
            {
                double v = Math.Sqrt(Math.Max(0.0, mu * (2.0 / r - 1.0 / a)));
                return surfaceSpeed0 * (v / Math.Max(1.0, v0));
            };

            double sinTheta = Math.Abs(Vector3d.Dot(state.RelVel, state.RelPos))
                / Math.Max(1.0, state.RelVel.magnitude * state.RelPos.magnitude);
            if (sinTheta < 0.05) sinTheta = 0.05; // guard a near-grazing sample, same as BurstAltitude

            double area = Math.PI * objectRadiusM * objectRadiusM; // nominal-sphere cross-section
            double cd = AdvancedSettings.DragCoefficient;

            double topAltitude = home.atmosphereDepth;
            double step = Math.Max(1.0, AdvancedSettings.BurstStepMeters);

            double speed = surfaceSpeed0;
            double prevVacSpeed = vacuumSurfaceSpeedAtRadius(home.Radius + topAltitude);

            for (double h = topAltitude; h > stopAltitudeM; h -= step)
            {
                double hNext = Math.Max(stopAltitudeM, h - step);
                double vacSpeedNext = vacuumSurfaceSpeedAtRadius(home.Radius + hNext);

                // Gravity's exact contribution over this step (can be negative if the step climbs,
                // which never happens on a descending path, but stays correct either way).
                speed += vacSpeedNext - prevVacSpeed;
                prevVacSpeed = vacSpeedNext;

                // Drag's dissipative contribution: forward-Euler using local density at the step's
                // midpoint altitude and the current (already gravity-updated) speed. dt derived
                // from the vertical descent rate (sinTheta component of the velocity vector).
                double midAltitude = (h + hNext) * 0.5;
                double rho = home.GetDensity(home.GetPressure(midAltitude), home.GetTemperature(midAltitude));
                double dt = (h - hNext) / Math.Max(1.0, speed * sinTheta);
                double dragDecel = 0.5 * rho * speed * speed * cd * area / Math.Max(1.0, massKg);
                speed = Math.Max(0.0, speed - dragDecel * dt);
            }

            return speed;
        }

        // ---- Facility check: energy band decides both the damage radius AND how much of KSC ----
        // ---- a hit within that radius would plausibly take out (SentryScenario applies it). -----
        private static (bool damaged, double radiusM, ConsequenceReport.FacilityTier tier) FacilityCheck(
            double energyKt, double latDeg, double lonDeg, SentrySettings settings)
        {
            if (settings != null && !settings.facilityDestructionEnabled)
                return (false, 0.0, ConsequenceReport.FacilityTier.None);

            double baseRadius;
            ConsequenceReport.FacilityTier tier;
            if (energyKt < AdvancedSettings.FacilityThresholdLowKt)
            {
                baseRadius = 0.0;
                tier = ConsequenceReport.FacilityTier.None;
            }
            else if (energyKt < AdvancedSettings.FacilityThresholdMidKt)
            {
                baseRadius = AdvancedSettings.FacilityRadiusLowM;
                tier = ConsequenceReport.FacilityTier.Single;
            }
            else if (energyKt < AdvancedSettings.FacilityThresholdHighKt)
            {
                baseRadius = AdvancedSettings.FacilityRadiusMidM;
                tier = ConsequenceReport.FacilityTier.Several;
            }
            else
            {
                baseRadius = AdvancedSettings.FacilityRadiusHighM;
                tier = ConsequenceReport.FacilityTier.Levelled;
            }

            if (baseRadius <= 0.0) return (false, 0.0, ConsequenceReport.FacilityTier.None);
            double radius = baseRadius * AdvancedSettings.AtmosphereThinnessMultiplier;

            double distance = SurfaceDistance(latDeg, lonDeg, AdvancedSettings.KscLatitudeDeg, AdvancedSettings.KscLongitudeDeg);
            bool damaged = distance <= radius;
            return (damaged, radius, damaged ? tier : ConsequenceReport.FacilityTier.None);
        }

        private static double SurfaceDistance(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
        {
            // Haversine great-circle angular distance, scaled by the home body's own radius
            // (deliberately not passed in - this is only ever compared against a radius already in
            // metres, and the caller's home body is always the one KSC actually sits on).
            CelestialBody home = FlightGlobals.GetHomeBody();
            double bodyRadius = home != null ? home.Radius : 600000.0;

            double phi1 = lat1Deg * Math.PI / 180.0;
            double phi2 = lat2Deg * Math.PI / 180.0;
            double dPhi = (lat2Deg - lat1Deg) * Math.PI / 180.0;
            double dLambda = (lon2Deg - lon1Deg) * Math.PI / 180.0;

            double sinDPhi = Math.Sin(dPhi / 2.0);
            double sinDLambda = Math.Sin(dLambda / 2.0);
            double aHav = sinDPhi * sinDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinDLambda * sinDLambda;
            double c = 2.0 * Math.Atan2(Math.Sqrt(aHav), Math.Sqrt(Math.Max(0.0, 1.0 - aHav)));
            return bodyRadius * c;
        }

        // ---- Penalty model: single power-law formula, minus offset, clamped at 0 ----------------
        // Rebalanced, replacing the earlier three-region interpolation (flat-zero /
        // log-space ramp / piecewise log-log segments) with one smooth formula, per the owner's
        // explicit request. Also chosen for a real mathematical property the owner wants: for a
        // comet's fragments to sum to the same total penalty as one intact impact of the same
        // total energy, the curve must be LINEAR in energy - any curvature makes fragmenting
        // either cost strictly more or strictly less than staying intact, in one direction only.
        // The original per-class calibration table isn't linear (it has a real S-shape - the
        // D->E->F jump is far steeper in log-log space than C->D or F->I), so no single smooth
        // formula reproduces it exactly; three candidate fits (least-squares power law, a
        // fragment-weighted variant, and strict linearity) were computed and compared, and the
        // owner picked the least-squares fit - its exponent (~0.96) is close enough to 1 to keep
        // fragment totals close to consistent while still tracking the original table reasonably
        // well everywhere except Class F (see below).
        //
        // PenaltyCurveK/PenaltyCurveExponent: least-squares fit of penalty = k * energyKt^p,
        // fitted in log-log space through the five real calibration anchors (Class C/D/E/F/I:
        // 4.3/17/69/300/1400 kt -> 5/15/60/400/1000 rep). PenaltyCurveOffset is that curve's own
        // value at Class B's energy (0.86 kt) - subtracting it and clamping at 0 means Class A and
        // B both read exactly 0 (B by construction, A because the curve is monotonic and A's
        // energy is below B's), with C upward ramping in smoothly, no piecewise boundaries
        // anywhere. A large enough impact needs no special-case extrapolation either - unlike the
        // old piecewise table, this formula has no top anchor to run out of; it just keeps
        // growing with energy, so the reputation floor (SentryScenario.ReportConfirmedImpact) is
        // the only thing that can still cap an individual penalty, per the owner's separate,
        // already-implemented correction that impacts should never be artificially limited.
        //
        // Resulting values (vs. the original per-class table, for reference): C ~3.7 (was 5),
        // D ~16.4 (was 15), E ~65.9 (was 60), F ~274 (was 400, -31%), I ~1204 (was 1000, +20%). F
        // is the largest divergence, since it sits at the steepest part of the original curve - an
        // accepted trade-off, confirmed with the owner, for one formula with near-linear fragment
        // consistency instead of three exactly-calibrated regions.
        private const double PenaltyCurveK = 1.1463;
        private const double PenaltyCurveExponent = 0.9605;
        private const double PenaltyCurveOffset = 0.992; // curve's own value at Class B's energy (0.86 kt)

        private static double InterpolatePenaltyTable(double energyKt)
        {
            double raw = PenaltyCurveK * Math.Pow(energyKt, PenaltyCurveExponent);
            return Math.Max(raw - PenaltyCurveOffset, 0.0);
        }

        // Continuous (non-banded) lead-time mitigation - see AdvancedSettings.MitigationFloor/
        // MitigationDecayPerYear/MitigationPower for the calibration this shape targets (full
        // penalty at zero warning, ~90% at a routine ~100-day tracking-station warning, ~30% at
        // the 1-year calibration point).
        private static double MitigationFactor(double leadYears)
        {
            double floor = AdvancedSettings.MitigationFloor;
            double decay = AdvancedSettings.MitigationDecayPerYear;
            double power = AdvancedSettings.MitigationPower;
            double t = Math.Pow(Math.Max(0.0, leadYears), power);
            return floor + (1.0 - floor) * Math.Exp(-decay * t);
        }
    }
}
