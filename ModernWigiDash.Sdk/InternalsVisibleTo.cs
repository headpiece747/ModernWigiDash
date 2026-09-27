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

// The Hardware grant is exactly one thing: the reconnect backoff core
// (ReconnectBackoff), the pure doubling rule the display engine's policy and
// the widget feed loops both compute through. Sdk is the only project both
// reference, so the rule has to live here to have one owner; Hardware could see
// it over the Widgets->Hardware edge instead, but that would make the layering
// depend on a direction the transports do not otherwise need. Public would work
// too and would be a worse answer: the core is an implementation detail of three
// loops, not an API this app offers.
[assembly: InternalsVisibleTo("ModernWigiDash.Hardware")]

// The App grant is exactly one thing: the named-map acquire policy
// (NamedMapAcquirePolicy), shared by the two adapters that read a producer-owned
// named map - LibreHardwareService's sensors map (App) and the AIDA64 panel map
// (Hardware). The producers live in different projects, so the leaf is the only
// place both can reach without App depending on a Hardware-internal or
// Hardware-internals being widened for it.
[assembly: InternalsVisibleTo("ModernWigiDash.App")]
