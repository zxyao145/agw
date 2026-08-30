using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;
using Agw.Tools.Contracts.Abstractions;
using Agw.Tools.HumanInteraction;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Basic;

public class AskUserQuestionOption
{
    [Description(
        """
            The display text for this option that the user will see and select.
            Should be concise (1-5 words) and clearly describe the choice.
            """
    )]
    public string Label { get; set; } = "";

    [Description(
        """
            Explanation of what this option means or what will happen if chosen.
            Useful for providing context about trade-offs or implications.
            """
    )]
    public string Description { get; set; } = "";

    [Description(
        """
            Optional preview content rendered when this option is focused.
            Use for mockups, code snippets, or visual comparisons that help users compare options.
            Only supported for single-select questions (not multiSelect).
            """
    )]
    public string? Preview { get; set; }
}

public class AskUserQuestionQuestion
{
    [Description(
        """
            The complete question to ask the user. Should be clear, specific, and end with a question mark.
            Example: "Which library should we use for date formatting?"
            If multiSelect is true, phrase it accordingly, e.g. "Which features do you want to enable?"
            """
    )]
    public string Question { get; set; } = "";

    [Description(
        """
            Very short label displayed as a chip/tag (max 12 chars).
            Examples: "Auth method", "Library", "Approach".
            """
    )]
    public string Header { get; set; } = "";

    [Description(
        """
            The available choices for this question. Must have 2-4 options.
            Each option should be a distinct, mutually exclusive choice (unless multiSelect is enabled).
            There should be no 'Other' option, that will be provided automatically.
            """
    )]
    public List<AskUserQuestionOption> Options { get; set; } = [];

    [Description(
        """
            Set to true to allow the user to select multiple options instead of just one.
            Use when choices are not mutually exclusive. Default false.
            """
    )]
    public bool MultiSelect { get; set; }
}

public class AskUserQuestionAnnotation
{
    [Description(
        """
            The preview content of the selected option, if the question used previews.
            """
    )]
    public string? Preview { get; set; }

    [Description(
        """
            Free-text notes the user added to their selection.
            """
    )]
    public string? Notes { get; set; }
}

public class AskUserQuestionMetadata
{
    [Description(
        """
            Optional identifier for the source of this question (e.g., "remember" for /remember command).
            Used for analytics tracking.
            """
    )]
    public string? Source { get; set; }
}

public class AskUserQuestionToolParams
{
    [Description(
        """
            Questions to ask the user (1-4 questions).
            """
    )]
    public List<AskUserQuestionQuestion> Questions { get; set; } = [];

    [Description(
        """
            User answers collected by the human-interaction host, keyed by question text.
            This is host-managed response data; values supplied by the model are ignored.
            """
    )]
    public Dictionary<string, string>? Answers { get; set; }

    [Description(
        """
            Optional host-managed per-question annotations from the user (e.g., notes on preview
            selections), keyed by question text. Values supplied by the model are ignored.
            """
    )]
    public Dictionary<string, AskUserQuestionAnnotation>? Annotations { get; set; }

    [Description(
        """
            Optional metadata for tracking and analytics purposes. Not displayed to the user.
            """
    )]
    public AskUserQuestionMetadata? Metadata { get; set; }
}

