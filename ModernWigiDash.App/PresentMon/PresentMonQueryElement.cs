using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Managed, blittable mirror of the PresentMon API v3.4 <c>PM_QUERY_ELEMENT</c>
/// struct (32 bytes, sequential on x64). <see cref="Metric"/>/<see cref="Stat"/>/
/// <see cref="DeviceId"/>/<see cref="ArrayIndex"/> are registration inputs;
/// <see cref="DataOffset"/>/<see cref="DataSize"/> are filled in by the service
/// on <c>pmRegisterDynamicQuery</c>/<c>pmRegisterFrameQuery</c> and describe where
/// the element's value lives inside each polled blob.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PresentMonQueryElement(
    uint Metric,
    uint Stat,
    uint DeviceId,
    uint ArrayIndex,
    ulong DataOffset,
    ulong DataSize);
