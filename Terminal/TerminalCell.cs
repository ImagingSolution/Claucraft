using System;

namespace Claucraft.Terminal;

public struct TerminalCell
{
    /// <summary>
    /// The cell's Unicode code point, not a UTF-16 code unit. Emoji live above the BMP
    /// (U+1F300 and up) and need two UTF-16 units, so storing a <see cref="char"/> here
    /// would split them into two lone surrogates — each of which renders as U+FFFD.
    /// </summary>
    public int Character;
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

    /// <summary>The cell's text. Always go through this rather than casting back to char.</summary>
    public readonly string Text => CodePointToString(Character);

    /// <summary>
    /// Renders a code point as text, mapping the wide-char trail marker to a space and
    /// anything unrepresentable (a stray surrogate, an out-of-range value) to U+FFFD.
    /// </summary>
    public static string CodePointToString(int codePoint)
    {
        if (codePoint <= 0) return " ";
        if (codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF)) return "�";
        return char.ConvertFromUtf32(codePoint);
    }
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
