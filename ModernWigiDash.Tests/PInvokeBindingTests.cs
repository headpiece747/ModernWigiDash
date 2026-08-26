using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ModernWigiDash.Tests;

/// <summary>
/// The P/Invoke binding pin (ADR-0020): every [DllImport] in src spells its
/// EntryPoint (DebtGuardTests' shape rule), and this class probes each
/// spelled (dll, entry point) pair against the real DLL, so a misspelled
/// export fails at the gate instead of throwing
/// EntryPointNotFoundException on the first real call (the 2026-08-26
/// hotkey crash, which every fake-injecting test missed). The probe is an
/// export-table lookup only (GetModuleHandle/LoadLibrary + GetProcAddress);
/// it never calls the imported function, so the check is safe on every
/// machine.
/// </summary>
[TestClass]
public sealed class PInvokeBindingTests
{
    [TestMethod]
    public void SrcDllImports_EverySpelledEntryPointProbesARealExportInTheNamedDll()
    {
        var root = RepoScan.GetRepoRoot();
        var misses = new List<string>();
        var probed = 0;
        foreach (var project in RepoScan.SrcProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/"))
                    continue; // generated code is not house text

                var code = RepoScan.StripCode(File.ReadAllText(file), stripStrings: false);
                foreach (var dllImport in RepoScan.FindDllImports(code))
                {
                    if (dllImport.EntryPoint is null)
                    {
                        misses.Add($"{rel}:{RepoScan.LineAt(code, dllImport.Index)}: no explicit EntryPoint (DebtGuardTests spells the same rule)");
                        continue;
                    }

                    var dll = ResolveDll(dllImport.DllExpression, code);
                    if (dll is null)
                    {
                        misses.Add($"{rel}:{RepoScan.LineAt(code, dllImport.Index)}: dll {dllImport.DllExpression} does not resolve to a string literal");
                        continue;
                    }

                    if (ResolveExport(dll, dllImport.EntryPoint) == IntPtr.Zero)
                        misses.Add($"{rel}:{RepoScan.LineAt(code, dllImport.Index)}: {dllImport.EntryPoint} not found in {dll}");
                    else
                        probed++;
                }
            }
        }

        Assert.IsTrue(probed > 0,
            "the probe found no [DllImport] pairs in src - the scan is broken, not the bindings");
        Assert.AreEqual(0, misses.Count,
            "P/Invoke binding misses: " + string.Join("; ", misses.OrderBy(m => m))
            + ". Every [DllImport] in src must spell an export that exists in its DLL (ADR-0020): a miss here is the EntryPointNotFoundException the first real call would throw (the 2026-08-26 hotkey crash).");
    }

    [TestMethod]
    public void Probe_KnownExport_ResolvesAndForgedExport_DoesNot()
    {
        // Anti-tautology controls (the DisplayGeometry ConstValue precedent):
        // the probe mechanism must be able to both find and miss.
        Assert.AreNotEqual(IntPtr.Zero, ResolveExport("user32.dll", "RegisterHotKey"),
            "the probe cannot resolve a known export - the GetProcAddress binding itself is broken on this machine");
        Assert.AreEqual(IntPtr.Zero, ResolveExport("user32.dll", "DefinitelyNotAnExport_12345"),
            "the probe resolved a name that is not an export - the probe is tautological");
    }

    [TestMethod]
    public void FindDllImports_InjectedViolations_StayVisibleToTheRule()
    {
        // Negative verification (the house shape): the extractor the
        // DebtGuard rule and this probe both run must keep seeing an
        // injected violation - a scan that loses it lets a silent binding
        // through both pins.
        var snippet = RepoScan.StripCode("""
            using System.Runtime.InteropServices;
            internal static class Injected
            {
                [DllImport("user32.dll")]
                private static extern int NoEntryPointHere();

                [DllImport(SomeConst, EntryPoint = "SomeExport", SetLastError = true)]
                private static extern int SpelledViaConst();

                // [DllImport("user32.dll")] must stay invisible (a comment).
            }
            """, stripStrings: false);
        var refs = RepoScan.FindDllImports(snippet);
        Assert.AreEqual(2, refs.Count,
            "the extractor must find exactly the two real [DllImport] attributes (the comment is stripped): " + string.Join("; ", refs.Select(r => r.DllExpression)));
        Assert.IsNull(refs[0].EntryPoint,
            "an injected [DllImport] without an EntryPoint must stay visible to the rule (negative verification)");
        Assert.AreEqual("SomeExport", refs[1].EntryPoint,
            "the spelled EntryPoint of an injected attribute must be captured");
        Assert.AreEqual("SomeConst", refs[1].DllExpression,
            "a const-identifier dll must be captured as an identifier, not dropped");
    }

    // --- helpers ---

    /// <summary>
    /// Resolves a DllImport's dll expression: a string literal is its own
    /// value; an identifier must name a const string in the same file.
    /// </summary>
    private static string? ResolveDll(string expression, string code)
    {
        if (expression.Length > 1 && expression.StartsWith('"') && expression.EndsWith('"'))
            return expression[1..^1];

        var constDef = new Regex($@"const\s+string\s+{Regex.Escape(expression)}\s*=\s*""(?<dll>[^""]+)""");
        var match = constDef.Match(code);
        return match.Success && match.Groups["dll"].Value.Length > 0 ? match.Groups["dll"].Value : null;
    }

    /// <summary>
    /// The export-table lookup: a non-zero address means the DLL carries the
    /// export. The loaded modules stay for the process lifetime (a test-host
    /// load of system DLLs; nothing to free). GetModuleHandleW/LoadLibraryW
    /// take LPCWSTR (the default string marshaling); GetProcAddress takes an
    /// LPCSTR, so its extern pins CharSet.Ansi - the one load-bearing
    /// marshaling choice in this file.
    /// </summary>
    private static IntPtr ResolveExport(string dllName, string entryPoint)
    {
        var module = GetModuleHandleW(dllName);
        if (module == IntPtr.Zero)
            module = LoadLibraryW(dllName);
        if (module == IntPtr.Zero)
            return IntPtr.Zero;
        return GetProcAddress(module, entryPoint);
    }

    // The probe's own P/Invoke surface follows the house rule it pins:
    // explicit entry points (the W exports take LPCWSTR; GetProcAddress is
    // the lone ANSI export).
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW")]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpLibFileName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}
