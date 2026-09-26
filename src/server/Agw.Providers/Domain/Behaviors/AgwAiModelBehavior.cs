using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Providers.Domain.Behaviors;

public sealed class AgwAiModelBehavior
{
    private readonly AgwAiModel _model;

    public AgwAiModelBehavior(AgwAiModel model)
    {
        _model = model;
    }

    /// <summary>
    /// <para>设置模型定义：上下文窗口必须大于零，输出上限必须大于零且小于上下文窗口。</para>
    /// <para>Sets the model definition: the context window must be positive, and the output limit must be positive and below the context window.</para>
    /// </summary>
    public void Define(string name, string? description, int maxContextWindowTokens, int maxOutputTokens)
    {
        EnsureTokenLimits(maxContextWindowTokens, maxOutputTokens);
        _model.Name = name;
        _model.Description = description;
        _model.MaxContextWindowTokens = maxContextWindowTokens;
        _model.MaxOutputTokens = maxOutputTokens;
    }

    private static void EnsureTokenLimits(int maxContextWindowTokens, int maxOutputTokens)
    {
        if (maxContextWindowTokens <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "maxContextWindowTokens must be greater than zero.");
        }

        if (maxOutputTokens <= 0 || maxOutputTokens >= maxContextWindowTokens)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "maxOutputTokens must be greater than zero and less than maxContextWindowTokens."
            );
        }
    }
}
