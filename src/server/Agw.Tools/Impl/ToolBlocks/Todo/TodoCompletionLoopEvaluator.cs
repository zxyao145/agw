using System.Text;
using Microsoft.Agents.AI;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

/// <summary>
/// A <see cref="LoopEvaluator"/> that keeps re-invoking the wrapped agent until a <see cref="AgwTodoProvider"/> has no
/// remaining (incomplete) todo items, optionally only while the agent is operating in one of a configured set of modes
/// tracked by an <see cref="AgentModeProvider"/>.
/// </summary>
[Experimental("MAAI001")]
public sealed class TodoCompletionLoopEvaluator : LoopEvaluator
{
    /// <summary>
    /// The placeholder token within <see cref="DefaultFeedbackMessageTemplate"/> that is replaced with the remaining
    /// todo items.
    /// </summary>
    public const string RemainingTodosPlaceholder = "{remaining_todos}";

    /// <summary>The default template used to build feedback while incomplete todo items remain.</summary>
    public const string DefaultFeedbackMessageTemplate =
        "You still have incomplete todo items. Continue working until every item is complete, marking each item as "
        + "complete when finished. The following items are still open:\n"
        + RemainingTodosPlaceholder;

    private readonly HashSet<string>? _modes;
    private readonly string _feedbackMessageTemplate;

    /// <summary>Initializes a new instance of the <see cref="TodoCompletionLoopEvaluator"/> class.</summary>
    public TodoCompletionLoopEvaluator(TodoCompletionLoopEvaluatorOptions? options = null)
    {
        if (options?.Modes is not null)
        {
            var modeSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string mode in options.Modes)
            {
                if (string.IsNullOrWhiteSpace(mode))
                {
                    throw new ArgumentException("Mode names must not be null, empty, or whitespace.", nameof(options));
                }

                modeSet.Add(mode);
            }

            if (modeSet.Count == 0)
            {
                throw new ArgumentException(
                    "At least one mode must be supplied when modes are specified. Leave Modes null to apply in every mode.",
                    nameof(options)
                );
            }

            this._modes = modeSet;
        }

        this._feedbackMessageTemplate = options?.FeedbackMessageTemplate ?? DefaultFeedbackMessageTemplate;
    }

    /// <inheritdoc />
    public override async ValueTask<LoopEvaluation> EvaluateAsync(
        LoopContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        AgwTodoProvider agwTodoProvider =
            context.Agent.GetService<AgwTodoProvider>()
            ?? throw new InvalidOperationException(
                $"{nameof(TodoCompletionLoopEvaluator)} requires a {nameof(AgwTodoProvider)} to be registered on the agent, but none could be resolved via GetService."
            );

        if (this._modes is not null)
        {
            AgentModeProvider modeProvider =
                context.Agent.GetService<AgentModeProvider>()
                ?? throw new InvalidOperationException(
                    $"{nameof(TodoCompletionLoopEvaluator)} was configured with modes but no {nameof(AgentModeProvider)} could be resolved from the agent via GetService."
                );

            string currentMode = await modeProvider
                .GetModeAsync(context.Session, cancellationToken)
                .ConfigureAwait(false);
            if (!this._modes.Contains(currentMode))
            {
                return LoopEvaluation.Stop();
            }
        }

        List<AgwTodoItem> remaining = await agwTodoProvider
            .GetRemainingTodosAsync(context.Session, cancellationToken)
            .ConfigureAwait(false);
        if (remaining.Count == 0)
        {
            return LoopEvaluation.Stop();
        }

        string feedback = this._feedbackMessageTemplate.Replace(
            RemainingTodosPlaceholder,
            FormatRemainingTodos(remaining)
        );
        return LoopEvaluation.Continue(feedback);
    }

    private static string FormatRemainingTodos(List<AgwTodoItem> remaining)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < remaining.Count; i++)
        {
            AgwTodoItem item = remaining[i];
            sb.Append("- ").Append(item.Id).Append(": ").Append(item.Title);
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                sb.Append(" — ").Append(item.Description);
            }

            if (i < remaining.Count - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
