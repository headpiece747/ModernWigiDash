// <copyright file="DisplayHidTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using LibUsbDotNet.LibUsb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The WigiDash display transport: the connect loop, the two provider legs, the
/// init sequence, and the standby ritual, composed over a
/// <see cref="DisplayWire"/> that owns the live backend and the frame and touch
/// wires built on it.
///
/// This file used to hold all four of those plus the transfer plumbing, 671
/// lines, so answering "what happens when the display is busy?" meant reading the
/// loop, both leg bodies, the adapters' TryOpen and the fold — and the reason the
/// answer was unreachable showed up in a fifth place (the architecture review's
/// transport candidate, 2026-09-27). The jobs that were not the connect loop
/// moved to the module that owns them:
///
/// <list type="bullet">
/// <item><see cref="DisplayWire"/> — the backend, the tracked control/bulk
/// transfers, and the drain that keeps a native handle from being freed
/// mid-transfer. Every path's transfers go through it.</item>
/// <item><see cref="DisplayFrameSender"/> — the frame path's wire: the reused
/// header buffer, the header/bulk/abort bracketing, and the 30 FPS zero-alloc
/// fast path.</item>
/// <item><see cref="DisplayTouchReader"/> — the touch path's wire: the control
/// IN, the short-transfer rule, and the vendor report's parse.</item>
/// </list>
///
/// What stays here is what is genuinely the transport's: the ORDER the two legs
/// are tried in, the fold that decides which failure is worth reporting, and the
/// init gate between them. The legs themselves own their acquisition
/// (<see cref="WinUsbBulkDevice"/>, <see cref="LibUsbTransferBackend"/>), so no
/// driver-stack ownership lives here — which is the claim the glossary makes and
/// the one this file can now actually be true about.
/// </summary>
internal sealed class DisplayHidTransport : IDisplayTransport
{
    private readonly ILogger<DisplayHidTransport> _logger;

    /// <summary>The live backend and the transfers over it. Composition root
    /// wiring only — the wire owns its own state.</summary>
    private readonly DisplayWire _wire = new();

    private readonly DisplayFrameSender _frameSender;
    private readonly DisplayTouchReader _touchReader;

    private int _isDisposed;

    // Standby is the one-shot line of the shutdown path — always fires.
    private readonly DiagLog _standbyLog = new("STANDBY", 1);

    /// <summary>
    /// The transport's connection truth — kept for test observability only.
    /// It is NOT on the <see cref="IDisplayTransport"/> seam: production
    /// callers (the engine) gate on their own ConnectionState, and the
    /// transport's methods read the wire's flag directly.
    /// </summary>
    internal bool IsConnected => _wire.IsConnected;

    /// <summary>
    /// The worst-case duration a hung device can hold the transport's teardown
    /// — the derivation root of the close-budget policy. The max of the two
    /// backend stall bounds: the WinUSB bulk pipe timeout (an in-flight frame
    /// write holds the teardown drain for up to that long) and the LibUsb
    /// leg's chunk-timeout product over a full frame (every chunk exhausting
    /// its timeout).
    /// </summary>
    internal static TimeSpan CloseBound => TimeSpan.FromMilliseconds(Math.Max(
        DisplayProtocolConstants.BulkPipeTimeoutMs,
        (long)ChunkedBulkWrite.WorstCaseWrite(DisplayGeometry.FrameBufferSize).TotalMilliseconds));

    /// <summary>
    /// The production close-budget policy, derived ONCE from the static
    /// <see cref="CloseBound"/>. It is constant by construction (the pipe
    /// timeouts and the frame size are all constants), so the engine can read the
    /// same value before it holds a transport instead of constructing a
    /// throwaway one to ask it — that fallback allocated a provider list and a
    /// logger on every read, on the reconnect path, inside the module whose
    /// whole job is never stalling on close (2026-09-27).
    /// </summary>
    internal static readonly CloseBudgetPolicy DefaultCloseBudgets = CloseBudgetPolicy.Create(CloseBound);

    /// <summary>
    /// The close-budget policy this transport hands to the engine: the
    /// worst-case teardown bound and the engine's two bounded close waits,
    /// derived together, so the never-stall-on-close relation (waits strictly
    /// shorter than the worst case) holds by construction. The engine reads
    /// these values; it never hard-codes a second spelling. An adapter with a
    /// different bound overrides this; the HID transport is the constant case,
    /// so it is the shared default too.
    /// </summary>
    public CloseBudgetPolicy CloseBudgets => DefaultCloseBudgets;

