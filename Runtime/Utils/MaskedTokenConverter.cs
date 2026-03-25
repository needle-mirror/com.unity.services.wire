using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Unity.Services.Wire.Internal
{
    /// <summary>
    /// Mask string values
    /// </summary>
    internal class MaskedTokenConverter : JsonConverter<string>
    {
        public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.WriteValue(value);
                return;
            }

            writer.WriteValue(value.GetHashCode().ToString());
        }

        public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            // we'll just read the raw value back instead of throwing for now
            return reader.Value?.ToString();
        }
    }


    /// <summary>
    /// Ensures that the token gets serialized unmasked
    /// </summary>
    internal class MaskedTokenContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            if (property.PropertyName == "token"
                && property.PropertyType == typeof(string))
            {
                property.Converter = new MaskedTokenConverter();
            }

            return property;
        }
    }
}
