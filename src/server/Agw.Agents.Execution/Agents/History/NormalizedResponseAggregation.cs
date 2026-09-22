using Agw.Projects.Contracts.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

internal static class NormalizedResponseAggregation
{
    internal static AgentResponse Aggregate(IEnumerable<AgentResponseUpdate> updates)
    {
        var input = updates.ToList();
        var response = input.ToAgentResponse();
        if (!input.Any(update => IsNormalized(update.AdditionalProperties)))
            return response;
        var accumulator = new Accumulator();
        foreach (var update in input.Where(update => IsNormalized(update.AdditionalProperties)))
            accumulator.Add(update);
        var projected = accumulator.ReadMessages().ToDictionary(message => message.MessageId!);
        var emitted = new HashSet<string>();
        response.Messages = response
            .Messages.SelectMany(message =>
            {
                if (!IsNormalized(message.AdditionalProperties))
                    return new[] { message };
                return message.MessageId is { } id && emitted.Add(id) ? new[] { projected[id] } : [];
            })
            .ToList();
        return response;
    }

    internal sealed class Accumulator
    {
        private readonly Dictionary<string, AdditionalPropertiesDictionary> _headers = new(StringComparer.Ordinal);
        private readonly AgentMessageProjection _projection = new(
            new ConversationMessageWriteScope
            {
                ProjectId = Guid.Empty,
                ContextId = "aggregation",
                Generation = 0,
                ProducerId = Guid.NewGuid(),
            },
            TimeProvider.System
        );

        internal void Add(AgentResponseUpdate update)
        {
            _headers[update.MessageId!] = new(update.AdditionalProperties!);
            var header = ModelMessageAdapter.ToMessage(update);
            var kind = Enum.Parse<AgentMessageOperationKind>(
                update.AdditionalProperties!["messageOperation"]!.ToString()!
            );
            var operation = new AgentMessageOperation
            {
                CanonicalId = Guid.Parse(update.MessageId!),
                SourceId = update.MessageId!,
                Header = header,
                Kind = kind,
                State = Enum.Parse<ConversationMessageState>(
                    update.AdditionalProperties["messageState"]!.ToString()!,
                    true
                ),
            };
            if (kind is AgentMessageOperationKind.AppendText or AgentMessageOperationKind.PutBlock)
            {
                foreach (var content in update.Contents)
                    _projection.Apply(
                        operation with
                        {
                            Content = content,
                            BlockId = content.AdditionalProperties!["blockId"]!.ToString(),
                        }
                    );
            }
            else
                _projection.Apply(operation);
        }

        internal IReadOnlyList<ChatMessage> ReadMessages() =>
            _projection
                .ReadMessages()
                .Select(message =>
                {
                    // Preserve the producer's identity when returning a complete framework response.
                    message.AdditionalProperties = new(_headers[message.MessageId!]);
                    message.AdditionalProperties["messageOperation"] = "PutMessage";
                    return message;
                })
                .ToArray();
    }

    internal static bool IsNormalized(AdditionalPropertiesDictionary? properties) =>
        properties?.GetValueOrDefault("messageOperation")?.ToString()
            is "AppendText"
                or "PutBlock"
                or "PutMessage"
                or "SealMessage";
}
