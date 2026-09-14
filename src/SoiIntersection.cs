using System;
using UnityEngine;

namespace Sentry
{
    public struct EncounterResult
    {
        public bool EncounterFound;
        public bool AlreadyInsideSoi;
        public double EntryUT;
        public double CapturePeA;
        public bool IsImpact;
        public double ThresholdAltitude;
        public string Note;

        // When the object reaches the threshold altitude (atmosphere top, or the surface for an
        // airless body). Only meaningful when IsImpact is true. This is the "impact UT" the alert
        // state machine keys on.
        public double ImpactUT;
        // When the capture orbit reaches periapsis (for impacts this is a point below the
        // threshold the object never actually reaches; still useful as a sanity check).
        public double PeriapsisUT;

        // Diagnostics only - lets a "no encounter" verdict be sanity-checked from the log
        // instead of being a black box.
        public double MoidDistance;      // closest the two orbit curves ever come, ignoring time
        public double ClosestSampledGap; // closest approach actually sampled while stepping in time
        public double GameTimeToPe;      // the game's own timeToPe on the capture orbit (cross-check)
    }

    // Reproduces KSP's own patched-conic model: pure two-body around the Sun until the asteroid
    // crosses into the home body's sphere of influence, then pure two-body around the home body.
    // Must match what the game's own encounter node would show, not "real" n-body physics.
    public static class SoiIntersection
    {
        private const double MinStepSeconds = 60.0;
        private const double MaxStepSeconds = 86400.0;
        // entryUT is the first bracket edge *inside* the SOI, so any slack here biases the
        // reconstructed periapsis high. 1 s gave +2..15 m vs the game's readout; 0.01 s is ~7
        // extra iterations.
        private const double BisectionToleranceSeconds = 0.01;
        private const int MaxAdvanceIterations = 200000; // safety net against pathological loops.

        // MOID (minimum orbit intersection distance) is found in two stages:
        //   1. a coarse grid over both orbits' true anomalies (CoarseSamples x CoarseSamples cells),
        //   2. local refinement (compass/pattern search) from each local minimum of that grid. Cost is
        // ~57,600 cheap distance checks plus a few hundred orbit evaluations - well under 1 ms.
        private const int CoarseSamples = 240;        // 1.5 degrees on a full ellipse
        private const int MaxBasinsToRefine = 6;
        private const double RefineStopRadians = 1e-7; // ~1.4 mm of arc at Kerbin's orbital radius
        private const int MaxRefineIterations = 20000;
        private const double HyperbolicAsymptoteMarginDeg = 2.0;

        // Kept only for validation runs (verbose F11): the old 2000-sample brute force.
        private const int BruteForceSampleCount = 2000;

