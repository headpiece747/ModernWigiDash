using System.Diagnostics.CodeAnalysis;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the Now Playing widget: every string and ratio the
/// render methods lay out — the meta line, the time format, the friendly app
/// name, the progress and seek ratios, the playback-rate text. The only media
/// state it reads is the seam's neutral vocabulary
/// (<see cref="MediaPlaybackStatus"/> / <see cref="MediaRepeatMode"/>), so it
/// is truly WinRT-free and assertable without a canvas; the widget's draw
/// methods are thin adapters. Also owns the two rules the monitor's tap
/// policy applies to its live state: the repeat cycle
/// (<see cref="NextRepeatMode"/>) and the seek enablement
/// (<see cref="CanSeekNow"/>).
/// </summary>
public static class NowPlayingPresentation
{
    /// <summary>
    /// The known media apps mapped to display names, in precedence order: the
    /// first matching token wins, so "itunes" outranks the bare "apple"/"music"
    /// tokens (an Apple iTunes AUMID must not read Apple Music) and "spotify"
    /// outranks a bare "music". The order is load-bearing behavior, not style:
    /// a reorder is a behavior change, and the collision pins in
    /// NowPlayingPresentationTests fail on one.
    /// </summary>
    private static readonly (string Token, string Name)[] AppNameRules =
    [
        ("spotify", "Spotify"),
        ("chrome", "Chrome"),
        ("msedge", "Edge"),
        ("firefox", "Firefox"),
        ("vlc", "VLC"),
        ("itunes", "iTunes"),
        ("apple", "Apple Music"),
        ("music", "Apple Music"),
        ("mediaplayer", "Windows Media Player"),
        ("wmplayer", "Windows Media Player"),
        ("discord", "Discord"),
        ("foobar", "foobar2000"),
        ("steam", "Steam"),
    ];

    /// <summary>
    /// "m:ss" (or "h:mm:ss" past an hour); invalid durations read "0:00".
    /// </summary>
    public static string FormatTime(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
        {
            return "0:00";
        }
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// The friendly name for a media app id: the first matching entry of
    /// <see cref="AppNameRules"/> wins; anything else falls back to the last
    /// AUMID segment, truncated to 16 characters.
    /// </summary>
    public static string FriendlyAppName(string appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return "Media";
        }
        string lower = appId.ToLowerInvariant();
        foreach (var (token, name) in AppNameRules)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return name;
            }
        }
        return FallbackSegment(appId);
    }

    /// <summary>
    /// The last AUMID segment of an unknown app id: the substring after the
    /// last '!' (the AUMID package/app separator) or the whole id, then after
    /// the last '.' if any, truncated to 16 characters.
    /// </summary>
    private static string FallbackSegment(string appId)
    {
        int sep = appId.LastIndexOf('!');
        string name = sep >= 0 ? appId[(sep + 1)..] : appId;
        int dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }
        return name.Length > 16 ? name[..16] : name;
    }

    /// <summary>
    /// The "Track n/m · genre / genre" line under the album; empty when the
    /// snapshot carries neither a track number nor genres.
    /// </summary>
    public static string MetaLine(int trackNumber, int albumTrackCount, IReadOnlyList<string> genres)
    {
        List<string> parts = [];
        if (trackNumber > 0)
        {
            parts.Add(albumTrackCount > 0 ? $"Track {trackNumber}/{albumTrackCount}" : $"Track {trackNumber}");
        }
        if (genres.Count > 0)
        {
            parts.Add(string.Join(" / ", genres.Take(2)));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The idle verdict: no snapshot, or a session that has Closed or
    /// Stopped (the states where the widget draws its idle panel instead of
    /// the media view). One spelling for the render gate; assertable here
    /// without a canvas. <c>false</c> carries the null fact forward (a
    /// non-idle render always has a live snapshot), so the widget's
    /// flow analysis keeps its null-proofing when the predicate replaces
    /// the inline check.
    /// </summary>
    public static bool IsIdle([NotNullWhen(false)] MediaSnapshot? snap)
        => snap is null
            || snap.Status is MediaPlaybackStatus.Closed
            or MediaPlaybackStatus.Stopped;

    /// <summary>Clamped position/duration ratio; zero when the duration is unknown.</summary>
    public static double ProgressRatio(double positionSeconds, double durationSeconds)
        => durationSeconds > 0 ? Math.Clamp(positionSeconds / durationSeconds, 0.0, 1.0) : 0.0;

    /// <summary>Clamped tap-point → seek ratio along the progress bar; zero when the bar is empty.</summary>
    public static double SeekRatio(double tapX, double barLeft, double barWidth)
        => barWidth > 0 ? Math.Clamp((tapX - barLeft) / barWidth, 0.0, 1.0) : 0.0;

    /// <summary>
    /// The seek tap's enablement rule: the session reports seekable AND
    /// carries a positive duration (a zero-duration track would seek to a
    /// single point). One spelling for the touch handler's gate.
    /// </summary>
    public static bool CanSeekNow(MediaSnapshot snap)
        => snap.CanSeek && snap.Duration.TotalSeconds > 0;

    /// <summary>"1.5×" when the playback rate deviates from 1.0, else null (nothing to show).</summary>
    public static string? PlaybackRateText(double rate)
        => Math.Abs(rate - 1.0) > 0.001 ? $"{DisplayFormat.Value(rate, "0.0")}×" : null;

    /// <summary>
    /// The repeat-mode cycle the tap policy walks: None → List → Track →
    /// None. Any mode outside the named two (including the seam's
    /// <c>Unknown</c>) degrades to None, so an OS value the projection does
    /// not name lands on the cycle's start. One spelling, owned here: the
    /// monitor's tap policy applies it to its own live repeat state.
    /// Assertable here without a widget or an SMTC session.
    /// </summary>
    public static MediaRepeatMode NextRepeatMode(MediaRepeatMode current)
        => current switch
        {
            MediaRepeatMode.None => MediaRepeatMode.List,
            MediaRepeatMode.List => MediaRepeatMode.Track,
            _ => MediaRepeatMode.None
        };

    /// <summary>
    /// The live progress position in seconds: the snapshot's position, advanced
    /// by the time elapsed since the snapshot while playback is active (the
    /// render clock keeps the bar moving between SMTC refreshes). A paused or
    /// stopped snapshot reads its raw position.
    /// </summary>
    public static double ExtrapolatedPosition(MediaSnapshot snap, DateTimeOffset now)
    {
        double posSec = snap.Position.TotalSeconds;
        if (snap.IsPlaying)
            posSec += (now - snap.LastUpdated).TotalSeconds;
        return posSec;
    }
}
