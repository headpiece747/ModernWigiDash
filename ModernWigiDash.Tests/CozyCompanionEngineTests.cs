namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets.Todo;

[TestClass]
public class CozyCompanionEngineTests
{
    [TestMethod]
    public void CalculateLevel_AtZeroXp_IsZero()
    {
        Assert.AreEqual(0, CozyCompanionEngine.CalculateLevel(0));
        Assert.AreEqual(0, CozyCompanionEngine.CalculateLevel(-50));
    }

    [TestMethod]
    public void CalculateLevel_MatchesSquareRootFormula()
    {
        // Level = floor(sqrt(XP / 100))
        Assert.AreEqual(0, CozyCompanionEngine.CalculateLevel(99));
        Assert.AreEqual(1, CozyCompanionEngine.CalculateLevel(100));
        Assert.AreEqual(1, CozyCompanionEngine.CalculateLevel(399));
        Assert.AreEqual(2, CozyCompanionEngine.CalculateLevel(400));
        Assert.AreEqual(3, CozyCompanionEngine.CalculateLevel(900));
        Assert.AreEqual(5, CozyCompanionEngine.CalculateLevel(2500));
    }

    [TestMethod]
    public void XpRequiredForLevel_MatchesLevelSquaredTimesHundred()
    {
        Assert.AreEqual(0, CozyCompanionEngine.XpRequiredForLevel(0));
        Assert.AreEqual(100, CozyCompanionEngine.XpRequiredForLevel(1));
        Assert.AreEqual(400, CozyCompanionEngine.XpRequiredForLevel(2));
        Assert.AreEqual(900, CozyCompanionEngine.XpRequiredForLevel(3));
    }

    [TestMethod]
    public void XpToNextLevel_ComputesDeltaToTarget()
    {
        Assert.AreEqual(100, CozyCompanionEngine.XpToNextLevel(0));
        Assert.AreEqual(50, CozyCompanionEngine.XpToNextLevel(50));
        Assert.AreEqual(300, CozyCompanionEngine.XpToNextLevel(100)); // Next is level 2 at 400
        Assert.AreEqual(150, CozyCompanionEngine.XpToNextLevel(250));
    }

    [TestMethod]
    public void LevelProgressFraction_ComputesNormalizedRange()
    {
        Assert.AreEqual(0f, CozyCompanionEngine.LevelProgressFraction(0), 0.001f);
        Assert.AreEqual(0.5f, CozyCompanionEngine.LevelProgressFraction(50), 0.001f);
        Assert.AreEqual(0f, CozyCompanionEngine.LevelProgressFraction(100), 0.001f);
        // Level 1: 100 to 400 (range 300). At 250 XP: (250-100)/300 = 150/300 = 0.5
        Assert.AreEqual(0.5f, CozyCompanionEngine.LevelProgressFraction(250), 0.001f);
    }

    [TestMethod]
    public void TreatsEarned_AwardsOneTreatPerTenXp()
    {
        Assert.AreEqual(0, CozyCompanionEngine.TreatsEarned(0));
        Assert.AreEqual(0, CozyCompanionEngine.TreatsEarned(9));
        Assert.AreEqual(1, CozyCompanionEngine.TreatsEarned(10));
        Assert.AreEqual(2, CozyCompanionEngine.TreatsEarned(25));
        Assert.AreEqual(10, CozyCompanionEngine.TreatsEarned(100));
    }

    [TestMethod]
    public void Feed_WithInsufficientTreats_ReturnsFalse()
    {
        int treats = 4;
        int xp = 100;
        bool result = CozyCompanionEngine.Feed(ref treats, ref xp);

        Assert.IsFalse(result);
        Assert.AreEqual(4, treats);
        Assert.AreEqual(100, xp);
    }

    [TestMethod]
    public void Feed_WithEnoughTreats_ConsumesFiveTreatsAndAddsAffection()
    {
        int treats = 7;
        int xp = 100;
        bool result = CozyCompanionEngine.Feed(ref treats, ref xp);

        Assert.IsTrue(result);
        Assert.AreEqual(2, treats);
        Assert.AreEqual(150, xp);
    }