        public static EncounterResult Predict(Orbit astOrbit, CelestialBody homeBody, double startUT)
        {
            double threshold = homeBody.atmosphere ? homeBody.atmosphereDepth : 0.0;

            // Already-inside case: the asteroid's reference body IS the home body, so there's no
            // SOI crossing left to find - it's already on a capture orbit.
            if (astOrbit.referenceBody == homeBody)
            {
                bool escapes = astOrbit.eccentricity >= 1.0 || astOrbit.ApR > homeBody.sphereOfInfluence;
                bool impact = astOrbit.PeA < threshold;
                EncounterResult inside = new EncounterResult
                {
                    EncounterFound = true,
                    AlreadyInsideSoi = true,
                    EntryUT = startUT,
                    CapturePeA = astOrbit.PeA,
                    IsImpact = impact,
                    ThresholdAltitude = threshold,
                    Note = escapes ? "already inside SOI, orbit escapes after periapsis" : "already inside SOI",
                    MoidDistance = double.NaN,
                    ClosestSampledGap = double.NaN,
                    GameTimeToPe = double.NaN,
                    ImpactUT = double.NaN,
                    PeriapsisUT = double.NaN
                };
                // Time from "now" to periapsis / threshold radius on the current orbit.
                double rNow = astOrbit.GetRadiusAtUT(startUT);
                double nuNow = astOrbit.TrueAnomalyAtUT(startUT); // game's own; sign tells inbound/outbound
                double dtPe = TimeBetweenTrueAnomalies(astOrbit, homeBody, nuNow, 0.0);
                if (dtPe < 0.0 && astOrbit.eccentricity < 1.0) dtPe += astOrbit.period; // outbound on a bound orbit: next Pe is a lap away
                inside.PeriapsisUT = startUT + dtPe;
                if (impact)
                {
                    double nuHit = -InboundTrueAnomalyAtRadius(astOrbit, homeBody.Radius + threshold);
                    double dtHit = TimeBetweenTrueAnomalies(astOrbit, homeBody, nuNow, nuHit);
                    if (dtHit < 0.0 && astOrbit.eccentricity < 1.0) dtHit += astOrbit.period;
                    inside.ImpactUT = startUT + dtHit;
                }
                return inside;
            }

            if (astOrbit.referenceBody != homeBody.orbit.referenceBody)
            {
                return Miss(threshold, "reference body isn't the home body's parent - unsupported (only the home body's own neighbourhood is scanned)");
            }

            double rSoi = homeBody.sphereOfInfluence;

            // Step 1: cheap radial-band rejection.
            double homeApR = homeBody.orbit.ApR;
            double homePeR = homeBody.orbit.PeR;
            double astPeR = astOrbit.PeR;
            double astApR = astOrbit.eccentricity >= 1.0 ? double.PositiveInfinity : astOrbit.ApR;

            if (astPeR > homeApR + rSoi || astApR < homePeR - rSoi)
            {
                return Miss(threshold, "radial band rejection");
            }

            // Step 2: geometric MOID filter - ignores time, just asks "do these two curves ever
            // come within R_SOI of each other anywhere along their shape?"
            double moid = MinCurveSeparation(astOrbit, homeBody.orbit);
            if (moid > rSoi)
            {
                EncounterResult moidMiss = Miss(threshold, "MOID filter rejection");
                moidMiss.MoidDistance = moid;
                return moidMiss;
            }

            // Step 3: conservative time advancement. vMax bounds how fast the two bodies could
            // possibly be closing, so stepping by (currentGap - R_SOI) / vMax can never jump past
            // an encounter, no matter how far out we start.
            double vMax = astOrbit.getOrbitalSpeedAtDistance(astOrbit.PeR)
                        + homeBody.orbit.getOrbitalSpeedAtDistance(homeBody.orbit.PeR);

            double maxHorizon = startUT + HorizonSeconds(astOrbit, homeBody);

            double tPrev = startUT;

            if (Distance(astOrbit, homeBody.orbit, tPrev) < rSoi)
            {
                // Shouldn't normally happen (referenceBody check above would have caught it), but
                // guard anyway rather than bisecting a zero-width bracket.
                EncounterResult odd = Miss(threshold, "already within R_SOI at start time but reference body is not the home body (unexpected) - skipped");
                odd.MoidDistance = moid;
                return odd;
            }

            double t = tPrev;
            int iterations = 0;
            bool bracketed = false;
            double closestGap = double.PositiveInfinity;

            while (t < maxHorizon && iterations < MaxAdvanceIterations)
            {
                double d = Distance(astOrbit, homeBody.orbit, t);
                if (d < closestGap) closestGap = d;

                if (d < rSoi)
                {
                    bracketed = true;
                    break;
                }

                double step = (d - rSoi) / vMax;
                if (step < MinStepSeconds) step = MinStepSeconds;
                if (step > MaxStepSeconds) step = MaxStepSeconds;

                tPrev = t;
                t += step;
                iterations++;
            }

            if (!bracketed)
            {
                EncounterResult timeMiss = Miss(threshold, iterations >= MaxAdvanceIterations
                    ? "gave up: hit iteration cap"
                    : "no SOI crossing within time horizon");
                timeMiss.MoidDistance = moid;
                timeMiss.ClosestSampledGap = closestGap;
                return timeMiss;
            }

            // Step 4: bisect [tPrev, t] down to sub-second precision on d(t) - R_SOI.
            double lo = tPrev, hi = t;
            while (hi - lo > BisectionToleranceSeconds)
            {
                double mid = 0.5 * (lo + hi);
                double dMid = Distance(astOrbit, homeBody.orbit, mid);
                if (dMid >= rSoi) lo = mid; else hi = mid;
            }
            double entryUT = hi;

            // Step 5: reconstruct the capture orbit from relative state vectors at entry.
            Vector3d relPos = astOrbit.getRelativePositionAtUT(entryUT) - homeBody.orbit.getRelativePositionAtUT(entryUT);
            Vector3d relVel = astOrbit.getOrbitalVelocityAtUT(entryUT) - homeBody.orbit.getOrbitalVelocityAtUT(entryUT);

            Orbit captureOrbit = new Orbit();
            captureOrbit.UpdateFromStateVectors(relPos.xzy, relVel.xzy, homeBody, entryUT);

            // Step 6: verdict, plus timing along the capture orbit. At entry the object sits on
            // the SOI boundary heading inwards, i.e. at true anomaly -nu(R_SOI). It reaches the
            // threshold radius at -nu(R + threshold) and periapsis at 0.
            bool isImpact = captureOrbit.PeA < threshold;
            double nuEntry = -InboundTrueAnomalyAtRadius(captureOrbit, relPos.magnitude);
            double periapsisUT = entryUT + TimeBetweenTrueAnomalies(captureOrbit, homeBody, nuEntry, 0.0);
            double impactUT = double.NaN;
            if (isImpact)
            {
                double nuHit = -InboundTrueAnomalyAtRadius(captureOrbit, homeBody.Radius + threshold);
                impactUT = entryUT + TimeBetweenTrueAnomalies(captureOrbit, homeBody, nuEntry, nuHit);
            }

            return new EncounterResult
            {
                EncounterFound = true,
                EntryUT = entryUT,
                CapturePeA = captureOrbit.PeA,
                IsImpact = isImpact,
                ThresholdAltitude = threshold,
                Note = "SOI encounter found",
                ImpactUT = impactUT,
                PeriapsisUT = periapsisUT,
                MoidDistance = moid,
                ClosestSampledGap = closestGap,
                GameTimeToPe = captureOrbit.timeToPe
            };
        }

