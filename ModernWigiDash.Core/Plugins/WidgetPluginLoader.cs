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
        if (!typeof(ModernWidget).IsAssignableFrom(widgetType) || widgetType.IsAbstract || widgetType.IsInterface)
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
                if (typeof(ModernWidget).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                {
                    RegisterBuiltInPlugin(type);
                }
            }
            return assembly;
        }
        catch
        {
            alc.Unload();
            _loadContexts.Remove(dllPath);
            return null;
        }
    }

    public ModernWidget? CreateInstance(string pluginId)
    {
        if (_registeredPlugins.TryGetValue(pluginId, out var info))
        {
            return (ModernWidget?)Activator.CreateInstance(info.WidgetType);
        }
        return null;
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
