using System.IO;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the persisted profile file: the LocalAppData path, trusted load via
/// the existing import pipeline, atomic tmp+replace save, and the debounced
/// MarkDirty/Flush policy. The 30 FPS render loop never touches this module —
/// only user mutations arm the debounce.
/// </summary>
internal sealed class ProfilePersistence : IDisposable
{
    public const string DirectoryName = "ModernWigiDash";
    public const string FileName = "profile.json";
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly string _profilePath;
    private readonly Func<ProfileLayout> _profileProvider;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;
    private CancellationTokenSource? _debounceCts;
    private int _dirtyVersion;
    private bool _disposed;

    /// <param name="profileProvider">Reads the current profile at save time —
    /// the window's profile reference can be swapped on import, so the module
    /// must never hold a stale reference.</param>
    public ProfilePersistence(
        string profilePath,
        Func<ProfileLayout> profileProvider,
        TimeSpan? debounceDelay = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _profilePath = profilePath;
        _profileProvider = profileProvider;
        _debounceDelay = debounceDelay ?? DebounceDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public static string DefaultProfilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DirectoryName, FileName);

    /// <summary>
    /// Reads the persisted profile through the one import boundary
    /// (<see cref="ProfileOps.ImportProfileFile"/>). Returns null when absent,
    /// oversized, corrupt, or unparseable — the caller falls back to the
    /// starter profile. The app's own file loads as TRUSTED input: the
    /// untrusted-import rules would wipe the user's configured ActionCommand,
    /// absolute ImagePath, and BackgroundImagePath on every restart.
    /// </summary>
    public ProfileLayout? Load(WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        ProfileImportOutcome outcome = ProfileOps.ImportProfileFile(_profilePath, loader, context, trusted: true);
        if (outcome is ProfileImportOutcome.Loaded(var profile))
        {
            return profile;
        }
        if (outcome is ProfileImportOutcome.TooLarge(var bytes))
        {
            _log?.Invoke($"Profile file too large ({bytes} bytes); ignoring");
            return null;
        }
        if (outcome is ProfileImportOutcome.Failed(var detail))
        {
            _log?.Invoke($"Profile load failed, falling back to starter: {detail}");
            return null;
        }
        // Absent: the first-boot case, nothing to fall back from.
        return null;
    }

    /// <summary>
    /// Synchronous save: export → temp file → atomic replace, so a crash
    /// never leaves a torn profile.json. Failures are logged, never thrown.
    /// </summary>
    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(_profilePath)!;
            Directory.CreateDirectory(dir);
            string json = ProfileOps.ExportJson(_profileProvider());
            string tmp = _profilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _profilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Profile save failed: {ex.Message}");
            // The write or the move may have failed AFTER creating the temp
            // file (e.g. the move onto a directory path) — remove the litter
            // best-effort so a later save never trips over a stale .tmp.
            try
            {
                File.Delete(_profilePath + ".tmp");
            }
            catch (IOException)
            {
                // Best-effort cleanup; the save failure is already logged.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the save failure is already logged.
            }
        }
    }

    /// <summary>
    /// Arms the debounce: the save fires DebounceDelay after the LAST call —
    /// repeated mutations within the window coalesce into one write.
    /// </summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var cts = _debounceCts;
        int version = ++_dirtyVersion;
        _ = DebounceSaveAsync(version, cts.Token);
    }

    /// <summary>Saves immediately and cancels any pending debounce.</summary>
    public void Flush()
    {
        _debounceCts?.Cancel();
        Save();
    }

    public void Dispose()
    {
        _disposed = true;
        _debounceCts?.Cancel();
    }

    private async Task DebounceSaveAsync(int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounceDelay, _timeProvider, token).ConfigureAwait(false);
            if (version != Volatile.Read(ref _dirtyVersion)) return;
            Save();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer MarkDirty or a Flush — expected.
        }
    }
}
