using System.Runtime.Versioning;
using FluentAssertions;
using WinFsp.Native;
using Xunit;

namespace WinFsp.Native.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  Unit tests for the async name-lookup API:
//    - SecurityByNameResult / DirInfoByNameResult result structs
//    - IFileSystem.GetFileSecurityByName / GetDirInfoByName (ValueTask) contract
//      (synchronous fast path + truly-async path + the GetDirInfoByName default)
//    - TestMemFs's concrete implementations
//  Plus mount integration tests that drive the OnGetSecurityByName /
//  OnGetDirInfoByName dispatch through a real WinFsp mount.
// ════════════════════════════════════════════════════════════════════════════

public class SecurityByNameResultTests
{
    [Fact]
    public void Success_SetsAttributesAndDefaultsSecurityDescriptorToNull()
    {
        var r = SecurityByNameResult.Success(0x10u);

        r.Status.Should().Be(NtStatus.Success);
        r.FileAttributes.Should().Be(0x10u);
        r.SecurityDescriptor.Should().BeNull();
    }

    [Fact]
    public void Success_CarriesSecurityDescriptorWhenProvided()
    {
        var sd = new byte[] { 1, 2, 3, 4 };

        var r = SecurityByNameResult.Success(0x80u, sd);

        r.Status.Should().Be(NtStatus.Success);
        r.FileAttributes.Should().Be(0x80u);
        r.SecurityDescriptor.Should().BeSameAs(sd);
    }

    [Fact]
    public void Error_SetsStatusAndLeavesAttributesAndSdAtDefault()
    {
        var r = SecurityByNameResult.Error(NtStatus.ObjectNameNotFound);

        r.Status.Should().Be(NtStatus.ObjectNameNotFound);
        r.FileAttributes.Should().Be(0u);
        r.SecurityDescriptor.Should().BeNull();
    }

    [Fact]
    public void Error_PreservesReparseStatus()
    {
        // GetFileSecurityByName may legitimately return STATUS_REPARSE; the result must round-trip it.
        var r = SecurityByNameResult.Error(NtStatus.Reparse);
        r.Status.Should().Be(NtStatus.Reparse);
    }
}

public class DirInfoByNameResultTests
{
    [Fact]
    public void Success_CarriesDirInfo()
    {
        var di = new FspDirInfo { FileInfo = new FspFileInfo { FileSize = 42, FileAttributes = 0x80u } };
        di.SetFileName("readme.txt");

        var r = DirInfoByNameResult.Success(di);

        r.Status.Should().Be(NtStatus.Success);
        r.DirInfo.FileInfo.FileSize.Should().Be(42UL);
        r.DirInfo.FileInfo.FileAttributes.Should().Be(0x80u);
    }

    [Fact]
    public void Error_SetsStatus()
    {
        var r = DirInfoByNameResult.Error(NtStatus.ObjectNameNotFound);
        r.Status.Should().Be(NtStatus.ObjectNameNotFound);
    }
}

