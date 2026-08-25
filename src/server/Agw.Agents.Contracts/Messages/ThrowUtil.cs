using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Contracts.Messages;

public class ThrowUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: NotNull]
    public static T IfNull<T>([NotNull] T argument, [CallerArgumentExpression("argument")] string paramName = "")
    {
        if (argument == null)
        {
            ArgumentNullException(paramName);
        }

        return argument;
    }

    [DoesNotReturn]
    public static void ArgumentNullException(string paramName)
    {
        throw new AgwException(ErrorCodes.InvalidParam, $"Argument '{paramName}' cannot be null.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: NotNull]
    public static string IfNullOrWhitespace(
        [NotNull] string? argument,
        [CallerArgumentExpression("argument")] string paramName = ""
    )
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            if (argument == null)
            {
                ArgumentNullException(paramName);
            }
            else
            {
                ArgumentException(paramName, "Argument is whitespace");
            }
        }

        return argument;
    }

    [DoesNotReturn]
    public static void ArgumentException(string paramName, string? message)
    {
        throw new AgwException(ErrorCodes.InvalidParam, message ?? $"Argument '{paramName}' is invalid.");
    }

    [DoesNotReturn]
    internal static void ArgumentNullException(string message, string paramName)
    {
        throw new AgwException(ErrorCodes.InvalidParam, message);
    }
}
