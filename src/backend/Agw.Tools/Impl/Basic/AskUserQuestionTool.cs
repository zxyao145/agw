using System.Text;
using System.Text.RegularExpressions;

using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

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
        User answers collected by the permission component, keyed by question text.
        Populated by the host before invocation; the tool echoes them back.
        """
    )]
    public Dictionary<string, string>? Answers { get; set; }

    [Description(
        """
        Optional per-question annotations from the user (e.g., notes on preview selections),
        keyed by question text.
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

    /// <summary>
    /// Pre-formatted summary suitable to feed back to the model (mirrors TS
    /// mapToolResultToToolResultBlockParam output).
    /// </summary>
    public string Summary { get; set; } = "";
}

internal class AskUserQuestionTool : IAgwTool
{
    private static readonly Regex _htmlDocumentTag = new(@"<\s*(html|body|!doctype)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _htmlScriptStyleTag = new(@"<\s*(script|style)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "ask_user_question";

    public string Category => "Interaction";

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

        if (toolParams.Questions is null || toolParams.Questions.Count == 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "At least one question is required.");
        }

        if (toolParams.Questions.Count > 4)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "At most 4 questions are allowed.");
        }

        // Uniqueness: question texts must be unique across the request,
        // option labels must be unique within each question.
        var seenQuestions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in toolParams.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.Question))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Question text is required.");
            }
            if (!seenQuestions.Add(q.Question))
            {
                throw new AgwException(ErrorCodes.InvalidParam,
                    "Question texts must be unique across the request.");
            }
            if (q.Options is null || q.Options.Count < 2 || q.Options.Count > 4)
            {
                throw new AgwException(ErrorCodes.InvalidParam,
                    $"Question '{q.Question}' must have 2-4 options.");
            }

            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var opt in q.Options)
            {
                if (string.IsNullOrWhiteSpace(opt.Label))
                {
                    throw new AgwException(ErrorCodes.InvalidParam,
                        $"Option label is required for question '{q.Question}'.");
                }
                if (!seenLabels.Add(opt.Label))
                {
                    throw new AgwException(ErrorCodes.InvalidParam,
                        $"Option labels must be unique within question '{q.Question}'.");
                }

                // Lightweight HTML preview validation (fragment-only, no script/style).
                var err = ValidateHtmlPreview(opt.Preview);
                if (err is not null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam,
                        $"Option '{opt.Label}' in question '{q.Question}': {err}");
                }
            }
        }

        var answers = toolParams.Answers ?? new Dictionary<string, string>();
        var summary = BuildSummary(answers, toolParams.Annotations);

        return new AskUserQuestionToolResult
        {
            Questions = toolParams.Questions,
            Answers = answers,
            Annotations = toolParams.Annotations,
            Summary = summary
        };
    }

    private static string BuildSummary(
        Dictionary<string, string> answers,
        Dictionary<string, AskUserQuestionAnnotation>? annotations)
    {
        if (answers.Count == 0)
        {
            return "User has not answered any questions yet.";
        }

        var parts = new List<string>(answers.Count);
        foreach (var (question, answer) in answers)
        {
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

        return "User has answered your questions: " + string.Join(", ", parts)
               + ". You can now continue with the user's answers in mind.";
    }

    /// <summary>
    /// Mirrors the TS validateHtmlPreview helper. Rejects full HTML documents and
    /// script/style blocks. Returns null if the preview is valid or absent.
    /// Note: When previews are markdown only, callers may skip this — the checks
    /// here are defensive and safe to run regardless of intended format.
    /// </summary>
    private static string? ValidateHtmlPreview(string? preview)
    {
        if (string.IsNullOrEmpty(preview)) return null;

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
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
