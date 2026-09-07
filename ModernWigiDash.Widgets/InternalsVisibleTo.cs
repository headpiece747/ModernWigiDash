using System.Runtime.CompilerServices;

// The inspector's calendar feed editor (App) routes its parse/serialize through
// the shared CalendarFeedsCodec owner and maps its typed records to display
// drafts, so it needs the internal feed record types (CalendarFeed / IcsUrlFeed
// / CalDavFeed) and the codec itself. The App is the host layer; exposing these
// internals to it is the same trust the Tests assembly already has.
[assembly: InternalsVisibleTo("ModernWigiDash.Tests")]
[assembly: InternalsVisibleTo("ModernWigiDash.App")]
