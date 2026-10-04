# Windows GUI MCP Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop MCP server that lets an Agent capture and operate one window selected by the local user.

**Architecture:** A single WPF process runs a local window picker and an MCP stdio server. The picker starts hidden; `show_window_picker` or an actual operation without a selected target opens it. Metadata discovery never shows the UI. Win32 enumerates and activates the selected HWND, Windows Graphics Capture supplies frames, and SendInput supplies mouse/keyboard events; MCP tools expose those operations to the Agent.

**Tech Stack:** .NET 10 LTS, WPF targeting `net10.0-windows10.0.18362.0`, ModelContextProtocol 2.1.0, Windows SDK projections from the .NET target framework, Vortice.Direct3D11 3.8.3, Win32 APIs.

**Spec:** `docs/superpowers/specs/2026-10-04-window-agent-mcp/design.md`

## Global Constraints

- Windows desktop only; the window capture API floor and target platform are Windows 10 version 1903 (Build 18362).
- A human selects exactly one visible top-level window in the local picker; MCP arguments never accept a window handle or change the binding.
- MCP startup and metadata discovery must not show the picker. A dedicated `show_window_picker` tool only displays the local human-operated selector; operations without a target display it and return `no_window_selected`.
- Screenshot and action coordinates are physical pixels in the selected window's client area, origin at top-left; reject out-of-range coordinates.
- Enable Per-Monitor V2 DPI awareness; recalculate capture and input mapping when the target size or DPI changes.
- Use `SendInput`; verify the selected target is foreground before each input, serialize actions, and do not retry partial input batches.
- Release tool-owned held mouse/keyboard state on error, cancellation, target loss, disconnect, and at the 60-second hold watchdog.
- Keep MCP JSON-RPC on stdout only; write diagnostics to stderr and never log screenshots or `type_text` contents.
- Publish a self-contained, single-file `win-x64` executable; put generated output under ignored `artifacts/`.
- Bootstrap .NET 10 locally in ignored `.tools/dotnet/`; place NuGet packages and the CLI home in ignored `.tools/`, do not modify system PATH or install globally. Build and publish scripts change to the repository root so `global.json` applies from any caller directory.
- Automated tests and CI were not part of the original implementation scope. The 2026-10-04 public release request adds a Windows-only unit test project and GitHub CI/release workflows below.
- Pin MCP SDK 2.1.0 while SDK 2.2.0 has an open binary content serialization issue affecting image blocks: [upstream issue #1835](https://github.com/modelcontextprotocol/csharp-sdk/issues/1835).

## Execution Status (2026-10-04)

Source files for Tasks 1–6 are present. The local .NET 10 SDK (10.0.302) and NuGet dependencies are installed under ignored `.tools/`. The target remains `net10.0-windows10.0.18362.0` to support Windows 10 version 1903 and later. The build and publish scripts created the self-contained `win-x64` executable under ignored `artifacts/`. The original implementation scope did not include automated tests; the public release plan below adds a Windows-only test suite and CI.

## Review Focus

These are the five behaviors most likely to fail in real use. The original implementation plan did not include automated tests, so review each implementation path directly; the public release plan below adds regression tests for the pure input helpers:

1. Target HWND is destroyed or reused: verify PID/process identity, clear the session, and never silently bind a different window.
2. Target is minimized, resized, or moves to a monitor with different DPI: reject stale frames/coordinates and rebuild the capture frame pool.
3. Focus activation fails or SendInput inserts only part of a batch: send no later action, report the failure, release tool-owned held inputs, and do not retry.
4. A request is cancelled or MCP disconnects during a drag or explicit hold: attempt button/key release and dispose capture resources.
5. Screenshot image content and MCP stdio framing: return PNG bytes as an MCP image block; keep all non-protocol output off stdout.

---

### Task 1: Bootstrap the SDK and create the WPF/MCP host shell

**Files:**
- Create: `.gitignore`
- Create: `global.json`
- Create: `eng/install-dotnet.ps1`
- Create: `src/WindowAgentMcp/WindowAgentMcp.csproj`
- Create: `src/WindowAgentMcp/app.manifest`
- Create: `src/WindowAgentMcp/App.xaml`
- Create: `src/WindowAgentMcp/App.xaml.cs`
- Create: `src/WindowAgentMcp/MainWindow.xaml`
- Create: `src/WindowAgentMcp/MainWindow.xaml.cs`
- Create: `src/WindowAgentMcp/Mcp/McpServerHost.cs`

**Interfaces:**
- `App` owns process lifetime, creates the service provider, shows `MainWindow`, starts MCP stdio processing on a background task, and shuts down WPF after stdin EOF/client disconnect; closing WPF cancels MCP and disposes services.
- `McpServerHost.RunAsync(CancellationToken)` starts the configured MCP host and waits for stdio/client shutdown; `StopAsync(CancellationToken)` closes it and releases registered services.
- `MainWindow` initially shows target status, a refreshable window list control, and Connect/Disconnect buttons; Task 2 supplies the enumeration and binding behavior.

- [ ] Create `global.json` pinned to SDK `10.0.100` with `rollForward` set to `latestFeature`.
- [ ] Create `eng/install-dotnet.ps1` to invoke Microsoft's `dotnet-install.ps1` for channel `10.0`, installing only into `.tools/dotnet` and making no system PATH changes.
- [ ] Create `.gitignore` entries for `.tools/`, `artifacts/`, `bin/`, `obj/`, and user-specific IDE files.
- [ ] Create a WPF `WinExe` project targeting `net10.0-windows10.0.18362.0`, with nullable reference types, implicit usings, and `ApplicationManifest` enabled.
- [ ] Pin package references to `ModelContextProtocol` 2.1.0, `Vortice.Direct3D11` 3.8.3, and `Microsoft.Extensions.Hosting` 10.0.0; use Windows SDK projections supplied by the target framework rather than the legacy `.winmd` Contracts package.
- [ ] Declare Per-Monitor V2 DPI awareness and supported OS floor in `app.manifest`.
- [ ] Before creating capture APIs, check for Windows 10 build 18362 or later and show a clear unsupported-OS message otherwise.
- [ ] Implement the WPF application lifetime so the picker UI remains responsive while MCP reads stdio asynchronously; configure logging to stderr only.
- [ ] Run `eng/install-dotnet.ps1`, then build the empty WPF/MCP shell with `.tools/dotnet/dotnet.exe build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit only the scaffold files with message `chore: scaffold Windows GUI MCP server`.

### Task 2: Enumerate windows and bind the user-selected target

**Files:**
- Create: `src/WindowAgentMcp/Windows/WindowDescriptor.cs`
- Create: `src/WindowAgentMcp/Windows/NativeMethods.cs`
- Create: `src/WindowAgentMcp/Windows/WindowCatalog.cs`
- Create: `src/WindowAgentMcp/Windows/WindowSession.cs`
- Create: `src/WindowAgentMcp/Windows/TargetWindowManager.cs`
- Modify: `src/WindowAgentMcp/MainWindow.xaml`
- Modify: `src/WindowAgentMcp/MainWindow.xaml.cs`
- Modify: `src/WindowAgentMcp/App.xaml.cs`

**Interfaces:**
- `WindowDescriptor(nint Handle, uint ProcessId, long ProcessStartUtcTicks, string Title, string ProcessName, string ClassName, bool IsMinimized)`; `ProcessStartUtcTicks` is `Process.StartTime.ToUniversalTime().Ticks`.
- `WindowCatalog.EnumerateVisibleWindows() -> IReadOnlyList<WindowDescriptor>`.
- `TargetWindowManager.Current -> WindowSession?`; `SelectAsync(WindowDescriptor, CancellationToken) -> Task`; `ClearAsync() -> Task`.
- `WindowSession` holds the original HWND/process identity and owns one capture session; it exposes `IsValid()` and is asynchronously disposable.

- [ ] Add Unicode-safe Win32 declarations for `EnumWindows`, `IsWindow`, `IsWindowVisible`, `IsIconic`, `GetWindowTextW`, `GetClassNameW`, `GetWindowThreadProcessId`, `DwmGetWindowAttribute`, and process metadata lookup.
- [ ] Implement window enumeration; omit this process's windows, untitled helper windows, hidden windows, and cloaked windows, but keep minimized state visible in the list.
- [ ] Capture process start time with PID/title/class metadata when constructing a descriptor; omit windows whose process identity cannot be read, and revalidate `IsWindow`, HWND PID, and process start time before creating a session and before returning a capture.
- [ ] Implement `TargetWindowManager.SelectAsync` to validate the selected descriptor (including process start time) before replacing the current session, then dispose the old session and bind only that HWND; propagate explicit failures to the UI.
- [ ] Populate the WPF list with title/process/PID/minimized status and implement Refresh, Connect, Disconnect, and reselect.
- [ ] Display the bound target and status; if the target closes, clear the session rather than finding a replacement by title.
- [ ] Run `dotnet build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit with message `feat: add explicit target window selection`.

### Task 3: Capture the target window and return a client-area PNG

**Files:**
- Create: `src/WindowAgentMcp/Capture/CapturedImage.cs`
- Create: `src/WindowAgentMcp/Capture/Direct3DDeviceFactory.cs`
- Create: `src/WindowAgentMcp/Capture/GraphicsCaptureInterop.cs`
- Create: `src/WindowAgentMcp/Capture/WindowCaptureService.cs`
- Modify: `src/WindowAgentMcp/Windows/WindowSession.cs`
- Modify: `src/WindowAgentMcp/Windows/TargetWindowManager.cs`

**Interfaces:**
- `CapturedImage(byte[] PngBytes, int Width, int Height, string CoordinateSpace, uint Dpi, string TargetTitle)`; `CoordinateSpace` is `client-px`.
- `WindowCaptureService.StartAsync(nint hwnd, CancellationToken) -> Task`; `CaptureAsync(CancellationToken) -> Task<CapturedImage>`; `DisposeAsync() -> ValueTask`.
- `WindowSession.CaptureAsync(CancellationToken) -> Task<CapturedImage>`.

- [ ] Create a Direct3D 11 device with Vortice and wrap its DXGI device as the WinRT `IDirect3DDevice` required by Windows.Graphics.Capture.
- [ ] Declare `IGraphicsCaptureItemInterop.CreateForWindow` and create a `GraphicsCaptureItem` for the exact selected HWND.
- [ ] Create a free-threaded frame pool and capture session; listen for `FrameArrived` and expose the next fresh frame through a cancellation-aware task.
- [ ] Convert the frame surface with `SoftwareBitmap.CreateCopyFromSurfaceAsync`; derive one frame-to-physical-screen transform from the WGC item/frame size, `GetWindowRect`, `GetClientRect`, and `ClientToScreen` under Per-Monitor V2 awareness. Normalize the frame to physical window pixels before cropping the exact client area, then report those same client-pixel dimensions; if the geometry does not reconcile, return `capture_unavailable` instead of guessing. Encode the crop as PNG with `BitmapEncoder` into an in-memory stream.
- [ ] On target close, device loss, unsupported capture, or minimized target, return the matching stable error; on size/DPI changes recreate the frame pool, recompute the transform, and discard stale frames.
- [ ] Store and dispose frame pool, capture session, frame, software bitmap, WinRT stream, and Direct3D objects in deterministic ownership order.
- [ ] Run `dotnet build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit with message `feat: capture selected window as MCP-ready PNG`.

### Task 4: Implement guarded mouse and keyboard input

**Files:**
- Create: `src/WindowAgentMcp/Input/InputModels.cs`
- Create: `src/WindowAgentMcp/Input/HeldInputRegistry.cs`
- Create: `src/WindowAgentMcp/Input/InputController.cs`
- Modify: `src/WindowAgentMcp/Windows/NativeMethods.cs`
- Modify: `src/WindowAgentMcp/Windows/WindowSession.cs`

**Interfaces:**
- `MouseButton`: `Left`, `Right`.
- `InputController.MoveMouseAsync(int x, int y, int durationMs, CancellationToken) -> Task`.
- `InputController.ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken) -> Task`.
- `InputController.MouseDownAsync(int x, int y, MouseButton button, CancellationToken) -> Task`; `MouseUpAsync(MouseButton button, CancellationToken) -> Task`.
- `InputController.DragAsync(int startX, int startY, int endX, int endY, MouseButton button, int durationMs, CancellationToken) -> Task`.
- `InputController.ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken) -> Task`.
- `InputController.TypeTextAsync(string text, CancellationToken) -> Task`.
- `InputController.KeyPressAsync(string key, IReadOnlyList<string> modifiers, CancellationToken) -> Task`; `KeyDownAsync(string key, CancellationToken) -> Task`; `KeyUpAsync(string key, CancellationToken) -> Task`.

- [ ] Add `INPUT` layouts/constants and P/Invokes for `SendInput`, `SetCursorPos`, `GetForegroundWindow`, `SetForegroundWindow`, `GetClientRect`, and `ClientToScreen`.
- [ ] Implement a shared guard that validates the current target identity, checks `IsIconic`, validates client-pixel bounds, foregrounds the selected HWND, and rechecks foreground immediately before input.
- [ ] Map client-pixel coordinates to physical screen coordinates with Per-Monitor V2 awareness; reject out-of-range points and stale target dimensions.
- [ ] Implement immediate/interpolated pointer movement, left/right clicks (single/double), explicit down/up holds, interpolated drag with guaranteed release cleanup, and horizontal/vertical wheel units.
- [ ] Implement Unicode `type_text` batches and a key catalog for letters, digits, function/navigation/control keys; support Ctrl/Alt/Shift chords and reject Windows-key/app-switching system shortcuts even when composed across calls.
- [ ] Track tool-owned held keys/buttons with a 60-second watchdog; reject duplicate down/unowned up; serialize input calls; on incomplete `SendInput`, cancellation, exception, unbind, or disposal, attempt releases and never replay the original batch.
- [ ] Map failures to stable `WindowAgentException` codes (`no_window_selected`, `window_closed`, `window_minimized`, `capture_unavailable`, `focus_failed`, `out_of_bounds`, `stale_coordinates`, `input_incomplete`, `input_failed`, `invalid_argument`, `invalid_key`, `invalid_button`, `state_error`, `unsupported_shortcut`, `request_cancelled`, `internal_error`); return a static UIPI/permission hint for incomplete input without claiming Windows identified UIPI as the cause.
- [ ] Run `dotnet build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit with message `feat: add guarded mouse and keyboard control`.

