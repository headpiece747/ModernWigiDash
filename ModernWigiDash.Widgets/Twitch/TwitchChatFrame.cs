namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch chat frame's visual facts (the widget's look, grouped as one
/// concept): the design size, the channel name, the three color hexes, the
/// font size, and the auto-connect flag (the empty-state hint follows it).
/// A struct: the render tick builds it per frame with no heap allocation.
/// The canvas's face takes this frame plus the connection's live state and
/// messages — the groupings follow the concepts, not a flat parameter list.
/// </summary>
internal readonly record struct TwitchChatFrame(
    SKSize DefaultSize,
    string ChannelName,
    string BackgroundHex,
    string HeaderColorHex,
    string MessageColorHex,
    int FontSize,
    bool AutoConnect);
