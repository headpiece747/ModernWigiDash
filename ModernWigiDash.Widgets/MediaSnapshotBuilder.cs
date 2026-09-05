namespace ModernWigiDash.Widgets;

/// <summary>
/// The media-session snapshot builder (the MediaSessionMonitor's snapshot
/// composition rule): given the session's source app id, media properties,
/// playback info, and timeline properties, composes the neutral
/// <see cref="MediaSnapshot"/> record. Pure and testable without a widget
/// instance or WinRT surface (the monitor's RefreshAsync calls this with the
/// data it reads from the seam).
/// </summary>
internal static class MediaSnapshotBuilder
{
    /// <summary>
    /// Composes the snapshot from the session's data. The metadata group is
    /// sanitized and defaulted in one place; the transport-control capability
    /// group is defaulted to disabled when the session reports no controls.
    /// </summary>
    public static MediaSnapshot Build(
        string? sourceAppId,
        MediaPropertiesData? props,
        PlaybackInfoData? info,
        TimelinePropertiesData? timeline,
        TimeProvider clock)
    {
        var (title, artist, album, albumArtist, trackNumber, albumTrackCount, genres) = ExtractMeta(props);
        var (canPlay, canPause, canStop, canNext, canPrev, canSeek, canShuffle, canRepeat) = ExtractControls(info);

        return new MediaSnapshot
        {
            SourceAppId = sourceAppId ?? "",
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            TrackNumber = trackNumber,
            AlbumTrackCount = albumTrackCount,
            Genres = genres,
            Status = info?.PlaybackStatus ?? MediaPlaybackStatus.Closed,
            Position = timeline?.Position ?? TimeSpan.Zero,
            Duration = timeline?.EndTime ?? TimeSpan.Zero,
            LastUpdated = timeline?.LastUpdatedTime ?? clock.GetUtcNow(),
            Shuffle = info?.IsShuffleActive ?? false,
            Repeat = info?.AutoRepeatMode ?? MediaRepeatMode.None,
            PlaybackRate = info?.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1.0,
            CanPlay = canPlay,
            CanPause = canPause,
            CanStop = canStop,
            CanNext = canNext,
            CanPrev = canPrev,
            CanSeek = canSeek,
            CanShuffle = canShuffle,
            CanRepeat = canRepeat
        };
    }

    /// <summary>The metadata group of the snapshot, sanitized and defaulted in
    /// one place.</summary>
    internal static (string Title, string Artist, string Album, string AlbumArtist, int TrackNumber, int AlbumTrackCount, string[] Genres) ExtractMeta(MediaPropertiesData? props)
        => (
            Sanitize(props?.Title, ""),
            Sanitize(props?.Artist, ""),
            Sanitize(props?.AlbumTitle, ""),
            Sanitize(props?.AlbumArtist, ""),
            props?.TrackNumber ?? 0,
            props?.AlbumTrackCount ?? 0,
            props?.Genres?.ToArray() ?? []);

    /// <summary>The transport-control capability group, defaulted to disabled
    /// when the session reports no controls.</summary>
    internal static (bool Play, bool Pause, bool Stop, bool Next, bool Prev, bool Seek, bool Shuffle, bool Repeat) ExtractControls(PlaybackInfoData? info)
        => (
            info?.Controls.IsPlayEnabled ?? false,
            info?.Controls.IsPauseEnabled ?? false,
            info?.Controls.IsStopEnabled ?? false,
            info?.Controls.IsNextEnabled ?? false,
            info?.Controls.IsPreviousEnabled ?? false,
            info?.Controls.IsPlaybackPositionEnabled ?? false,
            info?.Controls.IsShuffleEnabled ?? false,
            info?.Controls.IsRepeatEnabled ?? false);

    /// <summary>Strips control characters (space kept) and caps at 256 chars;
    /// falls back when nothing survives.</summary>
    internal static string Sanitize(string? input, string fallback)
    {
        if (string.IsNullOrEmpty(input)) return fallback;
        var clean = new string([.. input.Where(c => !char.IsControl(c) || c == ' ').Take(256)]);
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }
}
