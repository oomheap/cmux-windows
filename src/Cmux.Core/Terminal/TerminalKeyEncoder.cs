namespace Cmux.Core.Terminal;

[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
    Meta = 8,
}

public enum TerminalKey
{
    Enter,
    Escape,
    Backspace,
    Tab,
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    Insert,
    Delete,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,
    KeypadDecimal,
    KeypadAdd,
    KeypadSubtract,
    KeypadMultiply,
    KeypadDivide,
    KeypadEnter,
}

public readonly record struct TerminalKeyEncodingOptions(
    bool ApplicationCursorKeys = false,
    bool ApplicationKeypad = false,
    bool AutoNewline = false);

/// <summary>
/// Encodes logical terminal keys into VT/XTerm input sequences.
/// </summary>
public static class TerminalKeyEncoder
{
    private const string Esc = "\x1b";

    public static string? Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        TerminalKeyEncodingOptions options = default)
    {
        if (key == TerminalKey.Tab)
            return modifiers.HasFlag(TerminalKeyModifiers.Shift) ? $"{Esc}[Z" : "\t";

        if (TryEncodeKeypad(key, options.ApplicationKeypad, out var keypadSequence))
            return keypadSequence;

        var sequence = GetBaseSequence(key, options);
        if (sequence == null)
            return null;

        if (ShouldApplyXtermModifiers(key) &&
            TryApplyXtermModifier(sequence, modifiers, out var modifiedSequence))
        {
            return modifiedSequence;
        }

        if (key == TerminalKey.Backspace && modifiers.HasFlag(TerminalKeyModifiers.Alt))
            return Esc + sequence;

        return sequence;
    }

    public static bool TryEncodeControlLetter(char letter, out string sequence)
    {
        sequence = string.Empty;

        var upper = char.ToUpperInvariant(letter);
        if (upper is < 'A' or > 'Z')
            return false;

        sequence = ((char)(upper - 'A' + 1)).ToString();
        return true;
    }

    private static string? GetBaseSequence(TerminalKey key, TerminalKeyEncodingOptions options)
    {
        if (options.ApplicationCursorKeys)
        {
            var appSequence = key switch
            {
                TerminalKey.Up => $"{Esc}OA",
                TerminalKey.Down => $"{Esc}OB",
                TerminalKey.Right => $"{Esc}OC",
                TerminalKey.Left => $"{Esc}OD",
                TerminalKey.Home => $"{Esc}OH",
                TerminalKey.End => $"{Esc}OF",
                _ => null,
            };

            if (appSequence != null)
                return appSequence;
        }

        return key switch
        {
            TerminalKey.Enter => options.AutoNewline ? "\r\n" : "\r",
            TerminalKey.Escape => Esc,
            TerminalKey.Backspace => "\x7f",
            TerminalKey.Up => $"{Esc}[A",
            TerminalKey.Down => $"{Esc}[B",
            TerminalKey.Right => $"{Esc}[C",
            TerminalKey.Left => $"{Esc}[D",
            TerminalKey.Home => $"{Esc}[H",
            TerminalKey.End => $"{Esc}[F",
            TerminalKey.Insert => $"{Esc}[2~",
            TerminalKey.Delete => $"{Esc}[3~",
            TerminalKey.PageUp => $"{Esc}[5~",
            TerminalKey.PageDown => $"{Esc}[6~",
            TerminalKey.F1 => $"{Esc}OP",
            TerminalKey.F2 => $"{Esc}OQ",
            TerminalKey.F3 => $"{Esc}OR",
            TerminalKey.F4 => $"{Esc}OS",
            TerminalKey.F5 => $"{Esc}[15~",
            TerminalKey.F6 => $"{Esc}[17~",
            TerminalKey.F7 => $"{Esc}[18~",
            TerminalKey.F8 => $"{Esc}[19~",
            TerminalKey.F9 => $"{Esc}[20~",
            TerminalKey.F10 => $"{Esc}[21~",
            TerminalKey.F11 => $"{Esc}[23~",
            TerminalKey.F12 => $"{Esc}[24~",
            _ => null,
        };
    }

    private static bool TryEncodeKeypad(TerminalKey key, bool applicationKeypad, out string sequence)
    {
        sequence = key switch
        {
            TerminalKey.Keypad0 => applicationKeypad ? $"{Esc}Op" : "0",
            TerminalKey.Keypad1 => applicationKeypad ? $"{Esc}Oq" : "1",
            TerminalKey.Keypad2 => applicationKeypad ? $"{Esc}Or" : "2",
            TerminalKey.Keypad3 => applicationKeypad ? $"{Esc}Os" : "3",
            TerminalKey.Keypad4 => applicationKeypad ? $"{Esc}Ot" : "4",
            TerminalKey.Keypad5 => applicationKeypad ? $"{Esc}Ou" : "5",
            TerminalKey.Keypad6 => applicationKeypad ? $"{Esc}Ov" : "6",
            TerminalKey.Keypad7 => applicationKeypad ? $"{Esc}Ow" : "7",
            TerminalKey.Keypad8 => applicationKeypad ? $"{Esc}Ox" : "8",
            TerminalKey.Keypad9 => applicationKeypad ? $"{Esc}Oy" : "9",
            TerminalKey.KeypadDecimal => applicationKeypad ? $"{Esc}On" : ".",
            TerminalKey.KeypadAdd => applicationKeypad ? $"{Esc}Ok" : "+",
            TerminalKey.KeypadSubtract => applicationKeypad ? $"{Esc}Om" : "-",
            TerminalKey.KeypadMultiply => applicationKeypad ? $"{Esc}Oj" : "*",
            TerminalKey.KeypadDivide => applicationKeypad ? $"{Esc}Oo" : "/",
            TerminalKey.KeypadEnter => applicationKeypad ? $"{Esc}OM" : "\r",
            _ => string.Empty,
        };

        return sequence.Length > 0;
    }

    private static bool ShouldApplyXtermModifiers(TerminalKey key)
    {
        return key is TerminalKey.Up
            or TerminalKey.Down
            or TerminalKey.Right
            or TerminalKey.Left
            or TerminalKey.Home
            or TerminalKey.End
            or TerminalKey.Insert
            or TerminalKey.Delete
            or TerminalKey.PageUp
            or TerminalKey.PageDown
            or TerminalKey.F1
            or TerminalKey.F2
            or TerminalKey.F3
            or TerminalKey.F4
            or TerminalKey.F5
            or TerminalKey.F6
            or TerminalKey.F7
            or TerminalKey.F8
            or TerminalKey.F9
            or TerminalKey.F10
            or TerminalKey.F11
            or TerminalKey.F12;
    }

    private static bool TryApplyXtermModifier(
        string sequence,
        TerminalKeyModifiers modifiers,
        out string modifiedSequence)
    {
        modifiedSequence = sequence;

        var modifierCode = ToXtermModifierCode(modifiers);
        if (modifierCode == 0)
            return false;

        if (sequence.Length == 3 && sequence[0] == '\x1b' && sequence[1] == 'O')
        {
            modifiedSequence = $"{Esc}[1;{modifierCode}{sequence[2]}";
            return true;
        }

        if (sequence.Length >= 3 && sequence[0] == '\x1b' && sequence[1] == '[')
        {
            var parameter = sequence[2..^1];
            var final = sequence[^1];
            modifiedSequence = string.IsNullOrEmpty(parameter)
                ? $"{Esc}[1;{modifierCode}{final}"
                : $"{Esc}[{parameter};{modifierCode}{final}";
            return true;
        }

        return false;
    }

    private static int ToXtermModifierCode(TerminalKeyModifiers modifiers)
    {
        var code = 0;

        if (modifiers.HasFlag(TerminalKeyModifiers.Shift))
            code |= 1;
        if (modifiers.HasFlag(TerminalKeyModifiers.Alt))
            code |= 2;
        if (modifiers.HasFlag(TerminalKeyModifiers.Control))
            code |= 4;
        if (modifiers.HasFlag(TerminalKeyModifiers.Meta))
            code |= 8;

        return code == 0 ? 0 : code + 1;
    }
}
