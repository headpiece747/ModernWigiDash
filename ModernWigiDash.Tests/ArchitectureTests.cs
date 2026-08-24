using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The executable architecture pins: the layering, the ADR-0001 synchronous
/// seam, the widget conventions, and the house rules live in CONTEXT.md, the
/// ADRs, and .opencode/rules/dotnet-rules.md. This class turns them into tests
/// so a violation fails the gate (run-gates.ps1) instead of waiting for a
/// manual arch-check pass. Each failure message spells the rule, the
/// violation, and the fix. The raw-scan mechanics live in RepoScan, shared
/// with the debt guardrails (DebtGuardTests).
/// </summary>
[TestClass]
public sealed class ArchitectureTests
{
    private static readonly string[] AllProjects =
    [
        "ModernWigiDash.Sdk",
        "ModernWigiDash.Core",
        "ModernWigiDash.Hardware",
        "ModernWigiDash.Widgets",
        "ModernWigiDash.App",
        "ModernWigiDash.Tests",
    ];

    [TestMethod]
    public void ProjectReferences_OnlyTheDocumentedLayeringEdges_Hold()
    {
        // CONTEXT.md "Architecture Overview": dependency direction is inward,
        // Sdk is the lowest layer, App the top. This allowlist is that rule.
        var root = RepoScan.GetRepoRoot();
        var expected = new Dictionary<string, SortedSet<string>>
        {
            ["ModernWigiDash.Sdk"] = [],
            ["ModernWigiDash.Core"] = ["ModernWigiDash.Sdk"],
            ["ModernWigiDash.Hardware"] = ["ModernWigiDash.Sdk"],
            ["ModernWigiDash.Widgets"] = ["ModernWigiDash.Core", "ModernWigiDash.Sdk"],
            ["ModernWigiDash.App"] = ["ModernWigiDash.Core", "ModernWigiDash.Hardware", "ModernWigiDash.Sdk", "ModernWigiDash.Widgets"],
            ["ModernWigiDash.Tests"] = ["ModernWigiDash.App", "ModernWigiDash.Core", "ModernWigiDash.Hardware", "ModernWigiDash.Sdk", "ModernWigiDash.Widgets"],
        };

        var actual = new Dictionary<string, SortedSet<string>>();
        foreach (var project in AllProjects)
        {
            var csproj = Path.Combine(root, project, project + ".csproj");
            Assert.IsTrue(File.Exists(csproj),
                $"the allowlist names {project} but {project}.csproj is missing - create the project with its csproj, or the allowlist is stale and must be updated with CONTEXT.md.");

            var doc = XDocument.Load(csproj);
            var edges = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(n => n is not null)
                .ToHashSet();
            actual[project] = new SortedSet<string>(edges);
        }

        var unexpected = new List<string>();
        var missing = new List<string>();
        foreach (var (project, want) in expected)
        {
            foreach (var edge in actual[project].Except(want))
                unexpected.Add($"{project} -> {edge}");
            foreach (var edge in want.Except(actual[project]))
                missing.Add($"{project} -> {edge}");
        }

        Assert.AreEqual(0, unexpected.Count,
            "unexpected project reference(s) - dependency direction is inward (Sdk lowest, App top, CONTEXT.md Architecture Overview): "
            + string.Join("; ", unexpected)
            + ". Remove the edge (or move the consuming type into a layer that may own it); if the layering itself changed, update this allowlist and CONTEXT.md in the same commit.");
        Assert.AreEqual(0, missing.Count,
            "missing project reference(s): " + string.Join("; ", missing)
            + ". Add the reference, or the allowlist is stale and must be updated with CONTEXT.md.");

        // The slnx names exactly the allowlist's projects: no orphan project,
        // no forgotten one.
        var slnx = XDocument.Load(Path.Combine(root, "ModernWigiDash.slnx"));
        var slnxProjects = slnx.Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Path")?.Value ?? ""))
            .ToHashSet();
        Assert.IsTrue(slnxProjects.SetEquals(AllProjects),
            "the slnx project set diverges from the layering allowlist (slnx has: "
            + string.Join(", ", slnxProjects.OrderBy(p => p))
            + "). Add or remove the project in the slnx and update this allowlist together.");
    }

