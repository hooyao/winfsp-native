using System.Runtime.InteropServices;

namespace WinFsp.Native;

/// <summary>
/// File metadata information. Matches FSP_FSCTL_FILE_INFO (72 bytes).
/// Layout from winfsp/fsctl.h lines 277-290.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FspFileInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
    public ulong AllocationSize;
    public ulong FileSize;
    public ulong CreationTime;
    public ulong LastAccessTime;
    public ulong LastWriteTime;
    public ulong ChangeTime;
    public ulong IndexNumber;
    public uint HardLinks;
    public uint EaSize;
}

/// <summary>
/// Directory entry info header. Matches FSP_FSCTL_DIR_INFO (variable size).
/// Layout from winfsp/fsctl.h lines 299-310.
/// The FileNameBuf follows immediately after the Padding field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspDirInfo
{
    public const int FileNameBufMaxChars = 255;

    public ushort Size;
    public FspFileInfo FileInfo;
    public ulong NextOffset;
    private ulong _padding1;
    private ulong _padding2;
    public fixed char FileNameBuf[FileNameBufMaxChars];

    public void SetFileName(ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, FileNameBufMaxChars);
        fixed (char* p = FileNameBuf)
        {
            name[..len].CopyTo(new Span<char>(p, FileNameBufMaxChars));
        }
        Size = (ushort)(FileNameBufOffset + len * sizeof(char));
    }

    internal static readonly int FileNameBufOffset =
        (int)Marshal.OffsetOf<FspDirInfo>(nameof(FileNameBuf));
}

/// <summary>
/// Stream info header. Matches FSP_FSCTL_STREAM_INFO (variable size).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspStreamInfo
{
    public const int StreamNameBufMaxChars = 255;

    public ushort Size;
    public ulong StreamSize;
    public ulong StreamAllocationSize;
    public fixed char StreamNameBuf[StreamNameBufMaxChars];

    public void SetStreamName(ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, StreamNameBufMaxChars);
        fixed (char* p = StreamNameBuf)
        {
            name[..len].CopyTo(new Span<char>(p, StreamNameBufMaxChars));
        }
        Size = (ushort)(StreamNameBufOffset + len * sizeof(char));
    }

    internal static readonly int StreamNameBufOffset =
        (int)Marshal.OffsetOf<FspStreamInfo>(nameof(StreamNameBuf));
}