    [TestMethod]
    public void ResolveMood_TransitionsAccordingToActivity()
    {
        DateTime now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        // Idle with no activity -> Sleepy
        Assert.AreEqual(CozyMood.Sleepy, CozyCompanionEngine.ResolveMood(false, null, null, now));

        // Focus timer running -> Attentive (top priority!)
        Assert.AreEqual(CozyMood.Attentive, CozyCompanionEngine.ResolveMood(true, null, null, now));

        // Task completed within 2 hours -> Happy
        DateTime taskRecent = now.AddMinutes(-30);
        Assert.AreEqual(CozyMood.Happy, CozyCompanionEngine.ResolveMood(false, taskRecent, null, now));

        // Task completed long ago (>2h) -> Sleepy
        DateTime taskOld = now.AddHours(-3);
        Assert.AreEqual(CozyMood.Sleepy, CozyCompanionEngine.ResolveMood(false, taskOld, null, now));

        // Fed treats within 10 seconds -> Overjoyed celebration
        DateTime fedRecent = now.AddSeconds(-5);
        Assert.AreEqual(CozyMood.Overjoyed, CozyCompanionEngine.ResolveMood(false, null, fedRecent, now));

        // When focus timer is started, Focus mode takes top priority even if recently fed
        Assert.AreEqual(CozyMood.Attentive, CozyCompanionEngine.ResolveMood(true, null, fedRecent, now));

        // Fed treats past 10s but within 2 hours -> transitions smoothly to Happy
        DateTime fedContent = now.AddMinutes(-5);
        Assert.AreEqual(CozyMood.Happy, CozyCompanionEngine.ResolveMood(false, null, fedContent, now));
    }

