using System;
using UnityEngine;

namespace Unity.Services.Wire.Protocol.Internal
{
    static class CommandID
    {
        public static UInt32 currentId { private set; get; }

        public static UInt32 GenerateNewId()
        {
            return ++currentId;
        }

        [RuntimeInitializeOnLoadMethod]
        private static void ResetStaticsOnLoad()
        {
            currentId = 0;
        }
    }
}
