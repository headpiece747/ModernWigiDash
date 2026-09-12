using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The OS surface behind <see cref="HotkeyActionExecutor"/> as an injectable
/// delegate bag (the <c>WinUsbApi</c>/<c>HotkeyApi</c> house pattern): the
/// SendInput P/Invoke and the Process.Start shell-open. Production binds the
/// real externs once via <see cref="Default"/>; tests inject managed fakes, so
/// the executor's routing policy (which kind sends which inputs, the repeat and
/// delay clamping, the MaxActions guard, the chord parse, the mouse flags) is
/// scriptable without sending real keystrokes or spawning processes.
/// </summary>
internal sealed class HotkeyActionApi(
    HotkeyActionApi.SendInputFn sendInput,
    HotkeyActionApi.StartProcessFn startProcess)
{
    /// <summary>The number of inputs actually injected by SendInput.</summary>
    internal delegate uint SendInputFn(uint inputCount, IntPtr inputs, int inputSize);

    /// <summary>Starts a process with the given file path and arguments (shell-execute).</summary>
    internal delegate void StartProcessFn(string filePath, string arguments);

    /// <summary>The production binding: the real P/Invoke extern + Process.Start.</summary>
    public static readonly HotkeyActionApi Default = new(
        (count, buffer, size) => SendInputNative(count, buffer, size),
        (file, args) => { Process.Start(new ProcessStartInfo(file) { Arguments = args, UseShellExecute = true }); });

    internal HotkeyActionApi.SendInputFn SendInput { get; } = sendInput;
    internal HotkeyActionApi.StartProcessFn StartProcess { get; } = startProcess;

    // Entry point spelled explicitly so the binding cannot drift from the export
    // on a method rename (ADR-0020).
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(uint inputCount, IntPtr inputs, int inputSize);
}
