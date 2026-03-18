using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace Agw.Shared.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgwTextContent), "TextContent")]
[JsonDerivedType(typeof(AgwTextReasoningContent), "TextReasoningContent")]
[JsonDerivedType(typeof(AgwFunctionCallContent), "FunctionCallContent")]
[JsonDerivedType(typeof(AgwFunctionResultContent), "FunctionResultContent")]
[JsonDerivedType(typeof(AgwErrorContent), "ErrorContent")]
[JsonDerivedType(typeof(AgwUsageContent), "UsageContent")]
public abstract class AgwContent
{
    public string Type { get; set; } = "";

    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }

}



public class AgwTextContent : AgwContent
{
    public string? Content { get; set; }
}

public class AgwTextReasoningContent : AgwContent
{
    public string? Content { get; set; }
}

public class AgwFunctionCallContent : AgwContent
{
    public string? Content { get; set; }
}

public class AgwFunctionResultContent : AgwContent
{
    public string? Content { get; set; }
}

public class AgwErrorContent : AgwContent
{
    public string? ErrorCode { get; set; }

    public string? Details { get; set; }

    // message
    public string Content { get; set; } = default!;
}

public class AgwUsageContent : AgwContent
{
    public UsageDetails Content { get; set; } = default!;
}

