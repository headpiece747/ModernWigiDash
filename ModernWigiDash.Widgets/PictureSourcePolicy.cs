namespace ModernWigiDash.Widgets;

/// <summary>The picture widget's source-mode choice strings (the property vocabulary).</summary>
internal static class PictureSourceMode
{
    public const string Auto = "Auto";
    public const string SingleImage = "Single Image";
    public const string FolderCycle = "Folder (Cycle)";
}

/// <summary>
/// The picture widget's source-resolution policy: how (source mode, path-is-a-file,
/// path-is-a-folder) resolves to a single file or a cycling folder, and whether a
/// tap promises a cycle. The mode interpretation lives in exactly one place — the
/// render placeholder, the touch handler, and the active-file selection all consume
/// the verdict instead of each re-deriving the mode table. An unknown mode string
/// (a hand-edited profile) behaves like Auto, the property default.
/// </summary>
internal static class PictureSourcePolicy
{
    /// <summary>What the source points at: nothing, one file, or a cycling folder.</summary>
    public enum PictureSourceKind { None, File, Folder }

    /// <summary>
    /// Resolves the source: a forced folder mode is a folder only when the folder
    /// exists, a forced single image never reads the folder, and Auto (or an
    /// unknown hand-edited value) prefers the file over the folder.
    /// </summary>
    public static PictureSourceKind Resolve(string? sourceMode, bool fileExists, bool folderExists)
    {
        switch (sourceMode)
        {
            case PictureSourceMode.FolderCycle:
                return folderExists ? PictureSourceKind.Folder : PictureSourceKind.None;
            case PictureSourceMode.SingleImage:
                return fileExists ? PictureSourceKind.File : PictureSourceKind.None;
            default:
                // Auto (or an unknown hand-edited value — the property default's rule).
                if (fileExists)
                {
                    return PictureSourceKind.File;
                }

                return folderExists ? PictureSourceKind.Folder : PictureSourceKind.None;
        }
    }

    /// <summary>
    /// Whether a tap should promise picture cycling: only an actually cycling
    /// folder source (the forced mode, or Auto resolving to an existing folder) —
    /// a single-image widget, or a folder that does not exist, must not promise
    /// a tap-to-cycle behavior it cannot keep.
    /// </summary>
    public static bool CanCycle(string? sourceMode, bool fileExists, bool folderExists)
        => Resolve(sourceMode, fileExists, folderExists) == PictureSourceKind.Folder;

    /// <summary>The placeholder hint: the cycling promise only when <paramref name="canCycle"/> holds.</summary>
    public static string PlaceholderHint(bool canCycle)
        => canCycle ? "Click/Tap to Cycle Pictures" : "Tap to set an Image Path";
}
