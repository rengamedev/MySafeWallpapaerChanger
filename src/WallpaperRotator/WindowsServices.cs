using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WallpaperRotator;

public static class WallpaperService
{
    private const int SpiSetDesktopWallpaper = 0x0014;
    private const int UpdateIniFile = 0x01;
    private const int SendWinIniChange = 0x02;
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, string value, int flags);

    public static void Set(string path, WallpaperStyle style)
    {
        if (GetRegistryValues(style) is { } values)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
            key?.SetValue("WallpaperStyle", values.WallpaperStyle);
            key?.SetValue("TileWallpaper", values.TileWallpaper);
        }
        if (!SystemParametersInfo(SpiSetDesktopWallpaper, 0, Path.GetFullPath(path), UpdateIniFile | SendWinIniChange))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Values of HKCU\Control Panel\Desktop; null keeps the style chosen in Windows settings.</summary>
    public static (string WallpaperStyle, string TileWallpaper)? GetRegistryValues(WallpaperStyle style) => style switch
    {
        WallpaperStyle.Fill => ("10", "0"),
        WallpaperStyle.Fit => ("6", "0"),
        WallpaperStyle.Stretch => ("2", "0"),
        WallpaperStyle.Center => ("0", "0"),
        WallpaperStyle.Tile => ("0", "1"),
        WallpaperStyle.Span => ("22", "0"),
        _ => null
    };
}

public static class AutoStartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "WallpaperRotator";
    public static bool IsEnabled() { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(Name) is string; }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\""); else key.DeleteValue(Name, false);
    }
}

public sealed class SecretStore(AppPaths paths, DiagnosticLog log)
{
    private bool failureLogged;

    public void Save(string secret)
    {
        paths.EnsureCreated();
        if (string.IsNullOrWhiteSpace(secret)) { Delete(); return; }
        var input = Encoding.UTF8.GetBytes(secret.Trim());
        var protectedBytes = Protect(input);
        File.WriteAllBytes(paths.Secret, protectedBytes);
        failureLogged = false;
        CryptographicOperations.ZeroMemory(input);
    }
    public string? Load()
    {
        if (!File.Exists(paths.Secret)) return null;
        try
        {
            var plain = Unprotect(File.ReadAllBytes(paths.Secret));
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex)
        {
            // DPAPI fails when the file was copied from another Windows account or computer. Log once, not per request.
            if (!failureLogged) log.Write("Stored Wallhaven key could not be read; enter it again.", ex);
            failureLogged = true;
            return null;
        }
    }
    public void Delete() { try { if (File.Exists(paths.Secret)) File.Delete(paths.Secret); } catch { } }

    private static byte[] Protect(byte[] data) => Crypt(data, true);
    private static byte[] Unprotect(byte[] data) => Crypt(data, false);
    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.Size = data.Length;
            input.Data = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
