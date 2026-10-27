using System;
using System.IO;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Deep profile workspace module: unifies profile lifecycle, atomic mutations,
/// debounced persistence, mutation funnel dispatching, and manual import/export
/// portability behind a single authoritative interface.
/// <para>
/// Coordinates <see cref="ProfileSession"/>, <see cref="ProfilePersistence"/>,
/// and the import/export flows while maintaining the ADR-0021 theme bundle
/// invariant and ADR-0018 close-behavior merge invariant.
/// </para>
/// </summary>
internal sealed class ProfileWorkspace : IDisposable
{
    private readonly ProfilePersistence _persistence;
    private readonly ProfileSession _session;
    private readonly ProfileImportAdapter _importAdapter;
    private readonly Func<ProfileLayout> _profileProvider;
    private readonly Func<DialogHost> _dialogHostProvider;
    private readonly WidgetPluginLoader _loader;
    private readonly IModernWigiDashContext _context;

    /// <summary>
    /// Constructs a <see cref="ProfileWorkspace"/> coordinating the persistence,
    /// mutation session, and import adapter subsystems.
    /// </summary>
    public ProfileWorkspace(
        ProfilePersistence persistence,
        ProfileSession session,
        ProfileImportAdapter importAdapter,
        Func<ProfileLayout> profileProvider,
        Func<DialogHost> dialogHostProvider,
        WidgetPluginLoader loader,
        IModernWigiDashContext context)
    {
        _persistence = persistence;
        _session = session;
        _importAdapter = importAdapter;
        _profileProvider = profileProvider;
        _dialogHostProvider = dialogHostProvider;
        _loader = loader;
        _context = context;
    }

    /// <summary>The currently active profile layout.</summary>
    public ProfileLayout ActiveProfile => _profileProvider();

    /// <summary>The currently active page layout.</summary>
    public PageLayout ActivePage => ActiveProfile.ActivePage;

    /// <summary>The active page index.</summary>
    public int ActivePageIndex => ActiveProfile.ActivePageIndex;

    /// <summary>Whether the active page can be deleted.</summary>
    public bool CanDeletePage => _session.CanDeletePage;

    /// <summary>The underlying atomic mutation session.</summary>
    public ProfileSession Session => _session;

    /// <summary>The underlying persistence manager.</summary>
    public ProfilePersistence Persistence => _persistence;

    /// <summary>The import host adapter.</summary>
    public ProfileImportAdapter ImportAdapter => _importAdapter;

    // ── Mutation Facade ─────────────────────────────────────

    public PlacedWidgetInstance? PlaceWidgetCentered(string pluginId) => _session.PlaceWidgetCentered(pluginId);
    public bool RemoveWidget(PlacedWidgetInstance widget) => _session.RemoveWidget(widget);
    public bool ClearActivePage() => _session.ClearActivePage();
    public PageOpVerdict AddPage() => _session.AddPage();
    public PageOpVerdict DeletePage(int index) => _session.DeletePage(index);
    public PageOpVerdict RenamePage(int index) => _session.RenamePage(index);
    public PageOpVerdict SwitchToPage(int index) => _session.SwitchToPage(index);
    public void SetSnapToGrid(bool enabled) => _session.SetSnapToGrid(enabled);
    public void SetPageBackground(string hex) => _session.SetPageBackground(hex);
    public void ApplyRawWrite() => _session.ApplyRawWrite();
    public void CommitTransform(PlacedWidgetInstance? selection = null) => _session.CommitTransform(selection);

    // ── Portability & Persistence ───────────────────────────

    public void MarkDirty() => _persistence.MarkDirty();
    public void Save() => _persistence.Save();
    public void Flush() => _persistence.Flush();

    public bool SwapProfile(ProfileLayout newProfile) => _importAdapter.SwapProfile(newProfile);

    public ProfileExportFlowOutcome ExportToFile(string filePath, IProfileExportHost? host = null)
    {
        var exportHost = host ?? new DefaultExportHost(_profileProvider, _dialogHostProvider);
        return ProfileExportFlow.Run(filePath, exportHost);
    }

    public ProfileImportFlowOutcome ImportFromFile(string filePath, bool trusted = false)
    {
        var verdict = ProfileOps.ImportProfileFile(filePath, _loader, _context, trusted);
        return ProfileImportFlow.Run(verdict, _importAdapter);
    }

    public void ShowSuccess(string title, string message) => _dialogHostProvider().Info(title, message);
    public void ShowError(string title, string message) => _dialogHostProvider().Error(title, message);

    public void Dispose()
    {
        _persistence.Dispose();
    }

    private sealed class DefaultExportHost(Func<ProfileLayout> profileProvider, Func<DialogHost> dialogHostProvider) : IProfileExportHost
    {
        public ProfileLayout Profile => profileProvider();
        public ThemeSettings CurrentTheme => Theming.ThemeStore.Current;
        public void ShowSuccess(string title, string message) => dialogHostProvider().Info(title, message);
        public void ShowError(string title, string message) => dialogHostProvider().Error(title, message);
    }
}
