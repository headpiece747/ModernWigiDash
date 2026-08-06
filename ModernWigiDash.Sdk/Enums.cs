using SkiaSharp;

namespace ModernWigiDash.Sdk;

public enum WidgetSizeMode
{
    Fixed,       // Widget requires exact dimensions (e.g., exact 204x150 button)
    Resizable,   // Widget dynamically scales to whatever Width x Height the user drags it to
    AspectLocked // Resizable, but maintains exact aspect ratio (e.g., circular gauge/clock)
}

public enum TouchEventType
{
    TouchDown,
    TouchUp,
    TouchMove
}

public enum GridSizePreset
{
    Size1x1, // 203 x 148 px
    Size2x1, // 406 x 148 px
    Size3x1, // 609 x 148 px
    Size4x1, // 813 x 148 px
    Size5x1, // 1016 x 148 px
    Size1x2, // 203 x 296 px
    Size2x2, // 406 x 296 px
    Size3x2, // 609 x 296 px
    Size4x2, // 813 x 296 px
    Size5x2, // 1016 x 296 px
    Size1x3, // 203 x 444 px
    Size2x3, // 406 x 444 px
    Size3x3, // 609 x 444 px
    Size4x3, // 813 x 444 px
    Size5x3, // 1016 x 444 px
    Size1x4, // 203 x 592 px
    Size2x4, // 406 x 592 px
    Size3x4, // 609 x 592 px
    Size4x4, // 813 x 592 px
    Size5x4, // 1016 x 592 px (Full Screen)
    FreeForm // Custom arbitrary pixel dimensions
}

public static class GridSizeExtensions
{
    public const float ScreenWidth = 1016f;
    public const float ScreenHeight = 592f;
    public const float CellWidth = 203.2f;
    public const float CellHeight = 148f;

    public static SKSize ToSize(this GridSizePreset preset)
    {
        return preset switch
        {
            GridSizePreset.Size1x1 => new SKSize(203, 148),
            GridSizePreset.Size2x1 => new SKSize(406, 148),
            GridSizePreset.Size3x1 => new SKSize(609, 148),
            GridSizePreset.Size4x1 => new SKSize(813, 148),
            GridSizePreset.Size5x1 => new SKSize(1016, 148),
            GridSizePreset.Size1x2 => new SKSize(203, 296),
            GridSizePreset.Size2x2 => new SKSize(406, 296),
            GridSizePreset.Size3x2 => new SKSize(609, 296),
            GridSizePreset.Size4x2 => new SKSize(813, 296),
            GridSizePreset.Size5x2 => new SKSize(1016, 296),
            GridSizePreset.Size1x3 => new SKSize(203, 444),
            GridSizePreset.Size2x3 => new SKSize(406, 444),
            GridSizePreset.Size3x3 => new SKSize(609, 444),
            GridSizePreset.Size4x3 => new SKSize(813, 444),
            GridSizePreset.Size5x3 => new SKSize(1016, 444),
            GridSizePreset.Size1x4 => new SKSize(203, 592),
            GridSizePreset.Size2x4 => new SKSize(406, 592),
            GridSizePreset.Size3x4 => new SKSize(609, 592),
            GridSizePreset.Size4x4 => new SKSize(813, 592),
            GridSizePreset.Size5x4 => new SKSize(1016, 592),
            GridSizePreset.FreeForm => new SKSize(300, 200),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GridSizePreset")
        };
    }
}
