using System.Collections.Generic;
using System.Text;

namespace Claucraft.Services;

/// <summary>
/// Decoding for the paths git prints. Shared by the services that read porcelain output,
/// so the octal-escape handling lives in one place.
/// </summary>
public static class GitPath
{
    /// <summary>
    /// Decodes a git C-style quoted path: surrounding double quotes with \NNN octal, \" and \\
    /// escapes. Anything that is not quoted comes back unchanged.
    /// </summary>
    public static string Unquote(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"')
            return s;

        try
        {
            string inner = s[1..^1];
            var bytes = new List<byte>();
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '\\' && i + 1 < inner.Length)
                {
                    char n = inner[i + 1];
                    if (n == 'n') { bytes.Add((byte)'\n'); i++; continue; }
                    if (n == 't') { bytes.Add((byte)'\t'); i++; continue; }
                    if (n == 'r') { bytes.Add((byte)'\r'); i++; continue; }
                    if (n == '\\') { bytes.Add((byte)'\\'); i++; continue; }
                    if (n == '"') { bytes.Add((byte)'"'); i++; continue; }
                    if (n >= '0' && n <= '7' && i + 3 < inner.Length)
                    {
                        string oct = inner.Substring(i + 1, 3);
                        try
                        {
                            bytes.Add(System.Convert.ToByte(oct, 8));
                            i += 3;
                            continue;
                        }
                        catch { /* fall through and treat literally */ }
                    }
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        catch
        {
            return s[1..^1];
        }
    }
}
