using System.Text.Json;
using Json.Schema;
using Markdig;
using Markdig.Syntax;

namespace Agw.Extensions.TaskPilot;

public static class TaskPilotReceiptParser
{
    private const string ReceiptSchemaJson = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "ask": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["key", "title", "text"],
                "properties": {
                  "key": { "type": "string", "minLength": 1 },
                  "title": { "type": "string", "minLength": 1 },
                  "text": { "type": "string", "minLength": 1 },
                  "options": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["label", "value"],
                      "properties": {
                        "label": { "type": "string", "minLength": 1 },
                        "value": { "type": "string", "minLength": 1 }
                      }
                    }
                  },
                  "recommend": { "type": "integer", "minimum": 0 },
                  "about": { "type": "string" }
                }
              }
            },
            "notice": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "text"],
                "properties": {
                  "title": { "type": "string", "minLength": 1 },
                  "text": { "type": "string", "minLength": 1 },
                  "fyi": { "type": "boolean" }
                }
              }
            },
            "resolve": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["card", "reason"],
                "properties": {
                  "card": { "type": "string", "minLength": 1 },
                  "reason": { "type": "string", "minLength": 1 }
                }
              }
            },
            "later": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["key"],
                "properties": {
                  "key": { "type": "string", "minLength": 1 },
                  "at": { "type": "string", "format": "date-time" },
                  "cancel": { "type": "boolean" },
                  "about": { "type": "string" }
                },
                "oneOf": [
                  { "required": ["at"], "properties": { "cancel": { "const": false } } },
                  { "required": ["cancel"], "properties": { "cancel": { "const": true } } }
                ]
              }
            },
            "events": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "type", "detail"],
                "properties": {
                  "id": { "type": "string", "minLength": 1 },
                  "type": { "type": "string", "minLength": 1 },
                  "subject": { "type": "string" },
                  "scope": { "type": "string" },
                  "tags": { "type": "array", "items": { "type": "string" } },
                  "summary": { "type": "string" },
                  "link": { "type": "string" },
                  "detail": true
                }
              }
            }
          }
        }
        """;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().Build();
    private static readonly JsonSchema ReceiptSchema = BuildReceiptSchema();

    public static TaskPilotReceiptParseResult Parse(string message, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        string summary = message.Split('\n', 2)[0].Trim();
        if (summary.Length == 0)
        {
            return Invalid("The execution conclusion is empty.", TaskPilotRoundOutcome.Completed, summary);
        }

        TaskPilotRoundOutcome outcome =
            summary.StartsWith("没跑成：", StringComparison.Ordinal) ? TaskPilotRoundOutcome.RetryableFailure
            : summary.StartsWith("没事可做：", StringComparison.Ordinal) ? TaskPilotRoundOutcome.NoWork
            : TaskPilotRoundOutcome.Completed;

        MarkdownDocument document = Markdown.Parse(message, MarkdownPipeline);
        FencedCodeBlock? block = document
            .Descendants<FencedCodeBlock>()
            .LastOrDefault(code => string.Equals(code.Info?.Trim(), "taskpilot", StringComparison.Ordinal));
        if (block is null)
        {
            return new TaskPilotReceiptParseResult
            {
                IsValid = true,
                Receipt = new TaskPilotReceipt(),
                Outcome = outcome,
                Summary = summary,
            };
        }

        string json = block.Lines.ToString();
        try
        {
            using JsonDocument receiptDocument = JsonDocument.Parse(json);
            EvaluationResults evaluation = ReceiptSchema.Evaluate(receiptDocument.RootElement);
            if (!evaluation.IsValid)
            {
                return Invalid("The execution receipt does not match the TaskPilot schema.", outcome, summary);
            }

            TaskPilotReceipt? receipt = JsonSerializer.Deserialize<TaskPilotReceipt>(
                receiptDocument.RootElement,
                jsonOptions
            );
            return receipt is null
                ? Invalid("The execution receipt is empty.", outcome, summary)
                : new TaskPilotReceiptParseResult
                {
                    IsValid = true,
                    Receipt = receipt with
                    {
                        Ask = receipt.Ask?.Select(ask => ask with { Options = ask.Options ?? [] }).ToArray() ?? [],
                        Notice = receipt.Notice ?? [],
                        Resolve = receipt.Resolve ?? [],
                        Later = receipt.Later ?? [],
                        Events =
                            receipt.Events?.Select(emitted => emitted with { Tags = emitted.Tags ?? [] }).ToArray()
                            ?? [],
                    },
                    Outcome = outcome,
                    Summary = summary,
                };
        }
        catch (JsonException)
        {
            return Invalid("The execution receipt is not valid JSON.", outcome, summary);
        }
    }

    private static JsonSchema BuildReceiptSchema()
    {
        return JsonSchema.FromText(ReceiptSchemaJson, new BuildOptions { Dialect = Dialect.Draft202012 });
    }

    private static TaskPilotReceiptParseResult Invalid(string error, TaskPilotRoundOutcome outcome, string summary) =>
        new()
        {
            IsValid = false,
            Error = error,
            Outcome = outcome,
            Summary = summary,
        };
}
