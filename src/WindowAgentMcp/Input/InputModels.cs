namespace WindowAgentMcp.Input;

public enum MouseButton
{
    Left,
    Right
}

public readonly record struct KeyIdentity(ushort VirtualKey, ushort ScanCode, bool IsUnicode);

public readonly record struct HeldInputSnapshot(
    IReadOnlyList<KeyIdentity> Keys,
    IReadOnlyList<MouseButton> MouseButtons);

internal static class InputKeyCatalog
{
    private static readonly Dictionary<string, ushort> Keys = CreateKeys();

    public static bool TryParseKey(string? name, out ushort virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim().ToUpperInvariant().Replace(' ', '_');
        return Keys.TryGetValue(normalized, out virtualKey);
    }

    public static bool TryParseModifier(string? name, out ushort virtualKey)
    {
        if (TryParseKey(name, out virtualKey)
            && virtualKey is 0x10 or 0x11 or 0x12)
        {
            return true;
        }

        virtualKey = 0;
        return false;
    }

    private static Dictionary<string, ushort> CreateKeys()
    {
        var keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        for (var key = 'A'; key <= 'Z'; key++)
        {
            keys[key.ToString()] = key;
        }

        for (var key = '0'; key <= '9'; key++)
        {
            keys[key.ToString()] = key;
            keys[$"NUMPAD{key}"] = (ushort)(0x60 + key - '0');
        }

        for (var functionKey = 1; functionKey <= 24; functionKey++)
        {
            keys[$"F{functionKey}"] = (ushort)(0x6F + functionKey);
        }

        Add(0x08, "BACKSPACE", "BACK", "BS");
        Add(0x09, "TAB");
        Add(0x0D, "ENTER", "RETURN");
        Add(0x10, "SHIFT");
        Add(0x11, "CTRL", "CONTROL");
        Add(0x12, "ALT");
        Add(0x13, "PAUSE", "BREAK");
        Add(0x14, "CAPS_LOCK", "CAPSLOCK");
        Add(0x1B, "ESC", "ESCAPE");
        Add(0x20, "SPACE", "SPACEBAR");
        Add(0x21, "PAGE_UP", "PGUP");
        Add(0x22, "PAGE_DOWN", "PGDN");
        Add(0x23, "END");
        Add(0x24, "HOME");
        Add(0x25, "LEFT", "ARROW_LEFT");
        Add(0x26, "UP", "ARROW_UP");
        Add(0x27, "RIGHT", "ARROW_RIGHT");
        Add(0x28, "DOWN", "ARROW_DOWN");
        Add(0x2C, "PRINT_SCREEN", "PRTSC");
        Add(0x2D, "INSERT", "INS");
        Add(0x2E, "DELETE", "DEL");
        Add(0x5D, "APPLICATION", "APPS", "CONTEXT_MENU");
        Add(0x90, "NUM_LOCK", "NUMLOCK");
        Add(0x91, "SCROLL_LOCK", "SCROLLLOCK");
        Add(0x6A, "NUMPAD_MULTIPLY");
        Add(0x6B, "NUMPAD_ADD");
        Add(0x6D, "NUMPAD_SUBTRACT");
        Add(0x6E, "NUMPAD_DECIMAL");
        Add(0x6F, "NUMPAD_DIVIDE");
        Add(0xBA, "SEMICOLON");
        Add(0xBB, "EQUAL", "PLUS");
        Add(0xBC, "COMMA");
        Add(0xBD, "MINUS");
        Add(0xBE, "PERIOD", "DOT");
        Add(0xBF, "SLASH", "FORWARD_SLASH");
        Add(0xC0, "BACKTICK", "GRAVE");
        Add(0xDB, "LEFT_BRACKET");
        Add(0xDC, "BACKSLASH");
        Add(0xDD, "RIGHT_BRACKET");
        Add(0xDE, "QUOTE", "APOSTROPHE");
        return keys;

        void Add(ushort code, params string[] names)
        {
            foreach (var keyName in names)
            {
                keys[keyName] = code;
            }
        }
    }
}
