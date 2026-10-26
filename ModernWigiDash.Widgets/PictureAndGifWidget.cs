namespace ModernWigiDash.Widgets;

/// <summary>
/// Image and animated-GIF viewer: draws a single file or cycles a folder of
/// images, decoded by the MediaDecoder module on a background thread. The
/// source-resolution table (file vs. folder, the tap-to-cycle promise) lives
/// in PictureSourcePolicy.
/// </summary>
[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", Category = "Social & Visual")]
public class PictureAndGifWidget : ModernWidgetBase
{
    /// <summary>The "Image Folder/File Path" property: path to the image or folder of images.</summary>
    [WidgetProperty("Image Folder/File Path", WidgetPropertyType.Path, "Path to image or folder of images", "C:\\Pictures")]
    public string ImagePath { get; set; } = "C:\\Pictures";

    /// <summary>The "Source Mode" property: auto-detects file or folder, or forces one mode.</summary>
    [WidgetProperty("Source Mode", WidgetPropertyType.Choice, "Auto detects file or folder; forces one mode when set", "Auto", "Auto", "Single Image", "Folder (Cycle)")]
    public string SourceMode { get; set; } = "Auto";

    /// <summary>The "Fit Mode" property: aspect ratio scaling mode (Cover, Contain, Stretch).</summary>
    [WidgetProperty("Fit Mode", WidgetPropertyType.Choice, "Aspect ratio scaling mode", "Cover", "Cover", "Contain", "Stretch")]
    public string FitMode { get; set; } = "Cover";

    /// <summary>The "Corner Radius" property: the rounded corners radius.</summary>
    [WidgetProperty("Corner Radius", WidgetPropertyType.Number, "Rounded corners radius", 16f)]
    public float CornerRadius { get; set; } = 16f;

    /// <summary>The "Text Color" property: placeholder icon and hint color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Placeholder icon and hint color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private bool _disposed;

    /// <summary>Test seam: clock for the GIF advance and the folder-rescan throttle (defaults to the system clock). Bound to the media session on first use.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // The media session: installed media, the retirement set, the async load
    // pipeline, and the folder-scan state (the state that the widget's
    // properties feed in). Lazy, in the TwitchSession image: bound to the
    // Clock value at first use, so a test's FakeTimeProvider set before the
    // first render is what the session runs on.
    private PictureMediaSession? _mediaSession;

    private PictureMediaSession Session
        => _mediaSession ??= new PictureMediaSession(Clock, msg => Context?.LogError($"PictureAndGifWidget: {msg}"), () => Context?.RequestRender());

    /// <summary>The media session (test seam: the test surface drives the session's face directly).</summary>
    internal PictureMediaSession MediaSessionForTest => Session;

    // The draw owner: placeholder paints, the clip cache, and the draw paths
    // (the widget's draw paths and their disposal live in one place).
    private readonly PictureCanvas _canvas = new();

    /// <summary>Resets the media session when the path or the source mode changes.</summary>
    /// <param name="propertyName">The property that changed.</param>
    /// <param name="newValue">The property's new value.</param>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(ImagePath) or nameof(SourceMode))
        {
            Session.Reset();
            _canvas.InvalidateClip();
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <summary>
    /// Draws the active image or GIF frame (retiring replaced bitmaps on this
    /// thread), or the placeholder when nothing is installed.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var session = Session;
        session.Retirement.DisposeRetired();

        string? currentFile = session.GetActiveImageFile(SourceMode, ImagePath);

        if (!string.IsNullOrEmpty(currentFile) && session.FileExists(currentFile))
        {
            session.EnsureMediaLoad(currentFile);

            (SKBitmap[]? frames, SKBitmap? still, int frameIndex) = session.CurrentFrame();

            if (frames is { Length: > 1 })
            {
                _canvas.RenderImage(canvas, bounds, frames[frameIndex], FitMode, CornerRadius);
                return;
            }

            if (still is not null)
            {
                _canvas.RenderImage(canvas, bounds, still, FitMode, CornerRadius);
                return;
            }
        }

        bool canCycle = PictureSourcePolicy.CanCycle(SourceMode, session.FileExists(ImagePath), session.DirectoryExists(ImagePath));
        _canvas.RenderPlaceholder(canvas, bounds, TextColorHex, canCycle);
    }

    /// <summary>A release over an actually cycling folder advances to the next image and requests a render.</summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local space.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp && Session.AdvanceCycle(SourceMode, ImagePath))
        {
            Context?.RequestRender();
        }
    }

    /// <summary>Resets the media session, disposes the draw owner's Skia surfaces, and the retired bitmaps.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Session.Reset();
        _canvas.Dispose();
        Session.Retirement.DisposeAll();
        return base.DisposeAsync();
    }
}
