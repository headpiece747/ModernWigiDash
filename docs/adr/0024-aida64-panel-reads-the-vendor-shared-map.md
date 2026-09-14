# Read the AIDA64 panel from the vendor's shared map

## Status

Accepted (2026-09-13)

## Context

The vendor WigiDash service exposes `ReadAidaMmap(offset, length)` over WCF,
returning bytes from the vendor's shared map `Global\Gskill_Frontier_Aida64Widget`
where AIDA64 publishes its rendered sensor panel. Reading one full 1016×592
RGB565 panel through that call ships 1,202,944 raw bytes, base64-encoded inside a
SOAP envelope (~1.6 MB) per frame; the WCF-era fix had to raise the binding quota
to 20 MB just to fit a single frame (commit `167875a`). A panel is a 30 FPS-class
surface and the widget redraws on the app's render tick, so the round trip, not
the copy, was the binding cost.

The map itself is a named 8 MB section (`Global\Gskill_Frontier_Aida64Widget`,
mutex `Global\Gskill_Frontier_Aida64WidgetMutex`) whose layout is declared in the
vendor's `WigiDashWcf.dll` (`Aida64MmapDef`, decompiled 2026-09-13): magic
`0xC0FFFEEE` at offset 8, a master/slave counter pair, a header, then per-widget
records. The first widget's bitmap is a 66-byte BMP header (RGB565, BI_BITFIELDS,
masks F800/07E0/001F) followed by `width × height × 2` pixel bytes. Verified
against the live map on-device 2026-09-13: one widget, 1016×592, `bitmapSize`
1,203,010 at `bitmapOffset` 116.

## Decision

- The AIDA64 panel widget reads the vendor's shared map directly
  (`MemoryMappedAidaMmapSource` + `AidaMmapReader` under
  `ModernWigiDash.Hardware/Aida64`) instead of going through the WCF
  `ReadAidaMmap` call. `IWigiDashWcf` drops its AIDA operations and
  `WigiDashServiceClient` keeps only the HWiNFO sensor facet (ADR-0022).
- The reader validates the map header and the embedded BMP header before it
  trusts a frame, and returns null (never throws) on any mismatch. A torn read or
  an AIDA64 restart therefore degrades to the widget's placeholder for that frame
  and recovers on the next.
- The header's widget count is the vendor Manager's registered-widget count, not
  a liveness signal. AIDA64 still publishes its own "configure the LCD" placeholder
  into slot 0 while the count is 0, and the vendor Manager displays that image
  (verified on-device 2026-09-13). The reader therefore accepts a valid slot 0 at
  count 0 and shows it, matching the vendor Manager; only a negative count is
  malformed, and the widget record's geometry plus the BMP header remain the
  real validation.
- The payload is a bottom-up BMP body (the reader requires a positive biHeight)
  while an `SKBitmap` is top-down, so the widget copies rows in reverse. A
  straight copy drew the panel upside down on-device.
- The vendor creates the map mutex with a descriptor that denies a non-elevated
  process. An unopenable mutex is not a failure: the adapter copies without the
  lock, and the map-open failure is the AIDA64-is-down signal. A mutex that opens
  but cannot be acquired within the timeout still fails the read.
- No vendor binary is forked or bundled; only the map name, size, and header
  layout are mirrored, the same consumption-only stance as ADR-0022.
- Only the first widget record is read. The vendor's own Manager composites every
  widget the map carries, but the AIDA64 panel use case publishes one full-size
  widget (verified live: `numWidgets` = 1, 1016×592). A multi-widget map would
  show only the first panel; that is the accepted scope until a user needs it.

## Consequences

- A frame costs one 1.2 MB copy per redraw instead of a ~1.6 MB base64 SOAP
  round trip, and the render tick no longer waits on a WCF call.
- Unlocked reads can catch a torn frame while AIDA64 redraws. The reader rejects
  malformed frames, so such a frame shows the placeholder rather than garbage.
- AIDA64 must be running for the map to exist. Absent AIDA64 degrades to the
  house placeholder (the ADR-0017 image).
- The map layout is vendor-internal. A vendor update that changes it makes the
  reader degrade to the placeholder (the header validation is the tripwire)
  rather than misrendering the panel.
