using System.IO;
using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Platform seam for <see cref="PresentMonApiProbe"/>: finds and loads the
/// native library and resolves exports. Tests substitute a fake to drive the
/// unavailable-reason branches without the real PresentMonAPI2.dll.
/// </summary>
internal interface IPresentMonLibraryLoader
{
    /// <summary>
    /// Loads the first existing candidate path. Returns the module handle, or
    /// null with <paramref name="failureReason"/> set when a candidate exists
    /// but cannot be loaded, or null with a null reason when no candidate
    /// exists at all.
    /// </summary>
    IntPtr? LoadLibrary(string[] candidatePaths, out string? failureReason);

    /// <summary>Resolves a named export; null when the export is missing.</summary>
    IntPtr? GetExport(IntPtr library, string name);
}

/// <summary>Default loader over <see cref="NativeLibrary"/>.</summary>
internal sealed class NativePresentMonLibraryLoader : IPresentMonLibraryLoader
{
    public static readonly NativePresentMonLibraryLoader Instance = new();

    private NativePresentMonLibraryLoader()
    {
    }

    public IntPtr? LoadLibrary(string[] candidatePaths, out string? failureReason)
    {
        failureReason = null;
        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                return NativeLibrary.Load(path);
            }
            catch (Exception)
            {
                failureReason = $"PresentMonAPI2.dll at '{path}' could not be loaded.";
                return null;
            }
        }
        return null;
    }

    public IntPtr? GetExport(IntPtr library, string name)
    {
        try
        {
            return NativeLibrary.GetExport(library, name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
