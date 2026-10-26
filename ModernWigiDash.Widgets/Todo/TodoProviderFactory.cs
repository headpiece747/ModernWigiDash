namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The provider-composition seam: the property value + the first-run seed
/// gate + the companion-name sync, the one mapping to a task provider.
/// Super Productivity builds the server-backed provider; every other value
/// (Local Store, or an unknown hand-edited value) builds the file-backed
/// local provider over the default store, seeding the starter state only
/// when the profile cache is empty (a restored profile's tasks beat the
/// starter set — the gate reads the caller's task-cache JSON).
/// A test override (the widget's <c>ProviderFactory</c> seam) replaces the
/// mapping wholesale.
/// </summary>
internal sealed class TodoProviderFactory(TimeProvider clock, Func<string, ITaskProvider>? testOverride)
{
    /// <summary>Creates the provider for the property values.</summary>
    public ITaskProvider Create(string providerValue, string tasksJson, string companionName)
    {
        if (testOverride is not null)
            return testOverride(providerValue);

        if (string.Equals(providerValue, "Super Productivity", StringComparison.OrdinalIgnoreCase))
            return new SuperProductivityTaskProvider();

        var store = new TodoStore();
        // First-run seeding is the store's concern (the starter tasks land
        // only when the state file is absent); the profile-cache gate is the
        // caller's fact (its tasksJson), so a restored profile's tasks beat
        // the starter set.
        if (string.IsNullOrWhiteSpace(tasksJson) || string.Equals(tasksJson, "[]", StringComparison.Ordinal))
            store.SeedStarterState();
        var local = new LocalTaskProvider(store, clock);
        local.SetCompanionName(companionName);
        return local;
    }
}
