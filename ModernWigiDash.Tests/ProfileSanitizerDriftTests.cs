using System.Reflection;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

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
}
