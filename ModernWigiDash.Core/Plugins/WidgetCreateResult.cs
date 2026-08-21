namespace ModernWigiDash.Core.Plugins;

/// <summary>
/// The outcome of a widget instantiation, so a broken widget (constructor
/// threw) is distinguishable from an absent one (unknown plugin id). Pattern
/// match on the nested cases; <see cref="WidgetPluginLoader.CreateInstanceResult"/>
/// returns one per attempt.
/// </summary>
internal abstract record WidgetCreateResult
{
    private WidgetCreateResult()
    {
    }

    /// <summary>The widget was instantiated.</summary>
    internal sealed record Ok(IModernWidget Widget) : WidgetCreateResult;

    /// <summary>No plugin is registered under the requested id.</summary>
    internal sealed record NotFound : WidgetCreateResult;

    /// <summary>The widget's constructor threw; <see cref="Reason"/> carries the failure detail.</summary>
    internal sealed record Broken(string Reason) : WidgetCreateResult;
}
