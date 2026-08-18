namespace ModernWigiDash.Sdk;

/// <summary>
/// Optional interface widgets can implement to expose inspector buttons
/// (WidgetPropertyType.Button) that trigger a widget-specific action.
/// </summary>
public interface IWidgetActionInvoker
{
    /// <summary>Host call when the inspector button for
    /// <paramref name="propertyName"/> is clicked; the widget runs its action
    /// (e.g. Twitch login) and refreshes the inspector/render via the context.</summary>
    void InvokeWidgetAction(string propertyName);
}
