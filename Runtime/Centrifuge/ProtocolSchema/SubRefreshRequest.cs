using Newtonsoft.Json;
using UnityEngine.Scripting;

namespace Unity.Services.Wire.Protocol.Internal
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    class SubRefreshRequest
    {
        public string channel;
        public string token;

        [Preserve]
        public SubRefreshRequest() {}

        public SubRefreshRequest(string channel, string token)
        {
            this.channel = channel;
            this.token = token;
        }
    }
}
