using Newtonsoft.Json;
using UnityEngine.Scripting;

namespace Unity.Services.Wire.Protocol.Internal
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    class SubscribeRequest
    {
        public string channel;
        public string token;

        // NYI, recover/history related features
        //
        public bool recover;
        public ulong offset;
        public string epoch;

        [Preserve]
        public SubscribeRequest() {}
    }
}
