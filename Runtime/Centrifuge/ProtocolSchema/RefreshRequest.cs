using Newtonsoft.Json;
using UnityEngine.Scripting;

namespace Unity.Services.Wire.Protocol.Internal
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    class RefreshRequest
    {
        public string token;

        [Preserve]
        public RefreshRequest() {}

        public RefreshRequest(string token)
        {
            this.token = token;
        }
    }
}
