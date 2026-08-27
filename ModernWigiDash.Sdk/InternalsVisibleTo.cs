using System.Runtime.CompilerServices;

// The Tests grant: the Sdk test seams (the FrameDelivery internal ctor +
// counters, the FileLog clock, the telemetry facade's fake-clock store)
// are driven straight from the test project.
[assembly: InternalsVisibleTo("ModernWigiDash.Tests")]

// The Widgets grant is exactly one thing: the telemetry store facade's
// internal test seams (CreateStoreForTest / StoreForTest). The Widgets
// stores (LhmSensorStore, FrameTimeStore) forward those seams so the
// tests can rebind the SAME facade instance behind the production
// read/update surface with a fake clock. A public seam on the facade
// would be a test-only API on the production class (the facade's
// documented "no test-only twin" shape); a test-built facade instance
// would test the Sdk surface instead of the store surface.
[assembly: InternalsVisibleTo("ModernWigiDash.Widgets")]
