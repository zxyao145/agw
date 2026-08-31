namespace PiAgentSdk;

/// <summary>Provides the base exception for failures reported by the Pi SDK.</summary>
public class PiSdkException : Exception
{
    /// <summary>Initializes an exception with the supplied message.</summary>
    /// <param name="message">A description of the failure.</param>
    public PiSdkException(string message)
        : base(message) { }

    /// <summary>Initializes an exception with the supplied message and underlying cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public PiSdkException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Represents malformed, incomplete, or incompatible Pi RPC protocol data.</summary>
public sealed class PiProtocolException : PiSdkException
{
    /// <summary>Initializes a protocol exception with the supplied message.</summary>
    /// <param name="message">A description of the protocol violation.</param>
    public PiProtocolException(string message)
        : base(message) { }

    /// <summary>Initializes a protocol exception with the supplied message and underlying cause.</summary>
    /// <param name="message">A description of the protocol violation.</param>
    /// <param name="innerException">The exception raised while processing protocol data.</param>
    public PiProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Represents a command that Pi accepted at the transport level but rejected at the RPC level.</summary>
public sealed class PiRpcException : PiSdkException
{
    /// <summary>Initializes an RPC command failure.</summary>
    /// <param name="command">The Pi RPC command name.</param>
    /// <param name="error">The error returned by Pi.</param>
    public PiRpcException(string command, string error)
        : base($"Pi RPC command '{command}' failed: {error}")
    {
        Command = command;
        Error = error;
    }

    /// <summary>Gets the Pi RPC command that failed.</summary>
    public string Command { get; }

    /// <summary>Gets the error text returned by Pi.</summary>
    public string Error { get; }
}

/// <summary>Represents a Pi RPC command that did not complete within its configured timeout.</summary>
public sealed class PiCommandTimeoutException : TimeoutException
{
    /// <summary>Initializes a command timeout exception.</summary>
    /// <param name="command">The Pi RPC command that timed out.</param>
    /// <param name="timeout">The timeout applied to the command.</param>
    public PiCommandTimeoutException(string command, TimeSpan timeout)
        : base($"Pi RPC command '{command}' did not respond within {timeout}.")
    {
        Command = command;
        Timeout = timeout;
    }

    /// <summary>Gets the Pi RPC command that timed out.</summary>
    public string Command { get; }

    /// <summary>Gets the timeout applied to the command.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>Represents an unexpected exit of the Pi RPC child process.</summary>
public sealed class PiProcessExitException : PiSdkException
{
    /// <summary>Initializes an exception from the process exit information.</summary>
    /// <param name="exitCode">The process exit code, or <see langword="null"/> when unavailable.</param>
    /// <param name="stderr">The bounded tail of Pi standard error.</param>
    public PiProcessExitException(int? exitCode, string stderr)
        : base($"Pi RPC process exited unexpectedly with code {exitCode?.ToString() ?? "unknown"}. stderr: {stderr}")
    {
        ExitCode = exitCode;
        StandardError = stderr;
    }

    /// <summary>Gets the process exit code, or <see langword="null"/> when unavailable.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the bounded standard-error tail captured before the process exited.</summary>
    public string StandardError { get; }
}

/// <summary>Represents an attempt to start a second run on a busy <see cref="PiSession"/>.</summary>
public sealed class PiSessionBusyException : PiSdkException
{
    /// <summary>Initializes the single-active-run violation.</summary>
    public PiSessionBusyException()
        : base("Only one Pi run can be active in a session.") { }
}
