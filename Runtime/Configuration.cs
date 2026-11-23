using Unity.Services.Authentication.Internal;

namespace Unity.Services.Wire.Internal
{
    class Configuration
    {
        public IAccessToken token;

        // Lets the client reconnect as soon as a rejected token is replaced. Optional.
        public IAccessTokenObserver tokenObserver;

        public string address;

        public double CommandTimeoutInSeconds = 5.0;    // centrifuge specific

        public double RetrieveTokenTimeoutInSeconds = 5.0;

        public double ConnectionTimeoutInSeconds = 120.0;

        // TODO: Determine if this is still used?
        public IWebSocket WebSocket = null;  // for unit tests

        public INetworkUtil NetworkUtil; // for unit tests

        public double MaxServerPingDelay = 10.0; // centrifuge specific

        // Replaces the server TTL when scheduling token refreshes; for testing refreshes without waiting for expiry.
        public uint? RefreshTtlOverrideSeconds;
    }
}
