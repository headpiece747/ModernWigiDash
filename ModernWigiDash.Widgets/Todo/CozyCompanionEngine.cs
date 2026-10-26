namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Pure math and state transition logic for the Cozy Companion gamification engine:
/// level curves, mood resolution, snack treat economy, streak calculation, and streak shields.
/// </summary>
public static class CozyCompanionEngine
{
    /// <summary>
    /// Calculates the pet's companion level from total accumulated experience points:
    /// Level = floor(sqrt(Total XP / 100)).
    /// </summary>
    public static int CalculateLevel(int totalXp)
    {
        if (totalXp <= 0) return 0;
        return (int)Math.Floor(Math.Sqrt(totalXp / 100.0));
    }

    /// <summary>
    /// Computes the minimum total XP required to reach a specific level.
    /// </summary>
    public static int XpRequiredForLevel(int level)
    {
        if (level <= 0) return 0;
        return level * level * 100;
    }

    /// <summary>
    /// Calculates remaining XP needed to advance to the next level.
    /// </summary>
    public static int XpToNextLevel(int totalXp)
    {
        int safeXp = Math.Max(0, totalXp);
        int currentLevel = CalculateLevel(safeXp);
        int nextLevelXp = XpRequiredForLevel(currentLevel + 1);
        return Math.Max(0, nextLevelXp - safeXp);
    }

    /// <summary>
    /// Computes the completion progress towards the next level (0.0f to 1.0f).
    /// </summary>
    public static float LevelProgressFraction(int totalXp)
    {
        int safeXp = Math.Max(0, totalXp);
        int currentLevel = CalculateLevel(safeXp);
        int currentBase = XpRequiredForLevel(currentLevel);
        int nextBase = XpRequiredForLevel(currentLevel + 1);
        int delta = nextBase - currentBase;
        if (delta <= 0) return 0f;
        return Math.Clamp((float)(safeXp - currentBase) / delta, 0f, 1f);
    }

    /// <summary>
    /// Resolves the companion's current mood from activity timestamps and timer state:
    /// 1. Active Focus timer takes top priority (Attentive / Focus mode).
    /// 2. Feeding treats triggers an intense 10-second celebration (Overjoyed mode).
    /// 3. Completing tasks or feeding within the last 2 hours keeps pet Happy.
    /// 4. Otherwise, idle companion sleeps (Sleepy mode).
    /// </summary>
    public static CozyMood ResolveMood(
        bool isFocusRunning,
        DateTime? lastTaskCompletedUtc,
        DateTime? lastFedTimeUtc,
        DateTime nowUtc)
    {
        if (isFocusRunning)
        {
            return CozyMood.Attentive;
        }

        if (lastFedTimeUtc.HasValue && (nowUtc - lastFedTimeUtc.Value) < TimeSpan.FromSeconds(10))
        {
            return CozyMood.Overjoyed;
        }

        if ((lastFedTimeUtc.HasValue && (nowUtc - lastFedTimeUtc.Value) < TimeSpan.FromHours(2)) ||
            (lastTaskCompletedUtc.HasValue && (nowUtc - lastTaskCompletedUtc.Value) < TimeSpan.FromHours(2)))
        {
            return CozyMood.Happy;
        }

        return CozyMood.Sleepy;
    }

    /// <summary>
    /// Converts XP gained into earned snack treats (1 treat per 10 XP).
    /// </summary>
    public static int TreatsEarned(int xpGained)
    {
        if (xpGained <= 0) return 0;
        return xpGained / 10;
    }

    /// <summary>
    /// Checks whether the user has enough snack treats to feed the pet (requires 5 treats).
    /// </summary>
    public static bool CanFeed(int currentTreats) => currentTreats >= 5;

    /// <summary>
    /// Feeds the companion 5 snack treats to grant 50 bonus affection XP.
    /// Returns true if successful, false if insufficient treats.
    /// </summary>
    public static bool Feed(ref int snackTreats, ref int totalXp)
    {
        if (snackTreats < 5)
            return false;

        snackTreats -= 5;
        totalXp += 50;
        return true;
    }

