namespace ModernWigiDash.Core.Models;

/// <summary>
/// The untrusted-import sanitizer: the caps and value rules every foreign
/// profile passes before rehydration — the page/widget count caps, the
/// null/empty collection repair, the page-background and widget image/icon
/// paths restricted to safe relative paths, the ActionCommand values
/// (the Hotkey widget's Process.Start / SendInput) cleared, the ChannelName
/// (the IRC JOIN target) sanitized, and the InstanceId (the widgets' cache
/// file names) regenerated or deduplicated. The import file-size guard lives
/// here too, so the pre-parse rejection boundary sits with the rules it
/// protects, enforced at exactly one site: <see
/// cref="ProfileOps.ImportProfileFile"/>, the one file-import boundary the
/// window and the boot load both route through. The app's own persisted
/// profile passes the boundary as trusted input.
/// </summary>
public static class ProfileImportSanitizer
{
    /// <summary>Max widgets a page may carry after an import (startup DoS cap).</summary>
    private const int MaxWidgetsPerPage = 200;

    /// <summary>Max pages after an import (total-size DoS cap).</summary>
    private const int MaxPagesPerProfile = 50;

    /// <summary>Max widgets across the whole imported profile.</summary>
    private const int MaxTotalWidgets = 1000;

    /// <summary>Cap for string widget-property values at the import boundary
    /// (query keys, URLs, and labels all derive from them).</summary>
    private const int MaxPropertyStringLength = 256;

    /// <summary>Max bytes an import file may carry — the file-read counterpart
    /// of the sanitizer caps: far beyond any real exported profile, anything
    /// larger is untrusted junk and must be rejected before any parsing.</summary>
    internal const long MaxImportFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Widget property names that carry filesystem paths. Must match the
    /// widgets' <see cref="WidgetPropertyAttribute"/> declarations exactly —
    /// ProfileSanitizerDriftTests reflects over the Widgets assembly and asserts
    /// this set equals every <see cref="WidgetPropertyType.Path"/> property, so
    /// a renamed path property fails the build instead of silently disarming
    /// the import guard.
    /// </summary>
    internal static readonly string[] PathPropertyKeys =
        ["ActionCommand", "IconFile", "ImagePath"];

    /// <summary>True when an import file exceeds <see cref="MaxImportFileBytes"/> —
    /// the pre-read reject rule the single file-import boundary
    /// (<see cref="ProfileOps.ImportProfileFile"/>) applies, so the guard's
    /// boundary lives in one place with the cap it enforces.</summary>
    public static bool IsImportFileTooLarge(long fileLength) => fileLength > MaxImportFileBytes;

