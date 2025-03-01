using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Core.Scheduler.Internal;
using Unity.Services.Core.Telemetry.Internal;
using Unity.Services.Core.Threading.Internal;
using Unity.Services.Wire.Protocol.Internal;
using UnityEditor;

namespace Unity.Services.Wire.Internal
{
    class Client : IWire
    {
        internal enum ConnectionState
        {
            Disconnected,
            Connected,
            Connecting,
            Disconnecting
        }

        public readonly ISubscriptionRepository SubscriptionRepository;

        TaskCompletionSource<ConnectionState> m_ConnectionCompletionSource;
        TaskCompletionSource<ConnectionState> m_DisconnectionCompletionSource;
        internal ConnectionState m_ConnectionState = ConnectionState.Disconnected;
        IWebSocket m_WebsocketClient;

        internal IBackoffStrategy m_Backoff;
        readonly CommandManager m_CommandManager;
        readonly Configuration m_Config;
        readonly IMetrics m_Metrics;
        readonly IUnityThreadUtils m_ThreadUtils;
        readonly IWebsocketFactory m_WebsocketFactory;

        event Action m_OnConnected;

        internal bool m_WantConnected = false;

        internal byte[] k_PongMessage;

        bool m_Disabled = false;

        bool m_Pong = false;
        UInt32 m_ServerPingIntervalS = 0;
        TimeSpan m_NetworkCheckInterval = TimeSpan.FromSeconds(5d);
        IActionScheduler m_ActionScheduler;
        long m_PingDeadlineScheduledId = 0;
        long m_ReconnectionActionId = 0;

        public Client(Configuration config, Core.Scheduler.Internal.IActionScheduler actionScheduler, IMetrics metrics,
                      IUnityThreadUtils threadUtils, IWebsocketFactory websocketFactory)
        {
            k_PongMessage = Encoding.UTF8.GetBytes("{}");
            m_ThreadUtils = threadUtils;
            m_Config = InitNetworkUtil(config);
            m_Metrics = metrics;
            m_ActionScheduler = actionScheduler;
            m_WebsocketFactory = websocketFactory;
            SubscriptionRepository = new ConcurrentDictSubscriptionRepository();
            SubscriptionRepository.SubscriptionCountChanged += (int subscriptionCount) =>
            {
                m_Metrics.SendGaugeMetric("subscription_count", subscriptionCount);
                Logger.Log($"Subscription count changed: {subscriptionCount}");
            };
            m_Backoff = new ExponentialBackoffStrategy();
            m_CommandManager = new CommandManager(config, actionScheduler);

#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
#endif
        }

