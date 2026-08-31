using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace PiAgentSdk.MAF.Internal;

internal static partial class PiAgentSessionJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.TypeInfoResolverChain.Clear();
        options.TypeInfoResolverChain.Add(AgentAbstractionsJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(JsonContext.Default.Options.TypeInfoResolver!);
        return options;
    }

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    )]
    [JsonSerializable(typeof(PiAgentSession))]
    [ExcludeFromCodeCoverage]
    internal partial class JsonContext : JsonSerializerContext;
}
