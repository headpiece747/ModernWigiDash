namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The loaded PresentMon query surface: the six native function pointers the
/// query subsystem needs (register/free/poll dynamic, register/consume/free
/// frame) as one non-null capability. A half-loaded state is unrepresentable:
/// either every pointer resolved or the probe reports a failure reason. The
/// production adapter resolves the pointers from the loaded library; tests
/// bind in-memory fakes through the same seam.
/// </summary>
internal sealed class PresentMonQueryCapability
{
    private readonly PmRegisterDynamicQuery _registerDynamic;
    private readonly PmFreeDynamicQuery _freeDynamic;
    private readonly PmPollDynamicQuery _pollDynamic;
    private readonly PmRegisterFrameQuery _registerFrame;
    private readonly PmConsumeFrames _consumeFrames;
    private readonly PmFreeFrameQuery _freeFrame;

    public PresentMonQueryCapability(
        PmRegisterDynamicQuery registerDynamic,
        PmFreeDynamicQuery freeDynamic,
        PmPollDynamicQuery pollDynamic,
        PmRegisterFrameQuery registerFrame,
        PmConsumeFrames consumeFrames,
        PmFreeFrameQuery freeFrame)
    {
        _registerDynamic = registerDynamic;
        _freeDynamic = freeDynamic;
        _pollDynamic = pollDynamic;
        _registerFrame = registerFrame;
        _consumeFrames = consumeFrames;
        _freeFrame = freeFrame;
    }

    public PmStatus RegisterDynamic(IntPtr session, out IntPtr handle, PresentMonQueryElement[] elements, ulong count, double windowMs, double offsetMs)
        => _registerDynamic(session, out handle, elements, count, windowMs, offsetMs);

    public PmStatus FreeDynamic(IntPtr handle)
        => _freeDynamic(handle);

    public PmStatus PollDynamic(IntPtr handle, uint processId, byte[] blob, ref uint numSwapChains)
        => _pollDynamic(handle, processId, blob, ref numSwapChains);

    public PmStatus RegisterFrame(IntPtr session, out IntPtr handle, PresentMonQueryElement[] elements, ulong count, out uint blobSize)
        => _registerFrame(session, out handle, elements, count, out blobSize);

    public PmStatus ConsumeFrames(IntPtr handle, uint processId, byte[] blobs, ref uint framesToRead)
        => _consumeFrames(handle, processId, blobs, ref framesToRead);

    public PmStatus FreeFrame(IntPtr handle)
        => _freeFrame(handle);
}
