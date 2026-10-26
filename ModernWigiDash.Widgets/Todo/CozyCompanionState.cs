namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Persisted or live state of the cozy companion pet.
/// </summary>
public sealed record CozyCompanionState
{
    /// <summary>Pet companion name.</summary>
    public string Name { get; init; } = "Pochi";

    /// <summary>Total accumulated experience points.</summary>
    public int TotalXp { get; init; }

    /// <summary>Wallet of snack treats available to feed the pet.</summary>
    public int SnackTreats { get; init; }

    /// <summary>Current consecutive daily streak count.</summary>
    public int StreakDays { get; init; }

    /// <summary>Available streak freeze shields (earned every 7-day milestone, max 2).</summary>
    public int StreakShields { get; init; }

    /// <summary>Last date an active task was completed or streak touched.</summary>
    public DateOnly? LastActiveDate { get; init; }

    /// <summary>Timestamp of last completed task.</summary>
    public DateTime? LastTaskCompletedUtc { get; init; }

    /// <summary>Timestamp of last treat fed to the companion.</summary>
    public DateTime? LastFedTimeUtc { get; init; }
}
