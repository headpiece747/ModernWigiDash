namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The companion gamification face of the task provider.
/// </summary>
public interface ICompanionSource
{
    /// <summary>
    /// Renames the companion (persisted local gamification state in both
    /// modes). A blank name is a no-op.
    /// </summary>
    void SetCompanionName(string name);

    /// <summary>Feeds the companion pet treats.</summary>
    bool FeedCompanion();
}
