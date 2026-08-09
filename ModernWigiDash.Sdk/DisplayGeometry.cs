namespace ModernWigiDash.Sdk;

/// <summary>
/// The WigiDash display's active framebuffer geometry — the single source of
/// truth shared by every project layer (Sdk is the lowest common layer).
/// Hardware's <c>DisplayProtocolConstants</c> aliases these values and Core's
/// compositor derives its buffer size from them, so the pixel area can never
/// drift between projects.
/// </summary>
public static class DisplayGeometry
{
    /// <summary>
    /// Active framebuffer width in pixels (1016 = 1024 full display - 8px border).
    /// </summary>
    public const int FramebufferWidth = 1016;

    /// <summary>
    /// Active framebuffer height in pixels (592 = 600 full display - 8px border).
    /// </summary>
    public const int FramebufferHeight = 592;

    /// <summary>
    /// Bytes per pixel for the RGB565 little-endian framebuffer format.
    /// </summary>
    public const int BytesPerPixel = 2;

    /// <summary>
    /// Total framebuffer payload size in bytes (1016 * 592 * 2 = 1,202,944 bytes).
    /// </summary>
    public const int FrameBufferSize = FramebufferWidth * FramebufferHeight * BytesPerPixel;
}