[SupportedOSPlatform("windows")]
public class NameLookupContractTests
{
    [Fact]
    public async Task GetDirInfoByName_DefaultImplementation_CompletesSynchronouslyWithInvalidDeviceRequest()
    {
        IFileSystem fs = new MinimalFs();

        ValueTask<DirInfoByNameResult> task = fs.GetDirInfoByName(@"\", "x", new FileOperationInfo(), default);

        task.IsCompletedSuccessfully.Should().BeTrue("the default is an already-completed ValueTask");
        (await task).Status.Should().Be(NtStatus.InvalidDeviceRequest);
    }

    [Fact]
    public async Task GetFileSecurityByName_SynchronousImplementation_CompletesOnFastPath()
    {
        IFileSystem fs = new MinimalFs();

        ValueTask<SecurityByNameResult> task = fs.GetFileSecurityByName(@"\f", false, default);

        task.IsCompletedSuccessfully.Should().BeTrue("a sync impl returns an already-completed ValueTask (the dispatch fast path)");
        (await task).Status.Should().Be(NtStatus.ObjectNameNotFound);
    }

    [Fact]
    public async Task GetFileSecurityByName_AsyncImplementation_RoundTripsThroughValueTask()
    {
        // An async (await Task.Yield) implementation round-trips correctly through the
        // ValueTask<SecurityByNameResult> signature. (We don't assert IsCompletedSuccessfully:
        // after the async method returns, the yielded continuation may already have completed on
        // another thread, so the completed-state is a race — the result is what matters.)
        IFileSystem fs = new AsyncFs();

        SecurityByNameResult r = await fs.GetFileSecurityByName(@"\f", false, default);

        r.Status.Should().Be(NtStatus.Success);
        r.FileAttributes.Should().Be(0x80u);
    }

    [Fact]
    public async Task GetDirInfoByName_AsyncImplementation_RoundTripsThroughValueTask()
    {
        IFileSystem fs = new AsyncFs();

        DirInfoByNameResult r = await fs.GetDirInfoByName(@"\", "f.txt", new FileOperationInfo(), default);

        r.Status.Should().Be(NtStatus.Success);
        r.DirInfo.FileInfo.FileSize.Should().Be(7UL);
    }

    // Minimal IFileSystem: implements only the abstract members; GetDirInfoByName uses the interface default.
    [SupportedOSPlatform("windows")]
    private class MinimalFs : IFileSystem
    {
        public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
        { totalSize = 0; freeSize = 0; volumeLabel = ""; return NtStatus.Success; }

        public virtual ValueTask<SecurityByNameResult> GetFileSecurityByName(string fileName, bool getSecurityDescriptor, CancellationToken ct)
            => new(SecurityByNameResult.Error(NtStatus.ObjectNameNotFound));

        public ValueTask<CreateResult> CreateFile(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize, FileOperationInfo info, CancellationToken ct)
            => new(CreateResult.Error(NtStatus.AccessDenied));
        public ValueTask<CreateResult> OpenFile(string fileName, uint createOptions, uint grantedAccess, FileOperationInfo info, CancellationToken ct)
            => new(CreateResult.Error(NtStatus.ObjectNameNotFound));
        public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset, FileOperationInfo info, CancellationToken ct)
            => new(ReadResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset, bool writeToEndOfFile, bool constrainedIo, FileOperationInfo info, CancellationToken ct)
            => new(WriteResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(FsResult.Error(NtStatus.ObjectNameNotFound));
        public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(NtStatus.AccessDenied);
        public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker, nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
            => new(ReadDirectoryResult.Error(NtStatus.InvalidDeviceRequest));
    }

    // Provides truly-async (non-completed ValueTask) implementations of both name-lookup methods.
    // Re-declares IFileSystem so the new GetDirInfoByName re-maps the interface's default-implemented
    // member (a plain inherited method would not override a default interface method).
    [SupportedOSPlatform("windows")]
    private sealed class AsyncFs : MinimalFs, IFileSystem
    {
        public override async ValueTask<SecurityByNameResult> GetFileSecurityByName(string fileName, bool getSecurityDescriptor, CancellationToken ct)
        {
            await Task.Yield();
            return SecurityByNameResult.Success(0x80u);
        }

        public async ValueTask<DirInfoByNameResult> GetDirInfoByName(string dirName, string entryName, FileOperationInfo info, CancellationToken ct)
        {
            await Task.Yield();
            var di = new FspDirInfo { FileInfo = new FspFileInfo { FileSize = 7 } };
            di.SetFileName(entryName);
            return DirInfoByNameResult.Success(di);
        }
    }
}

[SupportedOSPlatform("windows")]
public class TestMemFsNameLookupTests
{
    private static TestMemFs NewFs() => new(AsyncMode.SyncCompleted, $@"\nlu-{Guid.NewGuid():N}");

    private static async Task Create(TestMemFs fs, string path, uint createOptions = 0, uint attrs = 0)
    {
        CreateResult r = await fs.CreateFile(path, createOptions, 0, attrs, null, 0, new FileOperationInfo(), default);
        r.Status.Should().Be(NtStatus.Success);
    }

    [Fact]
    public async Task GetFileSecurityByName_ExistingFile_ReturnsAttributesSynchronously()
    {
        var fs = NewFs();
        await Create(fs, @"\f.txt", attrs: 0x80u);

        ValueTask<SecurityByNameResult> task = fs.GetFileSecurityByName(@"\f.txt", false, default);

        task.IsCompletedSuccessfully.Should().BeTrue("a synchronous impl returns an already-completed ValueTask");
        SecurityByNameResult r = await task;
        r.Status.Should().Be(NtStatus.Success);
        (r.FileAttributes & 0x80u).Should().Be(0x80u);
        fs.GetFileSecurityByNameCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetFileSecurityByName_Directory_ReportsDirectoryAttribute()
    {
        var fs = NewFs();
        await Create(fs, @"\d", createOptions: 1); // FILE_DIRECTORY_FILE

        SecurityByNameResult r = await fs.GetFileSecurityByName(@"\d", false, default);

        r.Status.Should().Be(NtStatus.Success);
        (r.FileAttributes & 0x10u).Should().Be(0x10u); // FILE_ATTRIBUTE_DIRECTORY
    }

    [Fact]
    public async Task GetFileSecurityByName_Missing_ReturnsObjectNameNotFound()
    {
        var fs = NewFs();

        SecurityByNameResult r = await fs.GetFileSecurityByName(@"\nope", false, default);

        r.Status.Should().Be(NtStatus.ObjectNameNotFound);
    }

    [Fact]
    public async Task GetDirInfoByName_ExistingChild_ReturnsEntry()
    {
        var fs = NewFs();
        await Create(fs, @"\readme.txt", attrs: 0x80u);

        DirInfoByNameResult r = await fs.GetDirInfoByName(@"\", "readme.txt", new FileOperationInfo(), default);

        r.Status.Should().Be(NtStatus.Success);
        (r.DirInfo.FileInfo.FileAttributes & 0x80u).Should().Be(0x80u);
        fs.GetDirInfoByNameCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetDirInfoByName_MissingChild_ReturnsObjectNameNotFound()
    {
        var fs = NewFs();

        DirInfoByNameResult r = await fs.GetDirInfoByName(@"\", "nope.txt", new FileOperationInfo(), default);

        r.Status.Should().Be(NtStatus.ObjectNameNotFound);
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Mount integration: drive OnGetSecurityByName / OnGetDirInfoByName through a
//  real WinFsp mount (requires WinFsp installed — CI does `choco install winfsp`).
// ════════════════════════════════════════════════════════════════════════════

[SupportedOSPlatform("windows")]
public sealed class NameLookupMountFixture : IDisposable
{
    private readonly FileSystemHost _host;
    internal TestMemFs Fs { get; }
    public string Root { get; }

    public NameLookupMountFixture()
    {
        var prefix = $@"\winfsp-tests\namelookup-{Environment.ProcessId}";
        // Opt in to single-name query routing so exact-name lookups dispatch through GetDirInfoByName.
        Fs = new TestMemFs(AsyncMode.SyncCompleted, prefix, passQueryDirectoryFileName: true);
        _host = new FileSystemHost(Fs);
        int result = _host.Mount(null);
        if (result < 0)
            throw new InvalidOperationException($"Mount failed: 0x{result:X8}");
        Root = @"\" + prefix;
        if (!Root.EndsWith('\\'))
            Root += @"\";
    }

    public void Dispose() => _host.Dispose();
}

[SupportedOSPlatform("windows")]
public class NameLookupMountTests(NameLookupMountFixture fixture) : IClassFixture<NameLookupMountFixture>
{
    [Fact]
    public void OpeningAFile_InvokesGetFileSecurityByName()
    {
        var path = Path.Combine(fixture.Root, $"sec_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");

        int before = fixture.Fs.GetFileSecurityByNameCalls;
        // GetFileSecurityByName is called before every Create/Open for the access check.
        _ = File.ReadAllText(path);

        fixture.Fs.GetFileSecurityByNameCalls.Should().BeGreaterThan(before);
    }

    [Fact]
    public void ExactNameQuery_ResolvesEntryAndUsesGetDirInfoByName()
    {
        var name = $"byname_{Guid.NewGuid():N}.txt";
        File.WriteAllText(Path.Combine(fixture.Root, name), "hello");

        int before = fixture.Fs.GetDirInfoByNameCalls;
        // A literal single-name query (no wildcard); WinFsp routes single-entry lookups
        // through GetDirInfoByName when the file system implements it.
        var found = Directory.GetFiles(fixture.Root, name).Select(Path.GetFileName).ToArray();

        found.Should().Contain(name);
        fixture.Fs.GetDirInfoByNameCalls.Should().BeGreaterThan(before);
    }
}