    [TestMethod]
    public void TransportSeam_Adr0001_SynchronousByConstruction()
    {
        // ADR-0001 (docs/adr/0001-synchronous-transport-interface.md): the USB
        // transfer seam is synchronous. Blocking I/O wrapped in async is fake
        // async and forces sync-over-async bridges at the callers. The seam
        // carries no Task/ValueTask member at all — not even a lifetime
        // DisposeAsync (an async lifetime over a sync teardown is a bridge
        // with nothing behind it).
        var seamTypes = new[]
        {
            typeof(IDisplayTransport),
            typeof(ITransferBackend),
            typeof(DisplayHidTransport),
            typeof(WinUsbBulkDevice),
            typeof(LibUsbTransferBackend),
        };

        var violations = new List<string>();
        foreach (var type in seamTypes)
        {
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                // This toolchain's MethodBody has no IsAsync, so async is
                // detected by the two shapes an async method can take: a
                // Task-like return (the ADR-0001 rule's target) or the
                // compiler's AsyncMethodBuilderAttribute (the async void
                // shape).
                if (IsTaskLike(method.ReturnType))
                    violations.Add($"{type.Name}.{method.Name}() returns {method.ReturnType.Name}");
                else if (HasAsyncMethodBuilder(method))
                    violations.Add($"{type.Name}.{method.Name}() is async void");
            }

            var properties = type.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var property in properties)
            {
                if (IsTaskLike(property.PropertyType))
                    violations.Add($"{type.Name}.{property.Name} returns {property.PropertyType.Name}");
            }
        }

        Assert.AreEqual(0, violations.Count,
            "fake async on the synchronous transport seam (ADR-0001): " + string.Join("; ", violations)
            + ". Make the member blocking - it wraps blocking USB I/O. The seam carries no Task/ValueTask member at all, lifetime included.");
    }

    [TestMethod]
    public void Widgets_OneWidgetPerFile_AndCatalogIdsAreUnique()
    {
        // CONTEXT.md "Key Design Decisions": widget-per-file convention. Each
        // widget class lives in its own .cs with [WidgetMetadata]; the catalog
        // is discovered by reflection.
        var root = RepoScan.GetRepoRoot();
        var assembly = typeof(DisplayFormat).Assembly;

        // The reflection truth: every widget class carries [WidgetMetadata]
        // (Inherited = false, so a base class's attribute never counts).
        var widgets = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<WidgetMetadataAttribute>() is not null)
            .ToList();

        // The source truth: where the attributes actually sit.
        var widgetsDir = Path.Combine(root, "ModernWigiDash.Widgets");
        var perFile = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(widgetsDir, "*.cs", SearchOption.AllDirectories))
        {
            var count = 0;
            foreach (var line in File.ReadLines(file))
                if (line.Contains("[WidgetMetadata"))
                    count++;
            if (count > 0)
                perFile[file] = count;
        }

        var crowded = perFile.Where(kv => kv.Value > 1)
            .Select(kv => $"{Path.GetRelativePath(root, kv.Key)} holds {kv.Value} widgets");
        Assert.AreEqual(0, crowded.Count(),
            "one widget class per file (CONTEXT.md widget-per-file convention): " + string.Join("; ", crowded)
            + ". Split each widget into its own .cs file named after the class.");

        Assert.AreEqual(widgets.Count, perFile.Values.Sum(),
            "the [WidgetMetadata] source count and the reflection widget count disagree - a widget file lost its attribute (it silently leaves the catalog) or the attribute was added to a non-widget type. Fix the drift; the catalog is discovered by reflection.");

        var ids = widgets.Select(t => t.GetCustomAttribute<WidgetMetadataAttribute>()!.Id).ToList();
        var dupes = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.AreEqual(0, dupes.Count(),
            "duplicate [WidgetMetadata] Id(s): " + string.Join(", ", dupes)
            + ". The Id is the persisted widget-type key (profiles round-trip on it); duplicating it breaks existing profiles.");
    }

    [TestMethod]
    public void SdkContracts_OneContractPerFile_Holds()
    {
        // CONTEXT.md "Key Design Decisions": one type per file holds for the Sdk
        // contracts (split out of the former single IModernWidget.cs); a new
        // contract must keep that shape.
        var sdkDir = Path.Combine(RepoScan.GetRepoRoot(), "ModernWigiDash.Sdk");
        var contractFiles = new[]
        {
            "IModernWidget.cs",
            "IModernWigiDashContext.cs",
            "ModernWidgetBase.cs",
            "IWidgetActionInvoker.cs",
        };
        // The declaration is anchored at column zero on purpose: the pin is
        // about top-level file contracts, and a nested (indented) type inside
        // a contract file must not count as a second declaration.
        var typeDecl = new Regex(
            @"^(?:(?:public|internal|sealed|static|abstract|readonly|partial)\s+)*(?:class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline | RegexOptions.ExplicitCapture);

        var violations = new List<string>();
        foreach (var file in contractFiles)
        {
            var path = Path.Combine(sdkDir, file);
            if (!File.Exists(path))
            {
                violations.Add($"{file} is missing - the Sdk contract split (one contract per file) must not collapse back into a grab-bag file");
                continue;
            }

            var decls = typeDecl.Matches(File.ReadAllText(path)).Select(m => m.Groups["name"].Value).ToList();
            if (decls.Count != 1 || decls[0] != Path.GetFileNameWithoutExtension(file))
                violations.Add($"{file} declares {string.Join(", ", decls)} - expected exactly one top-level type named {Path.GetFileNameWithoutExtension(file)}");
        }

        Assert.AreEqual(0, violations.Count,
            "broken Sdk contract file split: " + string.Join("; ", violations)
            + ". Each contract owns one file named after the type (CONTEXT.md Key Design Decisions).");
    }

    [TestMethod]
    public void HouseRules_NoAmbientClockInSrc()
    {
        var violations = RepoScan.ScanSrc(new Regex(@"\bDateTime\.(?:Now|UtcNow)\b"));
        Assert.AreEqual(0, violations.Count,
            "ambient clock in src: " + string.Join("; ", violations)
            + ". dotnet-rules 5: TimeProvider over DateTime.Now/UtcNow where testability matters; inject the clock through the existing seam (the producer's timestamp, the widget's Clock property). Tests may use the ambient clock for fixtures.");
    }

    [TestMethod]
    public void HouseRules_NoAdHocHttpClientInSrc()
    {
        // The only allowed HttpClient constructions in src are the named
        // static long-lived clients: the price feeds' shared process-wide
        // client and the three singleton service clients (the updater, the
        // weather fetcher, the Twitch API client). Ad-hoc construction
        // (per-request, per-method) is the socket-exhaustion trap this pin
        // catches; both the explicit form (new HttpClient()) and the
        // target-typed form (HttpClient f = new()) must match, so a new
        // construction cannot hide behind either style.
        var allowed = new[]
        {
            "ModernWigiDash.App/Update/UpdateService.cs",
            "ModernWigiDash.Widgets/PriceFeedManager.cs",
            "ModernWigiDash.Widgets/Twitch/TwitchApiClient.cs",
            "ModernWigiDash.Widgets/WeatherClient.cs",
        };
        var hits = RepoScan.ScanSrc(new Regex(@"new\s+HttpClient\s*\("))
            .Concat(RepoScan.ScanSrc(new Regex(@"\bHttpClient\b[^;=\n]*=\s*new\s*\(")))
            .Select(v => v.Split(':', 2)[0].Replace('\\', '/'))
            .ToList();
        var disallowed = hits.Where(f => !allowed.Contains(f)).Distinct().ToList();
        Assert.AreEqual(0, disallowed.Count,
            "ad-hoc HttpClient construction: " + string.Join("; ", disallowed)
            + ". dotnet-rules 5: the only allowed HttpClient constructions are the named static long-lived clients in the allow-list above; every other HTTP consumer receives the client through an injected seam (tests inject a stub handler).");
        var drift = hits.Distinct().Except(allowed).Concat(allowed.Except(hits.Distinct())).ToList();
        Assert.AreEqual(0, drift.Count,
            "HttpClient allow-list drift: " + string.Join("; ", drift.OrderBy(f => f))
            + ". Each allowed site must construct exactly one long-lived client and no other file may; a new construction site or a retired one is a deliberate allow-list edit (dotnet-rules 5).");
    }

    [TestMethod]
    public void HouseRules_NoEmptyCatchInSrc()
    {
        var violations = RepoScan.ScanSrc(new Regex(@"catch\s*(?:\([^)]*\))?\s*(?:when\s*\([^)]*\))?\s*\{\s*\}"), raw: true);
        Assert.AreEqual(0, violations.Count,
            "empty catch block: " + string.Join("; ", violations)
            + ". dotnet-rules 6: expected failures should be explicit and an empty catch swallows the verdict; log through the injected log seam (or state in a comment why the skip is safe - a comment makes the catch non-empty to this pin) - never a silent block.");
    }

    [TestMethod]
    public void DisplayGeometry_PinsTheHardwarePixelArea()
    {
        // The WigiDash active pixel area is a vendor hardware fact (1016x592,
        // RGB565). Every layer aliases these constants (Hardware's
        // DisplayProtocolConstants, the compositor's buffer); if the hardware
        // ever changes, this pin is the place that makes the change visible.
        // The expected values are read through reflection on purpose: the
        // constants are compile-time, so a direct comparison folds to a
        // constant condition (CS0162 unreachable, or an always-true assert
        // the analyzers call a no-op, MSTEST0032). Reading the value at
        // runtime keeps the pin live: when a constant changes, this test
        // fails at its new value.
        var violations = new List<string>();
        if (ConstValue(nameof(DisplayGeometry.FramebufferWidth)) != 1016)
            violations.Add($"FramebufferWidth is {DisplayGeometry.FramebufferWidth}, expected 1016 - verify against the display and update every alias (DisplayProtocolConstants, the compositor buffer) in the same commit.");
        if (ConstValue(nameof(DisplayGeometry.FramebufferHeight)) != 592)
            violations.Add($"FramebufferHeight is {DisplayGeometry.FramebufferHeight}, expected 592 - verify against the display and update every alias in the same commit.");
        if (ConstValue(nameof(DisplayGeometry.BytesPerPixel)) != 2)
            violations.Add($"BytesPerPixel is {DisplayGeometry.BytesPerPixel}, expected 2 - the display is RGB565; verify against the hardware before changing this.");
        if (ConstValue(nameof(DisplayGeometry.FrameBufferSize)) != DisplayGeometry.FramebufferWidth * DisplayGeometry.FramebufferHeight * DisplayGeometry.BytesPerPixel)
            violations.Add("FrameBufferSize disagrees with width*height*bytesPerPixel - the payload size must stay the product of the other three constants.");
        Assert.AreEqual(0, violations.Count,
            "the WigiDash hardware pixel area pin failed: " + string.Join("; ", violations));
    }

    /// <summary>
    /// Reads a public static int constant at runtime (the anti-constant-fold
    /// trick the DisplayGeometry pin needs).
    /// </summary>
    private static int ConstValue(string name) =>
        (int)typeof(DisplayGeometry).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    // --- shared helpers ---

    private static bool IsTaskLike(Type type) =>
        type == typeof(Task)
        || type == typeof(ValueTask)
        || type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    /// <summary>
    /// The compiler stamps AsyncMethodBuilderAttribute on async methods; it
    /// is the toolchain-portable stand-in for MethodBody.IsAsync (absent from
    /// this machine's BCL surface).
    /// </summary>
    private static bool HasAsyncMethodBuilder(MethodInfo method) =>
        method.GetCustomAttributesData()
            .Any(attr => attr.AttributeType.Name == "AsyncMethodBuilderAttribute");
}
