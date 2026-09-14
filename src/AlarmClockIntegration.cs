namespace Sentry
{
    // Thin wrapper around KSP's stock Alarm Clock (AlarmClockScenario / AlarmTypeRaw), isolating
    // every call into that API in one place. AlarmClockScenario.CreateAlarmByType/AddAlarm/TryGetAlarm/DeleteAlarm are all
    // static methods that internally no-op (return null/false) if AlarmClockScenario.Instance
    // isn't loaded yet, so no null-check on Instance is needed here.
    public static class AlarmClockIntegration
    {
        // Creates a silent (no native popup, no native sound) alarm whose only effect is to kill
        // time warp as UT approaches `ut` - confirmed by decompiling AlarmClockScenario.
        // HandleWarpActions: it steps time warp down one level per frame once the current UT is
        // within the real-time window needed to reach 1x by `ut`, with TriggerAlarm() as a
        // backstop that forces warp to 1x regardless if the ramp didn't finish in time. This is
        // what stops an impact from being skipped over at high warp. SENTRY's own screen
        // message + klaxon (fired separately - see the "impact imminent" check in
        // SentryScenario.ApplyResult) is the only player-facing indicator; the native
        // AlarmClockMessageDialog is deliberately suppressed (AlarmActions.MessageEnum.No).
        public static uint CreateAlarm(Vessel vessel, string title, string description, double ut)
        {
            AlarmTypeBase alarm = AlarmClockScenario.CreateAlarmByType(typeof(AlarmTypeRaw));
            if (alarm == null) return 0;

            alarm.title = title;
            alarm.description = description;
            alarm.ut = ut;
            if (vessel != null) alarm.vesselId = vessel.persistentId;
            alarm.actions = new AlarmActions(AlarmActions.WarpEnum.KillWarp, AlarmActions.MessageEnum.No, false, true);

            return AlarmClockScenario.AddAlarm(alarm) ? alarm.Id : 0;
        }

        // One-shot, effectively-instant warp stop that reuses the stock Alarm Clock's own trigger
        // path instead of calling TimeWarp.SetRate directly. Confirmed by decompiling
        // AlarmClockScenario.Update/AlarmTypeBase.TriggerAlarm: an alarm whose `ut` is already <=
        // the current UT has its TimeToAlarm <= 0 on the very next AlarmClockScenario.Update()
        // tick, which triggers immediately - not the gradual per-frame ramp HandleWarpActions does
        // for a still-future alarm - via TimeWarp.fetch.CancelAutoWarp() followed by
        // TimeWarp.SetRate(0, instant: true). The CancelAutoWarp() call is the part our own direct
        // SetRate call was missing, which is the likely reason it wasn't reliably stopping warp.
        // Self-cleaning: deleteWhenDone removes the alarm from the stock UI the instant it fires.
        public static void StopWarpNow(Vessel vessel, string title, string description)
        {
            CreateAlarm(vessel, title, description, Planetarium.GetUniversalTime());
        }

        public static void UpdateAlarmUT(uint id, double newUt)
        {
            if (id == 0) return;
            AlarmTypeBase alarm;
            if (AlarmClockScenario.TryGetAlarm(id, out alarm) && alarm != null)
            {
                alarm.ut = newUt;
            }
        }

        // Safe no-op if id is 0 or the alarm is already gone (fired with deleteWhenDone, or the
        // player removed it manually via the stock Alarm Clock UI). The AlarmExists check isn't
        // just defensive: DeleteAlarm on an unknown id logs an [ERR] line, and an impactor's alarm
        // self-deletes the moment it fires, so the normal "record leaves Impact state right after
        // the alarm went off" path was reliably producing red errors in KSP.log for a completely
        // expected situation.
        public static void RemoveAlarm(uint id)
        {
            if (id == 0) return;
            if (!AlarmClockScenario.AlarmExists(id)) return;
            AlarmClockScenario.DeleteAlarm(id);
        }

        // True if `id` is nonzero AND the stock Alarm Clock still has it. Lets a caller notice a
        // record whose alarm the player deleted by hand via the stock UI (ArmAlarm's old guard was
        // just "id != 0", which stayed true forever in that case and silently left the record with
        // no working alarm for the rest of its time in Impact state).
        public static bool IsArmed(uint id)
        {
            return id != 0 && AlarmClockScenario.AlarmExists(id);
        }
    }
}
