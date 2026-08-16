using SkiaSharp;

namespace ModernWigiDash.Widgets.Twitch;

/// <summary>The kinds of IRC lines the chat widget acts on.</summary>
internal enum IrcMessageKind
{
    Ping,
    Privmsg,
    Notice,
    RoomState,
    Other
}

/// <summary>
/// One parsed IRC message, carrying only the facts the chat widget consumes:
/// <see cref="Text"/> is the chat text (Privmsg) or the raw notice text
/// (Notice), <see cref="PingPayload"/> is the PING payload to echo in a PONG.
/// </summary>
internal readonly record struct IrcMessage(
    IrcMessageKind Kind,
    string Username = "",
    string Login = "",
    string ColorHex = "",
    string Text = "",
    string PingPayload = "");

/// <summary>
/// Pure parser for the Twitch IRC wire format, extracted from
/// TwitchChatStreamWidget's HandleLine so the protocol is directly testable:
/// tag parsing, PING/PRIVMSG/NOTICE framing, escape sequences, the 400-char
/// cap, and the deterministic name palette. The widget keeps the message
/// list, the color resolution, and the connection state.
/// </summary>
internal static class TwitchIrcMessages
{
    private const int MaxMessageLength = 400;

    /// <summary>The deterministic name palette: a name hashes to one of these colors.</summary>
    internal static readonly SKColor[] NamePalette =
    {
        new(255, 121, 198), new(189, 147, 249), new(127, 202, 250), new(187, 247, 208),
        new(254, 240, 138), new(253, 186, 116), new(199, 210, 254), new(165, 243, 252)
    };

    /// <summary>
    /// Parses one IRC line. Returns true for every well-formed line (unknown
    /// commands parse as <see cref="IrcMessageKind.Other"/>) and false for the
    /// lines the widget previously dropped: an unterminated tag block or a
    /// line with fewer than two space-separated parts.
    /// </summary>
    internal static bool TryParse(string line, out IrcMessage message)
    {
        message = default;

        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            message = new IrcMessage(
                IrcMessageKind.Ping,
                PingPayload: line.Length > 5 ? line[5..].TrimStart(':') : "");
            return true;
        }

        string[] tags = [];
        if (line.StartsWith('@'))
        {
            var tagEnd = line.IndexOf(' ');
            if (tagEnd < 0) return false;
            tags = line[1..tagEnd].Split(';');
            line = line[(tagEnd + 1)..];
        }

        var parts = line.Split(' ', 4);
        if (parts.Length < 2) return false;
        var command = parts[1];
        var trailing = parts.Length > 3 ? parts[3] : "";

        switch (command)
        {
            case "ROOMSTATE":
                message = new IrcMessage(IrcMessageKind.RoomState);
                return true;
            case "PRIVMSG":
                if (!trailing.StartsWith(':')) return false;
                var text = Unescape(trailing[1..]);
                if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];
                var displayName = GetTag(tags, "display-name");
                var login = GetTag(tags, "login");
                string username;
                if (displayName.Length > 0) username = displayName;
                else if (login.Length > 0) username = login;
                else username = "user";
                message = new IrcMessage(
                    IrcMessageKind.Privmsg,
                    Username: username,
                    Login: login,
                    ColorHex: GetTag(tags, "color"),
                    Text: text);
                return true;
            case "NOTICE":
                message = new IrcMessage(IrcMessageKind.Notice, Text: trailing.TrimStart(':'));
                return true;
            default:
                message = new IrcMessage(IrcMessageKind.Other);
                return true;
        }
    }

    /// <summary>
    /// The palette color for a name: the deterministic hash rule the widget
    /// applied to messages without a chat-tag color.
    /// </summary>
    internal static SKColor PaletteColorFor(string name)
    {
        int hash = 17;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return NamePalette[hash % NamePalette.Length];
    }

    private static string GetTag(string[] tags, string key)
    {
        foreach (var t in tags)
        {
            var eq = t.IndexOf('=');
            var k = eq < 0 ? t : t[..eq];
            // IRCv3 tag keys are case-insensitive: servers emit lowercase, yet
            // a client echoing mixed case must still match.
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return eq < 0 ? "" : t[(eq + 1)..];
        }
        return "";
    }

    private static string Unescape(string s) =>
        s.Replace("\\s", " ").Replace("\\:", ";").Replace("\\\\", "\\");
}
