using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Durable;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class AgentflowMessageMapperTests
{
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
            new HumanGateApprovalDecision("request", true, text)
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
        var request = new HumanGateApprovalRequest("request", "node", null, "approval", "Approve?", []);

        var mapped = AgentflowMessageMapper.CreateHumanGateApprovalRequestMessage(request, "request-message");
        var rejected = AgentflowMessageMapper.CreateHumanGateRejectedMessage(request, "rejected-message");
        var error = AgentflowMessageMapper.CreateWorkflowErrorMessage(new Exception("failure"), "error-message");

        Assert.Equal("human-gate-request", mapped.AdditionalProperties!["type"]);
        Assert.Equal("request", mapped.AdditionalProperties["requestId"]);
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
        var request = new HumanGateApprovalRequest("request", "node", "Node", "approval", "Approve?", []);
        var node = new AgentflowHumanGateNode("node", "Node", null);
        var tool = new ToolApprovalRequestContent("tool-request", new FunctionCallContent("call", "tool"));
        AgwMessage[] Map() =>
            [
                AgentflowMessageMapper.CreateHumanGateApprovalRequestMessage(request, "fixed-id"),
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

    [Fact]
    public void CreateDurableFailure_PreservesSegmentIdentity()
    {
        var input = new DurableExecutionSegmentInput(Guid.CreateVersion7(), 3, [], null);

        var result = AgentflowMessageMapper.CreateDurableFailure(input, "failure");

        Assert.Equal(input.ExecutionId, result.ExecutionId);
        Assert.Equal(3, result.SegmentIndex);
        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal("failure", result.ErrorMessage);
        Assert.Null(result.Checkpoint);
        Assert.Empty(result.PendingInteractions);
    }
}
