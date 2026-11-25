using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ADM.Core;

/// <summary>
/// The file a download writes into. Writes land in the Windows cache; <see cref="Flush"/> returns only
/// once everything written so far is on the disk itself, so progress recorded after it survives a power cut.
/// </summary>
internal class PartFile : IDisposable
{
    readonly SafeFileHandle _handle;

    public PartFile(string path) =>
        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

    public virtual long Length => RandomAccess.GetLength(_handle);

    public virtual void SetLength(long length) => RandomAccess.SetLength(_handle, length);

    public virtual ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        RandomAccess.WriteAsync(_handle, data, offset, ct);

    public virtual ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) =>
        RandomAccess.ReadAsync(_handle, buffer, offset, ct);

    public virtual void Flush()
    {
        if (!FlushFileBuffers(_handle))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(new Win32Exception(error).Message, unchecked((int)0x80070000 | error));
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _handle.Dispose();
    }

    /// <summary>The bytes this user may still write in <paramref name="directory"/> (quotas included); unknown counts as unlimited.</summary>
    public static long FreeSpaceIn(string directory) =>
        GetDiskFreeSpaceEx(directory, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : long.MaxValue;

    /// <summary>The bytes the file takes up on disk, 0 if there is no file. A sparse file takes up only what has been written.</summary>
    public static long AllocatedBytes(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return 0;
        return ((long)high << 32) | low;
    }

    /// <summary>Fails, as a full disk does, when <paramref name="needed"/> bytes won't fit in the <paramref name="free"/> bytes there are.</summary>
    /// <param name="purpose">What the space is for, as a phrase to follow the drive's name; empty for the download itself.</param>
    public static void EnsureSpace(string folder, long needed, long free, string purpose = "")
    {
        if (needed <= free) return;
        var drive = Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\') ?? folder;
        throw new IOException($"Not enough disk space on {drive}{purpose} (needs {Format.Bytes(needed)}, {Format.Bytes(free)} free).", Net.DiskFullHResult);
    }

    /// <summary>
    /// Sets or clears the sparse flag through a handle opened without <see cref="FileOptions.Asynchronous"/>.
    /// False where the volume has no sparse files (FAT32, exFAT).
    /// </summary>
    public static bool TrySetSparse(SafeFileHandle handle, bool sparse)
    {
        const uint FsctlSetSparse = 0x000900C4;
        // FILE_SET_SPARSE_BUFFER is one BOOLEAN.
        byte setSparse = sparse ? (byte)1 : (byte)0;
        return DeviceIoControl(handle, FsctlSetSparse, ref setSparse, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetCompressedFileSizeW")]
    static extern uint GetCompressedFileSize(string path, out uint high);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableToCaller, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