        internal Configuration InitNetworkUtil(Configuration config)
        {
            if (config.NetworkUtil == null)
            {
                config.NetworkUtil = new NetworkUtil(); // Default impl
            }

            return config;
        }

#if UNITY_EDITOR
        async void PlayModeStateChanged(PlayModeStateChange state)
        {
            try
            {
                if (state != PlayModeStateChange.ExitingPlayMode)
                {
                    return;
                }

                Logger.Log("Exiting playmode, disconnecting, and cleaning subscription repo.");

                await DisconnectAsync();

                foreach (var sub in SubscriptionRepository.GetAll())
                {
                    sub.Value.Dispose();
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

#endif

        async Task<Reply> SendCommandAsync(UInt32 id, Command command)
        {
            if (m_Disabled || !m_WantConnected) return null;

            var time = DateTime.Now;
            var tags = new Dictionary<string, string> {{"method", command.GetMethod()}};
            m_CommandManager.RegisterCommand(id);

            if (m_WebsocketClient.GetState() != WebSocketState.Open)
            {
                if (m_ConnectionCompletionSource != null)
                {
                    Logger.Log($"Command attempt made while not connected, waiting for a connection to be established before sending command #{id}.");
                    await m_ConnectionCompletionSource.Task;
                    Logger.Log($"Connection established. Resuming expedition of command #{id}.");
                }
                else
                {
                    Logger.LogWarning($"No connection task found, impossible to wait until connection completion. Command #{id} will be sent anyway.");
                }
            }

            try
            {
                Logger.Log($"sending {command.GetMethod()} command: {command.ToString()}");
                m_WebsocketClient.Send(command.GetBytes());
                var reply = await m_CommandManager.WaitForCommandAsync(id);
                tags.Add("result", "success");
                m_Metrics.SendHistogramMetric("command", (DateTime.Now - time).TotalMilliseconds, tags);
                return reply;
            }
            catch (Exception)
            {
                tags.Add("result", "failure");
                m_Metrics.SendHistogramMetric("command", (DateTime.Now - time).TotalMilliseconds, tags);
                throw;
            }
        }

        internal void OnIdentityChanged(string playerId)
        {
            if (m_Disabled)
            {
                return;
            }
            m_ThreadUtils.Send(async() =>
            {
                try
                {
                    var connect = !string.IsNullOrEmpty(m_Config.token.AccessToken);
                    var action = connect ? "reconnect" : "disconnect";
                    Logger.Log($"PlayerID changed to [{ playerId }], next action: { action }");
                    await ResetAsync(connect);
                }
                catch (Exception e)
                {
                    Logger.LogException(e);
                }
            });
        }

        internal async Task DisconnectAsync()
        {
            if (m_ReconnectionActionId > 0)
            {
                m_ActionScheduler.CancelAction(m_ReconnectionActionId);
            }

            if (m_DisconnectionCompletionSource != null)
            {
                await m_DisconnectionCompletionSource.Task;
                return;
            }

            m_WantConnected = false;
            if (m_WebsocketClient == null)
            {
                ChangeConnectionState(ConnectionState.Disconnected);
                return;
            }

            m_DisconnectionCompletionSource = new TaskCompletionSource<ConnectionState>();
            ChangeConnectionState(ConnectionState.Disconnecting);
            m_WebsocketClient.Close();
            await m_DisconnectionCompletionSource.Task;
        }

        internal async Task ResetAsync(bool reconnect)
        {
            await DisconnectAsync();
            m_CommandManager.Clear();
            SubscriptionRepository.Clear();
            if (reconnect)
            {
                await ConnectAsync();
            }
        }

        public async Task ConnectAsync()
        {
            if (m_ReconnectionActionId > 0) // Accounting for unit tests
            {
                m_ActionScheduler.CancelAction(m_ReconnectionActionId);
                m_ReconnectionActionId = 0;
            }

            Logger.Log("Connection initiated. Checking state prior to connection.");
            while (m_ConnectionState == ConnectionState.Disconnecting)
            {
                Logger.Log(
                    "Disconnection already in progress. Waiting for disconnection to complete before proceeding.");
                await m_DisconnectionCompletionSource.Task;
            }

            while (m_ConnectionState == ConnectionState.Connecting)
            {
                Logger.Log("Connection already in progress. Waiting for connection to complete.");
                await m_ConnectionCompletionSource.Task;
            }

            if (m_ConnectionState == ConnectionState.Connected)
            {
                Logger.Log("Already connected.");
                return;
            }

            ChangeConnectionState(ConnectionState.Connecting);

            m_WantConnected = true;

            // initialize websocket object
            InitWebsocket();

            // Connect to the websocket server
            Logger.Log($"Attempting connection on: {m_Config.address}");
            m_WebsocketClient.Connect();
            await m_ConnectionCompletionSource.Task;
        }

        internal async void OnWebsocketOpen()
        {
            try
            {
                Logger.Log($"Websocket connected to : {m_Config.address}. Initiating Wire handshake.");
                var subscriptionRequests = await SubscribeRequest.getRequestFromRepo(SubscriptionRepository);
                if (m_Config.token.AccessToken == null)
                {
                    throw new EmptyTokenException();
                }
                var request = new ConnectRequest(m_Config.token.AccessToken, subscriptionRequests);
                var command = new Command(request);
                Reply reply;
                try
                {
                    reply = await SendCommandAsync(command.id, command);
                    m_Backoff.Reset();
                    SubscriptionRepository.RecoverSubscriptions(reply);
                    ChangeConnectionState(ConnectionState.Connected);

                    // ping pong
                    m_Pong = reply.connect.pong;
                    m_ServerPingIntervalS = reply.connect.ping;
                    SetupPingDeadline();
                }
                catch (CommandInterruptedException exception)
                {
                    // Wire handshake failed
                    m_ConnectionCompletionSource.TrySetException(
                        new ConnectionFailedException(
                            $"Socket closed during connection attempt: {exception.m_Code}"));
                    m_WebsocketClient.Close();
                }
                catch (Exception exception)
                {
                    // Unknown exception caught during connection
                    m_ConnectionCompletionSource.TrySetException(exception);
                    m_WebsocketClient.Close();
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        void SetupPingDeadline()
        {
            if (m_ServerPingIntervalS != 0)
            {
                m_PingDeadlineScheduledId = m_ActionScheduler.ScheduleAction(PingDeadline, m_ServerPingIntervalS + (uint)m_Config.MaxServerPingDelay);
            }
        }

        void CancelPingDeadline()
        {
            if (m_PingDeadlineScheduledId != 0)
            {
                m_ActionScheduler.CancelAction(m_PingDeadlineScheduledId);
                m_PingDeadlineScheduledId = 0;
            }
        }

        void PingDeadline()
        {
            m_PingDeadlineScheduledId = 0;
            if (m_ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            Logger.LogError("The connection to the server stalled. Disconnecting.");
            m_WebsocketClient.Close();
        }

        internal void OnWebsocketMessage(byte[] payload)
        {
            Logger.Log("WS received message: " + Encoding.UTF8.GetString(payload));
            var messages = BatchMessagesUtil
                .SplitMessages(payload); // messages can be batched so we need to split them..
            m_Metrics.SendSumMetric("message_received", messages.Count());

            foreach (var message in messages)
            {
                var reply = Reply.FromJson(message);
                if (reply.id > 0)
                {
                    HandleCommandReply(reply);
                }
                else if (reply.push != null)
                {
                    try
                    {
                        HandlePushMessage(reply.push);
                    }
                    catch (NotImplementedException)
                    {
                        Logger.LogError($"Could not process push message: {message}");
                    }
                    catch (Exception e)
                    {
                        Logger.LogException(e);
                    }
                }
                else
                {
                    // this is a server Ping `{}`
                    HandleServerPing();
                }
            }
        }

        void HandleServerPing()
        {
            CancelPingDeadline();
            if (m_Pong)
            {
                m_WebsocketClient.Send(k_PongMessage);
            }
            SetupPingDeadline();
        }

        void OnWebsocketError(string msg)
        {
            m_Metrics.SendSumMetric("websocket_error");
            Logger.LogError($"Websocket connection error: {msg}");
        }

        internal async void OnWebsocketClose(WebSocketCloseCode originalCode)
        {
            try
            {
                CancelPingDeadline();
                var code = (CentrifugeCloseCode)originalCode;
                Logger.Log("Websocket closed with code: " + code);
                ChangeConnectionState(ConnectionState.Disconnected);
                m_CommandManager.OnDisconnect(new CommandInterruptedException($"websocket disconnected: {code}",
                    code));
                if (m_DisconnectionCompletionSource != null)
                {
                    m_DisconnectionCompletionSource.SetResult(ConnectionState.Disconnected);
                    m_DisconnectionCompletionSource = null;
                }

                if (m_WantConnected && ShouldReconnect(code))
                {
                    if (m_Config.NetworkUtil.IsInternetReachable() == false)
                    {
                        Logger.LogWarning($"Internet is currently unreachable, will check again in {m_NetworkCheckInterval.TotalSeconds} seconds.");
                        m_WantConnected = false;
                        m_ReconnectionActionId = m_ActionScheduler.ScheduleAction(() => _ = CheckNetworkState(m_NetworkCheckInterval), m_NetworkCheckInterval.TotalSeconds);
                        return;
                    }

                    // TokenVerificationFailed is a special Wire custom error that happens when the token verification failed on server side to prevent any rate limitation on UAS;
                    // the server will wait a specified amount of time before retrying therefore it's useless to try again too early from the client.
                    var secondsUntilNextAttempt = (int)originalCode == 4333 ? 10.0f : m_Backoff.GetNext(); // TODO: get rid of the cast when the close code gets public
                    Logger.Log($"Retrying websocket connection in {secondsUntilNextAttempt} seconds.");
                    m_ActionScheduler.ScheduleAction(() => _ = ConnectAsync(), secondsUntilNextAttempt);
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        internal long CheckNetworkState(TimeSpan duration)
        {
            var minDuration = TimeSpan.FromSeconds(1d);
            if (duration < minDuration)
            {
                duration = minDuration;
            }

            if (m_Config.NetworkUtil.IsInternetReachable())
            {
                Logger.Log("Internet is reachable, connect again..");
                if (m_ReconnectionActionId > 0)
                {
                    m_ActionScheduler.CancelAction(m_ReconnectionActionId);
                }
                m_ReconnectionActionId = 0;
                _ = ConnectAsync();
                return m_ReconnectionActionId;
            }

            // Consciously using Action Scheduler instead of tasks (and related classes)
            // to eventually provide better WebGL support.
            Logger.Log($"Internet remains unreachable, check again in {duration.TotalSeconds} seconds..");
            return m_ActionScheduler.ScheduleAction(() => _ = CheckNetworkState(duration), duration.TotalSeconds);
        }

        private void InitWebsocket()
        {
            Logger.Log("Initializing Websocket.");

            if (m_WebsocketClient != null)
            {
                //  Unregister old listeners for safety
                m_WebsocketClient.OnOpen -= WebsocketOpenListener;
                m_WebsocketClient.OnMessage -= WebsocketMessageListener;
                m_WebsocketClient.OnError -= WebsocketErrorListener;
                m_WebsocketClient.OnClose -= WebsocketCloseListener;
            }
            // use the eventual websocket override instead of the default one
            m_WebsocketClient = m_WebsocketFactory.CreateInstance(m_Config.address);

            //  Add OnOpen event listener
            m_WebsocketClient.OnOpen += WebsocketOpenListener;
            // Add OnMessage event listener
            m_WebsocketClient.OnMessage += WebsocketMessageListener;
            // Add OnError event listener
            m_WebsocketClient.OnError += WebsocketErrorListener;
            // Add OnClose event listener
            m_WebsocketClient.OnClose += WebsocketCloseListener;
        }

        private bool ShouldReconnect(CentrifugeCloseCode code)
        {
            switch (code)
            {
                // irrecoverable error codes
                case CentrifugeCloseCode.WebsocketUnsupportedData:
                case CentrifugeCloseCode.WebsocketMandatoryExtension:
                case CentrifugeCloseCode.InvalidToken:
                case CentrifugeCloseCode.ForceNoReconnect:
                    return false;
                case CentrifugeCloseCode.WebsocketNotSet:
                case CentrifugeCloseCode.WebsocketNormal:
                case CentrifugeCloseCode.WebsocketAway:
                case CentrifugeCloseCode.WebsocketProtocolError:
                case CentrifugeCloseCode.WebsocketUndefined:
                case CentrifugeCloseCode.WebsocketNoStatus:
                case CentrifugeCloseCode.WebsocketAbnormal:
                case CentrifugeCloseCode.WebsocketInvalidData:
                case CentrifugeCloseCode.WebsocketPolicyViolation:
                case CentrifugeCloseCode.WebsocketTooBig:
                case CentrifugeCloseCode.WebsocketServerError:
                case CentrifugeCloseCode.WebsocketTlsHandshakeFailure:
                case CentrifugeCloseCode.Normal:
                case CentrifugeCloseCode.Shutdown:
                case CentrifugeCloseCode.BadRequest:
                case CentrifugeCloseCode.InternalServerError:
                case CentrifugeCloseCode.Expired:
                case CentrifugeCloseCode.SubscriptionExpired:
                case CentrifugeCloseCode.Stale:
                case CentrifugeCloseCode.Slow:
                case CentrifugeCloseCode.WriteError:
                case CentrifugeCloseCode.InsufficientState:
                case CentrifugeCloseCode.ForceReconnect:
                case CentrifugeCloseCode.ConnectionLimit:
                case CentrifugeCloseCode.ChannelLimit:
                // case CentrifugeCloseCode.TokenVerificationFailed:
                default:
                    return true;
            }
        }

        void ChangeConnectionState(ConnectionState state)
        {
            var tags = new Dictionary<string, string> {{"state", state.ToString()}, };
            m_Metrics.SendSumMetric("connection_state_change", 1, tags);
            m_ConnectionState = state;
            switch (state)
            {
                case ConnectionState.Disconnected:
                    Logger.Log("Wire disconnected.");
                    SubscriptionRepository.OnSocketClosed();
                    break;
                case ConnectionState.Connected:
                    Logger.Log("Wire connected.");
                    m_ConnectionCompletionSource.SetResult(ConnectionState.Connected);
                    m_ConnectionCompletionSource = null;
                    m_OnConnected?.Invoke();
                    m_OnConnected = null;
                    break;
                case ConnectionState.Connecting:
                    Logger.Log("Wire connecting...");
                    m_ConnectionCompletionSource = new TaskCompletionSource<ConnectionState>();

                    break;
                case ConnectionState.Disconnecting:
                    Logger.Log("Wire is disconnecting");

                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        // Handle push actions emitted from the server
        void HandlePushMessage(Push push)
        {
            var tags = new Dictionary<string, string> {{"push_type", push.GetPushType()}};
            m_Metrics.SendSumMetric("push_received", 1, tags);

            if (push.IsUnsub())
            {
                var subscription = SubscriptionRepository.GetSub(push.channel);
                if (subscription != null)
                {
                    subscription.OnKickReceived();
                    SubscriptionRepository.RemoveSub(subscription);
                }
                // we temporarily disable logging the subscription not found error
                // here because we saw the server can send those sometimes. We might
                // replug it later if we fix the issue on the server
                return;
            }

            if (push.IsPub())
            {
                var subscription = SubscriptionRepository.GetSub(push.channel);
                if (subscription != null)
                {
                    subscription.ProcessPublication(push.pub);
                }
                else
                {
                    Logger.LogError($"The Wire server sent a publication push messages related to an unknown channel: {push.channel}.");
                }

                return;
            }

            throw new NotImplementedException();
        }

        // Handle replies from commands issued by the client
        void HandleCommandReply(Reply reply)
        {
            m_CommandManager.OnCommandReplyReceived(reply);
        }

        async Task SubscribeAsync(Subscription subscription)
        {
            // we should ignore calls to this function if Wire has been disabled
            if (m_Disabled) return;

            if (m_ConnectionState != ConnectionState.Connected)
            {
                var tcs = new TaskCompletionSource<bool>();
                m_OnConnected += () =>
                {
                    tcs.SetResult(true);
                };
                try
                {
                    await ConnectAsync();
                }
                catch (Exception e)
                {
                    Logger.Log("Could not subscribe, issue while trying to connect. Subscription will resume when a connection is made.");
                    Logger.LogException(e);
                }
                await tcs.Task;
            }
            try
            {
                var token = await subscription.RetrieveTokenAsync();

                if (SubscriptionRepository.IsAlreadySubscribed(subscription))
                {
                    throw new AlreadySubscribedException(subscription.Channel);
                }

                var recover = SubscriptionRepository.IsRecovering(subscription);
                var request = new SubscribeRequest
                {
                    channel = subscription.Channel, token = token, recover = recover, offset = subscription.Offset
                };
                var command = new Command(request);
                var reply = await SendCommandAsync(command.id, command);

                subscription.Epoch = reply.subscribe.epoch;
                SubscriptionRepository.OnSubscriptionComplete(subscription, reply.subscribe);
            }
            catch (Exception exception)
            {
                subscription.OnError($"Subscription failed: {exception.Message}");
                throw;
            }
        }

        public IChannel CreateChannel(IChannelTokenProvider tokenProvider)
        {
            var subscription = new Subscription(tokenProvider);
            subscription.UnsubscribeReceived += async completionSource =>
            {
                try
                {
                    if (SubscriptionRepository.IsAlreadySubscribed(subscription))
                    {
                        await UnsubscribeAsync(subscription);
                    }
                    else
                    {
                        SubscriptionRepository.RemoveSub(subscription);
                    }

                    completionSource.SetResult(true);
                }
                catch (Exception e)
                {
                    completionSource.SetException(e);
                }
            };
            subscription.SubscribeReceived += async completionSource =>
            {
                try
                {
                    await SubscribeAsync(subscription);
                    completionSource.SetResult(true);
                }
                catch (Exception e)
                {
                    completionSource.SetException(e);
                }
            };
            subscription.KickReceived += () =>
            {
                SubscriptionRepository.RemoveSub(subscription);
            };
            subscription.DisposeReceived += () =>
            {
                SubscriptionRepository.RemoveSub(subscription);
            };
            return subscription;
        }

        // disconnects and prevent the Wire connection from being maintained
        public void Disable()
        {
            m_Disabled = true;
            m_ThreadUtils.Send(DisconnectAsync);
        }

        async Task UnsubscribeAsync(Subscription subscription)
        {
            // we should ignore calls to this function if Wire has been disconnected, it means the lifecycle of the Wire
            // connection has terminated
            if (!m_WantConnected || m_Disabled) return;

            if (!SubscriptionRepository.IsAlreadySubscribed(subscription))
            {
                throw new AlreadyUnsubscribedException(subscription.Channel);
            }

            var request = new UnsubscribeRequest {channel = subscription.Channel, };

            var command = new Command(request);
            await SendCommandAsync(command.id, command);
            SubscriptionRepository.RemoveSub(subscription);
        }

        private async void WebsocketOpenListener()
        {
            await m_ThreadUtils.PostAsync(OnWebsocketOpen);
        }

        private async void WebsocketCloseListener(WebSocketCloseCode code)
        {
            await m_ThreadUtils.PostAsync(() => OnWebsocketClose(code));
        }

        private async void WebsocketErrorListener(string msg)
        {
            await m_ThreadUtils.PostAsync(() => OnWebsocketError(msg));
        }

        private async void WebsocketMessageListener(byte[] data)
        {
            await m_ThreadUtils.PostAsync(() => OnWebsocketMessage(data));
        }
    }
}
