using Agw.Shared.Exceptions;

namespace Agw.Providers.Domain.Rules;

public static class ModelRules
{
    public static void ValidateTokenLimits(int maxContextWindowTokens, int maxOutputTokens)
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
