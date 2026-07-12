using System.Diagnostics;
using System.Text.Json;

using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows.Observability;

internal static class AgentflowNodeExecutionActivity
{
    internal const string SourceName = "Agentflow.Execution.Persistence";
    private const string CapturePropertyName = "AgentflowNodeExecutionCapture";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentflowNodeExecutionActivityScope StartAgent(
        AgentflowExecutionTraceContext execution,
        Guid agentflowId,
        string nodeId,
        string? nodeName,
        Guid? agentId,
        string? agentName,
        IReadOnlyList<ChatMessage> input)
    {
        return Start(
            execution,
            agentflowId,
            nodeId,
            nodeName,
            AgentflowNodeKind.Agent,
            agentId,
            agentName,
            input);
    }

    public static AgentflowNodeExecutionActivityScope StartHumanGate(
        AgentflowExecutionTraceContext execution,
        Guid agentflowId,
        string nodeId,
        string? nodeName,
        IReadOnlyList<ChatMessage> input)
    {
        return Start(
            execution,
            agentflowId,
            nodeId,
            nodeName,
            AgentflowNodeKind.HumanGate,
            null,
            null,
            input);
    }

    internal static bool TryCreateTrace(Activity activity, out AgentflowTrace? trace)
    {
        if (activity.GetCustomProperty(CapturePropertyName) is not AgentflowNodeExecutionCapture capture)
        {
            trace = null;
            return false;
        }

        trace = new AgentflowTrace
        {
            Id = Guid.CreateVersion7(),
            StartTimeUtc = activity.StartTimeUtc,
            ProjectId = capture.Execution.ProjectId,
            ContextId = capture.Execution.ContextId,
            TaskId = capture.Execution.TaskId,
            AgentflowId = capture.AgentflowId,
            NodeId = capture.NodeId,
            NodeName = capture.NodeName,
            NodeKind = capture.NodeKind,
            AgentId = capture.AgentId,
            AgentName = capture.AgentName,
            Input = capture.Input,
            DurationMilliseconds = Math.Max(0, (long)activity.Duration.TotalMilliseconds),
            Status = capture.Status,
            Error = capture.Error,
        };
        return true;
    }

    private static AgentflowNodeExecutionActivityScope Start(
        AgentflowExecutionTraceContext execution,
        Guid agentflowId,
        string nodeId,
        string? nodeName,
        AgentflowNodeKind nodeKind,
        Guid? agentId,
        string? agentName,
        IReadOnlyList<ChatMessage> input)
    {
        var activity = Source.StartActivity($"agentflow.node {nodeId}", ActivityKind.Internal);
        if (activity == null)
        {
            return new AgentflowNodeExecutionActivityScope(null, null);
        }

        var capture = new AgentflowNodeExecutionCapture(
            execution,
            agentflowId,
            nodeId,
            nodeName,
            nodeKind,
            agentId,
            agentName,
            SerializeInput(input));
        activity.SetCustomProperty(CapturePropertyName, capture);
        return new AgentflowNodeExecutionActivityScope(activity, capture);
    }

    private static string SerializeInput(IReadOnlyList<ChatMessage> input)
    {
        try
        {
            var messages = input.Select(message => new
            {
                role = message.Role.ToString(),
                message.MessageId,
                message.AuthorName,
                message.CreatedAt,
                contents = message.Contents
                    .Select(content => JsonSerializer.SerializeToElement(content, content.GetType(), JsonOptions))
                    .ToArray(),
                message.AdditionalProperties,
            });
            return JsonSerializer.Serialize(messages, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return $"[Unserializable: {exception.GetType().Name}]";
        }
    }

    internal sealed class AgentflowNodeExecutionCapture
    {
        public AgentflowNodeExecutionCapture(
            AgentflowExecutionTraceContext execution,
            Guid agentflowId,
            string nodeId,
            string? nodeName,
            AgentflowNodeKind nodeKind,
            Guid? agentId,
            string? agentName,
            string input)
        {
            Execution = execution;
            AgentflowId = agentflowId;
            NodeId = nodeId;
            NodeName = nodeName;
            NodeKind = nodeKind;
            AgentId = agentId;
            AgentName = agentName;
            Input = input;
        }

        public AgentflowExecutionTraceContext Execution { get; }
        public Guid AgentflowId { get; }
        public string NodeId { get; }
        public string? NodeName { get; }
        public AgentflowNodeKind NodeKind { get; }
        public Guid? AgentId { get; }
        public string? AgentName { get; }
        public string Input { get; }
        public AgentflowNodeExecutionStatus Status { get; set; } = AgentflowNodeExecutionStatus.Cancelled;
        public string? Error { get; set; }
    }
}

internal sealed class AgentflowNodeExecutionActivityScope : IDisposable
{
    private readonly AgentflowNodeExecutionActivity.AgentflowNodeExecutionCapture? _capture;
    private int _disposed;

    public AgentflowNodeExecutionActivityScope(
        Activity? activity,
        AgentflowNodeExecutionActivity.AgentflowNodeExecutionCapture? capture)
    {
        Activity = activity;
        _capture = capture;
    }

    public Activity? Activity { get; }

    public void Complete()
    {
        if (_capture != null)
        {
            _capture.Status = AgentflowNodeExecutionStatus.Succeeded;
            _capture.Error = null;
        }
    }

    public void Reject()
    {
        if (_capture != null)
        {
            _capture.Status = AgentflowNodeExecutionStatus.Rejected;
            _capture.Error = null;
        }
    }

    public void Cancel()
    {
        if (_capture != null)
        {
            _capture.Status = AgentflowNodeExecutionStatus.Cancelled;
            _capture.Error = null;
        }
    }

    public void Fail(Exception exception)
    {
        Fail($"{exception.GetType().FullName}: {exception.Message}");
    }

    public void Fail(string error)
    {
        if (_capture != null)
        {
            _capture.Status = AgentflowNodeExecutionStatus.Failed;
            _capture.Error = error;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Activity?.Stop();
        }
    }
}
