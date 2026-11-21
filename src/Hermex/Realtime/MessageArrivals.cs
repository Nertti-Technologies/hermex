namespace Hermex.Realtime;

/// <summary>
/// In-process "a message was stored" signal for the wait API (ADR-0003 D6). A waiter takes <see cref="Next"/>
/// before it checks the store, so a message committed between the check and the wait still wakes it.
/// Raised by <c>MailPersistenceService</c> after each committed batch. Every other code path that stores messages
/// (for example <c>.eml</c> import, ADR-0003 D14) must call <see cref="Pulse"/> after its commit too, or waiters
/// only see those messages at their next wake-up.
/// </summary>
internal sealed class MessageArrivals
{
    /// <summary>Concurrent waiters allowed (estimate: well above a parallel test run); more get 429.</summary>
    public const int MaxWaiters = 64;

    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiters;

    /// <summary>Callers currently waiting.</summary>
    public int Waiting => Volatile.Read(ref _waiters);

    /// <summary>Completes at the next <see cref="Pulse"/>.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    /// <summary>Wakes every current waiter.</summary>
    public void Pulse() =>
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

    /// <summary>Claims a waiter slot; false when <see cref="MaxWaiters"/> are already waiting.</summary>
    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _waiters) <= MaxWaiters)
            return true;
        Interlocked.Decrement(ref _waiters);
        return false;
    }

    /// <summary>Releases a slot claimed by <see cref="TryEnter"/>.</summary>
    public void Exit() => Interlocked.Decrement(ref _waiters);
}
