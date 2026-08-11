using System.IO;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the persisted profile file: the LocalAppData path, sanitized load via
/// the existing import pipeline, atomic tmp+replace save, and the debounced
/// MarkDirty/Flush policy. The 30 FPS render loop never touches this module —
/// only user mutations arm the debounce.
/// </summary>
public sealed class ProfilePersistence : IDisposable
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

    public string ProfilePath => _profilePath;

    /// <summary>
    /// Reads and sanitizes the persisted profile through the existing import
    /// pipeline (caps + rehydration). Returns null when absent, oversized,
    /// corrupt, or unparseable — the caller falls back to the starter profile.
    /// </summary>
    public ProfileLayout? Load(WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        try
        {
            if (!File.Exists(_profilePath)) return null;
            if (ProfileOps.IsImportFileTooLarge(new FileInfo(_profilePath).Length))
            {
                _log?.Invoke($"Profile file too large ({new FileInfo(_profilePath).Length} bytes); ignoring");
                return null;
            }
            return ProfileOps.ImportJson(File.ReadAllText(_profilePath), loader, context);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Profile load failed, falling back to starter: {ex.Message}");
            return null;
        }
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
            await Task.Delay(_debounceDelay, _timeProvider, token);
            if (version != Volatile.Read(ref _dirtyVersion)) return;
            Save();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
