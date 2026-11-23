using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Core.Scheduler.Internal;
using Unity.Services.Core.Telemetry.Internal;
using Unity.Services.Core.Threading.Internal;
using Unity.Services.Wire.Protocol.Internal;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.Services.Wire.Internal
{
    class Client : IWire
    {
        // Unsubscribe push codes from 2500 ask the client to resubscribe, e.g. 2501 when a subscription token expired.
        const uint k_MinResubscribeUnsubscribeCode = 2500;
        const int k_MaxResubscribeAttempts = 10;
        const double k_MaxResubscribeDelaySeconds = 30;

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
        event Action<Exception> m_OnConnectionAborted;

        internal bool m_WantConnected = false;

        internal byte[] k_PongMessage;

        bool m_Disabled = false;

        bool m_Pong = false;
        UInt32 m_ServerPingIntervalS = 0;
        TimeSpan m_NetworkCheckInterval = TimeSpan.FromSeconds(5d);
        IActionScheduler m_ActionScheduler;
        long m_PingDeadlineScheduledId = 0;
        long m_ReconnectionActionId = 0;
        readonly RefreshTimer m_ConnectionRefreshTimer;
        string m_ConnectAccessToken;

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
            m_ConnectionRefreshTimer = new RefreshTimer(actionScheduler, m_Config);

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
            if (m_Disabled || !m_WantConnected)
                return null;

            var time = DateTime.Now;
            var tags = new Dictionary<string, string> { { "method", command.GetMethod() } };
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
                Logger.Log($"Received {reply}");
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
                    Logger.Log($"PlayerID changed to [{playerId}], next action: {action}");
                    await ResetAsync(connect);
                }
                catch (Exception e)
                {
                    Logger.LogException(e);
                }
            });
        }

        internal Task DisconnectAsync()
        {
            CancelScheduledReconnect();
            StopWaitingForNewAccessToken();

            CancelRefreshes();

            if (m_DisconnectionCompletionSource != null)
            {
                return m_DisconnectionCompletionSource.Task;
            }

            m_WantConnected = false;
            if (m_WebsocketClient == null)
            {
                ChangeConnectionState(ConnectionState.Disconnected);
                return Task.CompletedTask;
            }

            // The server already closed the socket, and closing it again throws.
            if (m_ConnectionState == ConnectionState.Disconnected)
            {
                m_WebsocketClient = null;
                return Task.CompletedTask;
            }

            m_DisconnectionCompletionSource = new TaskCompletionSource<ConnectionState>();
            ChangeConnectionState(ConnectionState.Disconnecting);
            m_WebsocketClient.Close();
            m_WebsocketClient = null; // dispose the current ws client
            return m_DisconnectionCompletionSource.Task;
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

        public Task ConnectAsync()
        {
            CancelScheduledReconnect();
            StopWaitingForNewAccessToken();

            if (m_ConnectionState == ConnectionState.Connected)
            {
                Logger.Log("Already connected.");
                return Task.CompletedTask;
            }

            Logger.Log("Connection initiated. Checking state prior to connection.");
            if (m_ConnectionState == ConnectionState.Disconnecting)
            {
                Logger.Log(
                    "Disconnection already in progress. Waiting for disconnection to complete before proceeding.");
                return m_DisconnectionCompletionSource.Task;
            }

            if (m_ConnectionState == ConnectionState.Connecting)
            {
                Logger.Log("Connection already in progress. Waiting for connection to complete.");
                return m_ConnectionCompletionSource.Task;
            }

            ChangeConnectionState(ConnectionState.Connecting);

            // initialize websocket object
            InitWebsocket();

            // Connect to the websocket server
            Logger.Log($"Attempting connection on: {m_Config.address}");
            m_WantConnected = true;
            try
            {
                m_WebsocketClient.Connect();
            }
            catch (Exception e)
            {
                m_ConnectionCompletionSource.SetException(e);
                Logger.LogException(e);
                throw;
            }

            return m_ConnectionCompletionSource.Task;
        }

        internal async void OnWebsocketOpen()
        {
            var subscriptions = SubscriptionRepository.GetAll().ToArray();
            try
            {
                Logger.Log($"Websocket connected to : {m_Config.address}. Initiating Wire handshake.");
                if (m_Config.token.AccessToken == null)
                {
                    throw new EmptyTokenException();
                }

                // Subscriptions are restored with subscribe commands after the handshake so their tokens can be refreshed.
                m_ConnectAccessToken = m_Config.token.AccessToken;
                var command = new Command(new ConnectRequest(m_ConnectAccessToken));
                try
                {
                    var reply = await SendCommandAsync(command.id, command);
                    if (reply == null)
                    {
                        // SendCommandAsync returns null when the client is disabled or a disconnect
                        // cleared m_WantConnected while this open callback was queued on the main
                        // thread. Nothing to recover — abort the handshake.
                        throw new ConnectionFailedException("Wire connection aborted before the handshake completed.");
                    }
                    m_Backoff.Reset();
                    if (reply.connect == null)
                    {
                        // A connect reply with no connect result means the handshake did not
                        // succeed (e.g. an error reply). Fail cleanly instead of dereferencing null.
                        throw new ConnectionFailedException("Connect reply contained no connect result.");
                    }
                    ChangeConnectionState(ConnectionState.Connected);

                    // ping pong
                    m_Pong = reply.connect.pong;
                    m_ServerPingIntervalS = reply.connect.ping;
                    SetupPingDeadline();
                    m_ConnectionRefreshTimer.Schedule(reply.connect.expires, reply.connect.ttl, RefreshConnection);
                }
                catch (CommandInterruptedException exception)
                {
                    Logger.Log($"Command interrupted during connection: {exception.Message}");
                    Logger.LogException(exception);
                    // Wire handshake failed
                    m_ConnectionCompletionSource.TrySetException(
                        new ConnectionFailedException(
                            $"Socket closed during connection attempt: {exception.m_Code}"));
                    if (m_WebsocketClient?.GetState() == WebSocketState.Open)
                        m_WebsocketClient?.Close();
                }
                catch (Exception exception)
                {
                    // Unknown exception caught during connection
                    Logger.Log($"Unexpected exception '{exception.Message}' during connection");
                    Logger.LogException(exception);
                    m_ConnectionCompletionSource.TrySetException(exception);
                    m_WebsocketClient?.Close();
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
                m_ConnectionCompletionSource?.TrySetException(e);
                m_OnConnectionAborted?.Invoke(e);
                m_WebsocketClient?.Close();
            }

            Resubscribe(subscriptions);
        }

        void Resubscribe(IEnumerable<KeyValuePair<string, Subscription>> subscriptions)
        {
            if (m_ConnectionState != ConnectionState.Connected)
                return;

            Logger.Log("Attempting to restore subscriptions");
            _ = m_ThreadUtils.PostAsync(async() =>
            {
                foreach (var(_, subscription) in subscriptions)
                {
                    subscription.m_ResubscribeAttempts = 0;
                    await RestoreSubscriptionAsync(subscription);
                }
            });
        }

        async Task RestoreSubscriptionAsync(Subscription subscription)
        {
            // Skips subscriptions the caller unsubscribed from while the restore was pending.
            if (m_ConnectionState != ConnectionState.Connected || subscription.SubscriptionState == SubscriptionState.Synced
                || SubscriptionRepository.GetSub(subscription) != subscription)
            {
                return;
            }

            try
            {
                Logger.Log($"\tResubscribing Sub {subscription.Channel}");
                await subscription.SubscribeAsync();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to restore sub: {e}");
                ScheduleResubscribeRetry(subscription);
            }
        }

        void ScheduleResubscribeRetry(Subscription subscription)
        {
            if (subscription.m_ResubscribeAttempts >= k_MaxResubscribeAttempts)
            {
                return;
            }

            var delay = Math.Min(k_MaxResubscribeDelaySeconds, Math.Pow(2, subscription.m_ResubscribeAttempts));
            subscription.m_ResubscribeAttempts++;
            GetRefreshTimer(subscription).ScheduleIn(delay, () => _ = RestoreSubscriptionAsync(subscription));
        }

        void RefreshConnection() => _ = RefreshConnectionAsync();

        async Task RefreshConnectionAsync()
        {
            if (m_ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            var token = m_Config.token.AccessToken;
            if (string.IsNullOrEmpty(token))
            {
                Logger.LogWarning("No access token available to refresh the Wire connection.");
                return;
            }

            try
            {
                var command = new Command(new RefreshRequest(token));
                var reply = await SendCommandAsync(command.id, command);
                if (reply?.refresh != null)
                {
                    m_ConnectionRefreshTimer.Schedule(reply.refresh.expires, reply.refresh.ttl, RefreshConnection);
                }
            }
            catch (Exception e)
            {
                // The server closes the connection once the current token expires and the regular reconnection takes over.
                Logger.LogWarning($"Wire connection token refresh failed: {e.Message}");
            }
        }

        void ScheduleSubscriptionRefresh(Subscription subscription, bool expires, uint ttl)
        {
            GetRefreshTimer(subscription).Schedule(expires, ttl, () => _ = RefreshSubscriptionAsync(subscription));
        }

        RefreshTimer GetRefreshTimer(Subscription subscription)
        {
            if (subscription.m_RefreshTimer == null)
            {
                subscription.m_RefreshTimer = new RefreshTimer(m_ActionScheduler, m_Config);
            }

            return subscription.m_RefreshTimer;
        }

        async Task RefreshSubscriptionAsync(Subscription subscription)
        {
            if (!CanRefresh(subscription))
            {
                return;
            }

            try
            {
                var token = await subscription.RetrieveTokenAsync();
                if (!CanRefresh(subscription))
                {
                    return;
                }

                var command = new Command(new SubRefreshRequest(subscription.Channel, token));
                var reply = await SendCommandAsync(command.id, command);
                if (reply?.subRefresh != null)
                {
                    ScheduleSubscriptionRefresh(subscription, reply.subRefresh.expires, reply.subRefresh.ttl);
                }
            }
            catch (Exception e)
            {
                // The server unsubscribes the channel once its current token expires.
                Logger.LogWarning($"Wire subscription token refresh failed for {subscription.Channel}: {e.Message}");
            }
        }

        // Re-checked after fetching the token, since the socket can close or the channel be dropped meanwhile.
        bool CanRefresh(Subscription subscription) =>
            m_ConnectionState == ConnectionState.Connected && subscription.IsConnected
            && SubscriptionRepository.GetSub(subscription) == subscription;

        void CancelRefreshes()
        {
            m_ConnectionRefreshTimer.Cancel();
            foreach (var subscription in SubscriptionRepository.GetAll())
            {
                subscription.Value.m_RefreshTimer?.Cancel();
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
                    Logger.Log("Handling Server Ping");
                    Logger.Log($"Message was: {message}");

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
                // Read m_WebsocketClient once: DisconnectAsync can null it from the disconnect
                // path between this null check and the Send below, so snapshot it into a local.
                var websocketClient = m_WebsocketClient;
                if (websocketClient == null)
                {
                    // Ping was processed on the main thread after the connection was torn down.
                    Logger.Log("Server ping received after disconnect; skipping pong.");
                    return;
                }
                try
                {
                    Logger.Log("Sending Pong Message...");
                    websocketClient.Send(k_PongMessage);
                }
                catch (WebSocketInvalidStateException)
                {
                    Logger.LogWarning("Pong signal could not be sent to server!\r\n" +
                        "This may be due to the application being inactive for an extended period of time.\r\n" +
                        "Will attempt to re-establish connection.");
                    return; //no need to continue
                }
                catch (Exception e)
                {
                    Logger.LogException(e);
                    return;
                }
            }

            SetupPingDeadline();
        }

        void OnWebsocketError(string msg)
        {
            m_Metrics.SendSumMetric("websocket_error");
            Logger.LogError($"Websocket connection error: {msg}");
        }

        internal void OnWebsocketClose(WebSocketCloseCode originalCode)
        {
            try
            {
                CancelPingDeadline();
                CancelRefreshes();
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

                if (m_WantConnected && code == CentrifugeCloseCode.InvalidToken && m_Config.tokenObserver != null)
                {
                    if (AccessTokenChangedSinceConnect())
                    {
                        // Authentication already replaced the rejected token, so the backoff would only add delay.
                        Logger.Log("Wire token was rejected but has since been refreshed, reconnecting.");
                        m_ReconnectionActionId = m_ActionScheduler.ScheduleAction(() => _ = ConnectAsync());
                    }
                    else
                    {
                        // Retrying with the rejected token can't succeed; reconnect once Authentication issues a new one.
                        Logger.Log("Wire token was rejected, reconnecting once the access token changes.");
                        WaitForNewAccessToken();
                        m_OnConnectionAborted?.Invoke(new ConnectionFailedException($"The server rejected the access token ({code})"));
                    }
                }
                else if (m_WantConnected && ShouldReconnect(code))
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
                    var secondsUntilNextAttempt = code == CentrifugeCloseCode.TokenVerificationFailed ? 10.0f : m_Backoff.GetNext();
                    Logger.Log($"Retrying websocket connection in {secondsUntilNextAttempt} seconds.");
                    m_ReconnectionActionId = m_ActionScheduler.ScheduleAction(() => _ = ConnectAsync(), secondsUntilNextAttempt);
                }
                else if (m_WantConnected || m_Disabled)
                {
                    var reason = $"Wire received terminal close code: {code}";
                    if (m_WantConnected)
                    {
                        // Unsynced would suggest the connection comes back; the server said it won't.
                        foreach (var subscription in SubscriptionRepository.GetAll())
                        {
                            subscription.Value.OnError(reason);
                        }
                    }

                    m_OnConnectionAborted?.Invoke(new ConnectionFailedException(reason));
                }
            }
            catch (Exception e)
            {
                Logger.LogException(e);
            }
        }

        // A refresh can land after the rejected connect but before its close is handled, so check before waiting.
        bool AccessTokenChangedSinceConnect()
        {
            var accessToken = m_Config.token.AccessToken;
            return !string.IsNullOrEmpty(accessToken) && accessToken != m_ConnectAccessToken;
        }

        void WaitForNewAccessToken()
        {
            m_Config.tokenObserver.AccessTokenChanged -= OnAccessTokenChanged;
            m_Config.tokenObserver.AccessTokenChanged += OnAccessTokenChanged;
        }

        // Without this, a retry scheduled by an earlier close could override a later final close or token wait.
        void CancelScheduledReconnect()
        {
            if (m_ReconnectionActionId > 0)
            {
                m_ActionScheduler.CancelAction(m_ReconnectionActionId);
                m_ReconnectionActionId = 0;
            }
        }

        void StopWaitingForNewAccessToken()
        {
            if (m_Config.tokenObserver != null)
            {
                m_Config.tokenObserver.AccessTokenChanged -= OnAccessTokenChanged;
            }
        }

        void OnAccessTokenChanged(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                return;
            }

            m_ThreadUtils.Send(() =>
            {
                StopWaitingForNewAccessToken();
                if (!m_WantConnected || m_Disabled)
                {
                    return;
                }

                try
                {
                    _ = ConnectAsync();
                }
                catch (Exception e)
                {
                    Logger.LogException(e);
                }
            });
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

        // Stale and InvalidToken are final on protocol v2 but retried: an app paused mid-handshake can hit Stale,
        // and InvalidToken only gets here without a token observer, which leaves retrying as the only way back.
        private bool ShouldReconnect(CentrifugeCloseCode code)
        {
            switch (code)
            {
                case CentrifugeCloseCode.WebsocketUnsupportedData:
                case CentrifugeCloseCode.WebsocketMandatoryExtension:
                case CentrifugeCloseCode.BadRequest:
                case CentrifugeCloseCode.ForceNoReconnect:
                case CentrifugeCloseCode.ConnectionLimit:
                case CentrifugeCloseCode.ChannelLimit:
                case CentrifugeCloseCode.InappropriateProtocol:
                    return false;
                default:
                    return true;
            }
        }

        void ChangeConnectionState(ConnectionState state)
        {
            var tags = new Dictionary<string, string> { { "state", state.ToString() }, };
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
            var tags = new Dictionary<string, string> { { "push_type", push.GetPushType() } };
            m_Metrics.SendSumMetric("push_received", 1, tags);

            if (push.IsUnsub())
            {
                var subscription = SubscriptionRepository.GetSub(push.channel);
                if (subscription == null)
                {
                    // we temporarily disable logging the subscription not found error
                    // here because we saw the server can send those sometimes. We might
                    // replug it later if we fix the issue on the server
                    return;
                }

                if (push.unsubscribe.code >= k_MinResubscribeUnsubscribeCode)
                {
                    Logger.Log($"Resubscribing to {push.channel} after unsubscribe push: {push.unsubscribe.reason}");
                    subscription.m_RefreshTimer?.Cancel();
                    subscription.OnConnectivityChangeReceived(false);
                    Resubscribe(new[] { new KeyValuePair<string, Subscription>(push.channel, subscription) });
                    return;
                }

                subscription.OnKickReceived();
                SubscriptionRepository.RemoveSub(subscription);
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
            Logger.Log("Handle Command Reply");
            m_CommandManager.OnCommandReplyReceived(reply);
        }

        async Task SubscribeAsync(Subscription subscription)
        {
            // we should ignore calls to this function if Wire has been disabled
            if (m_Disabled)
                return;

            if (m_ConnectionState != ConnectionState.Connected)
            {
                await WaitForConnectionAsync();
            }

            try
            {
                if (SubscriptionRepository.IsAlreadySubscribed(subscription))
                {
                    throw new AlreadySubscribedException(subscription.Channel);
                }

                var recover = SubscriptionRepository.IsRecovering(subscription);
                var token = await subscription.RetrieveTokenAsync();

                // A restore the caller unsubscribed from while the token was being fetched must not subscribe again.
                if (recover && !SubscriptionRepository.IsRecovering(subscription))
                {
                    return;
                }

                var request = new SubscribeRequest { channel = subscription.Channel, token = token, recover = recover, offset = subscription.Offset };
                var command = new Command(request);
                var reply = await SendCommandAsync(command.id, command);

                subscription.Epoch = reply.subscribe.epoch;
                SubscriptionRepository.OnSubscriptionComplete(subscription, reply.subscribe);
                ScheduleSubscriptionRefresh(subscription, reply.subscribe.expires, reply.subscribe.ttl);
            }
            catch (Exception exception)
            {
                subscription.OnError($"Subscription failed: {exception.Message}");
                throw;
            }
        }

        internal async Task WaitForConnectionAsync()
        {
            if (m_ConnectionState == ConnectionState.Connected)
                return;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void onConnected() => tcs.TrySetResult(true);
            void onAborted(Exception e) => tcs.TrySetException(
                new ConnectionFailedException("Wire connection terminally failed before subscribe", e));

            m_OnConnected += onConnected;
            m_OnConnectionAborted += onAborted;
            var timeoutActionId = ScheduleConnectionTimeout(tcs);
            // Declared out here so the finally block below can observe its faults.
            Task connectTask = null;
            try
            {
                try
                {
                    connectTask = ConnectAsync();
                }
                catch (Exception e)
                {
                    Logger.LogException(e);
                    throw new ConnectionFailedException("Wire connection failed during ConnectAsync", e);
                }

                // The task returned by ConnectAsync only completes when the connection succeeds or
                // fails terminally. A retryable failure leaves it pending forever while the
                // reconnection loop keeps going, so it cannot be awaited on its own. We need a
                // separate timeout task on the side to signal an abort if it takes too long.
                await Task.WhenAny(connectTask, tcs.Task);

                if (connectTask.IsFaulted)
                {
                    Exception exception = connectTask.Exception?.InnerException ?? connectTask.Exception;
                    Logger.LogException(exception);
                    throw new ConnectionFailedException("Wire connection failed during ConnectAsync", exception);
                }

                await tcs.Task;
            }
            finally
            {
                CancelConnectionTimeout(timeoutActionId);
                m_OnConnected -= onConnected;
                m_OnConnectionAborted -= onAborted;

                // Don't surface a timeout/abort that lands after we stopped awaiting.
                ObserveFaults(tcs.Task);
                ObserveFaults(connectTask);
            }
        }

        static void ObserveFaults(Task task)
        {
            if (task == null)
            {
                return;
            }

            _ = task.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        long ScheduleConnectionTimeout(TaskCompletionSource<bool> tcs)
        {
            var timeoutSeconds = m_Config.ConnectionTimeoutInSeconds;
            if (timeoutSeconds <= 0)
            {
                return 0;
            }

            var e = new ConnectionFailedException($"Wire did not connect within {timeoutSeconds} seconds");
            return m_ActionScheduler.ScheduleAction(() => tcs.TrySetException(e), timeoutSeconds);
        }

        void CancelConnectionTimeout(long actionId)
        {
            if (actionId <= 0)
            {
                return;
            }

            try
            {
                m_ActionScheduler.CancelAction(actionId);
            }
            catch
            {
                // In all likelihood this just means the action had already completed, and even if
                // not then this is just the cancellation timeout. Not worth an error/exception.
            }
        }

        public IChannel CreateChannel(IChannelTokenProvider tokenProvider)
        {
            var subscription = new Subscription(tokenProvider);
            subscription.UnsubscribeReceived += async completionSource =>
            {
                subscription.m_RefreshTimer?.Cancel();
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
                subscription.m_RefreshTimer?.Cancel();
                SubscriptionRepository.RemoveSub(subscription);
            };
            subscription.DisposeReceived += () =>
            {
                subscription.m_RefreshTimer?.Cancel();
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
            if (!m_WantConnected || m_Disabled)
                return;

            if (!SubscriptionRepository.IsAlreadySubscribed(subscription))
            {
                throw new AlreadyUnsubscribedException(subscription.Channel);
            }

            var request = new UnsubscribeRequest { channel = subscription.Channel, };

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
