namespace WinFsp.Native;

/// <summary>
/// Filter and Action constants for <see cref="FileSystemHost.Notify(uint, uint, System.ReadOnlySpan{char})"/>.
///
/// Filter values match <c>FILE_NOTIFY_CHANGE_*</c> from the Windows SDK <c>WinNT.h</c>;
/// Action values match <c>FILE_ACTION_*</c>. WinFsp's <c>fsctl.h</c> uses the same numeric
/// definitions, so a record built with these constants is valid for both
/// <c>FspFileSystemNotify</c> (cache invalidation) and <c>NtNotifyChangeDirectoryFile</c>
/// (directory-change subscriptions).
/// </summary>
public static class FileNotify
{
    // ── Filter (bitwise OR) ──

    public const uint ChangeFileName   = 0x00000001;
    public const uint ChangeDirName    = 0x00000002;
    public const uint ChangeAttributes = 0x00000004;
    public const uint ChangeSize       = 0x00000008;
    public const uint ChangeLastWrite  = 0x00000010;
    public const uint ChangeLastAccess = 0x00000020;
    public const uint ChangeCreation   = 0x00000040;
    public const uint ChangeSecurity   = 0x00000100;

    // ── Action (one per record) ──

    public const uint ActionAdded            = 1;
    public const uint ActionRemoved          = 2;
    public const uint ActionModified         = 3;
    public const uint ActionRenamedOldName   = 4;
    public const uint ActionRenamedNewName   = 5;
}
