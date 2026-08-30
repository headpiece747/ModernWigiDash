using System.IO;
using System.Text.RegularExpressions;

namespace ModernWigiDash.Tests;

/// <summary>
/// The executable debt guardrails: the mechanical layer of "prevent
/// inadvertent debt". The anti-patterns a code review is asked to catch
/// (unbounded sync-over-async, stray async void, unowned handles, frame
/// pipeline bypass, dead helpers) are spelled as rules the gate runs before
/// every commit (the run-gates.ps1 test stage), so a violation is a red gate
/// instead of a review finding. The judgment layer (is an allow-list reason
/// true, is an abstraction earning its place) stays with the code-reviewer
/// agent; this class owns the mechanical layer. Each failure message spells
/// the rule, the violation, and the fix.
/// </summary>
[TestClass]
public sealed class DebtGuardTests
{
    // --- async/await without thread starvation ---

    [TestMethod]
    public void HouseRules_SyncOverAsync_OnlyAtTheDocumentedBudgetedSites()
    {
        // A sync-over-async on the UI thread starves the dispatcher (the 30 FPS
        // tick and the 16 ms touch poll share it). Every house sync point is
        // bounded by an explicit budget (CloseBudgets, the stop/shutdown waits)
        // or runs off the dispatcher (a threadpool continuation); the
        // allow-list is that rule, the same shape as the HttpClient pin: each
        // entry must still hit (a retired site is a deliberate allow-list edit,
        // not a silent shrink), and a new site must be added with its reason
        // before the gate can pass.
        var allowed = new Dictionary<string, string>
        {
            ["ModernWigiDash.Hardware/Transport/DisplayDeviceEngine.cs"] =
                "the standby verdict is read only once Wait confirms completion (CONTEXT.md DisplayDeviceEngine), the dispose-abandon budget reads the transport CloseBudgets, and the connect-fault continuation runs on a threadpool thread",
            ["ModernWigiDash.Core/Models/ProfileOps.cs"] =
                "the profile load runs the widget InitializeAsync/Dispose synchronously inside the load (CONTEXT.md StartupWiring): a synchronous user action, not a tick",
            ["ModernWigiDash.Sdk/FrameDelivery.cs"] =
                "the bounded 1s dispose wait on the sender task",
            ["ModernWigiDash.Sdk/PollLoop.cs"] =
                "the bounded 5s shutdown wait on the loop task",
            ["ModernWigiDash.Widgets/FeedLoop.cs"] =
                "the bounded 5s shutdown wait on the feed task",
        };

        // Monitor.Wait and Thread.Wait are thread joins, not sync-over-async.
        var patterns = new[]
        {
            new Regex(@"(?<!Monitor\.)(?<!Thread\.)\bWait\("),
            new Regex(@"GetAwaiter\(\)\.GetResult\(\)"),
            new Regex(@"\.\s*Result\b"),
        };
        var hitsByFile = new SortedDictionary<string, List<string>>();
        foreach (var pattern in patterns)
        {
            foreach (var hit in RepoScan.ScanSrc(pattern))
            {
                var file = hit.Split(':', 2)[0].Replace('\\', '/');
                hitsByFile.TryAdd(file, []);
                hitsByFile[file].Add(hit);
            }
        }

        var unlisted = hitsByFile.Keys.Where(f => !allowed.ContainsKey(f)).ToList();
        Assert.AreEqual(0, unlisted.Count,
            "sync-over-async outside the documented sites (dotnet-rules 5): " + string.Join("; ", unlisted)
            + ". A .Wait()/.Result/GetAwaiter().GetResult() on the UI thread starves the dispatcher (the 30 FPS tick and the 16 ms touch poll share it). Bound the wait by an explicit budget, move it off the dispatcher, or add the file to the allow-list above with its reason in the same commit.");

        var drift = allowed.Keys.Where(f => !hitsByFile.ContainsKey(f)).ToList();
        Assert.AreEqual(0, drift.Count,
            "sync-over-async allow-list drift: " + string.Join("; ", drift.OrderBy(f => f))
            + ". Each allow-listed file must still carry its documented sync point; a retired site is a deliberate allow-list edit with a CONTEXT.md note when the invariant moved.");
    }

