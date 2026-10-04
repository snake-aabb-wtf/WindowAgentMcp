using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowAgentMcp.Capture;
using WindowAgentMcp.Diagnostics;
using WindowAgentMcp.Errors;
using WindowAgentMcp.Input;
using WindowAgentMcp.Windows;

namespace WindowAgentMcp.Mcp;

[McpServerToolType]
public sealed class WindowAutomationTools
{
    [McpServerTool(Name = "show_window_picker")]
    [Description("Show the local window picker for the human to select and connect a target. This tool does not select or change a window itself.")]
    public static async Task<CallToolResult> ShowWindowPicker(
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken)
    {
        try
        {
            await windowPicker.ShowAsync(cancellationToken).ConfigureAwait(false);
            return Success(new TextContentBlock
            {
                Text = "The local window picker is open. Ask the human to select a window and click Connect before using screenshot or input tools."
            });
        }
        catch (OperationCanceledException)
        {
            return Error("request_cancelled", "The MCP request was cancelled.");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("selector.show_failed", exception: exception);
            return Error("internal_error", "The local window picker could not be opened.");
        }
    }

    [McpServerTool(Name = "screenshot")]
    [Description("Capture the currently selected window and return its client-area PNG.")]
    public static Task<CallToolResult> Screenshot(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => (await session.CaptureAsync(token).ConfigureAwait(false)),
            image => Success(
                new TextContentBlock { Text = $"Target: {image.TargetTitle}\nSize: {image.Width}x{image.Height}\nCoordinates: client-px, origin at top-left." },
                ImageContentBlock.FromBytes(image.PngBytes, "image/png")),
            cancellationToken);
    }

    [McpServerTool(Name = "move_mouse")]
    [Description("Move the mouse to a point in the selected window's client-area screenshot.")]
    public static Task<CallToolResult> MoveMouse(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Client-area x coordinate in physical pixels.")] int x,
        [Description("Client-area y coordinate in physical pixels.")] int y,
        [Description("Move duration in milliseconds, 0 through 5000.")] int duration_ms = 0)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.MoveMouseAsync(x, y, duration_ms, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"Mouse moved to client coordinate ({x}, {y})." }),
            cancellationToken);
    }

    [McpServerTool(Name = "click")]
    [Description("Click the left or right mouse button at a point in the selected window.")]
    public static Task<CallToolResult> Click(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Client-area x coordinate in physical pixels.")] int x,
        [Description("Client-area y coordinate in physical pixels.")] int y,
        [Description("Mouse button: left or right.")] string button,
        [Description("Number of clicks: 1 or 2.")] int click_count = 1)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.ClickAsync(x, y, ParseButton(button), click_count, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = "Mouse click input sent." }),
            cancellationToken);
    }

    [McpServerTool(Name = "mouse_down")]
    [Description("Press and hold the left or right mouse button at a client-area point.")]
    public static Task<CallToolResult> MouseDown(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Client-area x coordinate in physical pixels.")] int x,
        [Description("Client-area y coordinate in physical pixels.")] int y,
        [Description("Mouse button: left or right.")] string button)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.MouseDownAsync(x, y, ParseButton(button), token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"The {button.ToLowerInvariant()} mouse button is held." }),
            cancellationToken);
    }

    [McpServerTool(Name = "mouse_up")]
    [Description("Release a left or right mouse button held by this server.")]
    public static Task<CallToolResult> MouseUp(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Mouse button: left or right.")] string button)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.MouseUpAsync(ParseButton(button), token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"The {button.ToLowerInvariant()} mouse button was released." }),
            cancellationToken);
    }

    [McpServerTool(Name = "drag")]
    [Description("Drag the left or right mouse button along a straight line in the selected window.")]
    public static Task<CallToolResult> Drag(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Starting client-area x coordinate in physical pixels.")] int start_x,
        [Description("Starting client-area y coordinate in physical pixels.")] int start_y,
        [Description("Ending client-area x coordinate in physical pixels.")] int end_x,
        [Description("Ending client-area y coordinate in physical pixels.")] int end_y,
        [Description("Mouse button: left or right.")] string button,
        [Description("Drag duration in milliseconds, 50 through 10000.")] int duration_ms = 500)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.DragAsync(start_x, start_y, end_x, end_y, ParseButton(button), duration_ms, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"Drag input sent from ({start_x}, {start_y}) to ({end_x}, {end_y})." }),
            cancellationToken);
    }

    [McpServerTool(Name = "scroll")]
    [Description("Scroll vertically or horizontally at a client-area point; each unit is one standard wheel notch.")]
    public static Task<CallToolResult> Scroll(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Client-area x coordinate in physical pixels.")] int x,
        [Description("Client-area y coordinate in physical pixels.")] int y,
        [Description("Signed vertical wheel notches.")] int delta_y,
        [Description("Signed horizontal wheel notches.")] int delta_x = 0)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.ScrollAsync(x, y, delta_x, delta_y, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = "Scroll input sent." }),
            cancellationToken);
    }

    [McpServerTool(Name = "type_text")]
    [Description("Send Unicode text to the currently selected window.")]
    public static Task<CallToolResult> TypeText(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Unicode text to type. Its contents are not written to logs.")] string text)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.TypeTextAsync(text, token).ConfigureAwait(false),
            count => Success(new TextContentBlock { Text = $"Unicode text input sent ({count} characters)." }),
            cancellationToken);
    }

    [McpServerTool(Name = "key_press")]
    [Description("Press a named key, optionally with Ctrl, Alt, or Shift modifiers.")]
    public static Task<CallToolResult> KeyPress(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Supported key name, such as A, ENTER, F5, or LEFT.")] string key,
        [Description("Optional modifier names: Ctrl, Alt, or Shift.")] IReadOnlyList<string>? modifiers = null)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.KeyPressAsync(key, modifiers ?? [], token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = "Key press input sent." }),
            cancellationToken);
    }

    [McpServerTool(Name = "key_down")]
    [Description("Press and hold a named keyboard key.")]
    public static Task<CallToolResult> KeyDown(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Supported key name, such as A, CTRL, ENTER, or F5.")] string key)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.KeyDownAsync(key, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"Key '{key}' is held." }),
            cancellationToken);
    }

    [McpServerTool(Name = "key_up")]
    [Description("Release a named keyboard key held by this server.")]
    public static Task<CallToolResult> KeyUp(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        CancellationToken cancellationToken,
        [Description("Supported key name, such as A, CTRL, ENTER, or F5.")] string key)
    {
        return RunAsync(
            targetWindowManager,
            windowPicker,
            async (session, token) => await session.Input.KeyUpAsync(key, token).ConfigureAwait(false),
            () => Success(new TextContentBlock { Text = $"Key '{key}' was released." }),
            cancellationToken);
    }

    private static async Task<CallToolResult> RunAsync<T>(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        Func<WindowSession, CancellationToken, Task<T>> action,
        Func<T, CallToolResult> createSuccess,
        CancellationToken cancellationToken)
    {
        var session = targetWindowManager.Current;
        if (session is null)
        {
            var pickerResult = await ShowWindowPicker(windowPicker, cancellationToken).ConfigureAwait(false);
            if (pickerResult.IsError == true)
            {
                return pickerResult;
            }

            return Error("no_window_selected", "Select and connect a window in the local picker first.");
        }

        if (!session.IsValid())
        {
            await targetWindowManager.ClearAsync().ConfigureAwait(false);
            return Error("window_closed", "The selected window is no longer available. Select a window again.");
        }

        try
        {
            var result = await action(session, cancellationToken).ConfigureAwait(false);
            return createSuccess(result);
        }
        catch (WindowAgentException exception)
        {
            if (exception.Code == "window_closed")
            {
                await targetWindowManager.ClearAsync().ConfigureAwait(false);
            }

            return Error(exception.Code, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Error("request_cancelled", "The MCP request was cancelled.");
        }
        catch
        {
            return Error("internal_error", "The window operation failed unexpectedly.");
        }
    }

    private static Task<CallToolResult> RunAsync(
        TargetWindowManager targetWindowManager,
        WindowPickerPresenter windowPicker,
        Func<WindowSession, CancellationToken, Task> action,
        Func<CallToolResult> createSuccess,
        CancellationToken cancellationToken)
    {
        return RunAsync<object?>(
            targetWindowManager,
            windowPicker,
            async (session, token) =>
            {
                await action(session, token).ConfigureAwait(false);
                return null;
            },
            _ => createSuccess(),
            cancellationToken);
    }

    private static MouseButton ParseButton(string button)
    {
        if (string.Equals(button, "left", StringComparison.OrdinalIgnoreCase))
        {
            return MouseButton.Left;
        }

        if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
        {
            return MouseButton.Right;
        }

        throw new WindowAgentException("invalid_button", "button must be 'left' or 'right'.");
    }

    private static CallToolResult Success(params ContentBlock[] content)
    {
        return new CallToolResult
        {
            Content = content,
            IsError = false
        };
    }

    private static CallToolResult Error(string code, string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{code}: {message}" }],
            IsError = true
        };
    }
}
