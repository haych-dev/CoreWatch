using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CoreWatch.Services;

/// <summary>Human-readable formatting for sizes, rates and durations.</summary>
public static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double bytes, int decimals = 1)
    {
        if (bytes < 0 || double.IsNaN(bytes)) bytes = 0;
        int u = 0;
        while (bytes >= 1024 && u < Units.Length - 1) { bytes /= 1024; u++; }
        if (u == 0) return $"{bytes:0} B";
        return bytes.ToString(bytes >= 100 ? "0" : "F" + decimals, CultureInfo.InvariantCulture) + " " + Units[u];
    }

    public static string Bytes(ulong bytes, int decimals = 1) => Bytes((double)bytes, decimals);
    public static string Bytes(long bytes, int decimals = 1) => Bytes((double)bytes, decimals);
    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";
    public static string Gb(double bytes) => (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture);
    public static string Pct(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%";
    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }
}

/// <summary>Fixed-capacity history buffer; index 0 is the oldest sample.</summary>
public sealed class RingBuffer
{
    readonly double[] _data;
    int _start;

    public RingBuffer(int capacity) => _data = new double[capacity];
    public int Capacity => _data.Length;
    public int Count { get; private set; }
    public double this[int i] => _data[(_start + i) % _data.Length];
    public double Last => Count == 0 ? 0 : this[Count - 1];

    public void Push(double v)
    {
        if (Count < _data.Length) { _data[(_start + Count) % _data.Length] = v; Count++; }
        else { _data[_start] = v; _start = (_start + 1) % _data.Length; }
    }

    public double Max()
    {
        double m = 0;
        for (int i = 0; i < Count; i++) if (this[i] > m) m = this[i];
        return m;
    }
}

public static class Sys
{
    public static bool IsAdmin { get; } = CheckAdmin();

    static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Relaunches elevated; <paramref name="page"/> is the route key to reopen on.</summary>
    public static bool RestartAsAdmin(string? page = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = page != null ? $"--page {page}" : "" });
            Application.Current.Shutdown();
            return true;
        }
        catch { return false; } // user declined the UAC prompt
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    public static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Extracts and caches shell icons for executables.</summary>
public static class IconCache
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Cache.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                var info = new Native.SHFILEINFO();
                var res = Native.SHGetFileInfo(p, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), 0x100 /*ICON*/ | 0x0 /*LARGE*/);
                if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { Native.DestroyIcon(info.hIcon); }
            }
            catch { return null; }
        });
    }
}