    /// <summary>
    /// Pure state update for daily streak tracking, supporting 7-day milestones and streak freeze shields.
    /// </summary>
    public static StreakUpdateResult UpdateStreak(
        DateOnly today,
        DateOnly? lastActiveDate,
        int currentStreak,
        int shieldsAvailable)
    {
        if (!lastActiveDate.HasValue)
        {
            return new StreakUpdateResult(
                NewStreak: 1,
                ShieldsRemaining: shieldsAvailable,
                ShieldConsumed: false,
                StreakPreserved: true);
        }

        int daysDiff = today.DayNumber - lastActiveDate.Value.DayNumber;

        if (daysDiff <= 0)
        {
            return new StreakUpdateResult(
                NewStreak: currentStreak,
                ShieldsRemaining: shieldsAvailable,
                ShieldConsumed: false,
                StreakPreserved: true);
        }

        if (daysDiff == 1)
        {
            int newStreak = currentStreak + 1;
            int newShields = (newStreak % 7 == 0 && shieldsAvailable < 2)
                ? shieldsAvailable + 1
                : shieldsAvailable;

            return new StreakUpdateResult(
                NewStreak: newStreak,
                ShieldsRemaining: newShields,
                ShieldConsumed: false,
                StreakPreserved: true);
        }

        int missedDays = daysDiff - 1;
        if (shieldsAvailable >= missedDays)
        {
            return new StreakUpdateResult(
                NewStreak: currentStreak,
                ShieldsRemaining: shieldsAvailable - missedDays,
                ShieldConsumed: true,
                StreakPreserved: true);
        }

        return new StreakUpdateResult(
            NewStreak: 1,
            ShieldsRemaining: 0,
            ShieldConsumed: false,
            StreakPreserved: false);
    }

    /// <summary>
    /// Computes XP gained during a completed focus session (+10 XP per minute).
    /// </summary>
    public static int CalculateFocusXp(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return 0;
        return (int)Math.Floor(elapsed.TotalMinutes) * 10;
    }

    /// <summary>
    /// The XP a completed task grants (and an un-completed one takes back).
    /// </summary>
    public const int TaskCompletionXp = 10;

    /// <summary>
    /// Pure transition: a task's completion state changed. Completing a
    /// task grants XP, the treats that XP earns, and a streak touch;
    /// un-completing a task takes them back.
    /// </summary>
    public static CozyCompanionState OnTaskCompleted(CozyCompanionState companion, bool isDone, DateTime nowUtc)
    {
        if (isDone)
        {
            var streak = UpdateStreak(
                DateOnly.FromDateTime(nowUtc),
                companion.LastActiveDate,
                companion.StreakDays,
                companion.StreakShields);

            return companion with
            {
                TotalXp = companion.TotalXp + TaskCompletionXp,
                SnackTreats = companion.SnackTreats + TreatsEarned(TaskCompletionXp),
                StreakDays = streak.NewStreak,
                StreakShields = streak.ShieldsRemaining,
                LastActiveDate = DateOnly.FromDateTime(nowUtc),
                LastTaskCompletedUtc = nowUtc,
            };
        }

        return companion with
        {
            TotalXp = Math.Max(0, companion.TotalXp - TaskCompletionXp),
            SnackTreats = Math.Max(0, companion.SnackTreats - TreatsEarned(TaskCompletionXp)),
        };
    }

    /// <summary>
    /// Pure transition: applies a focus-session reward to the companion
    /// (XP plus the treats that XP earns). A zero reward is a no-op
    /// (the same state instance returns).
    /// </summary>
    public static CozyCompanionState OnFocusReward(CozyCompanionState companion, int focusXp)
    {
        if (focusXp <= 0)
        {
            return companion;
        }

        return companion with
        {
            TotalXp = companion.TotalXp + focusXp,
            SnackTreats = companion.SnackTreats + TreatsEarned(focusXp),
        };
    }
}