    [TestMethod]
    public void UpdateStreak_FirstActiveDay_StartsStreakAtOne()
    {
        DateOnly today = new(2026, 9, 27);
        var res = CozyCompanionEngine.UpdateStreak(today, null, 0, 0);

        Assert.AreEqual(1, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsFalse(res.ShieldConsumed);
        Assert.IsTrue(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_SameDay_PreservesCurrentStreak()
    {
        DateOnly today = new(2026, 9, 27);
        var res = CozyCompanionEngine.UpdateStreak(today, today, 5, 1);

        Assert.AreEqual(5, res.NewStreak);
        Assert.AreEqual(1, res.ShieldsRemaining);
        Assert.IsFalse(res.ShieldConsumed);
        Assert.IsTrue(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_NextConsecutiveDay_IncrementsStreak()
    {
        DateOnly yesterday = new(2026, 9, 26);
        DateOnly today = new(2026, 9, 27);
        var res = CozyCompanionEngine.UpdateStreak(today, yesterday, 3, 0);

        Assert.AreEqual(4, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsFalse(res.ShieldConsumed);
        Assert.IsTrue(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_ReachingSevenDayMilestone_AwardsShield()
    {
        DateOnly yesterday = new(2026, 9, 26);
        DateOnly today = new(2026, 9, 27);
        var res = CozyCompanionEngine.UpdateStreak(today, yesterday, 6, 0);

        Assert.AreEqual(7, res.NewStreak);
        Assert.AreEqual(1, res.ShieldsRemaining, "7-day milestone awards a shield");
        Assert.IsFalse(res.ShieldConsumed);
    }

    [TestMethod]
    public void UpdateStreak_MissedDayWithShield_ConsumesShieldAndPreservesStreak()
    {
        DateOnly twoDaysAgo = new(2026, 9, 25);
        DateOnly today = new(2026, 9, 27); // missed Sept 26
        var res = CozyCompanionEngine.UpdateStreak(today, twoDaysAgo, 10, 1);

        Assert.AreEqual(10, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsTrue(res.ShieldConsumed);
        Assert.IsTrue(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_MissedDayWithoutShield_ResetsStreakToOne()
    {
        DateOnly twoDaysAgo = new(2026, 9, 25);
        DateOnly today = new(2026, 9, 27); // missed Sept 26
        var res = CozyCompanionEngine.UpdateStreak(today, twoDaysAgo, 10, 0);

        Assert.AreEqual(1, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsFalse(res.ShieldConsumed);
        Assert.IsFalse(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_MissedMultipleDaysExceedingShields_ResetsStreakToOne()
    {
        DateOnly threeDaysAgo = new(2026, 9, 24);
        DateOnly today = new(2026, 9, 27); // 2 missed days (Sept 25, 26)
        // User has only 1 shield: cannot cover 2 missed days
        var res = CozyCompanionEngine.UpdateStreak(today, threeDaysAgo, 10, 1);

        Assert.AreEqual(1, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsFalse(res.ShieldConsumed);
        Assert.IsFalse(res.StreakPreserved);
    }

    [TestMethod]
    public void UpdateStreak_MissedMultipleDaysWithSufficientShields_ConsumesShieldsAndPreservesStreak()
    {
        DateOnly threeDaysAgo = new(2026, 9, 24);
        DateOnly today = new(2026, 9, 27); // 2 missed days (Sept 25, 26)
        // User has 2 shields: covers both missed days
        var res = CozyCompanionEngine.UpdateStreak(today, threeDaysAgo, 10, 2);

        Assert.AreEqual(10, res.NewStreak);
        Assert.AreEqual(0, res.ShieldsRemaining);
        Assert.IsTrue(res.ShieldConsumed);
        Assert.IsTrue(res.StreakPreserved);
    }

    [TestMethod]
    public void CalculateFocusXp_ComputesTenXpPerMinute()
    {
        Assert.AreEqual(0, CozyCompanionEngine.CalculateFocusXp(TimeSpan.FromSeconds(45)));
        Assert.AreEqual(10, CozyCompanionEngine.CalculateFocusXp(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(250, CozyCompanionEngine.CalculateFocusXp(TimeSpan.FromMinutes(25)));
    }

    [TestMethod]
    public void OnFocusReward_AppliesXpAndTheTreatsTheXpEarns()
    {
        var companion = new CozyCompanionState { TotalXp = 100, SnackTreats = 3 };

        var result = CozyCompanionEngine.OnFocusReward(companion, 50);

        Assert.AreEqual(150, result.TotalXp);
        Assert.AreEqual(8, result.SnackTreats); // 50 XP / 10 = 5 treats
    }

    [TestMethod]
    public void OnFocusReward_ZeroReward_IsANoop()
    {
        var companion = new CozyCompanionState { TotalXp = 100, SnackTreats = 3 };

        var result = CozyCompanionEngine.OnFocusReward(companion, 0);

        Assert.AreSame(companion, result);
    }

    [TestMethod]
    public void OnTaskCompleted_Completing_GrantsXpTreatsAndStreakTouch()
    {
        var nowUtc = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var companion = new CozyCompanionState { TotalXp = 100, SnackTreats = 3, StreakDays = 1, LastActiveDate = new DateOnly(2026, 10, 24) };

        var result = CozyCompanionEngine.OnTaskCompleted(companion, isDone: true, nowUtc);

        Assert.AreEqual(110, result.TotalXp);
        Assert.AreEqual(4, result.SnackTreats);
        Assert.AreEqual(2, result.StreakDays);
        Assert.AreEqual(new DateOnly(2026, 10, 25), result.LastActiveDate);
        Assert.AreEqual(nowUtc, result.LastTaskCompletedUtc);
    }

    [TestMethod]
    public void OnTaskCompleted_Uncompleting_TakesXpAndTreatsBack()
    {
        var companion = new CozyCompanionState { TotalXp = 5, SnackTreats = 0, LastTaskCompletedUtc = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc) };

        var result = CozyCompanionEngine.OnTaskCompleted(companion, isDone: false, DateTime.UtcNow);

        Assert.AreEqual(0, result.TotalXp); // clamped at zero
        Assert.AreEqual(0, result.SnackTreats);
    }
}
