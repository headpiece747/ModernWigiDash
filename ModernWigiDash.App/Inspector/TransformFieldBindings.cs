using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The transform face of the inspector: the six transform text boxes and the
/// opacity slider the controller's write-backs read from and write to, plus
/// the canvas-repaint request a landed write-back triggers. One narrow
/// adapter per face — adding a transform field touches this record (and the
/// controller's write/parse lines), never the window's wiring bag.
/// </summary>
internal sealed record TransformFieldBindings(
    TextBox PosX,
    TextBox PosY,
    TextBox WidthText,
    TextBox HeightText,
    TextBox ZIndexText,
    TextBox RotationText,
    Slider OpacitySlider,
    TextBlock OpacityValueText,
    Action RequestCanvasRender);
