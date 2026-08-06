using System.Reflection;
using System.Runtime.Loader;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Plugins;

public class PluginInfo
{
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public GridSizePreset DefaultGridSize { get; set; } = GridSizePreset.Size2x2;
    public Type WidgetType { get; set; } = null!;
    public string AssemblyPath { get; set; } = string.Empty;
}

public class WidgetPluginLoader
{
    private readonly Dictionary<string, AssemblyLoadContext> _loadContexts = [];
    private readonly Dictionary<string, PluginInfo> _registeredPlugins = [];

    public IReadOnlyCollection<PluginInfo> RegisteredPlugins => _registeredPlugins.Values;

    public void RegisterBuiltInPlugin(Type widgetType)
    {
        if (!typeof(IModernWidget).IsAssignableFrom(widgetType) || widgetType.IsAbstract || widgetType.IsInterface)
            return;

        var attr = widgetType.GetCustomAttribute<WidgetMetadataAttribute>();
        string id = attr?.Id ?? widgetType.Name;
        string name = attr?.DisplayName ?? widgetType.Name;

        _registeredPlugins[id] = new PluginInfo
        {
            PluginId = id,
            DisplayName = name,
            Description = attr?.Description ?? "",
            Author = attr?.Author ?? "Built-In",
            Version = attr?.Version ?? "1.0.0",
            Category = attr?.Category ?? "General",
            DefaultGridSize = attr?.DefaultGridSize ?? GridSizePreset.Size2x2,
            WidgetType = widgetType
        };
    }

    /// <summary>
    /// Registers every concrete <see cref="IModernWidget"/> in <paramref name="assembly"/>
    /// (usually the Widgets assembly), so adding a built-in widget needs no
    /// host-side registration — the [WidgetMetadata] attribute drives the catalog.
    /// </summary>
    public void RegisterBuiltInAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IModernWidget).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            {
                RegisterBuiltInPlugin(type);
            }
        }
    }

    public Assembly? LoadExternalAssembly(string dllPath)
    {
        if (!File.Exists(dllPath))
            return null;

        string contextName = $"Context_{Path.GetFileNameWithoutExtension(dllPath)}_{Guid.NewGuid()}";
        var alc = new AssemblyLoadContext(contextName, isCollectible: true);
        _loadContexts[dllPath] = alc;

        try
        {
            var assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(IModernWidget).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                {
                    RegisterBuiltInPlugin(type);
                }
            }
            return assembly;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plugin load failed for {dllPath}: {ex.Message}");
            alc.Unload();
            _loadContexts.Remove(dllPath);
            return null;
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
            System.Diagnostics.Debug.WriteLine($"Widget instantiation failed for {pluginId}: {ex.Message}");
            return null;
        }
    }

    public void UnloadExternalPlugin(string dllPath)
    {
        if (_loadContexts.TryGetValue(dllPath, out var alc))
        {
            alc.Unload();
            _loadContexts.Remove(dllPath);
        }
    }
}
