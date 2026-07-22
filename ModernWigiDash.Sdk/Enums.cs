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
    Size1x1, // 204 x 150 px
    Size2x1, // 408 x 150 px
    Size3x1, // 612 x 150 px
    Size4x1, // 816 x 150 px
    Size5x1, // 1024 x 150 px
    Size1x2, // 204 x 300 px
    Size2x2, // 408 x 300 px
    Size3x2, // 612 x 300 px
    Size4x2, // 816 x 300 px
    Size5x2, // 1024 x 300 px
    Size1x3, // 204 x 450 px
    Size2x3, // 408 x 450 px
    Size3x3, // 612 x 450 px
    Size4x3, // 816 x 450 px
    Size5x3, // 1024 x 450 px
    Size1x4, // 204 x 600 px
    Size2x4, // 408 x 600 px
    Size3x4, // 612 x 600 px
    Size4x4, // 816 x 600 px
    Size5x4, // 1024 x 600 px (Full Screen)
    FreeForm // Custom arbitrary pixel dimensions
}

public static class GridSizeExtensions
{
    public const float ScreenWidth = 1024f;
    public const float ScreenHeight = 600f;
    public const float CellWidth = 204.8f;
    public const float CellHeight = 150f;

    public static SKSize ToSize(this GridSizePreset preset)
    {
        return preset switch
        {
            GridSizePreset.Size1x1 => new SKSize(204, 150),
            GridSizePreset.Size2x1 => new SKSize(408, 150),
            GridSizePreset.Size3x1 => new SKSize(612, 150),
            GridSizePreset.Size4x1 => new SKSize(816, 150),
            GridSizePreset.Size5x1 => new SKSize(1024, 150),
            GridSizePreset.Size1x2 => new SKSize(204, 300),
            GridSizePreset.Size2x2 => new SKSize(408, 300),
            GridSizePreset.Size3x2 => new SKSize(612, 300),
            GridSizePreset.Size4x2 => new SKSize(816, 300),
            GridSizePreset.Size5x2 => new SKSize(1024, 300),
            GridSizePreset.Size1x3 => new SKSize(204, 450),
            GridSizePreset.Size2x3 => new SKSize(408, 450),
            GridSizePreset.Size3x3 => new SKSize(612, 450),
            GridSizePreset.Size4x3 => new SKSize(816, 450),
            GridSizePreset.Size5x3 => new SKSize(1024, 450),
            GridSizePreset.Size1x4 => new SKSize(204, 600),
            GridSizePreset.Size2x4 => new SKSize(408, 600),
            GridSizePreset.Size3x4 => new SKSize(612, 600),
            GridSizePreset.Size4x4 => new SKSize(816, 600),
            GridSizePreset.Size5x4 => new SKSize(1024, 600),
            _ => new SKSize(300, 200)
        };
    }
}
