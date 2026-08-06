# ADR-0002: Named-Pipe WCF Transport

**Date:** 2026-08-06  
**Status:** Accepted  
**Deciders:** Project owner

## Context

The WCF endpoint between the Windows Service and the WPF app was originally hosted over `basicHttpBinding` on `http://localhost:8733/`. The service binds to localhost, so there is no network exposure, but the transport still:

- Runs over TCP sockets (`localhost:8733`), which any local process can probe and, in principle, impersonate at the protocol level if the port is free.
- Relies on HTTP health probes for service discovery, which adds a TCP round-trip before the WCF handshake.
- Carries HTTP framing overhead for every frame (1.23 MB framebuffer payloads at ~30 FPS).

Named pipes on Windows are backed by kernel-level ACLs on the pipe namespace — a local process without the right identity cannot open the pipe at all. This removes the impostor-connection concern at the transport layer instead of the protocol layer.

## Decision

**Host the CoreWCF service over named pipes** instead of HTTP.

- Server: `builder.WebHost.UseNetNamedPipe(options => options.Listen(new Uri("net.pipe://localhost/ModernWigiDashDisplayService/")))` with a `NetNamedPipeBinding` (transport security mode) and endpoint relative path `WigiDash.svc`.
- Client: `System.ServiceModel.NetNamedPipeBinding` connecting to `net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc`.
- Service discovery: `DetectServicePort` probes the known pipe endpoints and requires the WCF `GetVersion` handshake — an impostor pipe cannot hijack frames without speaking the contract.
- The trailing slash on the `Listen` base URI is required: CoreWCF derives the pipe name from that URI, and without it the listener silently binds nothing.

## Consequences

**Positive:**
- Kernel-level ACL security on the pipe namespace — no TCP endpoint exists to probe.
- No HTTP framing overhead per frame; no HTTP health-probe round-trip during discovery.
- Client and service both fail closed if the pipe is absent (`EndpointNotFoundException`), which surfaces as a clean "service not running" state in the app.

**Negative:**
- Windows-only (the project already is).
- The OS-level pipe name is a random GUID published via CoreWCF's shared-memory mechanism — the pipe is not directly visible at a predictable `\\.\pipe\` name, which can confuse debugging.
- `UseUrls()` cannot be used for the pipe address (Kestrel rejects non-HTTP schemes); the listener must be configured exclusively through `UseNetNamedPipe`, which is a CoreWCF-specific hosting path.

**Rationale:** The security posture of the transport is the deciding factor. HTTP-on-localhost was acceptable but left the door open for any local process to connect; named pipes close that door at the kernel. The protocol-level `GetVersion` handshake remains as a second layer of defense against a process that somehow obtains pipe access.

## Alternatives considered

1. **Keep `basicHttpBinding` on localhost** — worked, but no kernel-level access control and TCP overhead per frame. Rejected for the security delta.
2. **Add Windows authentication to the HTTP binding** — `BasicHttpSecurityMode.TransportCredentialOnly` still leaves a TCP socket and adds auth plumbing to every call. Rejected: named pipes get the same guarantee for free.
3. **Use `UseUrls()` with a `net.pipe://` address** — Kestrel throws "Unrecognized scheme"; the pipe transport must be registered via `UseNetNamedPipe` only. Rejected as non-working.

## Date

2026-08-06
