using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ModernWigiDash.App.Update;

/// <summary>
/// Runtime update flow: check GitHub once at startup, download + verify the
/// slim zip, extract into a writable stage under %LOCALAPPDATA%, and write the
/// apply-update.cmd the swap runs on restart. All file paths live under
/// <see cref="UpdatesRoot"/> so a non-writable install dir never blocks the
/// download. I/O seams are injectable for tests.
/// </summary>
internal sealed class UpdateService
{
    public const string GitHubLatestUrl = "https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest";
    private const string RepoUserAgent = "ModernWigiDash-Updater/1.0";
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromMinutes(15);

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { UserAgent = { new("ModernWigiDash-Updater", "1.0") } }
    };

    private readonly Func<string, string?, Task<string?>> _downloadText;
    private readonly Func<string, string, IProgress<double>, CancellationToken, Task> _downloadFile;
    private readonly Func<string, string, bool> _sha256Matches;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly string _updatesRoot;
    private readonly Version? _currentVersion;

    public UpdateService(
        Func<string, string?, Task<string?>>? downloadText = null,
        Func<string, string, IProgress<double>, CancellationToken, Task>? downloadFile = null,
        Func<string, string, bool>? sha256Matches = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        string? updatesRoot = null,
        Version? currentVersion = null)
    {
        _updatesRoot = updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "updates");
        _downloadText = downloadText ?? DownloadTextAsync;
        _downloadFile = downloadFile ?? DownloadFileAsync;
        // The single digest-check expression: the seam IS the verification
        // (tests veto or accept via the same predicate the default implements).
        _sha256Matches = sha256Matches ?? DigestMatches;
        _startProcess = startProcess ?? StartProcess;
        _currentVersion = currentVersion;
    }

    private static bool DigestMatches(string actual, string expected)
        => actual.Length == expected.Length
           && CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.ASCII.GetBytes(actual),
               System.Text.Encoding.ASCII.GetBytes(expected));

    private static Process? StartProcess(ProcessStartInfo psi) => Process.Start(psi);

    public string StagedCmdPath(UpdateInfo info) => Path.Combine(_updatesRoot, "staged", info.Version, "apply-update.cmd");

    /// <summary>The LocalAppData updates root every staged/download path lives under.</summary>
    public string UpdatesRoot => _updatesRoot;

    /// <summary>One startup check: newer slim release → UpdateInfo, else null (silent).</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // _currentVersion is a test seam; production reads the assembly stamp
        // (null for 0.0.0 dev builds — the updater never runs against dev).
        Version? current = _currentVersion ?? AppVersion.Current;
        if (current is null) return null;
        string? json = await _downloadText(GitHubLatestUrl, RepoUserAgent).ConfigureAwait(false);
        return json is null ? null : UpdateChecker.ParseLatestRelease(json, current);
    }

    /// <summary>Downloads the slim zip, verifies SHA-256, extracts to staged/{version}, writes the cmd.
    /// The download is bounded by a 15-minute stall timeout: with ResponseHeadersRead
    /// the 10s <see cref="HttpClient.Timeout"/> expires at header arrival, so a
    /// mid-body stall must be cut off separately (the caller's token wins early).</summary>
    public async Task<bool> DownloadAndStageAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        string downloadDir = Path.Combine(_updatesRoot, "downloads");
        string stagedDir = Path.Combine(_updatesRoot, "staged", info.Version);
        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(stagedDir);
        string zipPath = Path.Combine(downloadDir, $"{info.Version}.zip");

        try
        {
            using var stallBound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallBound.CancelAfter(DownloadStallTimeout);
            await _downloadFile(info.ZipUrl, zipPath, progress, stallBound.Token).ConfigureAwait(false);
            if (!File.Exists(zipPath)) return false;

            string actual = ComputeSha256(zipPath);
            if (!_sha256Matches(actual, info.Sha256))
            {
                TryDeleteDirectory(downloadDir);
                FileLog.Write($"[UPDATE] download digest mismatch for v{info.Version}; download deleted");
                return false;
            }

            ExtractSlimZip(zipPath, stagedDir);
            WriteUpdaterCmd(StagedCmdPath(info), info.Version);
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(downloadDir);
            FileLog.Write($"[UPDATE] download/stage failed for v{info.Version}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes any existing staged/download folders — a stage present at
    /// startup means the last update was never applied (stale) — plus a leftover
    /// live-updater cmd from a previous restart.</summary>
    public void CleanupStale()
    {
        foreach (string sub in new[] { "staged", "downloads" })
        {
            string dir = Path.Combine(_updatesRoot, sub);
            if (Directory.Exists(dir)) TryDeleteDirectory(dir);
        }
        TryDeleteFile(Path.Combine(_updatesRoot, "apply-update-live.cmd"));
    }

    /// <summary>One startup recovery call: heal an interrupted swap and clear
    /// stale stages/downloads.</summary>
    public void RecoverAtStartup(string installDir)
    {
        CleanupStale();
        RecoverInterruptedSwap(installDir);
    }

    /// <summary>
    /// Launches the staged updater: reads the staged apply-update.cmd,
    /// substitutes the {{RELAUNCH}} marker, writes the result OUTSIDE the stage
    /// (the cmd deletes its own stage — a batch that deletes itself while
    /// running loses the rest of its script), and spawns cmd.exe hidden,
    /// detached from this process's job object (without ShellExecute the child
    /// is reaped when the app closes, and the swap never runs). Returns false
    /// (with a log line) when any step fails — the caller keeps the window
    /// open and hides the button. The whole launch protocol lives here; the
    /// window only confirms and closes.
    /// </summary>
    public bool LaunchUpdater(UpdateInfo info, string installDir)
    {
        try
        {
            string stageDir = Path.Combine(_updatesRoot, "staged", info.Version);
            string stagedCmd = StagedCmdPath(info);
            // AppContext.BaseDirectory ends with a separator — Path.Combine
            // (not string concat) so the relaunch path never doubles the
            // backslash (start "" "<path>\\exe" fails with "cannot find the
            // path specified" and silently skips the relaunch).
            string relaunch = $"start \"\" \"{Path.Combine(installDir, "ModernWigiDash.App.exe")}\"";

            string body = File.ReadAllText(stagedCmd);
            string substituted = body.Replace("{{RELAUNCH}}", relaunch);
            if (substituted.Length == body.Length)
                FileLog.Write("[UPDATE] relaunch marker missing in staged cmd; the updater will not relaunch the app");
            string liveCmd = Path.Combine(_updatesRoot, "apply-update-live.cmd");
            File.WriteAllText(liveCmd, substituted);

            // UseShellExecute=true detaches the updater from this process's job
            // object. With ShellExecute, cmd receives the arguments as one
            // string, so the doubled-quote form fails silently and the whole
            // command must be a single quoted argument to slash-c.
            string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string inner = $"\"{liveCmd}\" \"{installDir}\" \"{stageDir}\" ModernWigiDash.App.exe";
            var psi = new ProcessStartInfo(cmdExe, $"/c \"{inner}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            _startProcess(psi);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] launch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Repairs an interrupted swap: restores exe.old when the new exe is
    /// missing, otherwise clears the .old.</summary>
    public static void RecoverInterruptedSwap(string installDir)
    {
        string exe = Path.Combine(installDir, "ModernWigiDash.App.exe");
        string old = exe + ".old";
        if (!File.Exists(old)) return;
        if (!File.Exists(exe)) File.Move(old, exe);
        else File.Delete(old);
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Extracts the slim zip (root folder included) and returns the exe path.
    /// Every entry must stay under <paramref name="targetDir"/> — a crafted zip
    /// with ..-escaping entries writes outside the stage (the digest gate makes
    /// this defense-in-depth, not the primary control).</summary>
    internal static string ExtractSlimZip(string zipPath, string targetDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        foreach (string fullName in archive.Entries.Select(entry => entry.FullName))
        {
            string dest = Path.GetFullPath(Path.Combine(targetDir, fullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry escapes the stage directory: {fullName}");
        }
        ZipFile.ExtractToDirectory(zipPath, targetDir);
        string exe = Path.Combine(targetDir, "ModernWigiDash-win-x64", "ModernWigiDash.App.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("Slim zip has no ModernWigiDash.App.exe");
        return exe;
    }

    private void WriteUpdaterCmd(string path, string version)
    {
        // The full batch body lives in the embedded apply-update.cmd resource —
        // this method copies it into the stage with the version substituted.
        var assembly = typeof(UpdateService).Assembly;
        using var stream = assembly.GetManifestResourceStream("ModernWigiDash.App.Update.apply-update.cmd")
            ?? throw new InvalidOperationException("apply-update.cmd resource missing");
        using var reader = new StreamReader(stream);
        string body = reader.ReadToEnd().Replace("{{VERSION}}", version);
        File.WriteAllText(path, body);
    }

    private static async Task<string?> DownloadTextAsync(string url, string? userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent)) req.Headers.UserAgent.ParseAdd(userAgent);
        using var resp = await SharedHttp.SendAsync(req).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
    }

    /// <summary>Upper bound for a downloaded update package — the slim zip is
    /// ~10–40 MB; a larger stream is a poisoned/misbehaving release, and a
    /// bounded-time-but-unbounded-size download could fill the disk.</summary>
    internal const long MaxUpdateBytes = 500 * 1024 * 1024;

    /// <summary>The download size-cap gate (extracted so the security control
    /// is directly testable — the streaming loop calls it before every write).</summary>
    internal static void EnforceDownloadCap(long bytesRead)
    {
        if (bytesRead > MaxUpdateBytes)
            throw new InvalidDataException($"Update package exceeds the {MaxUpdateBytes / (1024 * 1024)} MB size cap");
    }

    private static async Task DownloadFileAsync(string url, string dest, IProgress<double> progress, CancellationToken ct)
    {
        using var resp = await SharedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            read += n;
            EnforceDownloadCap(read);
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            if (total > 0) progress.Report((double)read / total);
        }
    }

    private static void TryDeleteDirectory(string dir) { try { Directory.Delete(dir, true); } catch { /* best-effort: a locked file must not fail the caller */ } }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ } }
}