### Task 5: Expose screenshot and input functions as MCP tools

**Files:**
- Create: `src/WindowAgentMcp/Mcp/WindowAutomationTools.cs`
- Modify: `src/WindowAgentMcp/Mcp/McpServerHost.cs`
- Modify: `src/WindowAgentMcp/App.xaml.cs`
- Modify: `src/WindowAgentMcp/Windows/TargetWindowManager.cs`
- Create: `src/WindowAgentMcp/Windows/WindowPickerPresenter.cs`

**Interfaces:**
- Register `WindowAutomationTools` using `AddMcpServer().WithStdioServerTransport().WithTools<WindowAutomationTools>()`.
- Tool methods use `[McpServerTool(Name = "...")]`, descriptive attributes for each parameter, and `CancellationToken`.
- `screenshot` returns a text metadata block and `ImageContentBlock.FromBytes(pngBytes, "image/png")`.
- Action tool names and parameters match the approved spec exactly: `move_mouse`, `click`, `mouse_down`, `mouse_up`, `drag`, `scroll`, `type_text`, `key_press`, `key_down`, `key_up`.
- Domain errors return `CallToolResult` with `IsError = true` and a stable code/message; success results report input sent and the metadata required by the spec, without claiming the application accepted an action.

- [ ] Register all ten action tools plus `screenshot` and `show_window_picker`; no MCP tool accepts a target HWND or selects a window.
- [ ] Create the picker hidden and display it through the dispatcher only for explicit `show_window_picker` or an operation without a target; metadata discovery must have no visible-window side effect.
- [ ] Return screenshot PNG as `ImageContentBlock.FromBytes`; return dimensions, target title, and `client-px` origin in a `TextContentBlock`.
- [ ] Add parameter descriptions and numeric ranges: pointer duration 0–5000 ms, drag duration 50–10000 ms (default 500), click count 1 or 2, bounded coordinates, signed scroll notches.
- [ ] Route all actions through the target session and shared serialized input controller; convert expected failures to tool errors without leaking exception details or image/text payloads into logs, and return `no_window_selected` until the local picker binds a target.
- [ ] Return `move_mouse` target coordinates, `type_text` character count, and current button/key state where specified; describe input delivery only and leave application-result verification to a later screenshot.
- [ ] Ensure SDK logs and application diagnostics go to stderr and stdout is exclusively owned by the stdio MCP transport.
- [ ] Run `dotnet build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit with message `feat: expose window controls through MCP`.

### Task 6: Publish the executable and document MCP setup

**Files:**
- Create: `eng/publish.ps1`
- Create: `README.md`
- Modify: `.gitignore`
- Modify: `src/WindowAgentMcp/WindowAgentMcp.csproj`

**Interfaces:**
- `eng/publish.ps1` publishes Release `win-x64`, self-contained and single-file, to `artifacts/publish/win-x64/WindowAgentMcp.exe`.
- README documents build prerequisites, publish command, MCP stdio client configuration, local picker controls, tool names/coordinate convention, errors, and Windows/UIPI limitations.

- [ ] Configure self-contained single-file publishing without trimming WinRT/WPF assemblies; keep `artifacts/` ignored.
- [ ] Add an MCP client configuration example that launches the published executable directly with stdio.
- [ ] Document that the picker must bind a window before tools work, and that higher-integrity targets may reject SendInput.
- [ ] Run `eng/publish.ps1`; confirm the command completes and the expected executable is present. The public release plan below adds the automated test suite.
- [ ] Commit source and README changes only; leave generated SDK, package caches, and publish artifacts ignored.

### Lifecycle correction discovered during diagnostics

Codex `mcpServerStatus/list` creates and disposes temporary MCP connections. Showing the picker in every process startup caused the reported flicker. The existing single-process architecture is retained with on-demand presentation. The diagnostic reproduction uses the installed Codex app-server's actual status-list API, followed by direct MCP `initialize`, `tools/list`, `show_window_picker`, and a no-target `screenshot` check. See `docs/diagnostics/2026-10-04-selector-flicker.md` for evidence.

---

## Public GitHub Repository, CI, and First Release (2026-10-04)

**Goal:** Publish the verified Windows MCP server as a public MIT-licensed GitHub repository and release `v0.1.0` with an automatically built self-contained executable.

**Architecture:** Unit tests cover platform-independent input parsing and held-input state on `windows-latest`. Pull requests and pushes build and test the application. A pushed `v*` tag runs the same checks, publishes `win-x64`, and creates a GitHub Release with the executable attached.

**Tech Stack:** .NET 10, xUnit v3 with Microsoft.Testing.Platform, GitHub Actions, GitHub CLI.

**Files:**
- Create: `LICENSE`
- Create: `src/WindowAgentMcp.Tests/WindowAgentMcp.Tests.csproj`
- Create: `src/WindowAgentMcp.Tests/Input/InputKeyCatalogTests.cs`
- Create: `src/WindowAgentMcp.Tests/Input/HeldInputRegistryTests.cs`
- Create: `src/WindowAgentMcp/Properties/AssemblyInfo.cs`
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`
- Modify: `README.md`, `.gitignore`, and this plan