    /// <summary>InstanceId safety rule: a short ASCII token (letters, digits,
    /// '-', '_') that can never escape a directory or resolve outside it.
    /// GUIDs and the app's own generated ids always pass; only foreign
    /// profiles can fail.</summary>
    public static bool IsSafeInstanceId(string? id)
        => id is { Length: > 0 and <= 64 }
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Applies the untrusted-import rules: page widget caps, background path
    /// sanitization, and clearing action-command values (the Hotkey widget's
    /// Launch/URL/keystroke execution must be re-entered by the user after
    /// importing a foreign profile).
    /// </summary>
    internal static void SanitizeImportedProfile(ProfileLayout profile)
    {
        // Untrusted JSON may carry null collections ("pages": null etc.) —
        // repair them before any counting, or the sanitizer NREs on exactly
        // the input shape it exists for. Null PAGE ELEMENTS ("pages":[null])
        // are dropped the same way.
        profile.Pages ??= [];
        profile.Pages = profile.Pages.Where(p => p is not null).ToList();

        // A profile with zero pages cannot exist at runtime (the ctor creates
        // one, DeletePage refuses the last) — an imported JSON with an empty
        // pages array must be repaired here, or ActivePage hands out an orphan
        // page that is not part of the profile.
        if (profile.Pages.Count == 0)
        {
            profile.Pages.Add(new PageLayout());
        }

        // Null-element filtering may have shrunk the page list — re-clamp the
        // deserialized active index (its setter clamped against the ORIGINAL
        // count, so it can still point past the repaired list).
        profile.ActivePageIndex = Math.Min(profile.ActivePageIndex, profile.Pages.Count - 1);

        // The close behavior travels with the JSON, so a foreign profile can
        // dictate it. The untrusted-input rule: an ABSENT value (null) stays
        // absent — "this profile has no opinion", and the import merge keeps
        // the local value — while a PRESENT value must be a known spelling.
        // Anything else (hand-edited junk, a future value this build doesn't
        // know) normalizes to the safe default, so a foreign profile can only
        // override the local behavior by naming a behavior this build
        // recognizes.
        if (profile.CloseBehavior is not null && !CloseBehaviorPolicy.IsKnown(profile.CloseBehavior))
        {
            profile.CloseBehavior = CloseBehaviorPolicy.Quit;
        }

        if (!string.IsNullOrWhiteSpace(profile.ActivePage?.BackgroundImagePath))
        {
            profile.ActivePage.BackgroundImagePath = SafeRelativePath(profile.ActivePage.BackgroundImagePath);
        }

        if (profile.Pages.Count > MaxPagesPerProfile)
        {
            profile.Pages = profile.Pages.Take(MaxPagesPerProfile).ToList();
            // Truncation leaves the deserialized active index past the new
            // last page — re-clamp it, or swipe navigation targets a missing
            // page (InputController refuses out-of-range switches).
            profile.ActivePageIndex = Math.Min(profile.ActivePageIndex, profile.Pages.Count - 1);
        }

        // Enforce per-page and total widget caps in one pass: later pages are
        // emptied once the total budget is exhausted. The seen-InstanceId set
        // spans the whole import — a foreign profile must not be able to give
        // two widgets the SAME safe id (their cache files would collide).
        int total = 0;
        var seenInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in profile.Pages)
        {
            page.Widgets ??= [];
            // Null WIDGET elements ("widgets":[null]) must not reach the
            // per-widget sanitizer or rehydration.
            page.Widgets = page.Widgets.Where(w => w is not null).ToList();

            if (!string.IsNullOrWhiteSpace(page.BackgroundImagePath))
            {
                page.BackgroundImagePath = SafeRelativePath(page.BackgroundImagePath);
            }

            int remaining = MaxTotalWidgets - total;
            if (remaining <= 0)
            {
                page.Widgets.Clear();
                continue;
            }

            int allowed = Math.Min(page.Widgets.Count, Math.Min(MaxWidgetsPerPage, remaining));
            for (int i = 0; i < allowed; i++)
            {
                SanitizeWidgetValues(page.Widgets[i], seenInstanceIds);
            }
            if (page.Widgets.Count > allowed)
            {
                page.Widgets = page.Widgets.Take(allowed).ToList();
            }
            total += allowed;
        }
    }

    private static void SanitizeWidgetValues(PlacedWidgetInstance placed, HashSet<string> seenInstanceIds)
    {
        // Untrusted JSON may carry "propertyValues": null — repair before use.
        placed.PropertyValues ??= [];

        // ActionCommand drives Process.Start / SendInput on the Hotkey widget
        // (identified by its property name, widget-agnostically): a foreign
        // profile must not silently arm command execution. Cleared whenever
        // present — ActionType is NOT required (it defaults to "Launch App",
        // so omitting it in the profile is a valid bypass). ActionCommand is
        // also Path-typed and listed in PathPropertyKeys, but this unconditional
        // clear must run FIRST: the path rules alone would preserve a safe
        // relative command. PropertyValues hold JsonElement after
        // deserialization — normalize before inspecting. Whitespace-only
        // values ("   ") are cleared too: they cannot arm anything, but the
        // "imports never carry a command" invariant stays airtight.
        if (placed.PropertyValues.TryGetValue("ActionCommand", out var raw) &&
            ProfileOps.ConvertPropertyValue(raw, typeof(string)) is string command &&
            !string.IsNullOrEmpty(command))
        {
            placed.PropertyValues["ActionCommand"] = "";
        }

        // Image/icon paths: rooted, UNC, or ..\ paths would read arbitrary
        // local files; restrict imports to safe relative paths.
        foreach (var key in PathPropertyKeys)
        {
            if (placed.PropertyValues.TryGetValue(key, out var value) &&
                ProfileOps.ConvertPropertyValue(value, typeof(string)) is string path)
            {
                placed.PropertyValues[key] = SafeRelativePath(path);
            }
        }

        // TwitchChatStreamWidget's channel name rides the IRC JOIN command:
        // an embedded CR/LF would inject extra IRC lines on connect. Apply
        // the shared rule (Sdk's TwitchChannelRule — Core cannot reference
        // the Widgets assembly, so the rule lives in the lowest common layer;
        // the widget's NormalizeChannel is defense-in-depth at connect time).
        // Invalid names are cleared to empty — the widget's empty-channel
        // fallback then applies.
        if (placed.PropertyValues.TryGetValue("ChannelName", out var channelRaw) &&
            ProfileOps.ConvertPropertyValue(channelRaw, typeof(string)) is string channel)
        {
            placed.PropertyValues["ChannelName"] = TwitchChannelRule.Sanitize(channel, "");
        }

        // InstanceId is placement identity, not user data — but a foreign
        // profile can dictate it, and widgets key cache FILE NAMES by it (the
        // weather widget: "weather_{InstanceId}.json" under the app dir). An
        // id with .. segments would escape that directory on the next fetch,
        // and a DUPLICATE safe id would collide two widgets' cache files.
        // Regenerate unless the id is a safe token that no other widget in
        // this import already claimed.
        if (!IsSafeInstanceId(placed.InstanceId) || !seenInstanceIds.Add(placed.InstanceId))
        {
            placed.InstanceId = Guid.NewGuid().ToString();
            seenInstanceIds.Add(placed.InstanceId);
        }

        // Untrusted string property values feed query keys, URLs, and the
        // display — a 10 MB import could carry a 10 MB Location. Cap every
        // string value at a generous bound so the import can never create a
        // multi-hundred-MB allocation spike at geocode/parse time.
        foreach (var key in placed.PropertyValues.Keys.ToArray())
        {
            if (ProfileOps.ConvertPropertyValue(placed.PropertyValues[key], typeof(string)) is string text && text.Length > MaxPropertyStringLength)
            {
                placed.PropertyValues[key] = text[..MaxPropertyStringLength];
            }
        }
    }

    private static string SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return "";
        // Drive-relative ("C:foo") is not rooted but still resolves against a
        // drive — reject it so only genuinely relative paths survive.
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return "";
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "";
        if (path.Split(['\\', '/'], StringSplitOptions.None).Any(segment => string.Equals(segment, "..", StringComparison.Ordinal))) return "";
        return path;
    }
}