    [TestMethod]
    public void HouseRules_AsyncVoid_OnlyOnEventHandlers()
    {
        // async void outside an event handler swallows its exception into the
        // dispatcher (an unobserved task crash the user never sees). The
        // sanctioned form is the WPF event handler, which is void by contract
        // and carries EventArgs in its signature.
        var violations = new List<string>();
        foreach (var hit in RepoScan.ScanSrc(new Regex(@"async\s+void\s+[A-Za-z_]\w*\s*\(")))
        {
            var parts = hit.Split(new[] { ':' }, 3);
            var file = Path.Combine(RepoScan.GetRepoRoot(), parts[0].Replace('/', Path.DirectorySeparatorChar));
            var lineNo = int.Parse(parts[1]);
            var lines = File.ReadAllLines(file);

            // Collect the declaration (through the parameter list) so a
            // multi-line signature still sees its EventArgs.
            var decl = new List<string>();
            for (var i = lineNo - 1; i < lines.Length; i++)
            {
                decl.Add(lines[i]);
                if (lines[i].Contains('{') || lines[i].EndsWith(';'))
                    break;
            }
            var text = string.Join(' ', decl);
            if (!text.Contains("EventArgs"))
                violations.Add($"{parts[0]}:{lineNo}: async void without an EventArgs parameter");
        }

        Assert.AreEqual(0, violations.Count,
            "async void outside the event-handler shape: " + string.Join("; ", violations)
            + ". dotnet-rules 5: async void swallows exceptions into the dispatcher. Return Task and await it (or log the fault through the injected log seam before it is lost); only a WPF event handler (the EventArgs signature) may stay async void.");
    }

    // --- disposal of unmanaged handles ---

    [TestMethod]
    public void HouseRules_HandleAcquiringFiles_CarryTheirDisposalEvidence()
    {
        // An unmanaged handle that nobody closes is a leak the GC never sees.
        // The rule is visible per file: a file that acquires a handle (a
        // P/Invoke extern, a MemoryMappedFile, the UsbContext, a named mutex,
        // a loaded native library) carries its disposal evidence in the same
        // file - a type implementing IDisposable, a dispose member, a
        // using-scoped release, or an explicit release-API call - or names a
        // documented exception. The allow-list is bidirectional (the
        // HttpClient pin's shape): a file that no longer acquires a handle
        // makes its entry stale.
        var markers = new Regex(@"static\s+extern|MemoryMappedFile|NativeLibrary|UsbContext|new\s+Mutex\b|Mutex\.Open");
        var evidence = new Regex(
            @":\s*IDisposable|void\s+Dispose\s*\(|using\s*\(|using\s+[\w.]+\s+[A-Za-z_]\w*\s*=|CloseHandle\s*\(|WinUsb_Free\s*\(|FreeLibrary\s*\(");
        var allowed = new Dictionary<string, string>
        {
            ["ModernWigiDash.App/WindowChrome.cs"] =
                "one DWM window-attribute call; no handle is acquired or owned",
            ["ModernWigiDash.App/PresentMon/PresentMonLoader.cs"] =
                "the process-lifetime PresentMon service DLL (NativeLibrary.Load): the handle is deliberately not freed - the DLL belongs to the service directory and the client session lives as long as the process",
            ["ModernWigiDash.Widgets/HotkeyActionExecutor.cs"] =
                "the SendInput P/Invoke fires input events; the call acquires and owns no handle",
            ["ModernWigiDash.App/Hotkey/HotkeyApi.cs"] =
                "the RegisterHotKey/UnregisterHotKey P/Invoke registers and releases message-loop hotkeys; the calls acquire and own no handle",
        };

        var root = RepoScan.GetRepoRoot();
        var violations = new List<string>();
        var markerFiles = new List<string>();
        foreach (var project in RepoScan.SrcProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var code = RepoScan.StripCode(File.ReadAllText(file));
                var marker = markers.Match(code);
                if (!marker.Success)
                    continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                markerFiles.Add(rel);
                if (evidence.IsMatch(code))
                    continue;
                if (allowed.ContainsKey(rel))
                    continue;

                var lineNo = RepoScan.LineAt(code, marker.Index);
                violations.Add($"{rel}:{lineNo}: acquires a handle ({marker.Value}) but carries no disposal evidence in the file and no documented exception");
            }
        }

