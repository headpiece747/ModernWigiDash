namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the Now Playing widget: every string and ratio the
/// render methods lay out — the meta line, the time format, the friendly app
/// name, the progress and seek ratios, the playback-rate text. Assertable
/// without WinRT or a canvas; the widget's draw methods are thin adapters.
/// </summary>
public static class NowPlayingPresentation
{
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
    /// The 11 known media apps mapped to display names; anything else falls
    /// back to the last AUMID segment, truncated to 16 characters.
    /// </summary>
    public static string FriendlyAppName(string appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return "Media";
        }
        string lower = appId.ToLowerInvariant();

        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("msedge")) return "Edge";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("itunes")) return "iTunes";
        if (lower.Contains("apple") || lower.Contains("music")) return "Apple Music";
        if (lower.Contains("mediaplayer") || lower.Contains("wmplayer")) return "Windows Media Player";
        if (lower.Contains("discord")) return "Discord";
        if (lower.Contains("foobar")) return "foobar2000";
        if (lower.Contains("steam")) return "Steam";

        int slash = appId.LastIndexOf('!');
        string name = slash >= 0 ? appId[(slash + 1)..] : appId;
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

    /// <summary>Clamped position/duration ratio; zero when the duration is unknown.</summary>
    public static double ProgressRatio(double positionSeconds, double durationSeconds)
        => durationSeconds > 0 ? Math.Clamp(positionSeconds / durationSeconds, 0.0, 1.0) : 0.0;

    /// <summary>Clamped tap-point → seek ratio along the progress bar; zero when the bar is empty.</summary>
    public static double SeekRatio(double tapX, double barLeft, double barWidth)
        => barWidth > 0 ? Math.Clamp((tapX - barLeft) / barWidth, 0.0, 1.0) : 0.0;

    /// <summary>"1.5×" when the playback rate deviates from 1.0, else null (nothing to show).</summary>
    public static string? PlaybackRateText(double rate)
        => Math.Abs(rate - 1.0) > 0.001 ? $"{rate:0.0}×" : null;
}
