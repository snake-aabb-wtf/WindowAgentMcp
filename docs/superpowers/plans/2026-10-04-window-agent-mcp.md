# Windows GUI MCP Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop MCP server that lets an Agent capture and operate one window selected by the local user.

**Architecture:** A single WPF process runs a local window picker and an MCP stdio server. Win32 enumerates and activates the selected HWND, Windows Graphics Capture supplies frames, and SendInput supplies mouse/keyboard events; MCP tools expose those operations to the Agent.

**Tech Stack:** .NET 10 LTS, WPF, ModelContextProtocol 2.1.0, Microsoft.Windows.SDK.Contracts 10.0.28000.2705, Vortice.Direct3D11 3.8.3, Win32 APIs.

**Spec:** `docs/superpowers/specs/2026-10-04-window-agent-mcp/design.md`

## Global Constraints

- Windows desktop only; the window capture API floor is Windows 10 version 1903 (Build 18362).
- A human selects exactly one visible top-level window in the local picker; MCP arguments never accept a window handle or change the binding.
- Screenshot and action coordinates are physical pixels in the selected window's client area, origin at top-left; reject out-of-range coordinates.
- Enable Per-Monitor V2 DPI awareness; recalculate capture and input mapping when the target size or DPI changes.
- Use `SendInput`; verify the selected target is foreground before each input, serialize actions, and do not retry partial input batches.
- Release tool-owned held mouse/keyboard state on error, cancellation, target loss, disconnect, and at the 60-second hold watchdog.
- Keep MCP JSON-RPC on stdout only; write diagnostics to stderr and never log screenshots or `type_text` contents.
- Publish a self-contained, single-file `win-x64` executable; put generated output under ignored `artifacts/`.
- The workspace has .NET 8 runtime but no SDK. Bootstrap .NET 10 locally in ignored `.tools/dotnet/`; do not modify system PATH or install globally.
- Do not add or run automated tests in this task. Each implementation task must remain buildable and ends with a `dotnet build` or publish build command; this is a compile check, not a test run.
- Pin MCP SDK 2.1.0 while SDK 2.2.0 has an open binary content serialization issue affecting image blocks: [upstream issue #1835](https://github.com/modelcontextprotocol/csharp-sdk/issues/1835).

## Review Focus

These are the five behaviors most likely to fail in real use; there is no automated test suite in this plan, so review each implementation path directly:

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
- `McpServerHost.StartAsync(CancellationToken)` starts the configured MCP host; `StopAsync(CancellationToken)` closes it and releases registered services.
- `MainWindow` initially shows target status, a refreshable window list control, and Connect/Disconnect buttons; Task 2 supplies the enumeration and binding behavior.

- [ ] Create `global.json` pinned to SDK `10.0.100` with `rollForward` set to `latestFeature`.
- [ ] Create `eng/install-dotnet.ps1` to invoke Microsoft's `dotnet-install.ps1` for channel `10.0`, installing only into `.tools/dotnet` and making no system PATH changes.
- [ ] Create `.gitignore` entries for `.tools/`, `artifacts/`, `bin/`, `obj/`, and user-specific IDE files.
- [ ] Create a WPF `WinExe` project targeting `net10.0-windows10.0.18362.0`, with nullable reference types, implicit usings, and `ApplicationManifest` enabled.
- [ ] Pin package references to `ModelContextProtocol` 2.1.0, `Microsoft.Windows.SDK.Contracts` 10.0.28000.2705, `Vortice.Direct3D11` 3.8.3, and `Microsoft.Extensions.Hosting` 10.0.0.
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
- `CapturedImage(byte[] PngBytes, int Width, int Height, string CoordinateSpace)`; `CoordinateSpace` is `client-px`.
- `WindowCaptureService.StartAsync(nint hwnd, CancellationToken) -> Task`; `CaptureAsync(CancellationToken) -> Task<CapturedImage>`; `DisposeAsync() -> ValueTask`.
- `WindowSession.CaptureAsync(CancellationToken) -> Task<CapturedImage>`.

- [ ] Create a Direct3D 11 device with Vortice and wrap its DXGI device as the WinRT `IDirect3DDevice` required by Windows.Graphics.Capture.
- [ ] Declare `IGraphicsCaptureItemInterop.CreateForWindow` and create a `GraphicsCaptureItem` for the exact selected HWND.
- [ ] Create a free-threaded frame pool and capture session; listen for `FrameArrived` and expose the next fresh frame through a cancellation-aware task.
- [ ] Convert the frame surface with `SoftwareBitmap.CreateCopyFromSurfaceAsync`; derive one frame-to-physical-screen transform from the WGC item/frame size, `GetWindowRect`, `GetClientRect`, and `ClientToScreen` under Per-Monitor V2 awareness. Use that transform both to crop the exact client area and to report client-pixel dimensions; if the geometry does not reconcile, return `capture_unavailable` instead of guessing. Encode the crop as PNG with `BitmapEncoder` into an in-memory stream.
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
- [ ] Map failures to stable `WindowAgentError` codes (`no_window_selected`, `window_closed`, `window_minimized`, `capture_unavailable`, `focus_failed`, `out_of_bounds`, `input_incomplete`, `invalid_key`, `invalid_button`, `state_error`, `request_cancelled`); return a static UIPI/permission hint for incomplete input without claiming Windows identified UIPI as the cause.
- [ ] Run `dotnet build src/WindowAgentMcp/WindowAgentMcp.csproj`.
- [ ] Commit with message `feat: add guarded mouse and keyboard control`.

### Task 5: Expose screenshot and input functions as MCP tools

**Files:**
- Create: `src/WindowAgentMcp/Mcp/WindowAutomationTools.cs`
- Modify: `src/WindowAgentMcp/Mcp/McpServerHost.cs`
- Modify: `src/WindowAgentMcp/App.xaml.cs`
- Modify: `src/WindowAgentMcp/Windows/TargetWindowManager.cs`

**Interfaces:**
- Register `WindowAutomationTools` using `AddMcpServer().WithStdioServerTransport().WithTools<WindowAutomationTools>()`.
- Tool methods use `[McpServerTool(Name = "...")]`, descriptive attributes for each parameter, and `CancellationToken`.
- `screenshot` returns a text metadata block and `ImageContentBlock.FromBytes(pngBytes, "image/png")`.
- Action tool names and parameters match the approved spec exactly: `move_mouse`, `click`, `mouse_down`, `mouse_up`, `drag`, `scroll`, `type_text`, `key_press`, `key_down`, `key_up`.
- Domain errors return `CallToolResult` with `IsError = true` and a stable code/message; success results report input sent and the metadata required by the spec, without claiming the application accepted an action.

- [ ] Register all ten action tools plus `screenshot`; no MCP tool accepts a target HWND or selects a window.
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
- [ ] Run `eng/publish.ps1`; confirm the command completes and the expected executable is present. Do not run an automated test suite.
- [ ] Commit source and README changes only; leave generated SDK, package caches, and publish artifacts ignored.
