using System.Reflection;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The property-persistence facet of the widget host context: the members a
/// widget needs to commit property changes into the owning placed instance's
/// PropertyValues (so they survive Export→Import). Split from
/// <see cref="IModernWigiDashContext"/> so a widget that persists properties
/// depends on this capability explicitly instead of seeing every host service
/// (the <c>IWidgetActionInvoker</c> / <c>IWidgetEditorProvider</c> optional-facet
/// precedent). A host that does not track placed instances simply does not
/// implement it; <see cref="ModernWidgetBase.PersistingContext"/> is then null
/// and the commit degrades to the instance-only half.
/// </summary>
public interface IWidgetPropertyPersistingContext
{
    /// <summary>
    /// Persists a widget property change into the owning placed instance's
    /// PropertyValues, so the change survives Export→Import. The App's context
    /// resolves the placed instance by identity; a host without this facet
    /// skips the persistence half (the instance property still carries the
    /// value).
    /// </summary>
    void PersistProperty(object widget, string propertyName, object? value);

    /// <summary>
    /// The single commit owner for "set a property value on a placed widget":
    /// sets the instance property, raises
    /// <see cref="IModernWidget.OnPropertyChanged"/>, and persists into the
    /// owning placed instance's PropertyValues through
    /// <see cref="PersistProperty"/>. The inspector's write-back funnel and
    /// <see cref="ModernWidgetBase.SetProperty"/> both commit through here, so
    /// the instance ↔ PropertyValues invariant has one spelling: a write path
    /// that forgets the PropertyValues half cannot exist, because there is no
    /// other commit.
    /// </summary>
    void SetWidgetProperty(object widget, PropertyInfo property, object? value)
    {
        property.SetValue(widget, value);
        (widget as IModernWidget)?.OnPropertyChanged(property.Name, value);
        PersistProperty(widget, property.Name, value);
    }
}
