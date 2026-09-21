using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore.Chat
{
    /// <summary>
    /// Source-generated metadata for the types (de)serialized during chat downloads. Using generated
    /// metadata avoids reflection, which is a large cost when writing multi-hundred-megabyte chat files.
    /// </summary>
    [JsonSourceGenerationOptions(AllowTrailingCommas = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(ChatRoot))]
    [JsonSerializable(typeof(LegacyEmbeddedData))]
    [JsonSerializable(typeof(GqlCommentResponse))]
    internal sealed partial class ChatJsonSerializerContext : JsonSerializerContext
    {
    }

    internal static class ChatSerialization
    {
        /// <summary>Options used to read/write chat files. Matches the historic reflection-based options exactly.</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ChatJsonSerializerContext.Default, new DefaultJsonTypeInfoResolver()),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true
        };

        /// <summary>Options used for Twitch GQL responses, which use the same web defaults as HttpClient's JSON helpers.</summary>
        public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ChatJsonSerializerContext.Default, new DefaultJsonTypeInfoResolver())
        };
    }
}
