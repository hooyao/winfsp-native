namespace WinFsp.Native;

/// <summary>
/// Result of an async or sync Read operation.
/// </summary>
public readonly struct ReadResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }

    public static ReadResult Success(uint bytesTransferred) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
    };

    public static ReadResult EndOfFile() => new() { Status = NtStatus.EndOfFile };
    public static ReadResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of an async or sync Write operation.
/// </summary>
public readonly struct WriteResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }
    public FspFileInfo FileInfo { get; init; }

    public static WriteResult Success(uint bytesTransferred, FspFileInfo fileInfo) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
        FileInfo = fileInfo,
    };

    public static WriteResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of an async or sync ReadDirectory operation.
/// </summary>
public readonly struct ReadDirectoryResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }

    public static ReadDirectoryResult Success(uint bytesTransferred) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
    };

    public static ReadDirectoryResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of <see cref="IFileSystem.GetFileSecurityByName"/> — NTSTATUS plus the file
/// attributes and an optional security descriptor.
/// </summary>
public readonly struct SecurityByNameResult
{
    public int Status { get; init; }
    public uint FileAttributes { get; init; }

    /// <summary>
    /// Self-relative security descriptor bytes, or <c>null</c> to make WinFsp skip access
    /// checks (the common case for read-only file systems).
    /// </summary>
    public byte[]? SecurityDescriptor { get; init; }

    public static SecurityByNameResult Success(uint fileAttributes, byte[]? securityDescriptor = null) => new()
    {
        Status = NtStatus.Success,
        FileAttributes = fileAttributes,
        SecurityDescriptor = securityDescriptor,
    };

    public static SecurityByNameResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of <see cref="IFileSystem.GetDirInfoByName"/> — NTSTATUS plus the matched
/// directory entry.
/// </summary>
public readonly struct DirInfoByNameResult
{
    public int Status { get; init; }
    public FspDirInfo DirInfo { get; init; }

    public static DirInfoByNameResult Success(FspDirInfo dirInfo) => new()
    {
        Status = NtStatus.Success,
        DirInfo = dirInfo,
    };

    public static DirInfoByNameResult Error(int status) => new() { Status = status };
}
