namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The verdict of a task mutation over the Super Productivity wire: the
/// typed outcome the cache decision reads (the house's ConnectionState
/// precedent — a verdict, never a bool).
/// </summary>
internal enum TaskMutationOutcome
{
    /// <summary>The server confirmed the mutation.</summary>
    Confirmed,

    /// <summary>The server answered with a non-success status (seen and declined).</summary>
    Rejected,

    /// <summary>A network/connection fault (the server never saw the request).</summary>
    Faulted,
}