public class AskUserQuestionToolResult
{
    public List<AskUserQuestionQuestion> Questions { get; set; } = [];
    public Dictionary<string, string> Answers { get; set; } = [];
    public Dictionary<string, AskUserQuestionAnnotation>? Annotations { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>
    /// Pre-formatted summary suitable to feed back to the model (mirrors TS
    /// mapToolResultToToolResultBlockParam output).
    /// </summary>
    public string Summary { get; set; } = "";
}

internal class AskUserQuestionTool : IAgwTool
{
    private static readonly Regex _htmlDocumentTag = new(
        @"<\s*(html|body|!doctype)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex _htmlScriptStyleTag = new(
        @"<\s*(script|style)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public string Name => "ask_user_question";

    public string Category => "Interaction";

    public bool AllowInPlanMode => true;

    [Description(
        """
            Asks the user multiple choice questions to gather information, clarify ambiguity,
            understand preferences, make decisions or offer them choices.

            Use this tool when you need to:
            1. Gather user preferences or requirements
            2. Clarify ambiguous instructions
            3. Get decisions on implementation choices as you work
            4. Offer choices to the user about what direction to take

            Usage notes:
            - Users will always be able to select "Other" to provide custom text input
            - Use multiSelect: true to allow multiple answers to be selected for a question
            - If you recommend a specific option, make that the first option in the list and add
              "(Recommended)" at the end of the label

            Preview feature:
            Use the optional `preview` field on options when presenting concrete artifacts that users
            need to visually compare (ASCII mockups, code snippets, diagram variations, configuration
            examples). Do not use previews for simple preference questions where labels and descriptions
            suffice. Previews are only supported for single-select questions (not multiSelect).
            """
    )]
    public AskUserQuestionToolResult Execute(AskUserQuestionToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);
        ValidateQuestions(toolParams.Questions);
        var answers = ValidateAnswers(toolParams.Questions, toolParams.Answers, toolParams.Annotations);
        var summary = BuildSummary(toolParams.Questions, answers, toolParams.Annotations);

        return new AskUserQuestionToolResult
        {
            Questions = toolParams.Questions,
            Answers = answers,
            Annotations = toolParams.Annotations,
            Summary = summary,
        };
    }

    private static string BuildSummary(
        IReadOnlyList<AskUserQuestionQuestion> questions,
        Dictionary<string, string> answers,
        Dictionary<string, AskUserQuestionAnnotation>? annotations
    )
    {
        var parts = new List<string>(answers.Count);
        foreach (var item in questions)
        {
            var question = item.Question;
            var answer = answers[question];
            var sb = new StringBuilder();
            sb.Append('"').Append(question).Append("\"=\"").Append(answer).Append('"');

            if (annotations is not null && annotations.TryGetValue(question, out var ann))
            {
                if (!string.IsNullOrEmpty(ann.Preview))
                {
                    sb.Append(" selected preview:\n").Append(ann.Preview);
                }
                if (!string.IsNullOrEmpty(ann.Notes))
                {
                    sb.Append(" user notes: ").Append(ann.Notes);
                }
            }

            parts.Add(sb.ToString());
        }

        return "User has answered your questions: "
            + string.Join(", ", parts)
            + ". You can now continue with the user's answers in mind.";
    }

    internal static void ValidateQuestions(IReadOnlyList<AskUserQuestionQuestion>? questions)
    {
        if (questions is null || questions.Count == 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "At least one question is required.");
        }

