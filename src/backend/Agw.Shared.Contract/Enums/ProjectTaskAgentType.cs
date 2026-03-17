using System.Text.Json.Serialization;

namespace Agw.Shared.Enums;



//[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectTaskAgentType
{
    Agent = 0,
    Agentflow = 1,
}
