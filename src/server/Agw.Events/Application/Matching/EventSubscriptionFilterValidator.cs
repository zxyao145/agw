using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;
using Json.Logic;
using Json.Logic.Rules;

namespace Agw.Events.Application.Matching;

public static class EventSubscriptionFilterValidator
{
    private static readonly JsonSerializerOptions RuleJsonOptions = new()
    {
        Converters = { new LogicComponentConverter() },
    };

    public static void Validate(JsonElement? filter)
    {
        if (filter is { } condition && !TryValidate(condition, out var error))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Single-key object literals are not supported in JsonLogic filters. "
                    + "A single-key object must name a registered operator. "
                    + $"Validation failed: {error}"
            );
        }
    }

    public static bool TryValidate(JsonElement filter, out string? error)
    {
        try
        {
            return TryValidate(JsonNode.Parse(filter.GetRawText()), out error);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryValidate(JsonNode? filter, out string? error)
    {
        try
        {
            if (filter is JsonValue value && value.TryGetValue<bool>(out _))
            {
                error = null;
                return true;
            }
            if (filter is JsonObject { Count: 1 } rule)
                return TryValidateOperator(rule, out error);

            error = "A JsonLogic filter must be a boolean or an object with one operator.";
            return false;
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or JsonLogicException
                        or ArgumentException
                        or InvalidOperationException
                        or KeyNotFoundException
            )
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryValidateOperand(JsonNode? operand, out string? error)
    {
        if (operand is JsonArray array)
        {
            foreach (var item in array)
            {
                if (!TryValidateOperand(item, out error))
                    return false;
            }
        }
        else if (operand is JsonObject { Count: 1 } rule)
        {
            return TryValidateOperator(rule, out error);
        }

        error = null;
        return true;
    }

    private static bool TryValidateOperator(JsonObject rule, out string? error)
    {
        var operation = rule.First();
        var ruleType = RuleRegistry.GetRule(operation.Key);
        if (
            ruleType is null
            || typeof(LiteralRule).IsAssignableFrom(ruleType)
            || JsonSerializer.Deserialize<Rule>(rule.ToJsonString(), RuleJsonOptions) is null or LiteralRule
        )
        {
            error = $"JsonLogic operator '{operation.Key}' is invalid.";
            return false;
        }

        return TryValidateOperand(operation.Value, out error);
    }
}
