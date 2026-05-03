using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using WinFsp.Native;
using WinFsp.Native.Interop;
using Xunit;

namespace WinFsp.Native.Tests;

[SupportedOSPlatform("windows")]
[Collection("Sync")]
public class NotifyTests(SyncFixture fixture)
{
    private readonly FileSystemHost _host = fixture.Host;

    [Fact]
    public unsafe void NotifyInfoStruct_HeaderIsExactly12Bytes()
    {
        // Mirrors the static_assert in winfsp/inc/winfsp/fsctl.h:329:
        //   FSP_FSCTL_STATIC_ASSERT(12 == sizeof(FSP_FSCTL_NOTIFY_INFO));
        sizeof(FspFsctlNotifyInfo).Should().Be(12,
            "the C struct's size is asserted to be 12 in fsctl.h and the binding " +
            "must agree byte-for-byte");
    }

    [Fact]
    public void Notify_SimpleCall_ReturnsSuccessOrInvalidParameter()
    {
        // We cannot directly observe the kernel cache from a unit test (the integration
        // suite in the RamDrive repo covers behaviour). Here we just assert that the
        // P/Invoke marshals correctly: the call returns a well-formed NTSTATUS and
        // does not throw. The fixture is mounted with default CaseSensitiveSearch=false,
        // so this exercises the upper-casing branch too.
        int status = _host.Notify(
            FileNotify.ChangeFileName,
            FileNotify.ActionAdded,
            @"\probe-path");
        // Either STATUS_SUCCESS or a documented error from the FSD; never a managed exception.
        status.Should().BeOneOf(NtStatus.Success, NtStatus.InvalidParameter, NtStatus.InvalidDeviceRequest);
    }

    [Fact]
    public void Notify_VeryLongPath_DoesNotThrow()
    {
        // > stack-buffer threshold (4096 bytes) — exercises the ArrayPool fallback branch.
        var longName = @"\" + new string('a', 3000);
        int status = _host.Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, longName);
        // total record size = 12 + 6000 = 6012 bytes, fits inside ushort.MaxValue.
        status.Should().BeOneOf(NtStatus.Success, NtStatus.InvalidParameter, NtStatus.InvalidDeviceRequest);
    }

    [Fact]
    public void Notify_OversizedPath_ReturnsInvalidParameter()
    {
        // Header (12) + 2 * 33_000 chars = 66_012 bytes > ushort.MaxValue (65_535).
        // Notify must reject before calling the IOCTL.
        var oversized = @"\" + new string('a', 33_000);
        int status = _host.Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, oversized);
        status.Should().Be(NtStatus.InvalidParameter);
    }

    [Fact]
    public void FileNotify_ConstantsMatchWinNT()
    {
        // Spot-check against the well-known Win32 values to catch typos.
        // FILE_NOTIFY_CHANGE_*: WinNT.h
        FileNotify.ChangeFileName.Should().Be(0x00000001u);
        FileNotify.ChangeDirName.Should().Be(0x00000002u);
        FileNotify.ChangeAttributes.Should().Be(0x00000004u);
        FileNotify.ChangeSize.Should().Be(0x00000008u);
        FileNotify.ChangeLastWrite.Should().Be(0x00000010u);
        FileNotify.ChangeLastAccess.Should().Be(0x00000020u);
        FileNotify.ChangeCreation.Should().Be(0x00000040u);
        FileNotify.ChangeSecurity.Should().Be(0x00000100u);
        // FILE_ACTION_*: WinNT.h
        FileNotify.ActionAdded.Should().Be(1u);
        FileNotify.ActionRemoved.Should().Be(2u);
        FileNotify.ActionModified.Should().Be(3u);
        FileNotify.ActionRenamedOldName.Should().Be(4u);
        FileNotify.ActionRenamedNewName.Should().Be(5u);
    }
}
