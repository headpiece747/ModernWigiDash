using System.Reflection;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Plugins;

/// <summary>
/// One catalog entry. Only the fields the host actually consumes — the catalog
/// binds PluginId/DisplayName/Category, <see cref="CreateInstance"/> needs
/// WidgetType. The remaining metadata (description, author, version, grid
/// size) stays on the [WidgetMetadata] attribute.
/// </summary>
public class PluginInfo
{
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public Type WidgetType { get; set; } = null!;
}

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
            System.Diagnostics.Debug.WriteLine(message);
            FileLog.Write(message);
            return;
        }

        _registeredPlugins[id] = new PluginInfo
        {
            PluginId = id,
            DisplayName = name,
            Category = attr?.Category ?? "General",
            WidgetType = widgetType
        };
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
            System.Diagnostics.Debug.WriteLine(message);
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

    public IModernWidget? CreateInstance(string pluginId)
    {
        if (!_registeredPlugins.TryGetValue(pluginId, out var info))
        {
            return null;
        }

        try
        {
            return (IModernWidget?)Activator.CreateInstance(info.WidgetType);
        }
        catch (Exception ex)
        {
            // A widget whose constructor throws must not crash the host; surface
            // the failure so the catalog can show the plugin as broken.
            string message = $"Widget instantiation failed for {pluginId} ({info.WidgetType.FullName}): {ex.Message}";
            System.Diagnostics.Debug.WriteLine(message);
            FileLog.Write(message);
            return null;
        }
    }
}