    public DisplayHidTransport(ILogger<DisplayHidTransport>? logger = null)
    {
        _logger = logger ?? NullLogger<DisplayHidTransport>.Instance;
        _frameSender = new DisplayFrameSender(_wire, _logger);
        _touchReader = new DisplayTouchReader(_wire);
        ProviderFactories = [WinUsbProvider, LibUsbProvider];
    }

    /// <summary>
    /// The provider list <see cref="Connect"/> iterates: WinUSB first, the
    /// LibUsbDotNet fallback second. Test seam: tests replace the list — with
    /// fake WinUSB and/or LibUsb legs — to drive the connect and fallback
    /// policies deterministically.
    /// </summary>
    internal ConnectProvider[] ProviderFactories { get; set; }

    /// <summary>
    /// Test seam: the LibUsb leg's device lookup. Defaults to the shared-context
    /// find (VID/PID); tests inject a fake <see cref="IUsbDevice"/> so the
    /// open/config/claim/endpoint sequence and its teardown are drivable
    /// without hardware (the <see cref="WinUsbApi"/> delegate-bag precedent).
    /// </summary>
    internal Func<IUsbDevice?>? LibUsbDeviceProvider { get; set; }

    /// <summary>The real WinUSB leg. Internal so tests can keep it in a
    /// fake provider list (e.g. <c>[transport.WinUsbProvider, fakeLibUsb]</c>).</summary>
    internal ConnectProvider WinUsbProvider => new(
        "USB-WINUSB",
        TryCreateWinUsbBackend,
        "WinUSB",
        "Using WinUSB for all transfers (control + bulk)");

    /// <summary>The real LibUsbDotNet leg. Internal so tests can keep it in a
    /// fake provider list.</summary>
    internal ConnectProvider LibUsbProvider => new(
        "USB-LIBUSB",
        TryCreateLibUsbBackend,
        "LibUsbDotNet 3.0");

    /// <summary>
    /// Test seam: constructs the transport bound to an injected backend, so the
    /// connect/init/frame/touch policy is drivable without hardware.
    /// </summary>
    internal DisplayHidTransport(ITransferBackend backend, ILogger<DisplayHidTransport>? logger = null)
        : this(logger)
    {
        _wire.AdoptLive(backend);
    }

    public ConnectionState Connect()
    {
        if (_wire.IsConnected)
            return ConnectionState.Connected;

        _logger.LogInformation("Connecting to WigiDash...");

        // STRATEGY: try each provider in order — WinUSB first, LibUsbDotNet as
        // the fallback. WinUSB and LibUsbDotNet cannot share the same USB
        // interface, so exactly one backend is adopted: the first that
        // survives the init sequence. Each provider owns its open attempt and
        // partial-state teardown; the loop owns the init gate and the
        // dispose-on-init-failure (through the wire's release, like the other
        // teardowns: the backend handle must not be freed while a transfer
        // could be in flight). Starts at the weakest verdict (nothing found) so
        // any real leg failure outranks it; MoreSpecific folds the legs together.
        ConnectionState lastFailure = ConnectionState.DeviceNotFound;
        foreach (var provider in ProviderFactories)
        {
            // This attempt's file-log vocabulary: the loop binds the provider's
            // tag into a DiagLog once, and every line it emits for this leg —
            // exception, success, init failure — rides that tag (the DiagLog
            // module; no hand-baked "[TAG] " prefixes at call sites).
            var legLog = new DiagLog(provider.Tag, 1);
            ProviderAttempt attempt;
            try
            {
                attempt = provider.TryCreate();
            }
            catch (Exception ex)
            {
                // The real providers catch their own failures; this only fires
                // for a provider that let an exception escape, and is treated
                // as terminal like the LibUsb leg's connect exception.
                legLog.Write($"Connect exception: {ex.GetType().FullName}: {ex.Message}");
                _logger.LogError(ex, "Failed to connect to WigiDash");
                _wire.Release();
                return ConnectionState.DeviceNotResponding;
            }

            if (attempt.Backend is null)
            {
                // The leg's own tag, from the record: this line and the
                // open line below are the ONLY places a leg's name is spelled
                // for the file log. A leg that re-types its own tag would emit
                // under the old name forever after a rename, because its
                // DiagLog is built per attempt.
                legLog.Write($"Failed to open the {provider.DisplayName} device interface: {attempt.Failure}");
                // Remember the most informative failure for the return value.
                // A later leg failing for a different reason must not overwrite
                // a "busy" verdict with a vaguer one: DeviceBusy is the fact
                // the user can act on, so it outranks the not-found default and
                // only a DeviceNotResponding from another leg supersedes it.
                lastFailure = MoreSpecific(lastFailure, attempt.Failure);
                continue;
            }

            // Adopt the backend before init: SendInitCommands talks through it.
            _wire.Adopt(attempt.Backend);
            legLog.Write($"Direct {provider.DisplayName} connection opened");

            if (SendInitCommands())
            {
                // Connected only once init completes — the transport's contract
                // must not report connected while the init sequence is still
                // running (the engine's ConnectionState gate already blocks
                // frame flow until Connect() returns; this keeps the transport's
                // own flag truthful too).
                _wire.SetConnected(true);
                if (provider.SuccessLine is not null)
                {
                    legLog.Write(provider.SuccessLine);
                }
                _logger.LogInformation("Connected to WigiDash via {Via}", provider.DisplayName);
                return ConnectionState.Connected;
            }

            // Init failed through this stack — the same control sequence may
            // complete through the next provider's driver stack, so try it. The
            // loop owns this line (position-aware), spelled once from the tag.
            bool hasNextAttempt = Array.IndexOf(ProviderFactories, provider) < ProviderFactories.Length - 1;
            legLog.Write(hasNextAttempt
                ? "Init commands failed — trying the next provider"
                : "Init commands failed — connection failed");
            // The same teardown rule as the wire's release: the drain wait
            // covers any transfer still in flight before the handle is freed.
            _wire.Release();
            lastFailure = MoreSpecific(lastFailure, ConnectionState.DeviceNotResponding);
        }

        return lastFailure;
    }

