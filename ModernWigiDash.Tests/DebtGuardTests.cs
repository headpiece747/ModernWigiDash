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
            ["ModernWigiDash.Core/Models/WidgetRehydrator.cs"] =
                "the profile load runs the widget InitializeAsync/Dispose synchronously inside the load (CONTEXT.md StartupWiring): a synchronous user action, not a tick",
            ["ModernWigiDash.Sdk/LoopLifetime.cs"] =
                "the shared lifetime module's bounded join (the cancel / bounded-join / CTS-handoff dance owned once for PollLoop, FeedLoop, and FrameDelivery)",
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
        // A release API DECLARED in an extern bag is not disposal evidence
        // (the bag spells the entry point; the adapter that acquires the
        // handle calls the release): strip the declarations before the
        // evidence check so only a call - or the IDisposable / dispose /
        // using shapes - counts.
        var externDeclarations = new Regex(@"static\s+extern[^;{]*\)\s*;");
        var allowed = new Dictionary<string, string>
        {
            ["ModernWigiDash.App/WindowChrome.cs"] =
                "one DWM window-attribute call; no handle is acquired or owned",
            ["ModernWigiDash.App/PresentMon/PresentMonLoader.cs"] =
                "the process-lifetime PresentMon service DLL (NativeLibrary.Load): the handle is deliberately not freed - the DLL belongs to the service directory and the client session lives as long as the process",
            ["ModernWigiDash.Widgets/HotkeyActionApi.cs"] =
                "the SendInput P/Invoke fires input events; the call acquires and owns no handle (the executor routes through this bag; the GCHandle pins the input array for the duration of the call and frees it in the finally)",
            ["ModernWigiDash.App/Hotkey/HotkeyApi.cs"] =
                "the RegisterHotKey/UnregisterHotKey P/Invoke registers and releases message-loop hotkeys; the calls acquire and own no handle",
            ["ModernWigiDash.App/PresentMon/TrackedTargetResolver.cs"] =
                "the user32 GetForegroundWindow/GetWindowThreadProcessId/GetWindowText P/Invokes query window state; the calls acquire and own no handle (the toolhelp snapshot moved to ProcessTreeSource, which closes it in its finally)",
            ["ModernWigiDash.Hardware/Transport/WinUsbNative.cs"] =
                "the WinUSB extern bag: the file declares the release APIs but never calls them - WinUsbBulkDevice acquires the interface handle and releases it through the bag in its Dispose",
            ["ModernWigiDash.Hardware/Transport/SetupApiNative.cs"] =
                "the SetupAPI extern bag: the file declares CloseHandle but never calls it - WinUsbBulkDevice acquires the device handle and releases it through the bag in its Dispose",
            ["ModernWigiDash.App/StartupLaunchPolicy.cs"] =
                "the kernel32 GetStartupInfoW call reads the process STARTUPINFO structure; no handle is acquired or owned",
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
                if (evidence.IsMatch(externDeclarations.Replace(code, "")))
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
        // test options record's UsbEngine must be the fake-transport engine
        // (FakeTransport.InertEngine or an explicit DisplayDeviceEngine).
        // The pin scans every spelled construction site; the target-typed
        // new( form is invisible to a raw scan, so the window test sites
        // spell `new MainWindow(` explicitly (the deleted positional test
        // ctor turns a short target-typed call into a compile error).
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
            + ". The window's constructor starts its USB engine (the connect probe, the touch poll, the reconnect timer, the teardown standby), so a bare construction would wake, init, and put to sleep the user's attached display on every test run. Pass FakeTransport.InertEngine() (or an explicit DisplayDeviceEngine bound to a FakeTransport) as the test options record's UsbEngine property.");
    }

    // --- the connect seam stays a typed outcome, never a bool ---

    [TestMethod]
    public void HouseRules_ConnectSeam_StaysATypedOutcomeNotABool()
    {
        // A bool from Connect() is what made every hardware fault read as "no
        // physical device" (2026-09-27): absent, busy-because-another-client-
        // owns-the-handle, and init-failed all collapsed into one false, so a
        // PRESENT display was reported missing and the badge said "Simulated".
        // The behaviour is pinned by the transport and engine tests; this pins
        // the SHAPE, because "the enum quietly became a bool again" is a type
        // change no behavioural test can see - every one of them still passes
        // with a bool as long as it returns false at the right moments.
        var boolHits = RepoScan.ScanSrc(new Regex(@"\bbool\s+Connect\s*\("))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            boolHits,
            "the transport connect seam must not return bool: " + string.Join("; ", boolHits)
            + ". A bool cannot carry which fault occurred, so DeviceBusy (the display is there, another client owns the handle) and DeviceNotFound (nothing is plugged in) become indistinguishable again and the engine reports the absent wording for a present device. Return ConnectionState and let the caller name the fault.");

        var typedHits = RepoScan.ScanSrc(new Regex(@"\bConnectionState\s+Connect\s*\("))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                "ModernWigiDash.Hardware/Transport/DisplayHidTransport.cs",
                "ModernWigiDash.Hardware/Transport/IDisplayTransport.cs",
            },
            typedHits,
            "the connect seam must be declared once and implemented once: " + string.Join("; ", typedHits)
            + ". A second implementation (a new transport, a shim) answering with its own outcome type re-splits the vocabulary the engine publishes, and the fold in DisplayHidTransport.MoreSpecific stops covering every leg.");
    }

    [TestMethod]
    public void HouseRules_ConnectFailureStates_AreNamedInTheEnumNotInferred()
    {
        // The three failure states are the point of the whole change, and each
        // must keep a distinct name. A variant deleted or renamed silently
        // re-merges two faults the user has to be able to tell apart, so the set
        // is pinned by name rather than left to review. Pinned in the enum's own
        // file: a definition that moves out of ConnectionState is the drift this
        // catches, whether it was renamed, duplicated, or replaced by inference.
        var declared = RepoScan.ScanSrc(new Regex(@"\b(DeviceNotFound|DeviceBusy|DeviceNotResponding)\b"))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        const string enumFile = "ModernWigiDash.Hardware/Transport/ConnectionState.cs";
        Assert.IsTrue(
            declared.Contains(enumFile),
            "the connect failure vocabulary must be declared in ConnectionState: " + string.Join("; ", declared)
            + ". DeviceNotFound / DeviceBusy / DeviceNotResponding are the three facts the user acts on differently (check the cable / close the other app / the device is wedged); a variant that stops being declared there re-opens the collapse this change closed.");
    }

    [TestMethod]
    public void HouseRules_TheStartupLog_CarriesTheHostEnvironmentStamp()
    {
        // The OS build used to appear NOWHERE in production code - not in the
        // startup log, not in the crash log - so an environment-specific report
        // could only be answered by asking the reporter, never by reading the
        // log they already had (2026-09-27).
        //
        // HostEnvironmentStampTests pins the FORMAT. This pins the CALL, which
        // is the half that can rot unnoticed: delete the write from OnStartup
        // and every format assertion still passes, because none of them start
        // the app.
        // raw: true because the call site lives INSIDE an interpolated string
        // ($"[App] === Host === {HostEnvironmentStamp.Describe()}"), and the
        // stripped scan blanks string literals - which would make this pin see
        // zero writers and fail for the wrong reason, or worse, pass if the
        // expected list were ever emptied. An interpolation hole is code.
        var hits = RepoScan.ScanSrc(new Regex(@"HostEnvironmentStamp\.Describe\(\)"), raw: true)
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "ModernWigiDash.App/App.xaml.cs" },
            hits,
            "exactly one owner writes the host stamp into the startup log: " + string.Join("; ", hits)
            + ". A second writer is two spellings of the same fact; none at all means every future environment-specific report is unanswerable from the log.");
    }

    [TestMethod]
    public void HouseRules_UserStateDirectory_HasExactlyOneResolver()
    {
        // The user state dir (%LocalAppData%\ModernWigiDash) was resolved
        // independently by five owners - the two log paths, profile.json,
        // app_settings.json and app_theme.json - and a test host that
        // redirected one of them still wrote the other four into the
        // developer's real profile (2026-09-27: pinning just the logs left the
        // suite writing a real profile.json on every gate run, which is exactly
        // the non-default state the user refuses to verify against).
        //
        // UserStateDirectory.Resolve is the one owner, in the leaf project so
        // Core's theme path can reach it. A second resolver is the defect
        // coming back, so the count is pinned rather than reviewed: the theme
        // owner lives below App, and a seam only App could see would leave half
        // the callers unbound.
        var hits = RepoScan.ScanSrc(new Regex(@"GetFolderPath\(\s*Environment\.SpecialFolder\.LocalApplicationData\s*\)"))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "ModernWigiDash.Sdk/UserStateDirectory.cs" },
            hits,
            "the user state dir must be resolved in exactly one place: " + string.Join("; ", hits)
            + ". A second Environment.GetFolderPath(LocalApplicationData) is a user-data path the test host cannot redirect, so the test suite writes the developer's real profile again. Route every user-data path through UserStateDirectory.Resolve.");
    }

    [TestMethod]
    [DataRow("USB-WINUSB", "ModernWigiDash.Hardware/Transport/DisplayHidTransport.cs", "ModernWigiDash.Hardware/Transport/WinUsbBulkDevice.cs")]
    [DataRow("USB-LIBUSB", "ModernWigiDash.Hardware/Transport/DisplayHidTransport.cs", "ModernWigiDash.Hardware/Transport/LibUsbTransferBackend.cs")]
    public void HouseRules_ConnectLegTags_HaveExactlyTwoOwners(
        string tag, string recordOwner, string adapterOwner)
    {
        // CONTEXT.md:24 — "the tag is declared once at construction instead of
        // being hand-baked into each message string, so a category can never
        // drift between its call sites" and "no call site composes its own
        // [TAG] prefix". Two owners are legitimate and necessary: the
        // ConnectProvider record (the loop spells the leg's open/fail lines from
        // it) and the adapter's own bound DiagLog (its internal steps). A THIRD
        // site is the drift the invariant exists to prevent, and it is silent:
        // a leg body's DiagLog is built PER ATTEMPT, so after a rename the stale
        // one keeps emitting under the old tag indefinitely with nothing to
        // notice (2026-09-27, found by the architecture review).
        // raw: true, because the subject of the scan IS a string literal — the
        // default strip blanks literals, so the stripped scan finds zero sites
        // and the pin would "pass" or "fail" on an empty set for reasons that
        // have nothing to do with the rule.
        //
        // SITES, not files: counting distinct files is blind to the exact drift
        // it exists to catch, because a re-spelled tag in a leg body lands in
        // DisplayHidTransport.cs — the same file the record lives in — and the
        // file set does not change. (That was this pin's first shape, and the
        // mutation proved it: a third literal in the WinUSB leg stayed green.)
        var hits = RepoScan.ScanSrc(new Regex(Regex.Escape($"\"{tag}\"")), raw: true);
        var parts = hits.Select(hit => hit.Split(':', 3)).ToList();
        // One entry per SITE, sorted - deliberately not Distinct, so a third
        // literal in the record's own file shows up as a third entry and fails.
        var siteFiles = parts
            .Select(p => p[0].Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var annotated = string.Join("; ", parts
            .Select(p => p.Length > 2 ? $"{p[0].Replace('\\', '/')}:{p[1]}" : p[0].Replace('\\', '/')));
        CollectionAssert.AreEqual(
            new[] { adapterOwner, recordOwner }.OrderBy(f => f, StringComparer.Ordinal).ToList(),
            siteFiles,
            $"the \"{tag}\" leg tag must have exactly two owners, one SITE each: the ConnectProvider record (which the loop spells the leg's open/fail lines from) and the adapter's own DiagLog. Found {siteFiles.Count}: "
            + annotated
            + ". Two ways to fail, both silent at run time: a third site re-types a tag, so a rename leaves it emitting under the old name forever (a leg's DiagLog is constructed per attempt, not per lifetime); or one owner was renamed and the other was not, so the leg and its adapter log under different names with nothing in the build noticing.");
    }

    /// <summary>How long the display waits on the network, per src file. Every
    /// 30 s that is NOT a network wait is listed here with its reason, so a new
    /// one has to be a deliberate allow-list edit. The counts are per FILE on
    /// purpose: a line number moves on every unrelated edit, a count does not.
    /// The limit of this pin, stated so nobody trusts it for more than it is: a
    /// wait spelled in another unit, or spelled as a different number, is not a
    /// 30 s and does not appear here. It polices the value that was actually
    /// duplicated five times, not every timeout in the app.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> ThirtySecondWaitSites = new()
    {
        ["ModernWigiDash.Widgets/NetworkBudget.cs"] = (1, "the budget itself - the one owner"),
        ["ModernWigiDash.Widgets/PictureMediaSession.cs"] = (1, "the folder rescan period (a filesystem poll), not a network wait"),
        ["ModernWigiDash.Widgets/PriceFeedManager.cs"] = (3, "the REST poll cadence, the reconnect ceiling, and the retired-CTS grace - three different policies that happen to share the number"),
        ["ModernWigiDash.Widgets/Twitch/TwitchChatConnection.cs"] = (1, "the IRC reconnect ceiling, not a per-request wait"),
    };

    [TestMethod]
    public void HouseRules_AModuleNamedTestClass_DoesNotBuildAWindow()
    {
        // 12 test classes build a live MainWindow - an XAML tree, a profile load,
        // a telemetry session, a tray icon, a hotkey registration, a temp
        // settings store and a swapped FileLog path per construction. Some of
        // them have to: the window's WIRING is only visible through the window.
        //
        // But a class named after a MODULE is testing the module, and the module
        // takes its dependencies as arguments. AhkSpawnPolicyTests is the worked
        // example: its five cases used to build a window each to assert a spawn
        // count and one log line that the policy already owned, and its
        // interface (settings, an interpreter-existence probe, the launch seam)
        // made every one of those seams drivable with no window at all
        // (2026-09-27). 5 windows became 0, and the one window test that stayed
        // asserts the wiring - which is the only part the window owns.
        //
        // The markers are module-naming suffixes, so the rule is about intent: if
        // the class says it is testing a Policy / Layout / Presentation / Funnel /
        // Module / Sequence / Codec, it is not testing a Window.
        var moduleNamed = new Regex(@"(Policy|Funnel|Layout|Presentation|Module|Sequence|Codec|Catalog|Provider|Wire)Tests\.cs$");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(RepoScan.GetRepoRoot(), "*Tests.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(RepoScan.GetRepoRoot(), file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            if (!moduleNamed.IsMatch(Path.GetFileName(relative))) continue;
            if (File.ReadAllText(file).Contains("new MainWindow(", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.AreEqual(0, offenders.Count,
            "a test class named after a MODULE must drive that module, not build a window: the module takes its dependencies as arguments, so an XAML tree, a profile load, a telemetry session and a tray icon are startup the test never needed. Found: "
            + string.Join("; ", offenders)
            + ". Migrate the case to the module's own test class and leave the window to the wiring test.");
    }

    [TestMethod]
    public void HouseRules_TransportKeepsTheConnectLoop_NotTheWires()
    {
        // DisplayHidTransport was 671 lines holding four unrelated jobs: the
        // connect loop, the two provider legs, the frame wire, and the touch
        // wire. The frame and touch wires moved to DisplayFrameSender and
        // DisplayTouchReader, and the live backend plus every tracked transfer
        // moved to DisplayWire (the architecture review's transport candidate,
        // dated 2026-09-27). The file is the composition root and the connect
        // policy; the wires are not its business again.
        //
        // Each token below is one job's fingerprint. Answering "what does a frame
        // cost?" or "why was that touch dropped?" must mean reading ONE short
        // file, not grepping the transport.
        //
        // CmdSleepDevice is deliberately NOT here: the standby ritual (welcome
        // screen + sleep) is the transport's own job and stays. The pin caught
        // itself on that on its first run, which is the reason to run a new pin
        // against the real tree before trusting it to catch anything.
        var jobs = new Regex(
            @"MemoryMarshal|CmdFrameHeader|CmdFrameAbort|CmdGetTouch|"
            + @"TouchReportSize|FrameHeaderDataSize|TrackTransferBegin|TrackTransferEnd|"
            + @"BuildFrameHeader|_inFlightTransfers|_frameHeader|_touchBuffer");

        var hits = RepoScan.ScanSrc(jobs)
            .Where(hit => hit.StartsWith("ModernWigiDash.Hardware/Transport/DisplayHidTransport.cs:", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(0, hits.Count,
            "the transport owns the connect order, the failure fold, the init gate and the standby ritual - not the frame or touch wire. Found at: "
            + string.Join("; ", hits.Select(hit => hit.Split(':', 3)[0].Replace('\\', '/') + ":" + hit.Split(':', 3)[1]))
            + ". Frame framing belongs to DisplayFrameSender, the touch parse and its short-transfer rule to DisplayTouchReader, and the backend plus the tracked transfers to DisplayWire.");
    }

    [TestMethod]
    public void HouseRules_WeatherWidget_HoldsNoPaintOfItsOwn()
    {
        // CONTEXT.md describes WeatherWidgetRenderer as "the widget's draw paths".
        // That was FALSE: the widget drew the header (title, unit badge, subtitle,
        // staleness line) with four hoisted SKPaints of its own while the renderer
        // drew the five content modes with thirteen. So adding one paint to this
        // screen meant discovering that the hoisting discipline was split across
        // two owners, and a paint could be declared in the widget and missed by
        // the renderer's Dispose (found by the architecture review, 2026-09-27).
        //
        // One screen, one draw owner: every SKPaint on the weather screen is the
        // renderer's, released by its one Dispose call.
        // Stripped mode, so the pin counts CODE: the widget's own comment names
        // SKPaint when it explains that the paints moved to the renderer, and a
        // raw scan flagged that sentence as a violation. (The first shape of this
        // pin used raw and failed on its own documentation.)
        var hits = RepoScan.ScanSrc(new Regex(@"SKPaint|\.IsAntialias\s*=|\.Style\s*="))
            .Where(hit => hit.StartsWith("ModernWigiDash.Widgets/WeatherForecastWidget.cs:", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(0, hits.Count,
            "the weather widget must hold no SKPaint: the header joined the renderer, so the paints and their disposal live in one place. Found at: "
            + string.Join("; ", hits.Select(hit => hit.Split(':', 3)[0].Replace('\\', '/') + ":" + hit.Split(':', 3)[1]))
            + ". A paint declared here is native memory the renderer's Dispose cannot see, and the glossary's 'the widget's draw paths' claim stays false.");
    }

    [TestMethod]
    public void HouseRules_CalendarDetailGeometry_IsMeasuredOnceInOnePlace()
    {
        // The detail view's geometry used to be computed INSIDE the draw path,
        // which handed three of the numbers back to the widget for the touch
        // path. That made display and input two consumers of a geometry produced
        // as a side effect of drawing, and it is why a touch arriving before the
        // first detail draw found no card rect, no link target and no scroll
        // extent (the architecture review's calendar candidate, 2026-09-27).
        //
        // The rule now: CalendarLayout owns the arithmetic, the renderer's
        // canvas-free MeasureDetail is the ONE production caller, and the frame
        // it produces is what both the draw and the touch path read. A second
        // production caller means either the draw path re-derives the geometry
        // (it can then disagree with what it draws) or the widget measures a
        // second time and trusts the two to agree - both are the defect.
        var sites = RepoScan.ScanSrc(new Regex(@"ComputeDetail\("), raw: true)
            .Select(hit => hit.Split(':', 3)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var expected = new[]
        {
            "ModernWigiDash.Widgets/CalendarLayout.cs",            // the owner
            "ModernWigiDash.Widgets/CalendarWidgetRenderer.cs",     // the one measure pass
        };

        CollectionAssert.AreEqual(expected, sites,
            "the calendar detail geometry must be computed in exactly two files: the layout that owns it and the renderer's canvas-free measure. Found: "
            + string.Join("; ", sites)
            + ". A third production caller means a second measurement somewhere - the draw path re-deriving the geometry it was handed, or the widget measuring a frame the renderer will measure again. Both let the drawn layout and the hit-tested one drift apart, which is the whole defect this moved to fix.");
    }

    [TestMethod]
    public void HouseRules_WidgetDraw_OwnedByTheDrawOwnerNotInline()
    {
        // The one-draw-owner rule (CONTEXT.md): every widget delegates its draw
        // to exactly one owned draw-owner field (a *Canvas or *Renderer), so a
        // screen's paints and their disposal live in one place. A widget
        // regrowing an inline canvas.Draw re-splits the draw across two owners
        // (the paint is declared in the widget, missed by the draw-owner's
        // Dispose, and the glossary's "the widget's draw paths" claim goes false).
        //
        // The allow-list was the not-yet-migrated set (widgets that still drew
        // inline, with no draw-owner field); it is now empty, so the pin is a
        // pure forward ratchet: any widget with an inline canvas.Draw fails the
        // gate. A new entry is a deliberate edit with a reason.
        var hits = RepoScan.ScanSrc(new Regex(@"canvas\.Draw\w*\s*\("))
            .Where(hit => hit.Contains("Widget.cs:", StringComparison.Ordinal))
            .Select(hit => hit.Split(':', 2)[0].Replace('\\', '/'))
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var allowed = Array.Empty<string>();

        var offenders = hits.Where(hit => !allowed.Contains(hit)).ToList();
        Assert.AreEqual(0, offenders.Count,
            "a widget regrew an inline canvas.Draw: the one-draw-owner rule says every widget delegates its draw to one owned draw-owner field. Found at: "
            + string.Join("; ", offenders)
            + ". Migrate the draw to the widget's draw-owner (or add one); the allow-list is the not-yet-migrated set, and a new entry is a deliberate edit with a reason.");
    }

    [TestMethod]
    public void HouseRules_NamedMapAdapters_ShareTheOneAcquirePolicy()
    {
        // "Bounded, mutex-guarded copy of a producer-owned named map" is one
        // glossary name with one acquire budget, and it had two interfaces, two
        // private 100 ms constants, two abandoned-mutex catches, two release
        // catches and two diagnostics - the diagnostics already disagreeing in
        // spelling ("100ms" vs "100 ms") and each retyping a number its own
        // constant already held (found by the architecture review, 2026-09-27).
        // The two adapters stay: two producers, two real shapes, and one genuine
        // disagreement (what an unusable handle means) that each keeps on purpose.
        var adapters = new[]
        {
            "ModernWigiDash.Hardware/Aida64/MemoryMappedAidaMmapSource.cs",
            "ModernWigiDash.App/LibreHardwareService/MemoryMappedLhmMapSource.cs",
        };
        var retyped = new Regex(@"MutexTimeout|FromMilliseconds\(100\)|AbandonedMutexException|ReleaseMutex|not acquired within");
        var offenders = new List<string>();
        foreach (var adapter in adapters)
        {
            offenders.AddRange(RepoScan.ScanSrc(retyped)
                .Where(hit => hit.StartsWith(adapter + ":", StringComparison.Ordinal))
                .Select(hit => hit.Split(':', 3)[0].Replace('\\', '/') + ":" + hit.Split(':', 3)[1]));
        }

        Assert.AreEqual(0, offenders.Count,
            "a named-map adapter must take its acquire budget, its abandoned-mutex rule, its release and its diagnostic from NamedMapAcquirePolicy, not spell them again. Found at: "
            + string.Join("; ", offenders)
            + ". Two producers, two real shapes - but the budget is one decision. A second spelling is how the two diagnostics drifted into disagreeing about the same 100 ms.");
    }

    [TestMethod]
    public void HouseRules_NetworkBudget_IsTheOnlySpellingOfA30SecondWait()
    {
        // The network wait was spelled 30 s in five places across four files
        // (the weather client, the geocoder, both calendar legs, the Twitch API
        // client) with nothing tying them together: tightening the weather
        // client's header phase left the geocoder reading the body for another
        // half minute (found by the architecture review, 2026-09-27). It is now
        // one constant, NetworkBudget.Wait.
        // Stripped mode (the default), so this counts CODE references: a comment
        // that mentions the constant, or a doc <see cref>, is prose about the
        // owner, not a leg budgeting from it.
        var actual = RepoScan.ScanSrc(new Regex(@"FromSeconds\(30\)"))
            .GroupBy(hit => hit.Split(':', 3)[0].Replace('\\', '/'))
            .ToDictionary(g => g.Key, g => g.Count());
        var expected = ThirtySecondWaitSites
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        CollectionAssert.AreEquivalent(
            expected,
            actual,
            "the 30 s waits in src must match the allow-list exactly. Allowed today:"
            + Environment.NewLine + string.Join(Environment.NewLine,
                ThirtySecondWaitSites.OrderBy(kv => kv.Key)
                    .Select(kv => $"  {kv.Key} x{kv.Value.Count} - {kv.Value.Reason}"))
            + Environment.NewLine + "A new outbound leg that budgets its own 30 s is the drift this pin exists to stop: give it NetworkBudget.Wait, or, if the wait genuinely is not a network wait, add it here with its reason. A retired site is a deliberate allow-list edit, not a silent shrink."
            + Environment.NewLine + $"Found: {(actual.Count == 0 ? "(none)" : string.Join(", ", actual.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}")))}");
    }

    [TestMethod]
    public void HouseRules_NetworkLegs_BudgetFromTheSharedConstant()
    {
        // The other half of the same rule, and the half that actually proves the
        // consolidation: a stray-30s scan alone would still be green if every leg
        // stopped budgeting from the owner and went back to its own literal
        // spelled in another unit. Every network leg must name the constant.
        var expected = new Dictionary<string, int>
        {
            ["ModernWigiDash.Widgets/WeatherClient.cs"] = 1,
            ["ModernWigiDash.Widgets/WeatherGeocoder.cs"] = 2, // the body-phase deadline, in CancelAfter and in the reported message
            ["ModernWigiDash.Widgets/CalDavFetcher.cs"] = 1,
            ["ModernWigiDash.Widgets/IcsUrlFetcher.cs"] = 1,
            ["ModernWigiDash.Widgets/Twitch/TwitchApiClient.cs"] = 1,
            ["ModernWigiDash.Widgets/Steam/Services/SteamWebApiClient.cs"] = 1, // the Steam cluster's shared client Timeout (the one-wait shape)
        };
        // Stripped mode, so a doc <see cref="NetworkBudget.Wait"/> or a comment
        // naming the constant does not count as a leg budgeting from it.
        var actual = RepoScan.ScanSrc(new Regex(@"NetworkBudget\.Wait"))
            .GroupBy(hit => hit.Split(':', 3)[0].Replace('\\', '/'))
            .ToDictionary(g => g.Key, g => g.Count());

        CollectionAssert.AreEquivalent(
            expected,
            actual,
            "every network leg must take its wait from NetworkBudget.Wait (counted per file, so a line move is not a failure). Found: "
            + (actual.Count == 0 ? "(none)" : string.Join(", ", actual.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}")))
            + ". A site count that drops means a leg stopped naming the owner - either restore the reference or delete the leg.");
    }

    [TestMethod]
    public void HouseRules_PriceFeeds_DoNotReconnectAtAFlatRate()
    {
        // The price feeds rode FixedReconnectPolicy(5s) forever, while the display
        // engine's reconnect grew to 2 minutes and the Twitch loop's to 30 -
        // three loops, one failure mode, three answers, and nothing in the code,
        // the glossary or a test saying the flat one was deliberate (found by the
        // architecture review, 2026-09-27). A closed or rate-limiting exchange was
        // re-dialled every 5 seconds indefinitely, which is exactly the "absent
        // target re-probes forever at full rate" case the backoff rule exists to
        // stop. The first retry is still 5 s, so this is not a latency change;
        // it only makes the SECOND and later retries back off.
        var hits = RepoScan.ScanSrc(
            new Regex(@"FixedReconnectPolicy"), raw: true)
            .Where(hit => hit.StartsWith("ModernWigiDash.Widgets/PriceFeedManager.cs", StringComparison.Ordinal))
            .ToList();
        Assert.AreEqual(0, hits.Count,
            "the price feeds must hand their loops a backoff policy, not a constant delay: a flat rate re-probes a dead exchange at full rate forever, and it silently disagreed with the display engine and the Twitch loop about the same failure mode. Found at: "
            + string.Join("; ", hits)
            + ". Build one with ExponentialBackoffReconnectPolicy(first, MaxReconnectDelay) - it computes through the same ReconnectBackoff core the display uses. A constant delay remains legitimate for a caller that genuinely wants one; it was the production wiring of the price feeds that was wrong, not the type.");
    }

    [TestMethod]
    public void FindMainWindowCtors_InjectedViolations_StayVisibleToTheRule()
    {
        // Negative verification (the house shape): the extractor the window
        // constructor pin runs must keep seeing an injected bare
        // construction - a scan that loses it lets a real-engine test host
        // through the pin.
        var snippet = RepoScan.StripCode("""
            var bare = new MainWindow(new MainWindowTestOptions(pm, path, power, tray));
            var inert = new MainWindow(new MainWindowTestOptions(pm, path, power, tray, UsbEngine: FakeTransport.InertEngine()));
            var explicitEngine = new MainWindow(new MainWindowTestOptions(pm, path, power, tray, UsbEngine: new DisplayDeviceEngine(fake, ConnectionState.Connected)));
            // var commented = new MainWindow(new MainWindowTestOptions(pm, path, power, tray)); must stay invisible.
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

    // --- the color parse has one owner ---

    /// <summary>
    /// Every raw <c>SKColor.TryParse</c> in src, with its reason. The one owner
    /// is <c>SkColorCache</c> in the Sdk: a process-wide cache keyed by the hex
    /// string, so the per-frame parse sites (the widget base, the canvas draw
    /// owners, the seasonal palette, the compositor) run at most once per
    /// distinct value. A new render site routes through <c>SkColorCache</c>
    /// (or the widget base's <c>ColorOf</c> over it); a raw parse is a
    /// deliberate allow-list edit with a reason, and a retired site is a
    /// deliberate allow-list edit too.
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Reason)> ColorParseSites = new()
    {
        ["ModernWigiDash.Sdk/SkColorCache.cs"] = (1, "the one owner - the process-wide hex-to-SKColor cache"),
        ["ModernWigiDash.Widgets/Twitch/TwitchChatConnection.cs"] = (1, "the per-IRC-message user colors are unbounded; the process-wide cache would grow without bound, so this site parses raw"),
    };

    [TestMethod]
    public void HouseRules_ColorParsing_RoutesThroughTheOneOwner()
    {
        // The canvas extraction (rounds 1-7) re-spelled an uncached static
        // ColorOf in six canvas modules and left raw per-tick parses in the
        // seasonal palette and the compositor: the base class's cache doc said
        // the fallback rule was "declared once", but the move was mechanical
        // and the discipline did not travel (architecture review, round 8,
        // 2026-10-25). The fix was the one Sdk owner, SkColorCache, and every
        // site routed through it. Stripped mode (the default), so this counts
        // CODE references: a comment that mentions the parse is prose about
        // the owner, not a site bypassing it.
        var actual = RepoScan.ScanSrc(new Regex(@"SKColor\.TryParse"))
            .GroupBy(hit => hit.Split(':', 3)[0].Replace('\\', '/'))
            .ToDictionary(g => g.Key, g => g.Count());
        var expected = ColorParseSites
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        CollectionAssert.AreEquivalent(
            expected,
            actual,
            "the raw color parses in src must match the allow-list exactly. Allowed today:"
            + Environment.NewLine + string.Join(Environment.NewLine,
                ColorParseSites.OrderBy(kv => kv.Key)
                    .Select(kv => $"  {kv.Key} x{kv.Value.Count} - {kv.Value.Reason}"))
            + Environment.NewLine + "A new render site that parses a hex is the drift this pin exists to stop: route it through SkColorCache (or the widget base's ColorOf over it), or, if the value domain genuinely is unbounded, add it here with its reason. A retired site is a deliberate allow-list edit, not a silent shrink."
            + Environment.NewLine + $"Found: {(actual.Count == 0 ? "(none)" : string.Join(", ", actual.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}")))}");
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
