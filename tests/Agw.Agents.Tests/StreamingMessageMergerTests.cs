using System.Text.Json;
using Agw.Agents.Execution.Messaging;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// StreamingMessageMerger 的合并条件与合并结果，规则与客户端 execution-core 的 appendStreamingContents 一致。
/// Merge conditions and results of StreamingMessageMerger, whose rules match appendStreamingContents in the client's execution-core.
/// </summary>
public sealed class StreamingMessageMergerTests
{
    private static readonly DateTimeOffset FirstAt = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_SameBlockDeltas_AppendsBodyAndOverlaysProperties()
    {
        var pending = StreamingMessageMerger.Own(
            Delta("message-1", Text("Hel", "block:0", ("first", "a"))) with
            {
                CreatedAt = FirstAt,
            }
        );
        var incoming = Delta("message-1", Text("lo", "block:0", ("first", "b"), ("second", "c"))) with
        {
            CreatedAt = FirstAt.AddSeconds(1),
        };

        Assert.True(StreamingMessageMerger.CanMerge(pending, incoming));
        var merged = StreamingMessageMerger.Merge(pending, incoming);

        var content = Assert.IsType<AgwTextContent>(Assert.Single(merged.Contents));
        Assert.Equal("Hello", content.Content);
        Assert.Equal("b", content.AdditionalProperties?["first"]);
        Assert.Equal("c", content.AdditionalProperties?["second"]);
        Assert.Equal(FirstAt, merged.CreatedAt);
    }

    [Fact]
    public void Merge_OtherBlocksAndKinds_AppendContentsInOrder()
    {
        var pending = StreamingMessageMerger.Own(Delta("message-1", Reasoning("think", "block:0")));
        var incoming = Delta(
            "message-1",
            Reasoning("ing", "block:0"),
            Text("answer", "block:1"),
            Text("orphan", "block:0")
        );

        var merged = StreamingMessageMerger.Merge(pending, incoming);

        Assert.Collection(
            merged.Contents,
            content => Assert.Equal("thinking", Assert.IsType<AgwTextReasoningContent>(content).Content),
            content => Assert.Equal("answer", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("orphan", Assert.IsType<AgwTextContent>(content).Content)
        );
    }

    [Fact]
    public void Merge_TextWithoutBlockId_AppendsToTrailingTextWithoutBlockId()
    {
        var pending = StreamingMessageMerger.Own(Delta("message-1", new AgwTextContent { Content = "a" }));

        var merged = StreamingMessageMerger.Merge(pending, Delta("message-1", new AgwTextContent { Content = "b" }));

        Assert.Equal("ab", Assert.IsType<AgwTextContent>(Assert.Single(merged.Contents)).Content);
    }

    [Fact]
    public void Merge_MessageProperties_LaterValuesOverwriteAndPendingCopyIsIndependent()
    {
        var original = Delta("message-1", Text("a", "block:0")) with
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["producerScopeId"] = "p", ["stepIndex"] = 1 },
        };
        var pending = StreamingMessageMerger.Own(original);
        var incoming = Delta("message-1", Text("b", "block:0")) with
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["producerScopeId"] = "p", ["stepIndex"] = 2 },
        };

        var merged = StreamingMessageMerger.Merge(pending, incoming);

        Assert.Equal(2, merged.AdditionalProperties?["stepIndex"]);
        Assert.Equal(1, original.AdditionalProperties?["stepIndex"]);
        Assert.Equal("a", Assert.IsType<AgwTextContent>(Assert.Single(original.Contents)).Content);
    }

    [Theory]
    [MemberData(nameof(NonMergeableMessages))]
    public void CanMerge_TypedOrNonTextMessages_ReturnsFalse(AgwMessage message)
    {
        Assert.False(StreamingMessageMerger.CanMerge(message));
    }

    [Fact]
    public void CanMerge_DifferentMessageProducerOrAuthor_ReturnsFalse()
    {
        var pending = Delta("message-1", Text("a", "block:0")) with
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["producerScopeId"] = "p1" },
        };

        Assert.False(StreamingMessageMerger.CanMerge(pending, Delta("message-2", Text("b", "block:0"))));
        Assert.False(
            StreamingMessageMerger.CanMerge(
                pending,
                Delta("message-1", Text("b", "block:0")) with
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["producerScopeId"] = "p2" },
                }
            )
        );
        Assert.False(
            StreamingMessageMerger.CanMerge(
                pending,
                Delta("message-1", Text("b", "block:0")) with
                {
                    Author = "other",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["producerScopeId"] = "p1" },
                }
            )
        );
        Assert.True(
            StreamingMessageMerger.CanMerge(
                pending,
                Delta("message-1", Text("b", "block:0")) with
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["producerScopeId"] = JsonSerializer.SerializeToElement("p1"),
                    },
                }
            )
        );
    }

    public static TheoryData<AgwMessage> NonMergeableMessages() =>
        new(
            Delta("message-1", Text("a", "block:0")) with
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgwMessageClassifier.TypeKey] = AgwMessageTypes.TurnFinished,
                },
            },
            Delta("message-1", Text("a", "block:0")) with
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["agentflowInput"] = true },
            },
            Delta(
                "message-1",
                new AgwTextContent
                {
                    Content = "result",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType,
                    },
                }
            ),
            Delta("message-1", new AgwFunctionCallContent { Content = "{}" }),
            Delta("message-1", Text("a", "block:0"), new AgwErrorContent { Content = "failed" }),
            Delta("", Text("a", "block:0"))
        );

    private static AgwMessage Delta(string messageId, params AgwContent[] contents) =>
        new(messageId, "agent", AiRole.Assistant, [.. contents]);

    private static AgwTextContent Text(string text, string blockId, params (string Key, object Value)[] properties) =>
        new() { Content = text, AdditionalProperties = Properties(blockId, properties) };

    private static AgwTextReasoningContent Reasoning(string text, string blockId) =>
        new() { Content = text, AdditionalProperties = Properties(blockId, []) };

    private static AdditionalPropertiesDictionary Properties(string blockId, (string Key, object Value)[] properties)
    {
        var result = new AdditionalPropertiesDictionary { ["blockId"] = blockId };
        foreach (var (key, value) in properties)
            result[key] = value;
        return result;
    }
}
