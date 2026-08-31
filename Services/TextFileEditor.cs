using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Claucraft.Services;

/// <summary>Why a file opened read-only.</summary>
public enum EditBlock { None, TooLarge, Binary, NotUtf8 }

/// <summary>
/// A file as the explorer's editor holds it: the text to show, everything needed to write it
/// back the way it was found, and the stamp that says whether anything else has touched it.
/// </summary>
public sealed record TextFileDocument
{
    public string Path { get; init; } = "";
    public string Text { get; init; } = "";
    public EditBlock Block { get; init; }
    public bool Editable => Block == EditBlock.None;

    /// <summary>The line ending the file uses, restored on every save.</summary>
    public string Newline { get; init; } = "\r\n";
    /// <summary>Whether the file carried a UTF-8 byte order mark.</summary>
    public bool Bom { get; init; }

    public DateTime StampUtc { get; init; }
    public long Length { get; init; }
}

/// <summary>
/// Reads and writes the files the explorer edits.
///
/// The point of this class is that a save puts back what was there: the same encoding, the same
/// line endings - this repository has files of both kinds, and rewriting one as the other would
/// show up as a whole-file diff - and nothing at all for a file it could not read as text. A
/// file it cannot round-trip safely is reported read-only rather than opened for editing.
/// </summary>
public static class TextFileEditor
{
    /// <summary>Past this, the file opens as a preview - the panel is not a place to edit it.</summary>
    public const long MaxEditableBytes = 1_000_000;

    private const int PreviewLines = 30;

    public static TextFileDocument Read(string path)
    {
        var info = new FileInfo(path);
        var stamp = info.LastWriteTimeUtc;

        if (info.Length > MaxEditableBytes)
            return Blocked(path, EditBlock.TooLarge, PreviewOf(path), stamp, info.Length);

        var bytes = File.ReadAllBytes(path);

        // A NUL byte is the one reliable sign the file is not text at all.
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return Blocked(path, EditBlock.Binary, "", stamp, info.Length);

        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();

        string text;
        try
        {
            // Throws on anything that is not valid UTF-8, which is what keeps a Shift-JIS file
            // from being opened, silently mojibaked, and saved back over the original.
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (ArgumentException)
        {
            return Blocked(path, EditBlock.NotUtf8, PreviewOf(path), stamp, info.Length);
        }

        return new TextFileDocument
        {
            Path = path,
            Text = text,
            Block = EditBlock.None,
            Newline = DominantNewline(text),
            Bom = bom,
            StampUtc = stamp,
            Length = info.Length,
        };
    }

    /// <summary>True once anything but us has written the file since it was read.</summary>
    public static bool ChangedOnDisk(TextFileDocument doc)
    {
        try
        {
            var info = new FileInfo(doc.Path);
            if (!info.Exists) return true;
            return info.LastWriteTimeUtc != doc.StampUtc || info.Length != doc.Length;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes <paramref name="text"/> back in the document's own encoding and line endings.
    /// Returns the saved document, or null with the reason in <paramref name="error"/>.
    /// </summary>
    public static TextFileDocument? Write(TextFileDocument doc, string text, out string? error)
    {
        error = null;
        try
        {
            // The editor hands back whatever the platform put in; the file gets what it had.
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (doc.Newline != "\n") normalized = normalized.Replace("\n", doc.Newline);

            var body = new UTF8Encoding(false).GetBytes(normalized);
            if (doc.Bom)
            {
                var withBom = new byte[body.Length + 3];
                withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                body.CopyTo(withBom, 3);
                body = withBom;
            }

            File.WriteAllBytes(doc.Path, body);

            var info = new FileInfo(doc.Path);
            return doc with { Text = text, StampUtc = info.LastWriteTimeUtc, Length = info.Length };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Whichever ending the file uses more. A file with none - one line, no trailing break -
    /// gets CRLF, which is what a new line typed on this platform would have been anyway.
    /// </summary>
    private static string DominantNewline(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        if (crlf == 0 && lf == 0) return "\r\n";
        return crlf >= lf ? "\r\n" : "\n";
    }

    private static TextFileDocument Blocked(
        string path, EditBlock block, string text, DateTime stamp, long length) =>
        new()
        {
            Path = path,
            Text = text,
            Block = block,
            StampUtc = stamp,
            Length = length,
        };

    /// <summary>Head of a file that will not be opened for editing, so it can still be looked at.</summary>
    private static string PreviewOf(string path)
    {
        try { return string.Join("\n", File.ReadLines(path).Take(PreviewLines)); }
        catch { return ""; }
    }
}
