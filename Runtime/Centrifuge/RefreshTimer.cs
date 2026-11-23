using System;
using Unity.Services.Core.Scheduler.Internal;

namespace Unity.Services.Wire.Internal
{
    // Schedules a token refresh ahead of the server-side expiry of a connection or a subscription,
    // or a retry of a failed resubscription.
    class RefreshTimer
    {
        // The server disconnects once the token expires, so refresh at 80% of its lifetime.
        const double k_TtlRatio = 0.8;
        const double k_MinDelaySeconds = 1.0;

        readonly IActionScheduler m_ActionScheduler;
        readonly Configuration m_Config;
        long m_ActionId;

        public RefreshTimer(IActionScheduler actionScheduler, Configuration config)
        {
            m_ActionScheduler = actionScheduler;
            m_Config = config;
        }

        public void Schedule(bool expires, uint ttl, Action refresh)
        {
            Cancel();
            var delay = GetDelaySeconds(expires, m_Config.RefreshTtlOverrideSeconds ?? ttl);
            if (delay == null)
            {
                return;
            }

            ScheduleIn(delay.Value, refresh);
        }

        public void ScheduleIn(double delaySeconds, Action action)
        {
            Cancel();
            m_ActionId = m_ActionScheduler.ScheduleAction(() =>
            {
                m_ActionId = 0;
                action();
            }, delaySeconds);
        }

        public void Cancel()
        {
            if (m_ActionId == 0)
            {
                return;
            }

            m_ActionScheduler.CancelAction(m_ActionId);
            m_ActionId = 0;
        }

        internal static double? GetDelaySeconds(bool expires, uint ttl)
        {
            if (!expires || ttl == 0)
            {
                return null;
            }

            return Math.Max(k_MinDelaySeconds, ttl * k_TtlRatio);
        }
    }
}
