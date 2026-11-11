using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Events.Application.Matching;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSubscriptionFilterValidatorTests
{
    [Fact]
    public void TryValidate_ValidOperators_ReturnsTrue()
    {
        var rule = JsonNode.Parse(
            """{"and":[{"===":[{"var":"type"},"example.updated"]},{"==":[{"var":"data.state"},"open"]}]}"""
        );

        Assert.True(EventSubscriptionFilterValidator.TryValidate(rule, out var error), error);
    }

    [Fact]
    public void TryValidate_MultiPropertyObjectLiteral_ReturnsTrue()
    {
        var rule = JsonNode.Parse("""{"===":[{"var":"data"},{"state":"open","priority":"high"}]}""");

        Assert.True(EventSubscriptionFilterValidator.TryValidate(rule, out var error), error);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void TryValidate_BooleanFilter_ReturnsTrue(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.True(EventSubscriptionFilterValidator.TryValidate(document.RootElement, out var error), error);
    }

    [Theory]
    [InlineData("""{"===":[{"var":"type"},"b"],"note":"x"}""")]
    [InlineData("""{"===":[{"var":"type"},"a"],"===":[{"var":"type"},"b"]}""")]
    [InlineData("{}")]
    [InlineData("[true]")]
    [InlineData("\"example.updated\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void TryValidate_NonConditionFilter_ReturnsFalse(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(EventSubscriptionFilterValidator.TryValidate(document.RootElement, out var error));
        Assert.NotEmpty(error!);
    }

    [Fact]
    public void TryValidate_UnknownOperator_ReturnsFalse()
    {
        var rule = JsonNode.Parse("""{"unknownop":[true]}""");

        Assert.False(EventSubscriptionFilterValidator.TryValidate(rule, out _));
    }

    [Fact]
    public void TryValidate_InvalidOperatorArity_ReturnsFalse()
    {
        var rule = JsonNode.Parse("""{"===":["example.updated"]}""");

        Assert.False(EventSubscriptionFilterValidator.TryValidate(rule, out _));
    }

    [Fact]
    public void TryValidate_OperatorWithMissingOperand_ReturnsFalse()
    {
        var rule = JsonNode.Parse("""{"in":["open"]}""");

        Assert.False(EventSubscriptionFilterValidator.TryValidate(rule, out _));
    }

    [Fact]
    public void TryValidate_NestedUnknownOperator_ReturnsFalse()
    {
        var rule = JsonNode.Parse("""{"and":[true,{"unknownop":[true]}]}""");

        Assert.False(EventSubscriptionFilterValidator.TryValidate(rule, out _));
    }
}