        // How far ahead a "no encounter" verdict is good for. Used by the scanner to decide when
        // an unchanged orbit needs re-evaluating.
        public static double HorizonSeconds(Orbit astOrbit, CelestialBody homeBody)
        {
            double horizon = 20.0 * homeBody.orbit.period;
            if (astOrbit.eccentricity < 1.0)
            {
                horizon = 20.0 * Math.Max(homeBody.orbit.period, astOrbit.period);
            }
            return horizon;
        }

        // Comet "close approach": does the object come within `radius` of the home body (without
        // necessarily entering the SOI), and if so how close and when? Same guards and the same
        // conservative time advance as Predict, but instead of bisecting a boundary crossing and
        // building a capture orbit, it walks through the pass in small steps to find the minimum
        // distance, then polishes that minimum with a golden-section search. Returns NaN (and
        // closestUT = NaN) when no approach within `radius` is found inside the search horizon.
        // Only meaningful when Predict found no SOI encounter - an SOI crossing is a closer pass
        // than anything this reports, and the caller treats it as the higher state.
        public static double ClosestApproach(Orbit astOrbit, CelestialBody homeBody, double startUT, double radius, out double closestUT)
        {
            closestUT = double.NaN;
            if (astOrbit.referenceBody != homeBody.orbit.referenceBody) return double.NaN;

            double homeApR = homeBody.orbit.ApR;
            double homePeR = homeBody.orbit.PeR;
            double astPeR = astOrbit.PeR;
            double astApR = astOrbit.eccentricity >= 1.0 ? double.PositiveInfinity : astOrbit.ApR;
            if (astPeR > homeApR + radius || astApR < homePeR - radius) return double.NaN;

            double moid = MinCurveSeparation(astOrbit, homeBody.orbit);
            if (moid > radius) return double.NaN;

            double vMax = astOrbit.getOrbitalSpeedAtDistance(astOrbit.PeR)
                        + homeBody.orbit.getOrbitalSpeedAtDistance(homeBody.orbit.PeR);
            double maxHorizon = startUT + HorizonSeconds(astOrbit, homeBody);

            // Advance until we're inside the sphere (cannot overshoot: see Predict step 3).
            double t = startUT;
            int iterations = 0;
            bool inside = false;
            while (t < maxHorizon && iterations < MaxAdvanceIterations)
            {
                double d = Distance(astOrbit, homeBody.orbit, t);
                if (d < radius)
                {
                    inside = true;
                    break;
                }
                double step = (d - radius) / vMax;
                if (step < MinStepSeconds) step = MinStepSeconds;
                if (step > MaxStepSeconds) step = MaxStepSeconds;
                t += step;
                iterations++;
            }
            if (!inside) return double.NaN;

            // Walk through the pass in fixed steps (50 per "radius" of travel at the fastest
            // possible closing speed, so the minimum can't hide between two samples) until we
            // leave the sphere again, remembering the closest sample.
            double walkStep = Math.Max(MinStepSeconds, radius / vMax / 50.0);
            double bestT = t;
            double bestD = Distance(astOrbit, homeBody.orbit, t);
            double tw = t;
            int walked = 0;
            while (walked < MaxAdvanceIterations)
            {
                tw += walkStep;
                double d = Distance(astOrbit, homeBody.orbit, tw);
                if (d < bestD)
                {
                    bestD = d;
                    bestT = tw;
                }
                if (d >= radius || tw > maxHorizon) break;
                walked++;
            }

            // Polish: golden-section search for the minimum of d(t) within one step either side
            // of the best sample, down to 1 s.
            const double gr = 0.6180339887498949;
            double lo = bestT - walkStep, hi = bestT + walkStep;
            double x1 = hi - gr * (hi - lo), x2 = lo + gr * (hi - lo);
            double f1 = Distance(astOrbit, homeBody.orbit, x1);
            double f2 = Distance(astOrbit, homeBody.orbit, x2);
            while (hi - lo > 1.0)
            {
                if (f1 < f2)
                {
                    hi = x2; x2 = x1; f2 = f1;
                    x1 = hi - gr * (hi - lo);
                    f1 = Distance(astOrbit, homeBody.orbit, x1);
                }
                else
                {
                    lo = x1; x1 = x2; f1 = f2;
                    x2 = lo + gr * (hi - lo);
                    f2 = Distance(astOrbit, homeBody.orbit, x2);
                }
            }
            double tBest = 0.5 * (lo + hi);
            double dBest = Distance(astOrbit, homeBody.orbit, tBest);
            if (dBest > bestD)
            {
                // Shouldn't happen (the refined minimum can only improve on the sample), but never
                // report something worse than what we actually observed.
                dBest = bestD;
                tBest = bestT;
            }

            closestUT = tBest;
            return dBest;
        }

