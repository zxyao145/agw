using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class AgentflowMessageMapperTests
{
    [Fact]
    public void MapEvent_StreamedAndCompletedResponses_DeliversEachContentOnce()
    {
        var delivered = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var updates = new[] { "a", "a", " ", "b" }
            .Select(text => new AgentResponseUpdate(ChatRole.Assistant, text)
            {
                MessageId = "message",
                AuthorName = "worker",
            })
            .ToArray();
        var response = updates.ToAgentResponse();
        var messages = updates
            .SelectMany(update =>
                AgentflowMessageMapper.MapEvent(new AgentResponseUpdateEvent("worker", update), delivered)
            )
            .ToList();

        messages.AddRange(AgentflowMessageMapper.MapEvent(new AgentResponseEvent("worker", response), delivered));
        messages.AddRange(AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(response, "worker"), delivered));

        Assert.Equal(
            ["a", "a", " ", "b"],
            messages.Select(message => Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content)
        );
    }

    [Fact]
    public void MapEvent_FinalResponseContainsNewMessage_DeliversNewContent()
    {
        var delivered = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var update = new AgentResponseUpdate(ChatRole.Assistant, "answer") { MessageId = "answer" };
        Assert.Single(AgentflowMessageMapper.MapEvent(new AgentResponseUpdateEvent("worker", update), delivered));
        var response = new[] { update }.ToAgentResponse();
        response.Messages.Add(new ChatMessage(ChatRole.Assistant, "result") { MessageId = "result" });

        var result = Assert.Single(
            AgentflowMessageMapper.MapEvent(new AgentResponseEvent("worker", response), delivered)
        );

        Assert.Equal("result", result.MessageId);
        Assert.Empty(AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(response, "worker"), delivered));
    }

    [Fact]
    public void MapEvent_HeaderBeforeResponse_DeliversResponseContents()
    {
        var delivered = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var header = new AgentResponseUpdate { MessageId = "answer", Role = ChatRole.Assistant };
        Assert.Single(AgentflowMessageMapper.MapEvent(new AgentResponseUpdateEvent("worker", header), delivered));
        var response = new AgentResponse([new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "answer" }]);

        var message = Assert.Single(
            AgentflowMessageMapper.MapEvent(new AgentResponseEvent("worker", response), delivered)
        );

        Assert.Equal("answer", Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content);
    }

    [Fact]
    public void MapEvent_OutputWithoutMessageIds_PreservesEveryMessage()
    {
        var delivered = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var response = new AgentResponse([
            new ChatMessage(ChatRole.Assistant, "first"),
            new ChatMessage(ChatRole.Assistant, "second"),
        ]);

        var messages = AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(response, "worker"), delivered);

        Assert.Equal(
            ["first", "second"],
            messages.Select(message => Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content)
        );
    }

    [Fact]
    public void MapEvent_OutputUpdatesWithoutPriorDelivery_AggregatesEveryDelta()
    {
        var delivered = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var updates = new[] { "a", "b" }.Select(text => new AgentResponseUpdate(ChatRole.Assistant, text)
        {
            MessageId = "answer",
        });

        var message = Assert.Single(
            AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(updates, "worker"), delivered)
        );

        Assert.Equal("ab", Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("messages")]
    [InlineData("response")]
    [InlineData("responses")]
    [InlineData("update")]
    [InlineData("updates")]
    public void MapEvent_OutputShapes_PreserveMessageMetadata(string shape)
    {
        var properties = new AdditionalPropertiesDictionary { ["modelName"] = "model", ["nodeName"] = "Worker" };
        var message = new ChatMessage(ChatRole.Assistant, "answer")
        {
            MessageId = "message-id",
            AuthorName = "agent",
            AdditionalProperties = properties,
        };
        var response = new AgentResponse([message]);
        var update = new AgentResponseUpdate(ChatRole.Assistant, "answer")
        {
            MessageId = "message-id",
            AuthorName = "agent",
            AdditionalProperties = properties,
        };
        object output = shape switch
        {
            "message" => message,
            "messages" => new[] { message },
            "response" => response,
            "responses" => new[] { response },
            "update" => update,
            _ => new[] { update },
        };

        var mapped = Assert.Single(AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(output, "executor")));

        Assert.Equal("message-id", mapped.MessageId);
        Assert.Equal("agent", mapped.Author);
        Assert.Equal(AiRole.Assistant, mapped.Role);
        Assert.Equal("answer", Assert.IsType<AgwTextContent>(Assert.Single(mapped.Contents)).Content);
        Assert.Equal("model", mapped.AdditionalProperties!["modelName"]);
        Assert.Equal("Worker", mapped.AdditionalProperties["nodeName"]);
    }

    [Fact]
    public void MapEvent_UpdateAndCompleteResponse_PreserveContentOrder()
    {
        var update = new AgentResponseUpdate(ChatRole.Assistant, "partial") { MessageId = "part" };
        var response = new AgentResponse([
            new ChatMessage(ChatRole.Assistant, "first"),
            new ChatMessage(ChatRole.Assistant, "second"),
        ]);

        var partial = AgentflowMessageMapper.MapEvent(new AgentResponseUpdateEvent("worker", update));
        var completed = AgentflowMessageMapper.MapEvent(new AgentResponseEvent("worker", response));

        Assert.Equal("partial", Assert.IsType<AgwTextContent>(Assert.Single(Assert.Single(partial).Contents)).Content);
        Assert.Equal(
            ["first", "second"],
            completed.Select(message => Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content)
        );
        Assert.Empty(AgentflowMessageMapper.CreateWorkflowOutputMessages(null));
        Assert.Empty(AgentflowMessageMapper.MapEvent(new WorkflowOutputEvent(new object(), "worker")));
    }

    [Theory]
    [InlineData(" accepted ", "accepted")]
    [InlineData(" ", "")]
    [InlineData(null, "")]
    public void CreateHumanGateResponseMessages_AppendsHumanReplyWithoutMutatingRequest(string? text, string expected)
    {
        var original = new ChatMessage(ChatRole.Assistant, "review");
        var messages = new[] { original };

        var result = AgentflowMessageMapper.CreateHumanGateResponseMessages(
            messages,
            new WorkflowGateDecision
            {
                InteractionId = "request",
                Approved = true,
                ResponseText = text,
            }
        );

        Assert.Single(messages);
        Assert.Same(original, result[0]);
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Equal("human", result[1].AuthorName);
        Assert.Equal(expected, result[1].Text);
    }

    [Fact]
    public void CreateHumanGateMessages_PreserveProtocolFieldsAndErrors()
    {
        var request = InteractionTestData.Gate("request", "node", null, "approval", "Approve?", []);

        var mapped = InteractionMessageMapper.Create(request, "request-message");
        var rejected = AgentflowMessageMapper.CreateHumanGateRejectedMessage(request, "rejected-message");
        var error = AgentflowMessageMapper.CreateWorkflowErrorMessage(new Exception("failure"), "error-message");

        Assert.Equal("interaction-request", mapped.AdditionalProperties!["type"]);
        Assert.Equal("request", InteractionTestData.Read(mapped).InteractionId);
        Assert.False(mapped.AdditionalProperties.ContainsKey("nodeName"));
        Assert.False(mapped.AdditionalProperties.ContainsKey("inputPreview"));
        Assert.Equal(AiRole.System, rejected.Role);
        Assert.Equal("human-gate-rejected", rejected.AdditionalProperties!["type"]);
        Assert.Equal(
            "HumanGate rejected. Workflow stopped.",
            Assert.IsType<AgwTextContent>(Assert.Single(rejected.Contents)).Content
        );
        Assert.Equal("workflow-error", error.AdditionalProperties!["type"]);
        Assert.Equal("failure", Assert.IsType<AgwErrorContent>(Assert.Single(error.Contents)).Content);
    }

    [Fact]
    public void ControlMessageMapping_ExplicitIds_ProducesDeterministicPayloads()
    {
        var request = InteractionTestData.Gate("request", "node", "Node", "approval", "Approve?", []);
        var node = new AgentflowHumanGateNode("node", "Node", null);
        var tool = new ToolApprovalRequestContent("tool-request", new FunctionCallContent("call", "tool"));
        AgwMessage[] Map() =>
            [
                InteractionMessageMapper.Create(request, "fixed-id"),
                AgentflowMessageMapper.CreateHumanGateRejectedMessage(request, "fixed-id"),
                AgentflowMessageMapper.CreateHumanGateUnavailableMessage(node, "fixed-id"),
                AgentflowMessageMapper.CreateToolApprovalUnavailableMessage(tool, "fixed-id"),
                AgentflowMessageMapper.CreateWorkflowErrorMessage(null, "fixed-id"),
            ];

        var first = Map();
        var second = Map();

        Assert.All(first, message => Assert.Equal("fixed-id", message.MessageId));
        Assert.Equal(first.Select(message => message.Serialize()), second.Select(message => message.Serialize()));
    }
}
