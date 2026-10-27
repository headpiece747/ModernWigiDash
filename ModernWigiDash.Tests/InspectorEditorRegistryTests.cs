using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class InspectorEditorRegistryTests
{
    private sealed class DummyCustomEditor(EditorKind kind) : ICustomPropertyEditor
    {
        public EditorKind Kind => kind;
        public UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
            => new TextBlock { Text = "Dummy" };
    }

    [TestMethod]
    public void DefaultRegistry_ContainsStandardCustomEditors()
    {
        var registry = InspectorEditorRegistry.Default;

        Assert.IsTrue(registry.TryGetEditor(EditorKind.CalendarFeeds, out var calEditor));
        Assert.IsNotNull(calEditor);
        Assert.AreEqual(EditorKind.CalendarFeeds, calEditor.Kind);

        Assert.IsTrue(registry.TryGetEditor(EditorKind.TodoManager, out var todoEditor));
        Assert.IsNotNull(todoEditor);
        Assert.AreEqual(EditorKind.TodoManager, todoEditor.Kind);

        Assert.IsTrue(registry.TryGetEditor(EditorKind.LocationSearch, out var locEditor));
        Assert.IsNotNull(locEditor);
        Assert.AreEqual(EditorKind.LocationSearch, locEditor.Kind);

        Assert.IsTrue(registry.TryGetEditor(EditorKind.KeyCapture, out var keyEditor));
        Assert.IsNotNull(keyEditor);
        Assert.AreEqual(EditorKind.KeyCapture, keyEditor.Kind);
    }

    [TestMethod]
    public void CustomRegistration_RegistersAndRetrievesEditor()
    {
        var registry = new InspectorEditorRegistry();
        Assert.IsFalse(registry.TryGetEditor(EditorKind.IconPicker, out _));

        var dummy = new DummyCustomEditor(EditorKind.IconPicker);
        registry.Register(dummy);

        Assert.IsTrue(registry.TryGetEditor(EditorKind.IconPicker, out var retrieved));
        Assert.AreSame(dummy, retrieved);
    }
}
