using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using StreamDock.Core;

namespace StreamDock;

public static class WindowsServices
{
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled) key.SetValue("StreamDock", $"\"{Environment.ProcessPath}\" --tray", RegistryValueKind.String);
        else key.DeleteValue("StreamDock", false);
    }
    public static bool HasStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("StreamDock") is string;
    }
    public static void OpenUrl(string url)
    {
        Input.WebUrl(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    public static void OpenFolder(string path)
    {
        var folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { folder } });
    }
}

public sealed class DpapiSecretStore(string path) : ISecretStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] source, bool encrypt)
    {
        var input = new Blob { Length = source.Length, Data = Marshal.AllocHGlobal(source.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(source, 0, input.Data, source.Length);
            var ok = encrypt ? CryptProtectData(ref input, "StreamDock Spotify", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    public string? Read() => File.Exists(path) ? Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path), false)) : null;
    public void Write(string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp"; File.WriteAllBytes(temp, Transform(Encoding.UTF8.GetBytes(value), true)); File.Move(temp, path, true);
    }
    public void Delete() => File.Delete(path);
}
