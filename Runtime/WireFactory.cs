using Unity.Services.Authentication.Internal;
using Unity.Services.Core.Scheduler.Internal;
using Unity.Services.Core.Telemetry.Internal;
using Unity.Services.Core.Threading.Internal;

namespace Unity.Services.Wire.Internal
{
    class WireFactory : IWireFactory
    {
        readonly IAccessToken m_AccessToken;
        readonly IAccessTokenObserver m_TokenObserver;
        readonly IPlayerId m_PlayerId;
        readonly IActionScheduler m_ActionScheduler;
        readonly IMetrics m_Metrics;
        readonly IUnityThreadUtils m_ThreadUtils;

        public WireFactory(IAccessToken accessToken, IAccessTokenObserver tokenObserver, IPlayerId playerId,
                           IActionScheduler actionScheduler, IMetrics metrics, IUnityThreadUtils threadUtils)
        {
            m_AccessToken = accessToken;
            m_TokenObserver = tokenObserver;
            m_PlayerId = playerId;
            m_ActionScheduler = actionScheduler;
            m_Metrics = metrics;
            m_ThreadUtils = threadUtils;
        }

        public IWire Create(string address)
        {
            var client = new Client(
                new Configuration
                {
                    token = m_AccessToken,
                    tokenObserver = m_TokenObserver,
                    address = address,
                },
                m_ActionScheduler, m_Metrics, m_ThreadUtils, new WebSocketFactory());

            m_PlayerId.PlayerIdChanged += client.OnIdentityChanged;

            return client;
        }
    }
}
