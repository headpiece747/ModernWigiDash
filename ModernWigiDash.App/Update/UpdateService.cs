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
public sealed class UpdateService
{
    public const string GitHubLatestUrl = "https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest";
    private const string RepoUserAgent = "ModernWigiDash-Updater/1.0";

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { UserAgent = { new("ModernWigiDash-Updater", "1.0") } }
    };

    private readonly Func<string, string?, Task<string?>> _downloadText;
    private readonly Func<string, string, IProgress<double>, CancellationToken, Task> _downloadFile;
    private readonly Func<string, bool> _sha256Matches;
    private readonly string _updatesRoot;

    public UpdateService(
        Func<string, string?, Task<string?>>? downloadText = null,
        Func<string, string, IProgress<double>, CancellationToken, Task>? downloadFile = null,
        Func<string, bool>? sha256Matches = null,
        string? updatesRoot = null)
    {
        _updatesRoot = updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "updates");
        _downloadText = downloadText ?? DownloadTextAsync;
        _downloadFile = downloadFile ?? DownloadFileAsync;
        _sha256Matches = sha256Matches ?? (actual => true); // digest verified by DownloadAndStage
    }

    public string StagedCmdPath(UpdateInfo info) => Path.Combine(_updatesRoot, "staged", info.Version, "apply-update.cmd");

    /// <summary>One startup check: newer slim release → UpdateInfo, else null (silent).</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (AppVersion.IsDevBuild) return null;
        string? json = await _downloadText(GitHubLatestUrl, RepoUserAgent).ConfigureAwait(false);
        return json is null ? null : UpdateChecker.ParseLatestRelease(json, AppVersion.Current);
    }

    /// <summary>Downloads the slim zip, verifies SHA-256, extracts to staged/{version}, writes the cmd.</summary>
    public async Task<bool> DownloadAndStageAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        string downloadDir = Path.Combine(_updatesRoot, "downloads");
        string stagedDir = Path.Combine(_updatesRoot, "staged", info.Version);
        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(stagedDir);
        string zipPath = Path.Combine(downloadDir, $"{info.Version}.zip");

        try
        {
            await _downloadFile(info.ZipUrl, zipPath, progress, ct).ConfigureAwait(false);
            if (!File.Exists(zipPath)) return false;

            string actual = ComputeSha256(zipPath);
            if (!_sha256Matches(actual) || !string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(downloadDir);
                return false;
            }

            ExtractSlimZip(zipPath, stagedDir);
            WriteUpdaterCmd(StagedCmdPath(info), info.Version);
            return true;
        }
        catch
        {
            TryDeleteDirectory(downloadDir);
            return false;
        }
    }

    /// <summary>Deletes any existing staged/download folders — a stage present at
    /// startup means the last update was never applied (stale).</summary>
    public void CleanupStale()
    {
        foreach (string sub in new[] { "staged", "downloads" })
        {
            string dir = Path.Combine(_updatesRoot, sub);
            if (Directory.Exists(dir)) TryDeleteDirectory(dir);
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

    /// <summary>Extracts the slim zip (root folder included) and returns the exe path.</summary>
    internal static string ExtractSlimZip(string zipPath, string targetDir)
    {
        ZipFile.ExtractToDirectory(zipPath, targetDir);
        string exe = Path.Combine(targetDir, "ModernWigiDash-win-x64", "ModernWigiDash.App.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("Slim zip has no ModernWigiDash.App.exe");
        return exe;
    }

    private void WriteUpdaterCmd(string path, string version)
    {
        // The full batch body lives in the embedded apply-update.cmd resource;
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
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress.Report((double)read / total);
        }
    }

    private static void TryDeleteDirectory(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
