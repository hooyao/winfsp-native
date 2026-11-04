using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using FluentAssertions;
using Xunit;

namespace WinFsp.Native.Tests;

[SupportedOSPlatform("windows")]
public sealed class ReparseResolutionTests
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, nint overlapped);

    [Fact]
    public void Host_ResolvesFinalAndIntermediateRelativeSymlinks()
    {
        string prefix = $@"\winfsp-tests\reparse-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var fs = new TestMemFs(AsyncMode.Sync, prefix, reparsePoints: true);
        using var host = new FileSystemHost(fs);
        host.Mount(null).Should().Be(NtStatus.Success);
        host.MountPoint.Should().BeNull("prefix-only mounts must not reserve a drive letter");
        string root = @"\" + prefix + @"\";
        Directory.CreateDirectory(Path.Combine(root, "target"));
        File.WriteAllText(Path.Combine(root, "target", "data"), "contents");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), "target");
        File.CreateSymbolicLink(Path.Combine(root, "file"), @"target\data");

        File.ReadAllText(Path.Combine(root, "link", "data")).Should().Be("contents");
        File.ReadAllText(Path.Combine(root, "file")).Should().Be("contents");
        File.ReadAllText(Path.Combine(root, "target", "data")).Should().Be("contents");
        Directory.CreateSymbolicLink(Path.Combine(root, "trailing"), @"target\");
        File.ReadAllText(Path.Combine(root, "trailing", "data")).Should().Be("contents");
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(root, "link", "missing")));

        using var handle = CreateFile(Path.Combine(root, "link"), 0x80000000, 7, 0, 3, 0x02200000, 0);
        handle.IsInvalid.Should().BeFalse();
        bool ok = DeviceIoControl(handle, 0x000900A8, 0, 0, new byte[8], 8, out _, 0);
        int error = Marshal.GetLastWin32Error();
        ok.Should().BeFalse();
        error.Should().Be(122); // ERROR_INSUFFICIENT_BUFFER

        fs.CorruptReparseForTest(@"\link", [0x0C, 0, 0, 0xA0, 0, 0, 0, 0]);
        ok = DeviceIoControl(handle, 0x000900A8, 0, 0, new byte[16384], 16384, out _, 0);
        error = Marshal.GetLastWin32Error();
        ok.Should().BeFalse();
        error.Should().Be(4392); // malformed callback data must not reach the native resolver
    }

    [Fact]
    public void GetReparsePoint_AcceptsGuidBufferAndRejectsMissingGuid()
    {
        string prefix = $@"\winfsp-tests\guid-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var fs = new TestMemFs(AsyncMode.Sync, prefix, reparsePoints: true);
        using var host = new FileSystemHost(fs);
        host.Mount(null).Should().Be(NtStatus.Success);
        string path = @"\" + prefix + @"\generic";
        File.WriteAllText(path, "");
        using var handle = CreateFile(path, 0x80000000, 7, 0, 3, 0x02200000, 0);
        handle.IsInvalid.Should().BeFalse();

        byte[] data = new byte[27]; // 8-byte header + 16-byte GUID + 3-byte payload
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 3);
        Guid.NewGuid().TryWriteBytes(data.AsSpan(8, 16)).Should().BeTrue();
        data[24] = 1; data[25] = 2; data[26] = 3;
        fs.CorruptReparseForTest(@"\generic", data);
        byte[] output = new byte[16384];
        bool ok = DeviceIoControl(handle, 0x000900A8, 0, 0, output, (uint)output.Length, out uint returned, 0);
        ok.Should().BeTrue($"Win32 error {Marshal.GetLastWin32Error()}");
        output[..(int)returned].Should().Equal(data);

        byte[] missingGuid = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(missingGuid, 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(missingGuid.AsSpan(4), 3);
        fs.CorruptReparseForTest(@"\generic", missingGuid);
        ok = DeviceIoControl(handle, 0x000900A8, 0, 0, output, (uint)output.Length, out _, 0);
        int error = Marshal.GetLastWin32Error();
        ok.Should().BeFalse();
        error.Should().Be(4392);
    }
}