        // Validation only (verbose scans, comets only): plain fixed-step sampling of the distance
        // between fromUT and toUT. Used to cross-check ClosestApproach the same way the brute-force
        // MOID cross-checks the two-stage MOID search.
        public static double BruteForceClosestApproachForValidation(Orbit astOrbit, Orbit homeOrbit, double fromUT, double toUT, double stepSeconds, out double closestUT)
        {
            double best = double.PositiveInfinity;
            closestUT = double.NaN;
            for (double t = fromUT; t <= toUT; t += stepSeconds)
            {
                double d = Distance(astOrbit, homeOrbit, t);
                if (d < best)
                {
                    best = d;
                    closestUT = t;
                }
            }
            return best;
        }

        private static EncounterResult Miss(double threshold, string note)
        {
            return new EncounterResult
            {
                EncounterFound = false,
                IsImpact = false,
                ThresholdAltitude = threshold,
                Note = note,
                ImpactUT = double.NaN,
                PeriapsisUT = double.NaN,
                MoidDistance = double.NaN,
                ClosestSampledGap = double.NaN,
                GameTimeToPe = double.NaN
            };
        }

        private static double Distance(Orbit a, Orbit b, double ut)
        {
            Vector3d posA = a.getRelativePositionAtUT(ut);
            Vector3d posB = b.getRelativePositionAtUT(ut);
            return (posA - posB).magnitude;
        }

        // ---- Kepler timing helpers -------------------------------------------------------------
        // Written out by hand rather than via the game's GetUTforTrueAnomaly etc. because those
        // have undocumented sign/wrap conventions for hyperbolic orbits. The result is cross-
        // checked against the game's own timeToPe in the verbose log.

        // True anomaly (positive, radians) at which the orbit has the given radius. Clamped: if
        // the radius is below periapsis returns 0, above apoapsis returns the maximum.
        private static double InboundTrueAnomalyAtRadius(Orbit orbit, double radius)
        {
            double e = orbit.eccentricity;
            double p = orbit.semiMajorAxis * (1.0 - e * e); // semi-latus rectum; positive for hyperbolic too (a < 0, 1-e^2 < 0)
            if (e < 1e-12) return 0.0;
            double cosNu = (p / radius - 1.0) / e;
            if (cosNu > 1.0) cosNu = 1.0;
            if (cosNu < -1.0) cosNu = -1.0;
            return Math.Acos(cosNu);
        }

