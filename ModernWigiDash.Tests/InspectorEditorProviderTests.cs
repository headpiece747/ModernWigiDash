using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class InspectorEditorProviderTests
{
    /// <summary>
    /// A non-Hotkey widget implementing <see cref="IWidgetEditorProvider"/>:
    /// the inspector must route it through the special editors purely via the
    /// interface — no widget-type branches may fire.
    /// </summary>
    private sealed class ProviderWidget : ModernWidgetBase, IWidgetEditorProvider
    {
        [WidgetProperty("Label", WidgetPropertyType.Text, defaultValue: "x")]
        public string Label { get; set; } = "x";

        [WidgetProperty("Icon File", WidgetPropertyType.Path, defaultValue: "")]
        public string IconFile { get; set; } = "";

        [WidgetProperty("Icon", WidgetPropertyType.Icon, defaultValue: "")]
        public string Icon { get; set; } = "";

        [WidgetProperty("Action Type", WidgetPropertyType.Choice, "", "Launch App", "Launch App", "Open URL")]
        public string ActionType { get; set; } = "Launch App";

        [WidgetProperty("Command", WidgetPropertyType.Path, defaultValue: "")]
        public string ActionCommand { get; set; } = "";

        public EditorKind? GetEditorKind(PropertyInfo property)
        {
            if (property.Name == nameof(IconFile)) return EditorKind.IconPicker;
            if (property.Name == nameof(ActionCommand)) return EditorKind.ActionCommand;
            return null;
        }

        public PropertyInfo? GetIconFileCompanion(PropertyInfo iconProperty)
            => iconProperty.Name == nameof(Icon) ? GetType().GetProperty(nameof(IconFile)) : null;

        public string? ActionCommandVisibilityChoicePropertyName => nameof(ActionType);

        public bool IsActionCommandVisible(string? actionTypeValue)
            => actionTypeValue is "Launch App" or "Open URL";

        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    private static PlacedWidgetInstance Place() => new()
    {
        PluginId = "provider",
        DisplayName = "Provider",
        ActiveInstance = new ProviderWidget()
    };

    // ── model builder routing ───────────────────────────────

    [TestMethod]
    public void Describe_SkipsIconPickerCompanion_KeepsPickerAndCommand()
    {
        var descriptions = InspectorModelBuilder.Describe(Place());

        Assert.IsFalse(descriptions.Any(d => d.Property.Name == nameof(ProviderWidget.IconFile)),
            "The IconPicker companion property must not get a generic editor row");
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(ProviderWidget.Icon)),
            "The icon property itself must keep its editor");
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(ProviderWidget.ActionCommand)),
            "The ActionCommand property must keep its editor");
        CollectionAssert.AreEqual(
            new[] { "Label", "Icon", "ActionType", "ActionCommand" },
            descriptions.Select(d => d.Property.Name).ToArray());
    }

    [TestMethod]
    public void HotkeyButtonWidget_MapsSpecialEditors()
    {
        var widget = new HotkeyButtonWidget();
        var iconFile = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile))!;
        var icon = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.Icon))!;
        var command = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.ActionCommand))!;

        Assert.AreEqual(EditorKind.IconPicker, widget.GetEditorKind(iconFile));
        Assert.AreEqual(EditorKind.ActionCommand, widget.GetEditorKind(command));
        Assert.IsNull(widget.GetEditorKind(icon), "The Icon property itself is routed by its property type");

        Assert.IsNotNull(widget.GetIconFileCompanion(icon));
        Assert.AreEqual(iconFile, widget.GetIconFileCompanion(icon));

        Assert.AreEqual(nameof(HotkeyButtonWidget.ActionType), widget.ActionCommandVisibilityChoicePropertyName);
        Assert.IsTrue(widget.IsActionCommandVisible("Launch App"));
        Assert.IsTrue(widget.IsActionCommandVisible("Open URL"));
        Assert.IsFalse(widget.IsActionCommandVisible("Media Play / Pause"));
        Assert.IsFalse(widget.IsActionCommandVisible(null));
    }

    [TestMethod]
    public void Describe_HotkeyWidget_StillHidesIconFileCompanion()
    {
        var descriptions = InspectorModelBuilder.Describe(new PlacedWidgetInstance
        {
            PluginId = "hotkey",
            DisplayName = "Hotkey",
            ActiveInstance = new HotkeyButtonWidget()
        });

        Assert.IsFalse(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.IconFile)));
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.Icon)));
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.ActionCommand)));
    }

    // ── renderer routing (WPF controls need an STA thread) ──

    [TestMethod]
    public void Render_RoutesActionCommand_ToCommandPathEditor()
    {
        Exception? failure = null;
        string? fileTitle = null;
        string? fileFilter = null;
        string? folderTitle = null;

        var thread = new Thread(() =>
        {
            try
            {
                var placed = Place();
                var panel = new StackPanel();
                InspectorPanelRenderer.Render(
                    placed,
                    InspectorModelBuilder.Describe(placed),
                    panel.Children,
                    () => false,
                    new InspectorCallbacks
                    {
                        TryFindResource = _ => null,
                        ApplyInspectorPropertyValue = (_, _) => { },
                        ShowIconSelectorPopup = (_, _, _) => { },
                        AttachDropdownWithinWindow = _ => { },
                        BrowseFile = (title, filter) =>
                        {
                            fileTitle = title;
                            fileFilter = filter;
                            return null;
                        },
                        BrowseFolder = title =>
                        {
                            folderTitle = title;
                            return null;
                        }
                    });

                // Last row is the ActionCommand property: the File button must
                // be the action picker, not the image picker.
                var commandRow = panel.Children.OfType<StackPanel>().Last();
                var dock = commandRow.Children.OfType<DockPanel>().Single();
                var fileButton = dock.Children.OfType<Button>().Single(b => (string)b.Content == "File\u2026");
                fileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual("Select action file or executable", fileTitle);
                Assert.AreEqual("Programs and files (*.*)|*.*", fileFilter);

                var folderButton = dock.Children.OfType<Button>().Single(b => (string)b.Content == "Folder\u2026");
                folderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("Select action folder", folderTitle);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.IsNull(failure, failure?.ToString());
    }
}
