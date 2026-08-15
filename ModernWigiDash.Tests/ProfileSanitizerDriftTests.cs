using System.Reflection;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// Guards the import sanitizer's hardcoded property keys against drift: a
/// renamed path-typed widget property would silently disarm the only guard
/// between an imported profile JSON and Process.Start / SendInput execution.
/// Core cannot reference the Widgets assembly, so the key set stays a constant
/// in ProfileOps and this test reflects over the widget catalog to prove the
/// two still match.
/// </summary>
[TestClass]
public class ProfileSanitizerDriftTests
{
    [TestMethod]
    public void PathPropertyKeys_MatchEveryPathTypedWidgetProperty()
    {
        string[] reflected = typeof(HotkeyButtonWidget).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IModernWidget).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties())
            .Where(p => p.GetCustomAttribute<WidgetPropertyAttribute>()?.PropertyType == WidgetPropertyType.Path)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] declared = ProfileOps.PathPropertyKeys
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(reflected, declared,
            "ProfileOps.PathPropertyKeys drifted from the widgets' [WidgetProperty(WidgetPropertyType.Path)] declarations. " +
            $"Expected: {string.Join(", ", reflected)}; declared: {string.Join(", ", declared)}. A renamed path property would silently disarm the import sanitizer.");
    }

    [TestMethod]
    public void PathPropertyKeys_IncludeHotkeyCommandProperty()
    {
        Assert.IsTrue(ProfileOps.PathPropertyKeys.Contains("ActionCommand"),
            "ActionCommand drives Process.Start / SendInput and must always be covered by the sanitizer keys");

        var attr = typeof(HotkeyButtonWidget)
            .GetProperty("ActionCommand", BindingFlags.Public | BindingFlags.Instance)?
            .GetCustomAttribute<WidgetPropertyAttribute>();
        Assert.AreEqual(WidgetPropertyType.Path, attr?.PropertyType,
            "ActionCommand must remain Path-typed; the drift contract assumes it is covered by the path-key scan");
    }

    [TestMethod]
    public void ChannelNameKey_MatchesTwitchWidgetPropertyName()
    {
        // The import sanitizer keys on "ChannelName" by name (Core cannot
        // reference the Widgets assembly) — a renamed property would silently
        // disarm the CRLF-strip guard on the IRC JOIN target.
        var attr = typeof(TwitchChatStreamWidget)
            .GetProperty("ChannelName", BindingFlags.Public | BindingFlags.Instance)?
            .GetCustomAttribute<WidgetPropertyAttribute>();
        Assert.IsNotNull(attr,
            "TwitchChatStreamWidget must keep a [WidgetProperty] named ChannelName — the import sanitizer's IRC-injection guard keys on it");
    }

    [TestMethod]
    public void ChannelNameRule_IsSharedAndBehavioral()
    {
        // The import sanitizer and the widget's IRC JOIN path must agree on
        // what a channel may look like: both now call Sdk's TwitchChannelRule,
        // so pin the rule's contract behaviorally (cap, CR/LF rejection,
        // fallback) — a rule drift now fails this test instead of silently
        // disagreeing between the two call sites. (The cap constant is a
        // compile-time constant, so the over-cap rejection below is the pin,
        // not a constant comparison.)
        Assert.IsTrue(TwitchChannelRule.IsValid("somechannel"));
        Assert.IsFalse(TwitchChannelRule.IsValid(new string('x', 26)), "over-cap names must be rejected");
        Assert.IsFalse(TwitchChannelRule.IsValid("legit\rchannel"), "embedded CR must be rejected");
        Assert.IsFalse(TwitchChannelRule.IsValid("legit\nchannel"), "embedded LF must be rejected");
        Assert.AreEqual("", TwitchChannelRule.Sanitize("bad\nname", ""),
            "the sanitizer clears invalid imported channels to empty");
        Assert.AreEqual("okname", TwitchChannelRule.Sanitize("okname", ""));
    }
}