    /// <summary>
    /// Folds two legs' failures into the one verdict worth reporting, with a
    /// total order on how actionable they are. Only the second leg's report
    /// matters when both are equally specific, so the order is total (no
    /// "neither" case, no null): <see cref="ConnectionState.DeviceBusy"/> (the
    /// display is there, another client has it) beats
    /// <see cref="ConnectionState.DeviceNotResponding"/> (it is there and
    /// misbehaving) beats <see cref="ConnectionState.DeviceNotFound"/>. A busy
    /// device must never be reported as absent just because the fallback leg
    /// found nothing.
    /// </summary>
    internal static ConnectionState MoreSpecific(ConnectionState current, ConnectionState candidate) =>
        Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(ConnectionState state) => state switch
    {
        ConnectionState.DeviceBusy => 2,
        ConnectionState.DeviceNotResponding => 1,
        _ => 0,
    };

    /// <summary>
    /// WinUSB attempt: open a real <see cref="WinUsbBulkDevice"/> → backend.
    /// There is deliberately no PING here — the only PING lives in
    /// <see cref="SendInitCommands"/>. Partial-state teardown is owned here: a
    /// failed open disposes
    /// the device under the wire's swap lock (same rule as its release).
    /// Both failure exits dispose the LOCAL device — never the adopted global
    /// backend, which belongs to a previous provider attempt.
    ///
    /// The leg speaks no log tag of its own: the loop binds the leg's tag once
    /// and spells this attempt's open/fail lines from the verdict that comes
    /// back, so a rename in the provider record cannot leave a leg emitting
    /// under the old tag forever (CONTEXT.md:24 — "no call site composes its
    /// own [TAG] prefix"; this method did exactly that, 2026-09-27).
    /// </summary>
    private ProviderAttempt TryCreateWinUsbBackend()
    {
        var winUsb = new WinUsbBulkDevice();
        try
        {
            // The device parses its own Win32 error into the state, so this leg
            // simply forwards the verdict it was given — no second interpretation
            // of the failure here that could disagree with the device's.
            ConnectionState opened = winUsb.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);
            if (opened == ConnectionState.Connected)
            {
                return ProviderAttempt.Opened(winUsb);
            }

            _wire.AbandonAttempt(winUsb);
            return ProviderAttempt.Failed(opened);
        }
        catch (Exception)
        {
            // The device logs its own "Exception during Open" and returns a
            // state, so anything arriving HERE came from this leg's teardown,
            // not from the open. No line of its own: the loop's catch is
            // unreachable for it (this catch is the outer one), and the teardown
            // failure the user cares about is the connect verdict the loop
            // prints.
            _wire.AbandonAttempt(winUsb);
            return ProviderAttempt.Failed(ConnectionState.DeviceNotResponding);
        }
    }

    /// <summary>
    /// LibUsbDotNet attempt: a thin bind into the adapter, which owns the whole
    /// acquisition (find → open → configure → claim → endpoint → backend), its
    /// partial-state teardown, and its own libusb error vocabulary.
    /// CONTEXT.md's claim that no driver-stack ownership lives in the transport
    /// is true here now; it was not while this method carried the
    /// <c>Error.Access → DeviceBusy</c> table (2026-09-27), which was dead code
    /// because the adapter swallowed every exception into a null.
    /// </summary>
    private ProviderAttempt TryCreateLibUsbBackend()
    {
        // The leg's own file-log vocabulary (USB-FIND/OPEN/CONFIG/CLAIM/
        // ENDPOINT/DESC + the USB-LIBUSB attempt line) is owned by the adapter
        // — the same tags WinUsbBulkDevice binds for its Open (one vocabulary
        // across the seam). The transport keeps only the attempt's start line
        // and hands the device-lookup seam across.
        _logger.LogInformation("Connecting to WigiDash via LibUsbDotNet 3.0 (fallback)...");
        return LibUsbTransferBackend.TryOpen(LibUsbDeviceProvider);
    }

    /// <summary>
    /// Sends the device initialization sequence (PING + 3-page setup + blank
    /// framebuffer + GoToScreen). Called by <see cref="Connect"/>; internal so
    /// tests can drive the sequence through an injected backend.
    /// The verdict covers the wire steps that can fail: a failed page-setup
    /// or GoToScreen control write, or a failed blank-frame header/bulk
    /// write, fails the init — on-device, a reconnect observed exactly that
    /// split (every control write fine, the 1.2 MB init write timing out at
    /// the 30 s pipe bound), and a connected verdict for such a pipe would be
    /// a falsehood the engine would have to inherit.
    /// </summary>
    internal bool SendInitCommands()
    {
        _logger.LogInformation("Sending device initialization commands...");
        // The init sequence is ten-odd lines per attempt, so it rides the same
        // flood cadence as the enumeration behind it: full on the first attempt,
        // then every Nth (the attempt's verdict line is never suppressed).
        DiagLog initLog = new("USB-INIT", BackendDiag.ConnectTraceCadence, logFirst: true);

        // The init sequence (PING → wake → brightness → pages → blank frame →
        // GoToScreen) lives in its own module so the verdict is testable
        // without the connect loop. The wire's transfer tracking
        // (RunTracked) wraps the call, so the teardown drain still waits out
        // an in-flight init write.
        var seq = new InitSequence(_wire.Backend!, initLog);
        bool initOk = _wire.RunTracked(() => seq.Run());

        _logger.LogInformation("Device initialization complete (3 pages), ok={InitOk}", initOk);
        return initOk;
    }

    /// <summary>
    /// Switches the display to the specified screen. Private: only the standby
    /// path needs it — page navigation is compositor-side, frames are sent for
    /// Base screen 0 directly.
    /// </summary>
    private bool GoToScreen(byte screenId, byte transition = 0)
    {
        if (!_wire.IsConnected)
            return false;

        ushort wValue = (ushort)((transition << 2) | screenId);
        bool ok = _wire.ControlOut(DisplayProtocolConstants.CmdGoToScreen, wValue, null);
        if (ok)
        {
            _logger.LogInformation("GoToScreen 0x{ScreenId:X2} (wValue=0x{WValue:X4})", screenId, wValue);
        }
        return ok;
    }

    public bool GoToStandby()
    {
        if (!_wire.IsConnected)
            return false;

        // The vendor's own sleep ritual (its Manager's exit path is exactly
        // this): the Welcome screen, then the immediate-sleep command that
        // turns the backlight off. Without the sleep command the display
        // has no active auto-sleep — it would idle on the Welcome screen
        // with the backlight on.
        bool ok = GoToScreen(DisplayProtocolConstants.ScreenWelcome)
            && _wire.ControlOut(DisplayProtocolConstants.CmdSleepDevice, 0, null);
        if (ok)
        {
            // One-shot per shutdown (the standby guarantee) — Cadence 1.
            _standbyLog.Write("Display set to standby (welcome screen + sleep, backlight off)");
        }
        return ok;
    }

    /// <summary>The frame path. The framing, the header buffer and the
    /// zero-alloc fast path belong to <see cref="DisplayFrameSender"/>; this is
    /// the seam method the engine and the frame pipeline call.</summary>
    public FrameSendResult SendFrame(ReadOnlyMemory<byte> frameBuffer)
        => _frameSender.Send(frameBuffer);

    /// <summary>The touch path. The short-transfer rule and the vendor parse
    /// belong to <see cref="DisplayTouchReader"/>.</summary>
    public TouchReport? ReadTouch() => _touchReader.Read();

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;
        _wire.Release();
        GC.SuppressFinalize(this);
    }
}
