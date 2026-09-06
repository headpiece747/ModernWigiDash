using System.IO;
using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// One-shot load of the PresentMon API v3 interop surface. Owns the
/// unavailable-reason policy: a missing library, a missing export, or a
/// non-v3 API generation (checked via <c>pmGetApiVersion</c>) each map to the
/// reason string the producer surfaces. Extracted from the native wrapper so
/// those load-failure branches are plain unit tests against a fake loader
/// (ADR-0003) instead of an STA/WPF harness.
/// </summary>
internal sealed class PresentMonApiProbe
{
    private const string NotFoundReason =
        "PresentMonAPI2.dll not found. Install the PresentMon Service (C:\\Program Files\\Intel\\PresentMonSharedService).";
    private const string MissingExportsReason =
        "PresentMonAPI2.dll is missing required exports (incompatible version).";

    public IntPtr? Library { get; }
    public string? FailureReason { get; }

    public PmOpenSession? OpenSessionFn { get; }
    public PmCloseSession? CloseSessionFn { get; }
    public PmStartTrackingProcess? StartTrackingFn { get; }
    public PmStopTrackingProcess? StopTrackingFn { get; }
    public PresentMonQueryCapability? QueryCapability { get; }
    public PmGetApiVersion? GetApiVersionFn { get; }
    public PmGetIntrospectionRoot? GetIntrospectionRootFn { get; }
    public PmFreeIntrospectionRoot? FreeIntrospectionRootFn { get; }

    public PresentMonApiProbe(IPresentMonLibraryLoader loader)
    {
        if (loader.LoadLibrary(PresentMonLibraryCandidates(), out string? loadFailure) is not { } lib)
        {
            FailureReason = loadFailure ?? NotFoundReason;
            return;
        }

        Library = lib;
        OpenSessionFn = Resolve<PmOpenSession>(loader, lib, "pmOpenSession");
        CloseSessionFn = Resolve<PmCloseSession>(loader, lib, "pmCloseSession");
        StartTrackingFn = Resolve<PmStartTrackingProcess>(loader, lib, "pmStartTrackingProcess");
        StopTrackingFn = Resolve<PmStopTrackingProcess>(loader, lib, "pmStopTrackingProcess");
        GetApiVersionFn = Resolve<PmGetApiVersion>(loader, lib, "pmGetApiVersion");
        GetIntrospectionRootFn = Resolve<PmGetIntrospectionRoot>(loader, lib, "pmGetIntrospectionRoot");
        FreeIntrospectionRootFn = Resolve<PmFreeIntrospectionRoot>(loader, lib, "pmFreeIntrospectionRoot");

        // The query surface is one non-null capability: either every pointer
        // resolved or the probe reports a failure reason. A half-loaded state
        // is unrepresentable.
        var registerDynamic = Resolve<PmRegisterDynamicQuery>(loader, lib, "pmRegisterDynamicQuery");
        var freeDynamic = Resolve<PmFreeDynamicQuery>(loader, lib, "pmFreeDynamicQuery");
        var pollDynamic = Resolve<PmPollDynamicQuery>(loader, lib, "pmPollDynamicQuery");
        var registerFrame = Resolve<PmRegisterFrameQuery>(loader, lib, "pmRegisterFrameQuery");
        var consumeFrames = Resolve<PmConsumeFrames>(loader, lib, "pmConsumeFrames");
        var freeFrame = Resolve<PmFreeFrameQuery>(loader, lib, "pmFreeFrameQuery");
        if (registerDynamic is not null && freeDynamic is not null && pollDynamic is not null
            && registerFrame is not null && consumeFrames is not null && freeFrame is not null)
        {
            QueryCapability = new PresentMonQueryCapability(
                registerDynamic, freeDynamic, pollDynamic, registerFrame, consumeFrames, freeFrame);
        }

        bool anyMissing = OpenSessionFn is null || CloseSessionFn is null || StartTrackingFn is null
            || StopTrackingFn is null
            || QueryCapability is null
            || GetApiVersionFn is null || GetIntrospectionRootFn is null || FreeIntrospectionRootFn is null;
        if (anyMissing)
        {
            FailureReason = MissingExportsReason;
            return;
        }

        // The PmStatus enum and PM_QUERY_ELEMENT layout this code targets are
        // v3-shaped; the file version (3.0.3) is the service protocol version —
        // require the API generation, not a patch match. A failed call and a
        // wrong generation are distinct failures: the version cannot be read
        // (call failure) vs a readable, unsupported version.
        if (GetApiVersionFn!(out PmVersion version) != PmStatus.Success)
        {
            FailureReason = "Could not read the PresentMonAPI2.dll API version (pmGetApiVersion failed).";
        }
        else if (version.Major != 3)
        {
            FailureReason = $"PresentMonAPI2.dll version {version.Major}.{version.Minor}.{version.Patch} is not supported (v3.x required).";
        }
    }

    internal static string[] PresentMonLibraryCandidates() =>
    [
        // Shared-service layout used by the MSI since v2.3.1: the client API
        // ships next to PresentMonService.exe.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
        // SDK layout: header + loader live here; some installs also drop the API dll.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
    ];

    private static T? Resolve<T>(IPresentMonLibraryLoader loader, IntPtr library, string name) where T : Delegate
    {
        if (loader.GetExport(library, name) is not { } pointer)
        {
            return null;
        }
        try
        {
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
