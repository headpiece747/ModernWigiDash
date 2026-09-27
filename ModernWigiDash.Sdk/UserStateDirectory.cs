using System.IO;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The ONE resolver of the per-user state directory
/// (<c>%LocalAppData%\ModernWigiDash</c>), shared by every file that lives
/// there: the display log, the crash log, <c>profile.json</c>,
/// <c>app_settings.json</c> and <c>app_theme.json</c>.
///
/// It exists because those five paths used to resolve the folder independently
/// — four copies of the same
/// <c>Path.Combine(Environment.GetFolderPath(LocalApplicationData), "ModernWigiDash", ...)</c>
/// — and a test host that redirected one of them still wrote the other four
/// into the developer's real profile (2026-09-27: the display log was pinned
/// away, and the test suite went on writing a real <c>profile.json</c> on every
/// gate run, which is exactly the non-default state the user asked never to
/// verify against).
///
/// One seam, set once by the test host, redirects the whole surface. It lives
/// in the leaf project because the theme path's owner is in Core, which cannot
/// reference App, and a seam only one layer can see is a seam half the callers
/// miss.
///
/// Production never sets it: the default IS the production path.
/// </summary>
public static class UserStateDirectory
{
    /// <summary>
    /// The state directory's name, shared by every owner so the folder can
    /// never split between them (ADR-0021 moved the theme here; the profile
    /// and settings were already here).
    /// </summary>
    public const string Name = "ModernWigiDash";

    /// <summary>
    /// Resolves the state directory. The default is the production folder; the
    /// test host replaces it with a temp folder BEFORE any file path is built,
    /// so nothing in a test run can reach the real profile. Read at the moment
    /// a path is composed, not cached: a test that rebinds it mid-run must take
    /// effect for the next path it builds.
    /// </summary>
    public static Func<string> Resolve { get; set; } = ProductionDefault;

    /// <summary>
    /// The production folder, as a function so <see cref="Resolve"/> and
    /// <see cref="Reset"/> cannot spell it twice. A test that asserts on the
    /// PRODUCTION default calls <see cref="Reset"/> first: the seam is
    /// process-global, so a window test that bound it to a temp folder would
    /// otherwise decide what those assertions see, and the answer would depend
    /// on test order.
    /// </summary>
    public static string ProductionDefault() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Name);

    /// <summary>
    /// Restores the production folder. Call this before asserting that a
    /// default path really is the production one, so the assertion cannot be
    /// decided by whichever test class bound the seam earlier.
    /// </summary>
    public static void Reset() => Resolve = ProductionDefault;
}
