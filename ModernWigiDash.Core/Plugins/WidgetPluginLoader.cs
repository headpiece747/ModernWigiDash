using System.Reflection;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Plugins;

/// <summary>
/// One catalog entry. Only the fields the host actually consumes — the catalog
/// binds PluginId/DisplayName/Category, <see cref="WidgetPluginLoader.CreateInstance"/>
/// needs WidgetType. The remaining metadata (description, author, version, grid
/// size) stays on the [WidgetMetadata] attribute. Immutable: a catalog entry
/// never changes after registration.
/// </summary>
public sealed record PluginInfo(string PluginId, string DisplayName, string Category, Type WidgetType);

public class WidgetPluginLoader
{
    private readonly Dictionary<string, PluginInfo> _registeredPlugins = [];

    public IReadOnlyCollection<PluginInfo> RegisteredPlugins => _registeredPlugins.Values;

    /// <summary>
    /// Registers one widget type under its <see cref="WidgetMetadataAttribute"/>
    /// id. Deliberately public: tests register hand-rolled widget types
    /// directly, so removing or narrowing this entry point would break the
    /// test seam. Production hosts use <see cref="RegisterBuiltInAssembly"/>.
    /// A duplicate id is skipped (first registration wins) with a diagnostic.
    /// </summary>
    public void RegisterBuiltInPlugin(Type widgetType)
    {
        if (!typeof(IModernWidget).IsAssignableFrom(widgetType) || widgetType.IsAbstract || widgetType.IsInterface)
            return;

        var attr = widgetType.GetCustomAttribute<WidgetMetadataAttribute>();
        string id = attr?.Id ?? widgetType.Name;
        string name = attr?.DisplayName ?? widgetType.Name;

        if (_registeredPlugins.ContainsKey(id))
        {
            string message = $"WidgetPluginLoader: duplicate plugin id '{id}' from {widgetType.FullName}; keeping the first registration";
            FileLog.Write(message);
            return;
        }

        _registeredPlugins[id] = new PluginInfo(id, name, attr?.Category ?? "General", widgetType);
    }

    /// <summary>
    /// Registers every concrete <see cref="IModernWidget"/> in <paramref name="assembly"/>
    /// (usually the Widgets assembly), so adding a built-in widget needs no
    /// host-side registration — the [WidgetMetadata] attribute drives the catalog.
    /// A <see cref="ReflectionTypeLoadException"/> (broken/missing plugin
    /// dependency) registers the loadable subset instead of aborting the catalog.
    /// </summary>
    public void RegisterBuiltInAssembly(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            string message = $"WidgetPluginLoader: {ex.LoaderExceptions.Length} type(s) failed to load from {assembly.FullName}; registering the loadable subset";
            FileLog.Write(message);
            types = ex.Types.OfType<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (typeof(IModernWidget).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            {
                RegisterBuiltInPlugin(type);
            }
        }
    }

    /// <summary>
    /// Instantiates the plugin's widget type, distinguishing a broken widget
    /// (constructor threw, <see cref="WidgetCreateResult.Broken"/>) from an
    /// absent one (unknown id, <see cref="WidgetCreateResult.NotFound"/>), so
    /// hosts can surface the failure reason in their diagnostics.
    /// </summary>
    internal WidgetCreateResult CreateInstanceResult(string pluginId)
    {
        if (!_registeredPlugins.TryGetValue(pluginId, out var info))
        {
            return new WidgetCreateResult.NotFound();
        }

        try
        {
            return new WidgetCreateResult.Ok((IModernWidget)Activator.CreateInstance(info.WidgetType)!);
        }
        catch (Exception ex)
        {
            // A widget whose constructor throws must not crash the host; surface
            // the failure so the catalog can show the plugin as broken.
            // Activator.CreateInstance wraps a throwing constructor in
            // TargetInvocationException — unwrap it so the reason carries the
            // widget's actual failure, not the wrapper's boilerplate.
            if (ex is TargetInvocationException tie && tie.InnerException is not null)
            {
                ex = tie.InnerException;
            }
            string message = $"Widget instantiation failed for {pluginId} ({info.WidgetType.FullName}): {ex.Message}";
            FileLog.Write(message);
            return new WidgetCreateResult.Broken(message);
        }
    }

    /// <summary>Convenience wrapper over <see cref="CreateInstanceResult"/>: the
    /// widget instance, or null when the plugin is absent or broken. Call sites
    /// that cannot act on the distinction keep compiling unchanged.</summary>
    public IModernWidget? CreateInstance(string pluginId)
        => CreateInstanceResult(pluginId) switch
        {
            WidgetCreateResult.Ok ok => ok.Widget,
            _ => null
        };
}
