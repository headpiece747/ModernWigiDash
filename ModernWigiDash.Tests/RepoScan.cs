using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared raw-scan mechanics behind the house-rule pins (ArchitectureTests,
/// DebtGuardTests): the repo root embedded at build time, the src-project list,
/// and the comment/string stripping that keeps a scan matching real code tokens
/// only. One owner of the strip, so the pins police the same text they claim to.
/// </summary>
internal static class RepoScan
{
    internal const string RepoRootKey = "ModernWigiDashRepoRoot";

    /// <summary>The shipping projects (the Tests project is excluded: its
    /// fakes and fixtures legitimately use what src may not).</summary>
    internal static readonly string[] SrcProjects =
    [
        "ModernWigiDash.Sdk",
        "ModernWigiDash.Core",
        "ModernWigiDash.Hardware",
        "ModernWigiDash.Widgets",
        "ModernWigiDash.App",
    ];

    /// <summary>The test project (the window test-constructor pin scans its
    /// construction sites; the src pins never apply there).</summary>
    internal const string TestsProject = "ModernWigiDash.Tests";

    /// <summary>
    /// The repo root, embedded at build time (Tests csproj AssemblyMetadata)
    /// because the house test command runs from a temp BaseOutputPath, so the
    /// test assembly location is not the repo.
    /// </summary>
    internal static string GetRepoRoot()
    {
        var meta = typeof(RepoScan).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(m => m.Key == RepoRootKey);

        if (meta is null)
            Assert.Fail(
                "the ModernWigiDashRepoRoot AssemblyMetadata is missing - the Tests csproj must embed the repo root (the house-rule pins run from a temp output path).");

        return Path.GetFullPath(meta.Value!);
    }

    /// <summary>
    /// Scans every src project's .cs files and returns "file:line: match"
    /// entries per pattern hit. Stripped mode blanks comments and string/char
    /// literals first (a comment that names DateTime.UtcNow is documentation,
    /// not a use); raw mode scans the text as-is (the empty-catch pin uses it,
    /// because a documented catch comment must keep the body non-empty).
    /// </summary>
    internal static List<string> ScanSrc(Regex pattern, bool raw = false)
        => ScanProjects(SrcProjects, pattern, raw);

    /// <summary>
    /// The <see cref="ScanSrc"/> shape over the test project: the window
    /// test-constructor pin scans the test construction sites, which the src
    /// pins (the fakes legitimately use what src may not) must not police.
    /// </summary>
    internal static List<string> ScanTests(Regex pattern, bool raw = false)
        => ScanProjects([TestsProject], pattern, raw);

    private static List<string> ScanProjects(string[] projects, Regex pattern, bool raw)
    {
        var root = GetRepoRoot();
        var violations = new List<string>();
        foreach (var project in projects)
        {
            var dir = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/"))
                    continue; // generated code is not house text

                var code = raw ? File.ReadAllText(file) : StripCode(File.ReadAllText(file));
                foreach (Match match in pattern.Matches(code))
                {
                    var lineNo = 1;
                    for (var k = 0; k < match.Index; k++)
                        if (code[k] == '\n')
                            lineNo++;
                    violations.Add($"{rel}:{lineNo}: {match.Value}");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Strips comments and string/char literals down to blanks while preserving
    /// line structure, so a house-rule scan sees real code tokens only (a
    /// comment that names DateTime.UtcNow is documentation, not a use). With
    /// <paramref name="stripStrings"/> off, only comments are blanked: the
    /// dead-helper pin scans occurrences this way because interpolated-string
    /// holes are code (a call inside $"...{Call()}..." is a real use), while a
    /// name inside a plain string literal only ever makes the pin conservative.
    /// A raw string literal (three or more opening quotes) is blanked wholesale
    /// to its closing quote run: its body is literal content, not code the pins
    /// should see, and the quote run would otherwise corrupt the pairing of
    /// every string after it in the file. Every variant blanks in place, so
    /// indices and line numbers agree with the original text and with each
    /// other.
    /// </summary>
    internal static string StripCode(string source, bool stripStrings = true)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        var len = source.Length;

        while (i < len)
        {
            var c = source[i];

            if (c == '/' && i + 1 < len && source[i + 1] == '/')
            {
                var start = i;
                while (i < len && source[i] != '\n')
                    i++;
                sb.Append(Blank(source, start, i));
                continue;
            }

            if (c == '/' && i + 1 < len && source[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/'))
                    i++;
                i = Math.Min(len, i + 2);
                sb.Append(Blank(source, start, i));
                continue;
            }

            if (stripStrings && c == '"')
            {
                // A run of three or more quotes opens a raw string literal
                // (a normal string can open with at most two: the empty
                // string). Blank to the closing run (at least as long) so the
                // body stays invisible and the strings after the literal keep
                // their pairing.
                var run = 0;
                while (i + run < len && source[i + run] == '"')
                    run++;

                if (run >= 3)
                {
                    // The closing run may be longer than the opening one (the
                    // spec requires at least as long); consume the full run so
                    // the surplus does not re-enter the stream and mispair the
                    // strings after the literal.
                    var closeAt = -1;
                    var closeRun = 0;
                    for (var k = i + run; k + run <= len; k++)
                    {
                        var candidate = 0;
                        while (k + candidate < len && source[k + candidate] == '"')
                            candidate++;

                        if (candidate >= run)
                        {
                            closeAt = k;
                            closeRun = candidate;
                            break;
                        }
                    }

                    var end = closeAt >= 0 ? closeAt + closeRun : len;
                    sb.Append(Blank(source, i, end));
                    i = end;
                    continue;
                }

                var start = i;
                var j = i - 1;
                var verbatim = false;
                while (j >= 0 && (source[j] == '@' || source[j] == '$'))
                {
                    if (source[j] == '@')
                        verbatim = true;
                    j--;
                }

                i++;
                while (i < len)
                {
                    if (verbatim)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < len && source[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        i++;
                    }
                    else
                    {
                        if (source[i] == '\\')
                            i++;
                        else if (source[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }
                }

                sb.Append(Blank(source, start, i));
                continue;
            }

            if (stripStrings && c == '\'')
            {
                var start = i;
                i++;
                while (i < len && source[i] != '\'')
                {
                    if (source[i] == '\\')
                        i++;
                    i++;
                }
                i = Math.Min(len, i + 1);
                sb.Append(Blank(source, start, i));
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The 1-based line number of a character index in stripped code (line
    /// structure survives the strip, so indices map to the original lines).
    /// </summary>
    internal static int LineAt(string code, int index)
    {
        var lineNo = 1;
        for (var k = 0; k < index && k < code.Length; k++)
            if (code[k] == '\n')
                lineNo++;
        return lineNo;
    }

    /// <summary>
    /// One [DllImport] attribute found in comment-only stripped code: the
    /// attribute's index (for line mapping), its first argument (the dll
    /// name as a string literal or a const identifier), and the spelled
    /// entry point (null when the attribute leaves the binding to the
    /// method name).
    /// </summary>
    internal sealed record DllImportRef(int Index, string DllExpression, string? EntryPoint);

    /// <summary>
    /// Finds every [DllImport] attribute in comment-only stripped code
    /// (stripStrings: false: the dll name and the entry point live in string
    /// literals, and the full strip would blank them; a comment that merely
    /// names DllImport is blanked). The attribute's argument list is flat
    /// (literals and enum references, no nested parens), so the first close
    /// paren ends it even across line breaks.
    /// </summary>
    internal static List<DllImportRef> FindDllImports(string code)
    {
        var refs = new List<DllImportRef>();
        foreach (Match attr in new Regex(@"\[DllImport\((?<args>[^)]*)\)").Matches(code))
        {
            var args = attr.Groups["args"].Value;
            var dll = Regex.Match(args, @"^\s*(?<expr>""(?<lit>[^""]+)""|(?<ident>[A-Za-z_]\w*))");
            var entry = Regex.Match(args, @"\bEntryPoint\s*=\s*""(?<ep>[^""]+)""");
            refs.Add(new DllImportRef(
                attr.Index,
                dll.Success ? dll.Groups["expr"].Value : string.Empty,
                entry.Success ? entry.Groups["ep"].Value : null));
        }

        return refs;
    }

    /// <summary>
    /// One <c>new MainWindow(</c> construction found in Tests source: the
    /// call's index (for line mapping) and its argument text (for the
    /// inert-USB-engine rule, which checks the binding inside the argument
    /// list).
    /// </summary>
    internal sealed record MainWindowCtorRef(int Index, string Args);

    /// <summary>
    /// Finds every <c>new MainWindow(</c> call in fully stripped code (a
    /// comment that names the construction is documentation, not a site) and
    /// captures its argument list by balanced parens, so the window
    /// test-constructor pin can check each construction for the inert USB
    /// engine binding.
    /// </summary>
    internal static List<MainWindowCtorRef> FindMainWindowCtors(string code)
    {
        var refs = new List<MainWindowCtorRef>();
        foreach (Match match in new Regex(@"new\s+MainWindow\s*\(").Matches(code))
        {
            var open = match.Index + match.Length - 1;
            var depth = 0;
            var end = open;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '(')
                    depth++;
                else if (code[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            refs.Add(new MainWindowCtorRef(match.Index, code[(open + 1)..Math.Min(end, code.Length - 1)]));
        }

        return refs;
    }

    /// <summary>
    /// Blanks a source region, keeping newlines so line numbers survive.
    /// </summary>
    private static string Blank(string source, int start, int end)
    {
        var sb = new StringBuilder(end - start);
        for (var k = start; k < end; k++)
            sb.Append(source[k] == '\n' ? '\n' : ' ');
        return sb.ToString();
    }
}