        // Time to go from true anomaly nuFrom to nuTo along the orbit (negative if nuTo is
        // "earlier"). Handles elliptic and hyperbolic cases.
        private static double TimeBetweenTrueAnomalies(Orbit orbit, CelestialBody body, double nuFrom, double nuTo)
        {
            double e = orbit.eccentricity;
            double mu = body.gravParameter;
            double a = Math.Abs(orbit.semiMajorAxis);
            double n = Math.Sqrt(mu / (a * a * a)); // mean motion

            if (e < 1.0)
            {
                return (MeanAnomalyElliptic(nuTo, e) - MeanAnomalyElliptic(nuFrom, e)) / n;
            }

            if (e < 1.0 + 1e-9) e = 1.0 + 1e-9; // parabolic edge case: nudge to hyperbolic
            return (MeanAnomalyHyperbolic(nuTo, e) - MeanAnomalyHyperbolic(nuFrom, e)) / n;
        }

        private static double MeanAnomalyElliptic(double nu, double e)
        {
            double E = 2.0 * Math.Atan2(Math.Sqrt(1.0 - e) * Math.Sin(nu * 0.5), Math.Sqrt(1.0 + e) * Math.Cos(nu * 0.5));
            return E - e * Math.Sin(E);
        }

        private static double MeanAnomalyHyperbolic(double nu, double e)
        {
            // sinh(H) = sqrt(e^2 - 1) sin(nu) / (1 + e cos(nu))
            double sinhH = Math.Sqrt(e * e - 1.0) * Math.Sin(nu) / (1.0 + e * Math.Cos(nu));
            double H = Math.Log(sinhH + Math.Sqrt(sinhH * sinhH + 1.0)); // asinh
            return e * Math.Sinh(H) - H;
        }

        // ---- MOID ------------------------------------------------------------------------------

        private struct CurveSamples
        {
            public double[] Nu;
            public Vector3d[] Pos;
            public bool Wraps;   // full ellipse: index wraps around
            public double NuMin; // valid true-anomaly range (hyperbolic only)
            public double NuMax;
        }

        private static double MinCurveSeparation(Orbit astOrbit, Orbit homeOrbit)
        {
            CurveSamples ast = SampleCurve(astOrbit, CoarseSamples);
            CurveSamples home = SampleCurve(homeOrbit, CoarseSamples);

            int na = ast.Pos.Length;
            int nh = home.Pos.Length;
            double[] grid = new double[na * nh];
            for (int i = 0; i < na; i++)
            {
                Vector3d ap = ast.Pos[i];
                int row = i * nh;
                for (int j = 0; j < nh; j++)
                {
                    grid[row + j] = (ap - home.Pos[j]).magnitude;
                }
            }

            // Collect local minima of the coarse grid (cells no larger than any of their 8
            // neighbours), keep the best few, refine each.
            int[] bestI = new int[MaxBasinsToRefine];
            int[] bestJ = new int[MaxBasinsToRefine];
            double[] bestD = new double[MaxBasinsToRefine];
            int found = 0;
            for (int k = 0; k < MaxBasinsToRefine; k++) bestD[k] = double.PositiveInfinity;

            for (int i = 0; i < na; i++)
            {
                for (int j = 0; j < nh; j++)
                {
                    double d = grid[i * nh + j];
                    if (!IsLocalMin(grid, na, nh, i, j, d, ast.Wraps)) continue;

                    // Insert into the sorted "best" list if it beats the worst kept.
                    int slot = found < MaxBasinsToRefine ? found : MaxBasinsToRefine - 1;
                    if (found == MaxBasinsToRefine && d >= bestD[slot]) continue;
                    bestI[slot] = i; bestJ[slot] = j; bestD[slot] = d;
                    if (found < MaxBasinsToRefine) found++;
                    for (int k = slot; k > 0 && bestD[k] < bestD[k - 1]; k--)
                    {
                        double td = bestD[k]; bestD[k] = bestD[k - 1]; bestD[k - 1] = td;
                        int ti = bestI[k]; bestI[k] = bestI[k - 1]; bestI[k - 1] = ti;
                        int tj = bestJ[k]; bestJ[k] = bestJ[k - 1]; bestJ[k - 1] = tj;
                    }
                }
            }

            double coarseStep = 2.0 * Math.PI / CoarseSamples;
            double min = double.PositiveInfinity;
            for (int k = 0; k < found; k++)
            {
                double refined = RefineMinimum(astOrbit, homeOrbit, ast.Nu[bestI[k]], home.Nu[bestJ[k]], coarseStep, ast.NuMin, ast.NuMax);
                if (refined < min) min = refined;
            }
            if (found == 0)
            {
                // Degenerate (can't happen on a finite grid, but never return "no data").
                for (int idx = 0; idx < grid.Length; idx++) if (grid[idx] < min) min = grid[idx];
            }
            return min;
        }

