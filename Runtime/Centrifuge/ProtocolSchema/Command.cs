using System;
using System.Text;
using Newtonsoft.Json;
using Unity.Services.Wire.Internal;
using UnityEngine.Scripting;

namespace Unity.Services.Wire.Protocol.Internal
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    class Command
    {
        internal const string k_CONNECT = "CONNECT";
        internal const string k_SUBSCRIBE = "SUBSCRIBE";
        internal const string k_UNSUBSCRIBE = "UNSUBSCRIBE";
        internal const string k_PING = "PING";
        internal const string k_UNKNOWN = "UNKNOWN";

        public UInt32 id;
        public ConnectRequest connect;
        public SubscribeRequest subscribe;

        public UnsubscribeRequest unsubscribe;

        // missing requests here (not implemented/necessary for Wire)
        public PingRequest ping;

        [Preserve]
        public Command() {}

        public Command(PingRequest request)
        {
            id = CommandID.GenerateNewId();
            ping = request;
        }

        public Command(ConnectRequest request)
        {
            id = CommandID.GenerateNewId();
            connect = request;
        }

        public Command(SubscribeRequest request)
        {
            id = CommandID.GenerateNewId();
            subscribe = request;
        }

        public Command(UnsubscribeRequest request)
        {
            id = CommandID.GenerateNewId();
            unsubscribe = request;
        }

        public static Command FromJSON(byte[] data)
        {
            return JsonConvert.DeserializeObject<Command>(Encoding.UTF8.GetString(data));
        }

        public byte[] GetBytes()
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Formatting.None));
        }

        static readonly JsonSerializerSettings MaskedTokenSettings =  new()
        {
            ContractResolver = new MaskedTokenContractResolver(),
            Formatting = Formatting.None
        };

        public new string ToString()
        {
            return JsonConvert.SerializeObject(this, MaskedTokenSettings);
        }

        internal bool IsPing()
        {
            return ping != null;
        }

        public string GetMethod()
        {
            if (connect != null)
            {
                return k_CONNECT;
            }

            if (subscribe != null)
            {
                return k_SUBSCRIBE;
            }

            if (unsubscribe != null)
            {
                return k_UNSUBSCRIBE;
            }

            return IsPing() ? k_PING : k_UNKNOWN;
        }
    }
}
