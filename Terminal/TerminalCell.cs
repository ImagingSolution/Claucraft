using System;

namespace ClaudeCodeMDI.Terminal;

public struct TerminalCell
{
    public char Character;
    public int Foreground;   // -1 = default, 0-255 = ANSI color index, or 0xRRGGBB+flag
    public int Background;   // same as foreground
    public CellAttributes Attributes;

    public static TerminalCell Empty => new()
    {
        Character = ' ',
        Foreground = -1,
        Background = -1,
        Attributes = CellAttributes.None
    };
}

[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Inverse = 8,
    Dim = 16,
    /// <summary>
    /// Marks the second cell of a double-width (full-width) character.
    /// This cell should not be rendered independently.
    /// </summary>
    WideCharTrail = 32
}