        private static bool IsLocalMin(double[] grid, int na, int nh, int i, int j, double d, bool astWraps)
        {
            for (int di = -1; di <= 1; di++)
            {
                int ii = i + di;
                if (ii < 0 || ii >= na)
                {
                    if (!astWraps) continue;
                    ii = (ii + na) % na;
                }
                for (int dj = -1; dj <= 1; dj++)
                {
                    if (di == 0 && dj == 0) continue;
                    int jj = (j + dj + nh) % nh; // home orbit is always a full ellipse
                    if (grid[ii * nh + jj] < d) return false;
                }
            }
            return true;
        }

        // Compass search on (nuA, nuH): try a step in each of the four axis directions, move if
        // it helps, otherwise halve the step. Converges to the local minimum of a smooth function.
        private static double RefineMinimum(Orbit a, Orbit h, double nuA, double nuH, double step, double nuAMin, double nuAMax)
        {
            double best = Separation(a, h, nuA, nuH);
            int iterations = 0;
            while (step > RefineStopRadians && iterations < MaxRefineIterations)
            {
                iterations++;
                bool improved = false;

                double na = Clamp(nuA + step, nuAMin, nuAMax);
                double d = Separation(a, h, na, nuH);
                if (d < best) { best = d; nuA = na; improved = true; }

                na = Clamp(nuA - step, nuAMin, nuAMax);
                d = Separation(a, h, na, nuH);
                if (d < best) { best = d; nuA = na; improved = true; }

                d = Separation(a, h, nuA, nuH + step);
                if (d < best) { best = d; nuH = nuH + step; improved = true; }

                d = Separation(a, h, nuA, nuH - step);
                if (d < best) { best = d; nuH = nuH - step; improved = true; }

                if (!improved) step *= 0.5;
            }
            return best;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static double Separation(Orbit a, Orbit h, double nuA, double nuH)
        {
            return (a.getRelativePositionFromTrueAnomaly(nuA) - h.getRelativePositionFromTrueAnomaly(nuH)).magnitude;
        }

        private static CurveSamples SampleCurve(Orbit orbit, int count)
        {
            CurveSamples s = new CurveSamples();

            if (orbit.eccentricity < 1.0)
            {
                s.Wraps = true;
                s.NuMin = double.NegativeInfinity;
                s.NuMax = double.PositiveInfinity;
                s.Nu = new double[count];
                s.Pos = new Vector3d[count];
                for (int i = 0; i < count; i++)
                {
                    double nu = i * (2.0 * Math.PI / count);
                    s.Nu[i] = nu;
                    s.Pos[i] = orbit.getRelativePositionFromTrueAnomaly(nu);
                }
            }
            else
            {
                // Hyperbolic/parabolic: only the traversed range of true anomaly is physical.
                // Stay a couple of degrees clear of the asymptote, where radius blows up.
                double nuLimit = Math.Acos(-1.0 / orbit.eccentricity);
                double margin = HyperbolicAsymptoteMarginDeg * Math.PI / 180.0;
                s.Wraps = false;
                s.NuMin = -(nuLimit - margin);
                s.NuMax = nuLimit - margin;
                double stepNu = (s.NuMax - s.NuMin) / count;
                s.Nu = new double[count + 1];
                s.Pos = new Vector3d[count + 1];
                for (int i = 0; i <= count; i++)
                {
                    double nu = s.NuMin + i * stepNu;
                    s.Nu[i] = nu;
                    s.Pos[i] = orbit.getRelativePositionFromTrueAnomaly(nu);
                }
            }

            return s;
        }

        // Validation only: the phase 2 brute force (2000 x 2000 pairwise). ~28 ms per call.
        public static double BruteForceMoidForValidation(Orbit astOrbit, Orbit homeOrbit)
        {
            CurveSamples ast = SampleCurve(astOrbit, BruteForceSampleCount);
            CurveSamples home = SampleCurve(homeOrbit, BruteForceSampleCount);
            double min = double.PositiveInfinity;
            for (int i = 0; i < ast.Pos.Length; i++)
            {
                Vector3d ap = ast.Pos[i];
                for (int j = 0; j < home.Pos.Length; j++)
                {
                    double d = (ap - home.Pos[j]).magnitude;
                    if (d < min) min = d;
                }
            }
            return min;
        }
    }
}