        if (questions.Count > 4)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "At most 4 questions are allowed.");
        }

        var seenQuestions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Question))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Question text is required.");
            }
            question.Question = question.Question.Trim();
            if (string.IsNullOrWhiteSpace(question.Header))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Question header is required for '{question.Question}'."
                );
            }
            question.Header = question.Header.Trim();
            if (!seenQuestions.Add(question.Question))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Question texts must be unique across the request.");
            }
            if (question.Options is null || question.Options.Count < 2 || question.Options.Count > 4)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Question '{question.Question}' must have 2-4 options."
                );
            }

            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Label))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Option label is required for question '{question.Question}'."
                    );
                }
                option.Label = option.Label.Trim();
                if (string.IsNullOrWhiteSpace(option.Description))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Option description is required for '{option.Label}' in question '{question.Question}'."
                    );
                }
                option.Description = option.Description.Trim();
                if (!seenLabels.Add(option.Label))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Option labels must be unique within question '{question.Question}'."
                    );
                }

                if (question.MultiSelect && !string.IsNullOrEmpty(option.Preview))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Option '{option.Label}' in question '{question.Question}' cannot use a preview when multiSelect is enabled."
                    );
                }

                var error = ValidateHtmlPreview(option.Preview);
                if (error is not null)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Option '{option.Label}' in question '{question.Question}': {error}"
                    );
                }
            }
        }
    }

    internal static Dictionary<string, string> ValidateAnswers(
        IReadOnlyList<AskUserQuestionQuestion> questions,
        Dictionary<string, string>? answers,
        Dictionary<string, AskUserQuestionAnnotation>? annotations
    )
    {
        if (answers == null || answers.Count != questions.Count)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Every question requires a user-provided answer.");
        }

        var questionTexts = questions.Select(static question => question.Question).ToHashSet(StringComparer.Ordinal);
        foreach (var (question, answer) in answers)
        {
            if (!questionTexts.Contains(question) || string.IsNullOrWhiteSpace(answer))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Answers must contain one non-empty value for every requested question."
                );
            }
        }

        if (annotations != null && annotations.Keys.Any(question => !questionTexts.Contains(question)))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Annotations may only reference requested questions.");
        }

        return new Dictionary<string, string>(answers, StringComparer.Ordinal);
    }

    /// <summary>
    /// Mirrors the TS validateHtmlPreview helper. Rejects full HTML documents and
    /// script/style blocks. Returns null if the preview is valid or absent.
    /// Note: When previews are markdown only, callers may skip this — the checks
    /// here are defensive and safe to run regardless of intended format.
    /// </summary>
    private static string? ValidateHtmlPreview(string? preview)
    {
        if (string.IsNullOrEmpty(preview))
            return null;

        if (_htmlDocumentTag.IsMatch(preview))
        {
            return "preview must be an HTML fragment, not a full document (no <html>, <body>, or <!DOCTYPE>).";
        }
        if (_htmlScriptStyleTag.IsMatch(preview))
        {
            return "preview must not contain <script> or <style> tags. Use inline styles via the style attribute if needed.";
        }
        return null;
    }

    public AITool ToAITool()
    {
        Func<AskUserQuestionToolParams, AskUserQuestionToolResult> func = Execute;
        var innerFunction = (AIFunction)AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
        return new HumanInteractionRequiredAIFunction(innerFunction, new AskUserQuestionInteractionProtocol());
    }

    private sealed class AskUserQuestionInteractionProtocol : IHumanInteractionProtocol
    {
        public HumanInteractionRequest CreateRequest(string requestId, AIFunctionArguments arguments)
        {
            var toolParams = DeserializeArguments(arguments);
            ValidateQuestions(toolParams.Questions);
            toolParams.Answers = null;
            toolParams.Annotations = null;

            using var payload = JsonDocument.Parse(
                JsonUtil.Serialize(new { toolParams.Questions, toolParams.Metadata })
            );
            return new HumanInteractionRequest(
                requestId,
                "questions",
                "The agent needs your input to continue.",
                payload.RootElement.Clone()
            );
        }

        public AIFunctionArguments BindResponse(AIFunctionArguments arguments, HumanInteractionResponse response)
        {
            if (!response.ResponseData.HasValue)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Question response data is required.");
            }

            var responseData =
                JsonUtil.Deserialize<AskUserQuestionResponseData>(response.ResponseData.Value.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question response data is invalid.");
            var toolParams = DeserializeArguments(arguments);
            ValidateQuestions(toolParams.Questions);
            ValidateAnswers(toolParams.Questions, responseData.Answers, responseData.Annotations);

            var values = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
            {
                ["answers"] = responseData.Answers,
                ["annotations"] = responseData.Annotations,
            };
            return new AIFunctionArguments(values) { Services = arguments.Services };
        }

        public object CreateCancelledResult(AIFunctionArguments arguments, HumanInteractionResponse response)
        {
            var toolParams = DeserializeArguments(arguments);
            ValidateQuestions(toolParams.Questions);
            return new AskUserQuestionToolResult
            {
                Questions = toolParams.Questions,
                Cancelled = true,
                Summary = "User cancelled the question request without answering.",
            };
        }

        private static AskUserQuestionToolParams DeserializeArguments(AIFunctionArguments arguments)
        {
            var values = arguments.ToDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.Ordinal
            );
            return JsonUtil.Deserialize<AskUserQuestionToolParams>(JsonUtil.Serialize(values))
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question arguments are invalid.");
        }
    }

    private sealed class AskUserQuestionResponseData
    {
        public Dictionary<string, string>? Answers { get; set; }

        public Dictionary<string, AskUserQuestionAnnotation>? Annotations { get; set; }
    }
}
