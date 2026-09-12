# Architecture Conformance Report: ModernWigiDash

**Date:** 2026-09-12 | **Baseline:** CONTEXT.md layering table (machine-pinned by ArchitectureTests) | **Tooling:** Glider MCP + raw-scan

## Verdict: CONFORMANT

No critical, high, or medium findings. The code matches the architecture it claims.

## Step-by-step

### 1. Declared architecture
Inward-only layering, Sdk the leaf, App the top:
- Core -> Sdk
- Hardware -> Sdk
- Widgets -> Core + Sdk
- App -> Core + Hardware + Sdk + Widgets
- Tests -> all five

### 2. Project-level dependency direction (glider_get_project_graph)
13 edges, every one in the allowlist. Root = Tests, leaf = Sdk. **Pass.**

### 3. Cycles
`cycleGroupCount: 0` at project level; no type-level cycles surfaced. **Pass.**

### 4. Namespace-level leak probes (raw-scan of `using ModernWigiDash.*`)
- Sdk (leaf): imports no other repo project namespace. **Clean.**
- Core: imports no Hardware/Widgets/App namespace. **Clean.**
- Hardware: imports no Core/Widgets/App namespace. **Clean.**
- Widgets: imports Core.Models (HotkeyButtonWidget.cs, WeatherForecastWidget.cs) - a documented Widgets->Core edge. **Allowed.**
- Internal-type containment: DisplayHidTransport (Internal, Hardware) referenced only by Hardware + Tests (via InternalsVisibleTo), never by App. **Contained.**

### 5. Presentation boundary
Not applicable - desktop WPF app, no HTTP endpoints / API host layer.

## Notes
- The layering is not just checked here; it is machine-pinned by ArchitectureTests against the csproj files, so drift fails the gate before commit. This report corroborates that pin with an independent read.
- ADR-0001 (synchronous transport seam) holds: IDisplayTransport / ITransferBackend / DisplayHidTransport expose no async members (pinned by ArchitectureTests.TransportSeam_Adr0001_SynchronousByConstruction).