**Interfaces:** `WindowAgentMcp.Tests` references the application and accesses only `InputKeyCatalog` via `InternalsVisibleTo("WindowAgentMcp.Tests")`. Release tags use `v<semver>` and generate `artifacts/publish/win-x64/WindowAgentMcp.exe` before attaching it to the GitHub Release.

**Review Focus:**
- Unknown/system keys must remain unavailable, and only Ctrl/Alt/Shift qualify as modifiers.
- Held keys and buttons expire at the 60-second watchdog boundary and `TakeAll` clears the registry.
- Only intended source/docs/workflows/license are public; `.tools/`, `artifacts/`, local logs, and credentials stay excluded.
- The release job cannot publish an asset before build and tests succeed.
- Public commit metadata must use the account's ID-based `noreply` address, with commit content and messages preserved.

- [x] Add pinned xUnit v3/test SDK dependencies, enable the MTP runner in `global.json`, and add the Windows-targeted test project.
- [x] Test key aliases/case/whitespace, modifier allowlist, rejection of unsupported Windows key, held-input expiration, explicit release, and `TakeAll` state clearing.
- [x] Add a Windows CI workflow for pushes and pull requests that builds the app and executes the unit tests.
- [x] Add a Windows tag workflow that runs tests before publishing the self-contained single-file EXE and creating a release.
- [x] Add an MIT license and update README badges, repository links, and release/install instructions after determining the GitHub account.
- [x] Configure this repository with the account's ID-based `noreply` commit address and update the unpublished commit metadata without changing its content or messages.
- [x] Run local build and tests; review all staged paths and scan staged text for credentials before the first public push.
- [ ] Create a public `WindowAgentMcp` repository with GitHub CLI, push `main`, and push tag `v0.1.0`.
- [ ] Confirm the GitHub Actions run succeeds and the release contains the executable.
