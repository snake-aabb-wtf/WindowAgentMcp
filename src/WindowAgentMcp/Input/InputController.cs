using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WindowAgentMcp.Errors;
using WindowAgentMcp.Windows;

namespace WindowAgentMcp.Input;

public sealed class InputController : IAsyncDisposable
{
    private const int WheelDelta = 120;
    private const int MaximumPointerDurationMs = 5000;
    private const int MaximumDragDurationMs = 10000;
    private const int MaximumTextLength = 100000;
    private readonly WindowSession _session;
    private readonly HeldInputRegistry _heldInputs = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly Timer _watchdog;
    private int _disposed;

    public InputController(WindowSession session)
    {
        _session = session;
        _watchdog = new Timer(WatchdogTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            if (durationMs is < 0 or > MaximumPointerDurationMs)
            {
                throw new WindowAgentException("invalid_argument", $"duration_ms must be between 0 and {MaximumPointerDurationMs}.");
            }

            var area = await PreparePointAsync(x, y, token).ConfigureAwait(false);
            await MovePointerAsync(x, y, durationMs, area, token).ConfigureAwait(false);
        });
    }

    public Task ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            if (clickCount is not (1 or 2))
            {
                throw new WindowAgentException("invalid_argument", "click_count must be 1 or 2.");
            }

            if (_heldInputs.ContainsButton(button))
            {
                throw new WindowAgentException("state_error", "Release the held mouse button before clicking it.");
            }

            var area = await PreparePointAsync(x, y, token).ConfigureAwait(false);
            for (var click = 0; click < clickCount; click++)
            {
                if (click > 0)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }

                await PlaceCursorAsync(x, y, area, token).ConfigureAwait(false);
                SendBatch([CreateMouseInput(MouseDownFlag(button)), CreateMouseInput(MouseUpFlag(button))]);
            }
        });
    }

    public Task MouseDownAsync(int x, int y, MouseButton button, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            if (_heldInputs.ContainsButton(button))
            {
                throw new WindowAgentException("state_error", $"The {button.ToString().ToLowerInvariant()} mouse button is already held.");
            }

            var area = await PreparePointAsync(x, y, token).ConfigureAwait(false);
            await PlaceCursorAsync(x, y, area, token).ConfigureAwait(false);
            SendBatch([CreateMouseInput(MouseDownFlag(button))]);
        });
    }

    public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            if (!_heldInputs.ContainsButton(button))
            {
                throw new WindowAgentException("state_error", $"The {button.ToString().ToLowerInvariant()} mouse button is not held by this server.");
            }

            await EnsureTargetForegroundAsync(token).ConfigureAwait(false);
            SendBatch([CreateMouseInput(MouseUpFlag(button))]);
        });
    }

    public Task DragAsync(int startX, int startY, int endX, int endY, MouseButton button, int durationMs, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            if (durationMs is < 50 or > MaximumDragDurationMs)
            {
                throw new WindowAgentException("invalid_argument", $"duration_ms must be between 50 and {MaximumDragDurationMs}.");
            }

            if (_heldInputs.ContainsButton(button))
            {
                throw new WindowAgentException("state_error", $"Release the held mouse button before dragging it.");
            }

            var startArea = await PreparePointAsync(startX, startY, token).ConfigureAwait(false);
            _ = await PreparePointAsync(endX, endY, token).ConfigureAwait(false);
            await PlaceCursorAsync(startX, startY, startArea, token).ConfigureAwait(false);
            SendBatch([CreateMouseInput(MouseDownFlag(button))]);

            var steps = Math.Clamp((int)Math.Ceiling(durationMs / 16.0), 1, 625);
            var delay = TimeSpan.FromMilliseconds((double)durationMs / steps);
            for (var step = 1; step <= steps; step++)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                var area = GetFreshClientArea();
                EnsureSameClientSize(startArea, area);
                var x = Interpolate(startX, endX, step, steps);
                var y = Interpolate(startY, endY, step, steps);
                await PlaceCursorAsync(x, y, area, token).ConfigureAwait(false);
            }

            EnsureForegroundStillTarget();
            SendBatch([CreateMouseInput(MouseUpFlag(button))]);
        });
    }

    public Task ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            var area = await PreparePointAsync(x, y, token).ConfigureAwait(false);
            await PlaceCursorAsync(x, y, area, token).ConfigureAwait(false);
            var inputs = new List<NativeMethods.NativeInput>(2);
            if (deltaY != 0)
            {
                inputs.Add(CreateMouseInput(NativeMethods.MouseWheel, ToWheelData(deltaY)));
            }

            if (deltaX != 0)
            {
                inputs.Add(CreateMouseInput(NativeMethods.MouseHorizontalWheel, ToWheelData(deltaX)));
            }

            if (inputs.Count > 0)
            {
                SendBatch(inputs);
            }
        });
    }

    public Task<int> TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            if (text is null || text.Length > MaximumTextLength)
            {
                throw new WindowAgentException("invalid_argument", $"text must be at most {MaximumTextLength} UTF-16 code units.");
            }

            await EnsureTargetForegroundAsync(token).ConfigureAwait(false);
            var inputCount = checked(text.Length * 2);
            var inputs = new NativeMethods.NativeInput[inputCount];
            for (var index = 0; index < text.Length; index++)
            {
                var codeUnit = text[index];
                inputs[index * 2] = CreateUnicodeInput(codeUnit, keyUp: false);
                inputs[index * 2 + 1] = CreateUnicodeInput(codeUnit, keyUp: true);
            }

            if (inputs.Length > 0)
            {
                SendBatch(inputs);
            }

            return text.EnumerateRunes().Count();
        });
    }

    public Task KeyPressAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            var virtualKey = ParseKey(key);
            var modifierKeys = ParseModifiers(modifiers);
            if (modifierKeys.Contains(virtualKey))
            {
                throw new WindowAgentException("invalid_argument", "A key cannot also be listed as its own modifier.");
            }

            var currentlyHeld = _heldInputs.Snapshot().Keys
                .Where(held => !held.IsUnicode)
                .Select(held => held.VirtualKey)
                .ToHashSet();
            if (currentlyHeld.Contains(virtualKey))
            {
                throw new WindowAgentException("state_error", "Release the held key before using key_press for it.");
            }

            var addedModifiers = modifierKeys.Where(modifier => !currentlyHeld.Contains(modifier)).ToArray();
            EnsureAllowedShortcut(addedModifiers.Append(virtualKey));
            await EnsureTargetForegroundAsync(token).ConfigureAwait(false);

            var inputs = new List<NativeMethods.NativeInput>(addedModifiers.Length * 2 + 2);
            inputs.AddRange(addedModifiers.Select(modifier => CreateKeyboardInput(modifier, keyUp: false)));
            inputs.Add(CreateKeyboardInput(virtualKey, keyUp: false));
            inputs.Add(CreateKeyboardInput(virtualKey, keyUp: true));
            inputs.AddRange(addedModifiers.Reverse().Select(modifier => CreateKeyboardInput(modifier, keyUp: true)));
            SendBatch(inputs);
        });
    }

    public Task KeyDownAsync(string key, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            var virtualKey = ParseKey(key);
            var identity = new KeyIdentity(virtualKey, 0, false);
            if (_heldInputs.ContainsKey(identity))
            {
                throw new WindowAgentException("state_error", $"The key '{key}' is already held.");
            }

            EnsureAllowedShortcut([virtualKey]);
            await EnsureTargetForegroundAsync(token).ConfigureAwait(false);
            SendBatch([CreateKeyboardInput(virtualKey, keyUp: false)]);
        });
    }

    public Task KeyUpAsync(string key, CancellationToken cancellationToken)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            token.ThrowIfCancellationRequested();
            var virtualKey = ParseKey(key);
            var identity = new KeyIdentity(virtualKey, 0, false);
            if (!_heldInputs.ContainsKey(identity))
            {
                throw new WindowAgentException("state_error", $"The key '{key}' is not held by this server.");
            }

            await EnsureTargetForegroundAsync(token).ConfigureAwait(false);
            SendBatch([CreateKeyboardInput(virtualKey, keyUp: true)]);
        });
    }

    private async Task<T> ExecuteAsync<T>(CancellationToken cancellationToken, Func<CancellationToken, Task<T>> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _inputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ReleaseAllHeldInputs();
            }
            finally
            {
                _inputGate.Release();
            }

            throw new WindowAgentException("request_cancelled", "The input request was cancelled; held inputs were released.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseAllHeldInputs();
            throw new WindowAgentException("request_cancelled", "The input request was cancelled; held inputs were released.");
        }
        catch
        {
            ReleaseAllHeldInputs();
            throw;
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private Task ExecuteAsync(CancellationToken cancellationToken, Func<CancellationToken, Task> operation)
    {
        return ExecuteAsync(cancellationToken, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        });
    }

    private async Task<ClientAreaInfo> PreparePointAsync(int x, int y, CancellationToken cancellationToken)
    {
        await EnsureTargetForegroundAsync(cancellationToken).ConfigureAwait(false);
        var area = GetFreshClientArea();
        if (x < 0 || y < 0 || x >= area.Width || y >= area.Height)
        {
            throw new WindowAgentException("out_of_bounds", $"Coordinates must satisfy 0 <= x < {area.Width} and 0 <= y < {area.Height}.");
        }

        return area;
    }

    private ClientAreaInfo GetFreshClientArea()
    {
        var area = _session.GetClientAreaInfo();
        var capturedArea = _session.LastCapturedArea;
        if (capturedArea is null
            || capturedArea.Width != area.Width
            || capturedArea.Height != area.Height
            || capturedArea.Dpi != area.Dpi)
        {
            throw new WindowAgentException("stale_coordinates", "The target window size or DPI changed. Take a fresh screenshot before using coordinates.");
        }

        return area;
    }

    private static void EnsureSameClientSize(ClientAreaInfo expected, ClientAreaInfo actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height || expected.Dpi != actual.Dpi)
        {
            throw new WindowAgentException("stale_coordinates", "The target window size or DPI changed during the input action.");
        }
    }

    private Task PlaceCursorAsync(int x, int y, ClientAreaInfo area, CancellationToken cancellationToken)
    {
        EnsureSameClientSize(area, GetFreshClientArea());
        cancellationToken.ThrowIfCancellationRequested();
        var currentArea = GetFreshClientArea();
        EnsureSameClientSize(area, currentArea);
        EnsureForegroundStillTarget();

        int screenX;
        int screenY;
        try
        {
            screenX = checked(currentArea.ScreenX + x);
            screenY = checked(currentArea.ScreenY + y);
        }
        catch (OverflowException exception)
        {
            throw new WindowAgentException("out_of_bounds", "The selected client point cannot be represented as a screen coordinate.", exception);
        }

        if (!NativeMethods.SetCursorPos(screenX, screenY))
        {
            var error = Marshal.GetLastWin32Error();
            throw new WindowAgentException("input_failed", "Windows could not move the cursor to the selected client point.", new Win32Exception(error));
        }

        return Task.CompletedTask;
    }

    private async Task MovePointerAsync(int x, int y, int durationMs, ClientAreaInfo area, CancellationToken cancellationToken)
    {
        if (!NativeMethods.GetCursorPos(out var start))
        {
            var error = Marshal.GetLastWin32Error();
            throw new WindowAgentException("input_failed", "Windows could not read the current cursor position.", new Win32Exception(error));
        }

        var steps = durationMs == 0 ? 1 : Math.Clamp((int)Math.Ceiling(durationMs / 16.0), 1, 313);
        var delay = steps == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds((double)durationMs / steps);
        for (var step = 1; step <= steps; step++)
        {
            if (durationMs > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var currentArea = GetFreshClientArea();
            EnsureSameClientSize(area, currentArea);
            EnsureForegroundStillTarget();
            var ratio = (double)step / steps;
            var endX = checked(currentArea.ScreenX + x);
            var endY = checked(currentArea.ScreenY + y);
            var currentX = (int)Math.Round(start.X + (endX - start.X) * ratio);
            var currentY = (int)Math.Round(start.Y + (endY - start.Y) * ratio);
            if (!NativeMethods.SetCursorPos(currentX, currentY))
            {
                var error = Marshal.GetLastWin32Error();
                throw new WindowAgentException("input_failed", "Windows could not move the cursor.", new Win32Exception(error));
            }
        }
    }

    private async Task EnsureTargetForegroundAsync(CancellationToken cancellationToken)
    {
        EnsureTargetAvailable();
        var hwnd = _session.Descriptor.Handle;
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            _ = NativeMethods.SetForegroundWindow(hwnd);
            for (var attempt = 0; attempt < 20 && NativeMethods.GetForegroundWindow() != hwnd; attempt++)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        EnsureForegroundStillTarget();
    }

    private void EnsureForegroundStillTarget()
    {
        EnsureTargetAvailable();
        if (NativeMethods.GetForegroundWindow() != _session.Descriptor.Handle)
        {
            throw new WindowAgentException("focus_failed", "The selected window is no longer in the foreground; no input was sent.");
        }
    }

    private void EnsureTargetAvailable()
    {
        if (!_session.IsValid())
        {
            throw new WindowAgentException("window_closed", "The selected window is no longer available. Select a window again.");
        }

        if (NativeMethods.IsIconic(_session.Descriptor.Handle))
        {
            throw new WindowAgentException("window_minimized", "Restore the selected window before sending input.");
        }
    }

    private void EnsureAllowedShortcut(IEnumerable<ushort> extraKeys)
    {
        var keys = _heldInputs.Snapshot().Keys
            .Where(key => !key.IsUnicode)
            .Select(key => key.VirtualKey)
            .Concat(extraKeys)
            .ToHashSet();

        var alt = keys.Contains(0x12);
        var control = keys.Contains(0x11);
        var forbidden = (alt && (keys.Contains(0x09) || keys.Contains(0x1B) || keys.Contains(0x73)))
                        || (control && keys.Contains(0x1B))
                        || (control && alt && keys.Contains(0x2E));
        if (forbidden)
        {
            throw new WindowAgentException("unsupported_shortcut", "Application-switching and system shortcuts are not allowed.");
        }
    }

    private ushort ParseKey(string key)
    {
        if (!InputKeyCatalog.TryParseKey(key, out var virtualKey))
        {
            throw new WindowAgentException("invalid_key", $"The key name '{key}' is not supported. Windows-key aliases are not accepted.");
        }

        return virtualKey;
    }

    private static ushort[] ParseModifiers(IReadOnlyList<string>? modifiers)
    {
        if (modifiers is null || modifiers.Count == 0)
        {
            return [];
        }

        var parsed = new List<ushort>(modifiers.Count);
        foreach (var modifier in modifiers)
        {
            if (!InputKeyCatalog.TryParseModifier(modifier, out var key))
            {
                throw new WindowAgentException("invalid_key", "Modifiers may contain only Ctrl, Alt, or Shift.");
            }

            if (parsed.Contains(key))
            {
                throw new WindowAgentException("invalid_argument", "A modifier may only appear once.");
            }

            parsed.Add(key);
        }

        return parsed.ToArray();
    }

    private void SendBatch(IReadOnlyList<NativeMethods.NativeInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        EnsureForegroundStillTarget();
        var nativeInputs = inputs as NativeMethods.NativeInput[] ?? inputs.ToArray();
        var sent = NativeMethods.SendInput(
            checked((uint)nativeInputs.Length),
            nativeInputs,
            Marshal.SizeOf<NativeMethods.NativeInput>());

        var applied = Math.Min(sent, checked((uint)nativeInputs.Length));
        for (var index = 0; index < applied; index++)
        {
            ApplyHeldState(nativeInputs[index]);
        }

        if (sent != checked((uint)nativeInputs.Length))
        {
            throw new WindowAgentException(
                "input_incomplete",
                $"Windows accepted {sent} of {nativeInputs.Length} input events. Windows may not identify UIPI as the cause; check that the app and target use compatible privilege levels.");
        }
    }

    private void ApplyHeldState(NativeMethods.NativeInput input)
    {
        if (input.Type == NativeMethods.InputMouse)
        {
            var flags = input.Data.Mouse.Flags;
            if ((flags & NativeMethods.MouseLeftDown) != 0)
            {
                _heldInputs.PressButton(MouseButton.Left);
            }
            else if ((flags & NativeMethods.MouseLeftUp) != 0)
            {
                _heldInputs.ReleaseButton(MouseButton.Left);
            }
            else if ((flags & NativeMethods.MouseRightDown) != 0)
            {
                _heldInputs.PressButton(MouseButton.Right);
            }
            else if ((flags & NativeMethods.MouseRightUp) != 0)
            {
                _heldInputs.ReleaseButton(MouseButton.Right);
            }

            return;
        }

        var keyboard = input.Data.Keyboard;
        var identity = new KeyIdentity(
            keyboard.VirtualKey,
            keyboard.ScanCode,
            (keyboard.Flags & NativeMethods.KeyboardUnicode) != 0);
        if ((keyboard.Flags & NativeMethods.KeyboardKeyUp) != 0)
        {
            _heldInputs.ReleaseKey(identity);
        }
        else
        {
            _heldInputs.PressKey(identity);
        }
    }

    private void ReleaseAllHeldInputs()
    {
        var snapshot = _heldInputs.TakeAll();
        foreach (var key in snapshot.Keys)
        {
            TrySendRelease(CreateKeyboardRelease(key));
        }

        foreach (var button in snapshot.MouseButtons)
        {
            TrySendRelease(CreateMouseInput(MouseUpFlag(button)));
        }
    }

    private void TrySendRelease(NativeMethods.NativeInput input)
    {
        try
        {
            _ = NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.NativeInput>());
        }
        catch
        {
        }
    }

    private void WatchdogTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = ReleaseExpiredAsync();
    }

    private async Task ReleaseExpiredAsync()
    {
        try
        {
            await _inputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    var expired = _heldInputs.TakeExpired(DateTimeOffset.UtcNow);
                    foreach (var key in expired.Keys)
                    {
                        TrySendRelease(CreateKeyboardRelease(key));
                    }

                    foreach (var button in expired.MouseButtons)
                    {
                        TrySendRelease(CreateMouseInput(MouseUpFlag(button)));
                    }
                }
            }
            finally
            {
                _inputGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _watchdog.DisposeAsync().ConfigureAwait(false);
        await _inputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReleaseAllHeldInputs();
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private static NativeMethods.NativeInput CreateMouseInput(uint flags, uint mouseData = 0)
    {
        return new NativeMethods.NativeInput
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.NativeMouseInput
                {
                    MouseData = mouseData,
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.NativeInput CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.NativeInput
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.NativeKeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyboardKeyUp : 0
                }
            }
        };
    }

    private static NativeMethods.NativeInput CreateUnicodeInput(char codeUnit, bool keyUp)
    {
        return new NativeMethods.NativeInput
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.NativeKeyboardInput
                {
                    ScanCode = codeUnit,
                    Flags = NativeMethods.KeyboardUnicode | (keyUp ? NativeMethods.KeyboardKeyUp : 0)
                }
            }
        };
    }

    private static NativeMethods.NativeInput CreateKeyboardRelease(KeyIdentity key)
    {
        return new NativeMethods.NativeInput
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.NativeKeyboardInput
                {
                    VirtualKey = key.VirtualKey,
                    ScanCode = key.ScanCode,
                    Flags = NativeMethods.KeyboardKeyUp | (key.IsUnicode ? NativeMethods.KeyboardUnicode : 0)
                }
            }
        };
    }

    private static uint MouseDownFlag(MouseButton button) => button switch
    {
        MouseButton.Left => NativeMethods.MouseLeftDown,
        MouseButton.Right => NativeMethods.MouseRightDown,
        _ => throw new WindowAgentException("invalid_button", "Only left and right mouse buttons are supported.")
    };

    private static uint MouseUpFlag(MouseButton button) => button switch
    {
        MouseButton.Left => NativeMethods.MouseLeftUp,
        MouseButton.Right => NativeMethods.MouseRightUp,
        _ => throw new WindowAgentException("invalid_button", "Only left and right mouse buttons are supported.")
    };

    private static int Interpolate(int start, int end, int step, int steps)
    {
        return (int)Math.Round(start + (end - (double)start) * step / steps);
    }

    private static uint ToWheelData(int notches)
    {
        var delta = (long)notches * WheelDelta;
        if (delta is < int.MinValue or > int.MaxValue)
        {
            throw new WindowAgentException("invalid_argument", "A scroll delta is too large to represent as a Windows wheel event.");
        }

        return unchecked((uint)(int)delta);
    }
}
