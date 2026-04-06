using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Agw.Shared.Utils;

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
        throw new ArgumentNullException(paramName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: NotNull]
    public static string IfNullOrWhitespace([NotNull] string? argument, [CallerArgumentExpression("argument")] string paramName = "")
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
        throw new ArgumentException(message, paramName);
    }

    [DoesNotReturn]
    internal static void ArgumentNullException(string message, string paramName)
    {
        throw new ArgumentNullException(message, paramName);

    }
}
