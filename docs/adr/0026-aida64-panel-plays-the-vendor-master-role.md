# The AIDA64 panel widget plays the vendor master role

## Status

Accepted (2026-09-14)

## Context

ADR-0024 read the vendor's AIDA64 shared map **read-only**: the reader validated
the map and BMP headers and drew whatever sat in slot 0. On-device that produced
a static panel, usually AIDA64's own "configure the LCD" placeholder.

The vendor's protocol is two-sided. The reference tester
(`WigiDashServiceTest`) and a live run on 2026-09-14 proved it: a client must
**register a widget slot** (the header plus one 5x4 widget record) and **clear the
4-byte "new frame" marker** at the widget's bitmap offset after consuming a
frame. Only then does AIDA64 publish the next one. With registration plus ack,
AIDA64 pushed ~0.7-1 frame/s at 1016x592. Our read-only path did neither, so the
slot stayed exactly as AIDA64 had last left it.

Two facts constrain where the handshake can be written. The map is owned by the
vendor service (`InitAidaProvider` creates it) and the vendor's mutex is denied
to a non-elevated process, so the handshake cannot go through our read-only map
handle. And the service's binding caps **request** size (~64 KB), which fits the
4-116-byte handshake but not a 1.2 MB frame.

## Decision

- The AIDA64 panel widget registers the vendor's slot and drives the publish/ack
  protocol, playing the role the vendor Manager plays. `AidaPanelMaster` runs a
  background `PollLoop` (150 ms): it heartbeats the master counter, watches
  AIDA64's slave counter for liveness, and, when the marker at the slot's bitmap
  offset is non-zero, reads the frame directly from the map, publishes a copy,
  and clears the marker (the ack).
- The handshake writes go through the vendor WCF service
  (`InitAidaProvider` / `WriteAidaMmap`) behind the `IAidaMmapWriter` seam: the
  service's map handle carries the vendor's mutex, and the writes fit its request
  quota. Frame pixels are neither written nor read through WCF (the direct map
  read stands, ADR-0024).
- The ack zeroes the slot's BMP signature, so consumers read the master's
  **published copy** (an immutable `AidaPublishedFrame` from a three-deep ring,
  swapped by a volatile reference), never the slot.
- The master is created by the widget and started lazily on its first render. It
  does **not** de-initialize the vendor's provider on exit (the same
  shared-resource stance as the HWiNFO provider, ADR-0022/0025).
- The direct read-only reader is kept as the fallback and the test seam: when the
  master cannot register (no service), the widget still reads the slot, so a
  slot driven by another master keeps rendering.

## Consequences

- The AIDA64 panel shows live frames without the vendor Manager running, which is
  the deployment state whenever the app owns the display.
- Master mode writes to vendor shared state (the slot registration, the
  heartbeat, the ack). Two masters on slot 0 clear each other's frames, so master
  mode is mutually exclusive with the vendor Manager, which is already true when
  the app owns the device.
- The registration is left in place on exit and the vendor's provider is left
  initialized, so the vendor Manager's own init/deinit cycle is unaffected.
- The widget's render shape is unchanged: it draws the published copy, rebuilding
  only when the published generation changes.
