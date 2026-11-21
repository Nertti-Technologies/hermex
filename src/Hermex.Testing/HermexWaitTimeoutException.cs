namespace Hermex.Testing;

/// <summary>Thrown by <see cref="HermexClient.WaitForMessageAsync"/> when no matching message arrives in time.</summary>
public sealed class HermexWaitTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    public HermexWaitTimeoutException()
        : base("No matching message arrived in time.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public HermexWaitTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public HermexWaitTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The criteria that were waited for, as text.</summary>
    public string? Criteria { get; init; }

    /// <summary>How long the wait lasted.</summary>
    public TimeSpan Timeout { get; init; }
}
