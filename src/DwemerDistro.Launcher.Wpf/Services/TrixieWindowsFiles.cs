using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// The Windows file and secret primitives the Debian Trixie upgrade relies on: a file's identity (volume serial and
/// file index, which a same-volume rename keeps), an exclusive-open probe, a write-through rename that never copies
/// across volumes or replaces a file, and DPAPI protection of Core's capture token for the current Windows user.
/// </summary>
internal static class TrixieWindowsFiles
{
    private const uint OpenExisting = 3;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparsePointAttribute = 0x400;
    private const uint MoveReplaceExisting = 0x1;
    private const uint MoveWriteThrough = 0x8;
    private const int ProtectUiForbidden = 0x1;

    /// <summary>
    /// "volume-serial:file-index" of a regular file, opened for attributes only so a running distro's lock does not
    /// matter. Null when the file is missing, a link or junction, or Windows can't name it.
    /// </summary>
    internal static string? FileId(string path)
    {
        using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & ReparsePointAttribute) != 0 || (info.FileAttributes & 0x10) != 0)
            return null;
        return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
    }

    /// <summary>True when nothing else (WSL included) has the file open, so it can be renamed.</summary>
    internal static bool IsExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Renames on one volume, flushed before it returns. Never copies, never replaces an existing file.</summary>
    internal static void MoveSameVolume(string source, string destination)
    {
        if (!MoveFileExW(source, destination, MoveWriteThrough))
            throw new IOException($"Could not move {source} to {destination}.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    /// <summary>Replaces <paramref name="destination"/> with <paramref name="source"/>, flushed before it returns.</summary>
    internal static void ReplaceWriteThrough(string source, string destination)
    {
        if (!MoveFileExW(source, destination, MoveReplaceExisting | MoveWriteThrough))
            throw new IOException($"Could not replace {destination}.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    /// <summary>DPAPI, current user only, bound to <paramref name="purpose"/>. Base64 of the protected bytes.</summary>
    internal static string Protect(string secret, string purpose)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(Transform(plain, Encoding.UTF8.GetBytes(purpose), protect: true));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <returns>Null when the value was not protected by this Windows user for <paramref name="purpose"/>.</returns>
    internal static string? Unprotect(string? protectedValue, string purpose)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var plain = Transform(Convert.FromBase64String(protectedValue), Encoding.UTF8.GetBytes(purpose), protect: false);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inputBlob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, ProtectUiForbidden, ref output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, ProtectUiForbidden, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }

            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
