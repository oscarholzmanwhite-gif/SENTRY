using UnityEngine;

namespace Sentry
{
    // Debug keys for the scanner (Tracking Station scene only). The real scanning lives in
    // SentryScenario; this just pokes it.
    //   F11 = run a scan right now with per-object verbose logging (ignores the cache interval
    //         check but NOT the per-object cache - objects whose orbit and verdict are still
    //         valid are reported as cache hits).
    //   F8  = dump every persisted threat record.
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class OrbitScanner : MonoBehaviour
    {
        private void Start()
        {
            Debug.Log("[SENTRY] OrbitScanner loaded. F11 = verbose scan now, F8 = dump threat records.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (SentryScenario.Instance == null)
                {
                    Debug.Log("[SENTRY] F11: no SentryScenario instance in this scene.");
                    return;
                }
                SentryScenario.Instance.RequestScan(verbose: true);
                Debug.Log("[SENTRY] F11: verbose scan requested.");
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                DumpRecords();
            }
        }

        private void DumpRecords()
        {
            SentryScenario s = SentryScenario.Instance;
            if (s == null)
            {
                Debug.Log("[SENTRY] F8: no SentryScenario instance in this scene.");
                return;
            }

            double now = Planetarium.GetUniversalTime();
            Debug.Log(string.Format("[SENTRY] ---- Threat records (lastScanUT={0:F0}, now={1:F0}, scanRunning={2}) ----", s.LastScanUT, now, s.ScanRunning));
            int n = 0;
            foreach (ThreatRecord r in s.Records)
            {
                n++;
                Debug.Log(string.Format(
                    "[SENTRY]   {0} [{1}] class={2} comet={3}{4} state={5} entryUT={6:F0} impactUT={7:F0} periapsisUT={8:F0} capturePeA={9:F0}m moid={10:F0}m epoch={11:F1} ref={12} validUntil={13:F0} firstSeen={14:F0} lastEval={15:F0} lastChange={16:F0} autoTracked={17} alarmId={18} imminentAlertFired={19} lastKnownAlt={20:F0}m lastKnownSrfSpeed={21:F0}m/s captured={22} discoveryWarpStopped={23}",
                    r.Name, r.VesselId, r.ObjectClass, r.IsComet, r.IsComet ? "(" + r.CometType + ")" : "", r.State,
                    r.EntryUT, r.ImpactUT, r.PeriapsisUT, r.CapturePeA, r.Moid, r.OrbitEpoch, r.ReferenceBody,
                    r.ValidUntilUT, r.FirstSeenUT, r.LastEvaluatedUT, r.LastChangeUT, r.AutoTracked,
                    r.AlarmId, r.ImminentAlertFired, r.LastKnownAltitude, r.LastKnownSurfaceSpeed, r.Captured, r.DiscoveryWarpStopped));
            }
            Debug.Log(string.Format("[SENTRY] ---- {0} records ----", n));
        }
    }
}