        Assert.AreEqual(0, violations.Count,
            "handle acquired without visible disposal: " + string.Join("; ", violations)
            + ". dotnet-rules 6: close the handle in the same file (implement IDisposable and release in Dispose, scope the acquisition with using, or call the release API) or add a documented exception to the allow-list above - a process-lifetime handle is a deliberate entry with a reason, never an omission.");

        var stale = allowed.Keys.Where(rel => !markerFiles.Contains(rel)).OrderBy(rel => rel).ToList();
        Assert.AreEqual(0, stale.Count,
            "handle-disposal allow-list drift: " + string.Join("; ", stale)
            + ". Each allow-listed file must still acquire a handle; remove the entry when the acquisition left.");
    }

    // --- allocation handling and object pooling ---

    [TestMethod]
    public void HouseRules_FramePipeline_EncodeAndPoolHaveOneEntry()
    {
        // The render tick feeds the frame through FrameDelivery.Push: the
        // delivery encodes through its IRgb565Encoder and rents the buffer
        // from its FrameBufferPool (CONTEXT.md FrameDelivery). A second
        // encode or buffer site would bypass the pool (the per-frame ~1.2 MB
        // LOH churn the pool exists to kill) or double-own the encoder's
        // output. The pins: .Encode( in src sits only in FrameDelivery, and
        // FrameBufferPool is referenced only by FrameDelivery and its own
        // file.
        var encodeHits = RepoScan.ScanSrc(new Regex(@"\.Encode\("))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "ModernWigiDash.Sdk/FrameDelivery.cs" },
            encodeHits,
            "frame encode outside the delivery: " + string.Join("; ", encodeHits)
            + ". dotnet-rules 5: the render tick pushes frames through FrameDelivery.Push; the delivery owns the encode and the pool. A second encode site re-creates the per-frame LOH allocation the pool exists to kill.");

        var poolHits = RepoScan.ScanSrc(new Regex(@"\bFrameBufferPool\b"))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .OrderBy(f => f)
            .Distinct()
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "ModernWigiDash.Sdk/FrameBufferPool.cs", "ModernWigiDash.Sdk/FrameDelivery.cs" },
            poolHits,
            "frame buffer pool referenced outside its owner: " + string.Join("; ", poolHits)
            + ". The pool is the one buffer owner behind FrameDelivery (CONTEXT.md FrameDelivery); a second consumer means the tick (or a widget) allocates its own frame buffer and the pool is no longer the single allocation path.");
    }

    // --- dead helper methods ---

    [TestMethod]
    public void HouseRules_NoDeadPrivateHelpersInSrc()
    {
        // A private method is visible only inside its own type, so a helper
        // with no call site in the type's files (its partials, one type per
        // file) or in the project's XAML (event handlers are wired by name)
        // is dead - and a helper called only by dead helpers is dead with
        // them (the transitive sweep). A new private helper nothing calls is
        // debt the moment it lands: the gate refuses the commit, so a helper
        // is wired up (or deleted) in the same change that adds it. A
        // reflection-sensitive name goes in the allow-list below with its
        // reason, like the other pins' entries.
        var root = RepoScan.GetRepoRoot();
        var typeDecl = new Regex(
            @"^(?:(?:public|internal|sealed|static|abstract|readonly|partial)\s+)*(?:class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline);
        var privateMethod = new Regex(
            @"private\s+(?:(?:static|async|unsafe|new|ref)\s+)*(?<ret>[\w.]+(?:<[^;{}()]*>)?(?:\[\])?)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^;{}()]*>)?\s*\(");
        var typeKeywords = new HashSet<string> { "class", "interface", "record", "struct", "enum", "delegate" };
        // A C# keyword cannot be a method name. The modifier slot of the
        // privateMethod regex misreads a tuple-array field declaration
        // (private static readonly (T A, int B)[] Name) as a method called
        // "readonly" (ret "static", name "readonly", the tuple type read as
        // the parameter list). That fake candidate has no call site by
        // construction, so a keyword name is skipped, like a type ret.
        var methodKeywordNames = new HashSet<string>
        {
            "readonly", "static", "const", "volatile", "new", "ref", "out", "in",
            "partial", "async", "unsafe", "fixed", "managed", "unmanaged", "where",
            "this", "base", "var", "event", "operator", "yield", "await", "get",
            "set", "value", "stackalloc", "sizeof", "typeof", "throw", "null",
            "true", "false", "default", "is", "as", "not",
        };

        // Group files by the top-level types they declare: a private member's
        // reference scope is the type's files (its partials), not the project.
        // Two strip variants (same length, same indices): the full strip for
        // declarations and structural spans (a name inside a string literal
        // is not a declaration), the comment-only strip for occurrences
        // (interpolation holes are code, so a call inside $"...{Call()}..."
        // counts; a name inside a plain literal only makes the pin
        // conservative).
        var groups = new Dictionary<(string Project, string Type), List<string>>();
        var fileCode = new Dictionary<string, string>();
        var fileOccurrence = new Dictionary<string, string>();
        var xamlByProject = new Dictionary<string, string>();
        foreach (var project in RepoScan.SrcProjects)
        {
            var projectDir = Path.Combine(root, project);
            // Generated code (obj/bin, the WPF temp project's .g.cs) is not
            // house text: it can carry private members the pin would call
            // dead, and it changes between builds.
            var srcFiles = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var p = f.Replace('\\', '/');
                    return !p.Contains("/obj/") && !p.Contains("/bin/");
                });
            foreach (var file in srcFiles)
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var text = File.ReadAllText(file);
                fileCode[rel] = RepoScan.StripCode(text);
                fileOccurrence[rel] = RepoScan.StripCode(text, stripStrings: false);
                foreach (Match m in typeDecl.Matches(fileCode[rel]))
                {
                    var key = (project, m.Groups["name"].Value);
                    if (!groups.TryGetValue(key, out var files))
                        groups[key] = files = [];
                    files.Add(rel);
                }
            }

            var xaml = string.Join('\n', Directory
                .EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            if (xaml.Length > 0)
                xamlByProject[project] = xaml;
        }

        // Per candidate: the declaration span, the body span (for the
        // transitive sweep), and the enclosing top-level type.
        var candidates = new List<Candidate>();
        foreach (var (rel, code) in fileCode)
        {
            var topTypes = typeDecl.Matches(code).Select(m => (Index: m.Index, Name: m.Groups["name"].Value)).ToList();
            foreach (Match m in privateMethod.Matches(code))
            {
                if (typeKeywords.Contains(m.Groups["ret"].Value))
                    continue; // a type declaration, not a method
                if (methodKeywordNames.Contains(m.Groups["name"].Value))
                    continue; // a keyword name (a misread field), not a method

                var enclosing = topTypes.LastOrDefault(t => t.Index < m.Index).Name;
                if (enclosing is null)
                    continue; // private outside a type is not valid C#; skip.

                var body = BodySpan(code, m.Index + m.Length);
                candidates.Add(new Candidate
                {
                    File = rel,
                    Type = enclosing,
                    Name = m.Groups["name"].Value,
                    DeclStart = m.Index,
                    DeclEnd = m.Index + m.Length,
                    BodyStart = body.Start,
                    BodyEnd = body.End,
                });
            }
        }

        var dead = FindDead(candidates, groups, fileOccurrence, xamlByProject);

        // Reflection-sensitive names (a private method a serializer or a
        // reflection lookup reaches by string) are deliberate allow-list
        // entries with a reason.
        var allowListed = new Dictionary<string, string>();
        var violations = dead
            .Where(c => !allowListed.ContainsKey(c.File))
            .Select(c => $"{c.File}:{RepoScan.LineAt(fileCode[c.File], c.DeclStart)}: private method {c.Name} in {c.Type} has no call site in the type's files or the project XAML")
            .OrderBy(v => v)
            .ToList();

        Assert.AreEqual(0, violations.Count,
            "dead private helper(s) in src: " + string.Join("; ", violations)
            + ". A private method nothing calls is debt (dotnet-rules 4/10): wire it up or delete it in this commit. A reflection-sensitive name gets an allow-list entry above with its reason.");
    }

    // --- P/Invoke bindings ---

    [TestMethod]
    public void HouseRules_DllImports_NameTheirEntryPointExplicitly()
    {
        // Without an explicit EntryPoint the binding resolves to the method
        // name, so a rename silently re-targets the call (or, when the name
        // was never an export, the first real call throws
        // EntryPointNotFoundException - the 2026-08-26 hotkey crash,
        // invisible to every test because they inject fakes). Spelling the
        // entry point makes the export a construction fact the diff shows;
        // PInvokeBindingTests probes each spelled pair against the real DLL
        // at the gate (ADR-0020).
        var root = RepoScan.GetRepoRoot();
        var violations = new List<string>();
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
                    if (dllImport.EntryPoint is not null)
                        continue;
                    violations.Add($"{rel}:{RepoScan.LineAt(code, dllImport.Index)}: [DllImport] without an explicit EntryPoint (the binding defaults to the method name)");
                }
            }
        }

        violations = violations.OrderBy(v => v).ToList();
        Assert.AreEqual(0, violations.Count,
            "P/Invoke entry points must be spelled explicitly: " + string.Join("; ", violations)
            + ". Without EntryPoint the binding resolves to the method name, so a rename silently re-targets the call and a never-existing export crashes the first real call (the 2026-08-26 hotkey crash). Add EntryPoint = \"<export>\" to the attribute; PInvokeBindingTests probes the spelled export against the real DLL (ADR-0020).");
    }

    // --- window test construction must not reach the hardware ---

    [TestMethod]
    public void HouseRules_WindowTestCtors_BindAnInertUsbEngine()
    {
        // The window's constructor starts its USB engine (the connect probe,
        // the 16 ms touch poll, the 5 s reconnect timer, the teardown
        // standby), so a bare new MainWindow(...) in the test host would
        // wake, init, and put to sleep the user's attached display. The
        // constructor's usbEngine argument must be the fake-transport engine
        // (FakeTransport.InertEngine or an explicit DisplayDeviceEngine). The
        // pin scans every spelled construction site; the target-typed new(
        // form is invisible to a raw scan, so the window test sites spell
        // `new MainWindow(` explicitly (the deleted short test ctors turn a
        // short target-typed call into a compile error).
        var root = RepoScan.GetRepoRoot();
        var files = new SortedSet<string>();
        foreach (var hit in RepoScan.ScanTests(new Regex(@"new\s+MainWindow\s*\(")))
            files.Add(hit.Split(':', 2)[0].Replace('\\', '/'));

        var violations = new List<string>();
        foreach (var file in files)
        {
            var code = RepoScan.StripCode(File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))));
            foreach (var ctor in RepoScan.FindMainWindowCtors(code))
            {
                if (ctor.Args.Contains("InertEngine") || ctor.Args.Contains("DisplayDeviceEngine("))
                    continue;
                violations.Add($"{file}:{RepoScan.LineAt(code, ctor.Index)}: new MainWindow without an inert USB engine");
            }
        }

        violations = violations.OrderBy(v => v).ToList();
        Assert.AreEqual(0, violations.Count,
            "window test construction must bind an inert USB engine: " + string.Join("; ", violations)
            + ". The window's constructor starts its USB engine (the connect probe, the touch poll, the reconnect timer, the teardown standby), so a bare construction would wake, init, and put to sleep the user's attached display on every test run. Pass FakeTransport.InertEngine() (or an explicit DisplayDeviceEngine bound to a FakeTransport) as the constructor's usbEngine argument.");
    }

    [TestMethod]
    public void FindMainWindowCtors_InjectedViolations_StayVisibleToTheRule()
    {
        // Negative verification (the house shape): the extractor the window
        // constructor pin runs must keep seeing an injected bare
        // construction - a scan that loses it lets a real-engine test host
        // through the pin.
        var snippet = RepoScan.StripCode("""
            var bare = new MainWindow(pm, path, power, tray);
            var inert = new MainWindow(pm, path, power, tray, null, null, null, null, FakeTransport.InertEngine());
            var explicitEngine = new MainWindow(pm, path, power, tray, null, null, null, null, new DisplayDeviceEngine(fake, ConnectionState.Connected));
            // var commented = new MainWindow(pm, path, power, tray); must stay invisible.
            """);
        var refs = RepoScan.FindMainWindowCtors(snippet);
        Assert.AreEqual(3, refs.Count,
            "the extractor must find exactly the three real constructions (the comment is stripped): " + string.Join("; ", refs.Select(r => r.Args)));
        Assert.IsFalse(refs[0].Args.Contains("InertEngine") || refs[0].Args.Contains("DisplayDeviceEngine("),
            "an injected bare construction must stay visible to the rule (negative verification)");
        Assert.IsTrue(refs[1].Args.Contains("InertEngine"),
            "the InertEngine binding of an injected construction must be captured");
        Assert.IsTrue(refs[2].Args.Contains("DisplayDeviceEngine("),
            "the explicit DisplayDeviceEngine binding of an injected construction must be captured");
    }

    [TestMethod]
    public void StripCode_RawStringLiteral_BlanksTheBodyAndKeepsTheStringsAfter()
    {
        // A raw string literal in scanned source must be blanked wholesale:
        // its body is literal content, not code the pins should see, and the
        // strings after it must keep their pairing (a mispaired quote run
        // would corrupt the rest of the file's strip - the shape the
        // injected-violation snippet above relies on staying invisible).
        var source = "var a = \"\"\"\n    new MainWindow(pm, path, power, tray);\n    \"\"\";\nvar b = \"x\";\n";
        var code = RepoScan.StripCode(source);

        Assert.AreEqual(0, RepoScan.FindMainWindowCtors(code).Count,
            "the raw string body must be blanked (it is literal content, not code)");
        Assert.IsTrue(code.Contains("var b = "),
            "code after the raw string literal must survive the strip (the quote pairing stays sound)");
        Assert.IsFalse(code.Contains("x"),
            "the string after the raw string must be stripped like any other string");
    }

    [TestMethod]
    public void StripCode_RawStringLiteral_LongerClosingRun_ConsumesTheFullRun()
    {
        // The C# spec allows the closing run to be LONGER than the opening
        // run; the surplus must be consumed with the literal, not left in
        // the stream to re-pair as a phantom string (the same corruption
        // class the raw-string handling exists to kill).
        var source = "var a = \"\"\"\nbody\n\"\"\"\";\nvar b = \"x\";\n";
        var code = RepoScan.StripCode(source);

        Assert.IsTrue(code.Contains("var b = "),
            "the code after the literal must survive (the surplus closing quote must not open a phantom string)");
        Assert.IsFalse(code.Contains("x"),
            "the string after the literal must be stripped like any other string");
    }

    // --- shared helpers ---

    private sealed class Candidate
    {
        public required string File { get; init; }
        public required string Type { get; init; }
        public required string Name { get; init; }
        public int DeclStart { get; init; }
        public int DeclEnd { get; init; }
        public int BodyStart { get; init; }
        public int BodyEnd { get; init; }
    }

    /// <summary>
    /// The body span of a method whose parameter list's open paren ends just
    /// before <paramref name="afterOpenParen"/>: a brace body runs to the
    /// matching close brace; an expression body runs to the statement end.
    /// A truncated span (a lambda's block inside an expression body) only
    /// ever shrinks the attribution area, so occurrences outside it count as
    /// external references - the conservative direction for a dead-code pin.
    /// </summary>
    private static (int Start, int End) BodySpan(string code, int afterOpenParen)
    {
        var depth = 1;
        var i = afterOpenParen;
        while (i < code.Length && depth > 0)
        {
            if (code[i] == '(')
                depth++;
            else if (code[i] == ')')
                depth--;
            i++;
        }

        while (i < code.Length && char.IsWhiteSpace(code[i]))
            i++;

        if (i < code.Length && code[i] == '{')
        {
            var brace = 1;
            var j = i + 1;
            while (j < code.Length && brace > 0)
            {
                if (code[j] == '{')
                    brace++;
                else if (code[j] == '}')
                    brace--;
                j++;
            }
            return (i, Math.Min(j - 1, code.Length - 1));
        }

        if (i + 1 < code.Length && code[i] == '=' && code[i + 1] == '>')
        {
            var paren = 0;
            var j = i + 2;
            while (j < code.Length)
            {
                if (code[j] == '(')
                    paren++;
                else if (code[j] == ')')
                    paren--;
                else if (code[j] == ';' && paren == 0)
                    break;
                j++;
            }
            return (i, Math.Min(j, code.Length - 1));
        }

        return (i, i);
    }

    /// <summary>
    /// The reachability sweep: a candidate is alive when an external site
    /// (a property, a constructor, a field initializer - live code outside
    /// the candidate set) references it, a WPF XAML file wires it by name
    /// (event handlers), or an alive candidate references it. Everything
    /// else is dead. Occurrences are scanned in the comment-only strip
    /// (interpolation holes are code); the structural spans come from the
    /// full strip.
    /// </summary>
    private static List<Candidate> FindDead(
        List<Candidate> candidates,
        Dictionary<(string Project, string Type), List<string>> groups,
        Dictionary<string, string> fileOccurrence,
        Dictionary<string, string> xamlByProject)
    {
        var projectOf = fileOccurrence.Keys.ToDictionary(rel => rel, rel => rel.Split('/')[0]);
        var byType = new Dictionary<(string Project, string Type), List<Candidate>>();
        foreach (var c in candidates)
        {
            var key = (projectOf[c.File], c.Type);
            if (!byType.TryGetValue(key, out var list))
                byType[key] = list = [];
            list.Add(c);
        }

        var alive = new HashSet<(string File, string Name)>();
        foreach (var (key, list) in byType)
        {
            var scope = groups[key].Select(f => (File: f, Code: fileOccurrence[f])).ToList();

            // Occurrences of each candidate's name across the scope,
            // attributed: the candidate's own declaration is skipped (it is
            // not a call), an occurrence in its own body is a self-recursive
            // call (it does not keep the candidate alive from the outside),
            // an occurrence inside another candidate's body is that candidate
            // referencing it, and everything else is an external reference.
            var external = new Dictionary<(string File, string Name), int>();
            var refBy = new Dictionary<(string File, string Name), HashSet<(string File, string Name)>>();
            foreach (var c in list)
            {
                external[(c.File, c.Name)] = 0;
                refBy[(c.File, c.Name)] = [];

                // The XAML escape: a WPF event handler wired by name in the
                // project's .xaml is live even though no C# call site names
                // it - and so are the helpers only it calls.
                if (xamlByProject.TryGetValue(projectOf[c.File], out var xaml)
                    && Regex.IsMatch(xaml, $@"\b{Regex.Escape(c.Name)}\b"))
                    external[(c.File, c.Name)]++;
            }

            foreach (var c in list)
            {
                var namePattern = new Regex($@"\b{Regex.Escape(c.Name)}\b");
                foreach (var (file, code) in scope)
                {
                    foreach (Match occ in namePattern.Matches(code))
                    {
                        var pos = occ.Index;
                        if (file == c.File && pos >= c.DeclStart && pos < c.DeclEnd)
                            continue; // the declaration itself
                        if (file == c.File && pos >= c.BodyStart && pos <= c.BodyEnd)
                            continue; // a self-recursive call

                        var referenced = list.FirstOrDefault(o =>
                            o.File == file
                            && (o.File, o.Name) != (c.File, c.Name)
                            && pos >= o.BodyStart
                            && pos <= o.BodyEnd);
                        if (referenced is { } n)
                            refBy[(c.File, c.Name)].Add((n.File, n.Name));
                        else
                            external[(c.File, c.Name)]++;
                    }
                }
            }

            // Reachability from the outside, to a fixpoint.
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var c in list)
                {
                    var id = (c.File, c.Name);
                    if (alive.Contains(id))
                        continue;
                    if (external[id] > 0 || refBy[id].Any(r => alive.Contains(r)))
                    {
                        alive.Add(id);
                        changed = true;
                    }
                }
            }
        }

        return candidates
            .Where(c => !alive.Contains((c.File, c.Name)))
            .ToList();
    }
}
