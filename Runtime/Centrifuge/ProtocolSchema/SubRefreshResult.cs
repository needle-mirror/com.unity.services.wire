using System;
using Newtonsoft.Json;
using UnityEngine.Scripting;

namespace Unity.Services.Wire.Protocol.Internal
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    class SubRefreshResult
    {
        public bool expires;
        public UInt32 ttl;

        [Preserve]
        public SubRefreshResult() {}
    }
}
