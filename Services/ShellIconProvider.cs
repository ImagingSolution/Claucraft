using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Claucraft.Services;

/// <summary>
/// Hands back the icon Windows Explorer shows for a path, as an Avalonia image.
///
/// Two caches sit in front of the shell. A lookup key — the extension for ordinary files, the
/// full path for folders and for the types that carry their own icon — is resolved to a system
/// image list index, and the bitmap is cached against that index. So a folder of a thousand
/// .cs files costs one shell call, and the hundreds of folders that all share the generic
/// folder icon share one bitmap.
/// </summary>
public static class ShellIconProvider
{
    /// <summary>Types whose icon lives inside the file, so it cannot be shared by extension.</summary>
    private static readonly HashSet<string> PerFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".dll", ".cpl", ".msc", ".scr", ".url", ".appref-ms"
    };

    private static readonly Dictionary<string, int> _indexByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, IImage?> _bitmapByIndex = new();
    private static readonly object _gate = new();

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// The shell icon for <paramref name="path"/>, or null when the shell has nothing for it
    /// (or we are not on Windows) — callers fall back to their own glyph.
    /// </summary>
    public static IImage? GetIcon(string path, bool isDirectory)
    {
        if (!IsSupported || string.IsNullOrEmpty(path))
            return null;

        var key = CacheKey(path, isDirectory);

        lock (_gate)
        {
            if (_indexByKey.TryGetValue(key, out var known))
                return _bitmapByIndex.TryGetValue(known, out var hit) ? hit : null;
        }

        int index;
        try
        {
            index = IconIndex(path, isDirectory);
        }
        catch
        {
            index = -1;
        }

        if (index < 0)
        {
            lock (_gate)
            {
                // Remember the miss so a broken path is not probed on every tree refresh.
                _indexByKey[key] = -1;
                _bitmapByIndex[-1] = null;
            }
            return null;
        }

        lock (_gate)
        {
            _indexByKey[key] = index;
            if (_bitmapByIndex.TryGetValue(index, out var hit))
                return hit;
        }

        IImage? bitmap;
        try
        {
            bitmap = LoadBitmap(path, isDirectory);
        }
        catch
        {
            bitmap = null;
        }

        lock (_gate)
        {
            _bitmapByIndex[index] = bitmap;
        }
        return bitmap;
    }

    /// <summary>Drops every cached icon, so a theme or association change is picked up.</summary>
    public static void Clear()
    {
        lock (_gate)
        {
            _indexByKey.Clear();
            _bitmapByIndex.Clear();
        }
    }

    private static string CacheKey(string path, bool isDirectory)
    {
        if (isDirectory)
            return "dir:" + path;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return "name:" + Path.GetFileName(path);
        if (PerFileTypes.Contains(ext))
            return "path:" + path;
        return "ext:" + ext;
    }

    /// <summary>
    /// Whether the shell may read the file itself. Ordinary files are resolved from the
    /// extension alone, which keeps the lookup off the disk; the types that embed an icon,
    /// and folders (which may carry a desktop.ini), have to be read.
    /// </summary>
    private static bool NeedsRealFile(string path, bool isDirectory)
        => isDirectory || PerFileTypes.Contains(Path.GetExtension(path));

    private static int IconIndex(string path, bool isDirectory)
    {
        var info = new SHFILEINFOW();
        var flags = SHGFI_SYSICONINDEX;
        var attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        if (!NeedsRealFile(path, isDirectory))
            flags |= SHGFI_USEFILEATTRIBUTES;

        var result = SHGetFileInfoW(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
        return result == IntPtr.Zero ? -1 : info.iIcon;
    }

    private static IImage? LoadBitmap(string path, bool isDirectory)
    {
        var info = new SHFILEINFOW();
        var flags = SHGFI_ICON | SHGFI_LARGEICON;
        var attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        if (!NeedsRealFile(path, isDirectory))
            flags |= SHGFI_USEFILEATTRIBUTES;

        if (SHGetFileInfoW(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags) == IntPtr.Zero)
            return null;
        if (info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            return ToBitmap(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static IImage? ToBitmap(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var iconInfo))
            return null;

        var hbmColor = iconInfo.hbmColor;
        var hbmMask = iconInfo.hbmMask;

        try
        {
            if (hbmColor == IntPtr.Zero)
                return null;

            var bm = new BITMAP();
            if (GetGdiObject(hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0)
                return null;

            var w = bm.bmWidth;
            var h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 512 || h > 512)
                return null;

            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return null;

            byte[] pixels;
            try
            {
                pixels = ReadDib(hdc, hbmColor, w, h);
                if (pixels.Length == 0)
                    return null;

                // Icons under 32bpp come back with a zeroed alpha channel. Rebuild it from the
                // AND mask, where a set bit means transparent.
                if (!HasAlpha(pixels) && !ApplyMask(hdc, hbmMask, pixels, w, h))
                    return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            var stride = w * 4;
            var bitmap = new WriteableBitmap(
                new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            using (var fb = bitmap.Lock())
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
            }

            return bitmap;
        }
        finally
        {
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
        }
    }

    /// <summary>Pulls a GDI bitmap out as top-down 32bpp BGRA.</summary>
    private static byte[] ReadDib(IntPtr hdc, IntPtr hbm, int w, int h)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;   // negative: rows come back top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        var size = w * 4 * h;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, buffer, ref bmi, DIB_RGB_COLORS) == 0)
                return Array.Empty<byte>();

            var pixels = new byte[size];
            Marshal.Copy(buffer, pixels, 0, size);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0)
                return true;
        return false;
    }

    private static bool ApplyMask(IntPtr hdc, IntPtr hbmMask, byte[] pixels, int w, int h)
    {
        if (hbmMask == IntPtr.Zero)
            return false;

        var mask = ReadDib(hdc, hbmMask, w, h);
        if (mask.Length != pixels.Length)
            return false;

        // The monochrome mask widens to white where the icon is transparent.
        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i + 3] = mask[i] != 0 ? (byte)0 : (byte)255;

        return true;
    }

    // ── Win32 ──

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors0;
        public uint bmiColors1;
        public uint bmiColors2;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetGdiObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);
}
