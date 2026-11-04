using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinFsp.Native.Interop;

namespace WinFsp.Native;

/// <summary>
/// High-level file system host. Accepts an <see cref="IFileSystem"/> implementation and
/// handles all WinFSP plumbing: mounting, callback dispatch, async STATUS_PENDING management,
/// buffer pooling, context tracking, and cancellation.
///
/// Built on top of <see cref="WinFspFileSystem"/> (low-level API).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class FileSystemHost(IFileSystem fileSystem) : IDisposable
{
    private readonly IFileSystem _fs = fileSystem;
    private WinFspFileSystem? _rawFs;
    private GCHandle _selfHandle;
    private bool _synchronousIo;

    ~FileSystemHost() => Dispose(false);

    // ═══════════════════════════════════════════
    //  Configuration (set before Mount)
    // ═══════════════════════════════════════════

    public ushort SectorSize { get; set; } = 4096;
    public ushort SectorsPerAllocationUnit { get; set; } = 1;
    public ushort MaxComponentLength { get; set; } = 255;
    public ulong VolumeCreationTime { get; set; }
    public uint VolumeSerialNumber { get; set; }
    public uint FileInfoTimeout { get; set; }
    public bool CaseSensitiveSearch { get; set; }
    public bool CasePreservedNames { get; set; } = true;
    public bool UnicodeOnDisk { get; set; } = true;
    public bool PersistentAcls { get; set; } = true;
    public bool ReparsePoints { get; set; }
    public bool ReparsePointsAccessCheck { get; set; }
    public bool NamedStreams { get; set; }
    public bool ExtendedAttributes { get; set; }
    public bool ReadOnlyVolume { get; set; }
    public bool PostCleanupWhenModifiedOnly { get; set; } = true;
    public bool PassQueryDirectoryPattern { get; set; }
    public bool PassQueryDirectoryFileName { get; set; }
    public bool FlushAndPurgeOnCleanup { get; set; }
    public bool DeviceControl { get; set; }
    public bool AllowOpenInKernelMode { get; set; }
    public bool WslFeatures { get; set; }
    public bool RejectIrpPriorToTransact0 { get; set; }
    public bool SupportsPosixUnlinkRename { get; set; }
    public bool PostDispositionWhenNecessaryOnly { get; set; }
    public string? Prefix { get; set; }
    /// <summary>Use the UNC prefix without creating a drive-letter mount. Requires a nonempty Prefix and a null mount point.</summary>
    public bool PrefixOnly { get; set; }
    public string FileSystemName { get; set; } = "NTFS";

    // ═══════════════════════════════════════════
    //  Mount / Unmount
    // ═══════════════════════════════════════════

    public int Mount(string? mountPoint, bool synchronized = false, uint debugLog = 0)
        => MountEx(mountPoint, 0, synchronized, debugLog);

    public int MountEx(string? mountPoint, uint threadCount,
        bool synchronized = false, uint debugLog = 0)
    {
        // 1. Init callback
        int result;
        try { result = _fs.Init(this); }
        catch (Exception ex) { return HandleException(ex); }
        if (result < 0) return result;
        if (PrefixOnly && (mountPoint != null || string.IsNullOrEmpty(Prefix))) return NtStatus.InvalidParameter;

        _synchronousIo = _fs.SynchronousIo;

        // 2. Create low-level FS and configure VolumeParams
        _rawFs = new WinFspFileSystem();
        ConfigureVolumeParams(ref _rawFs.VolumeParams);
        PopulateInterface(ref _rawFs.Interface, ReparsePoints);

        // Store ourselves in UserContext so callbacks can find us
        _selfHandle = GCHandle.Alloc(this);
        _rawFs.UserContext = GCHandle.ToIntPtr(_selfHandle);

        // 3. Mount
        result = PrefixOnly
            ? _rawFs.MountAtPrefix(threadCount, synchronized, debugLog)
            : _rawFs.Mount(mountPoint, threadCount, synchronized, debugLog);
        if (result < 0) { Cleanup(); return result; }

        // 4. Mounted callback
        try { result = _fs.Mounted(this); }
        catch (Exception ex) { result = HandleException(ex); }
        if (result < 0) { Cleanup(); return result; }

        return NtStatus.Success;
    }

    public void Unmount() => Dispose();

    public string? MountPoint => _rawFs?.MountPoint;
    public IFileSystem FileSystem => _fs;

    // ═══════════════════════════════════════════
    //  Cache invalidation (FspFileSystemNotify)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Invalidate the WinFsp kernel <c>FileInfo</c> cache for a single path.
    /// Call this from <see cref="IFileSystem"/> callbacks that change the existence,
    /// name, size, or attributes of a path so subsequent opens/reads observe the
    /// post-mutation state — even when <see cref="FileInfoTimeout"/> is large.
    ///
    /// <para>Always invoke <em>after</em> the user-mode mutation has committed and
    /// outside any locks the kernel might re-enter (the call is a synchronous IOCTL).</para>
    ///
    /// <para>For case-insensitive volumes the path is upper-cased in place inside an
    /// internal stack buffer before the IOCTL — callers pass the user-supplied case.</para>
    ///
    /// <para>Allocation-free for paths up to ~2030 characters (well above MAX_PATH).
    /// Falls back to <see cref="ArrayPool{T}"/> for longer paths.</para>
    /// </summary>
    /// <param name="filter">Bitwise OR of <see cref="FileNotify"/> <c>Change*</c> values.</param>
    /// <param name="action">One of <see cref="FileNotify"/> <c>Action*</c> values.</param>
    /// <param name="fileName">Path relative to the mount root, backslash-separated, e.g. <c>\dir\file</c>.</param>
    /// <returns>NTSTATUS from <c>FspFileSystemNotify</c>. Caller decides how to handle non-success;
    /// the originating IRP MUST NOT be failed because of a notification error.</returns>
    public int Notify(uint filter, uint action, ReadOnlySpan<char> fileName)
    {
        if (_rawFs == null) return NtStatus.InvalidDeviceRequest;

        int nameByteCount = fileName.Length * sizeof(char);
        int totalSize = sizeof(FspFsctlNotifyInfo) + nameByteCount;
        if (totalSize > ushort.MaxValue) return NtStatus.InvalidParameter;

        const int StackThreshold = 4096;
        byte[]? rented = null;
        Span<byte> buffer = totalSize <= StackThreshold
            ? stackalloc byte[StackThreshold].Slice(0, totalSize)
            : (rented = ArrayPool<byte>.Shared.Rent(totalSize)).AsSpan(0, totalSize);

        try
        {
            fixed (byte* p = buffer)
            {
                var info = (FspFsctlNotifyInfo*)p;
                info->Size = (ushort)totalSize;
                info->Filter = filter;
                info->Action = action;

                char* nameBuf = (char*)(p + sizeof(FspFsctlNotifyInfo));
                fileName.CopyTo(new Span<char>(nameBuf, fileName.Length));

                if (!CaseSensitiveSearch && fileName.Length > 0)
                    FspApi.CharUpperBuffW(nameBuf, (uint)fileName.Length);

                return FspApi.FspFileSystemNotify(_rawFs.Handle, info, (nuint)totalSize);
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// String overload of <see cref="Notify(uint, uint, ReadOnlySpan{char})"/>.
    /// </summary>
    public int Notify(uint filter, uint action, string fileName)
        => Notify(filter, action, fileName.AsSpan());

    // ═══════════════════════════════════════════
    //  IDisposable
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_rawFs != null)
        {
            _rawFs.Unmount();
            if (disposing)
            {
                try { _fs.Unmounted(this); } catch { }
            }
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _rawFs?.Dispose();
        _rawFs = null;
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    // ═══════════════════════════════════════════
    //  Per-handle state
    // ═══════════════════════════════════════════

    private sealed class HandleState
    {
        public readonly FileOperationInfo Info = new();
        private string? _fileName;
        public string? FileName
        {
            get => _fileName;
            set { _fileName = value; Info.FileName = value; }
        }
    }

    // ═══════════════════════════════════════════
    //  Callback helpers
    // ═══════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FileSystemHost Self(nint fileSystem)
        => (FileSystemHost)GCHandle.FromIntPtr(FspApi.GetUserContext(fileSystem)).Target!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HandleState H(FspFullContext* ctx)
    {
        if (ctx->UserContext2 != 0)
            return (HandleState)GCHandle.FromIntPtr((nint)ctx->UserContext2).Target!;
        return null!;
    }

    private static void SetHandle(FspFullContext* ctx, HandleState hs)
    {
        var handle = GCHandle.Alloc(hs);
        ctx->UserContext2 = (ulong)(nint)GCHandle.ToIntPtr(handle);
    }

    private static void FreeHandle(FspFullContext* ctx)
    {
        if (ctx->UserContext2 != 0)
        {
            GCHandle.FromIntPtr((nint)ctx->UserContext2).Free();
            ctx->UserContext2 = 0;
        }
    }

    private int HandleException(Exception ex)
    {
        try { return _fs.ExceptionHandler(ex); }
        catch { return NtStatus.UnexpectedIoError; }
    }

    // ═══════════════════════════════════════════
    //  VolumeParams configuration
    // ═══════════════════════════════════════════

    private void ConfigureVolumeParams(ref FspVolumeParams vp)
    {
        vp.SectorSize = SectorSize;
        vp.SectorsPerAllocationUnit = SectorsPerAllocationUnit;
        vp.MaxComponentLength = MaxComponentLength;
        vp.VolumeCreationTime = VolumeCreationTime;
        vp.VolumeSerialNumber = VolumeSerialNumber;
        vp.FileInfoTimeout = FileInfoTimeout;
        vp.CaseSensitiveSearch = CaseSensitiveSearch;
        vp.CasePreservedNames = CasePreservedNames;
        vp.UnicodeOnDisk = UnicodeOnDisk;
        vp.PersistentAcls = PersistentAcls;
        vp.ReparsePoints = ReparsePoints;
        vp.ReparsePointsAccessCheck = ReparsePointsAccessCheck;
        vp.NamedStreams = NamedStreams;
        vp.ExtendedAttributes = ExtendedAttributes;
        vp.ReadOnlyVolume = ReadOnlyVolume;
        vp.PostCleanupWhenModifiedOnly = PostCleanupWhenModifiedOnly;
        vp.PassQueryDirectoryPattern = PassQueryDirectoryPattern;
        vp.PassQueryDirectoryFileName = PassQueryDirectoryFileName;
        vp.FlushAndPurgeOnCleanup = FlushAndPurgeOnCleanup;
        vp.DeviceControl = DeviceControl;
        vp.AllowOpenInKernelMode = AllowOpenInKernelMode;
        vp.WslFeatures = WslFeatures;
        vp.RejectIrpPriorToTransact0 = RejectIrpPriorToTransact0;
        vp.SupportsPosixUnlinkRename = SupportsPosixUnlinkRename;
        vp.PostDispositionWhenNecessaryOnly = PostDispositionWhenNecessaryOnly;
        vp.UmFileContextIsFullContext = true; // always FullContext for handle tracking
        if (Prefix != null) vp.SetPrefix(Prefix);
        vp.SetFileSystemName(FileSystemName);
    }

    // ═══════════════════════════════════════════
    //  Populate function pointers
    // ═══════════════════════════════════════════

    private static void PopulateInterface(ref FspFileSystemInterface iface, bool reparsePoints)
    {
        iface.GetVolumeInfo = &OnGetVolumeInfo;
        iface.SetVolumeLabel = &OnSetVolumeLabel;
        iface.GetSecurityByName = &OnGetSecurityByName;
        iface.Create = &OnCreate;
        iface.Open = &OnOpen;
        iface.Overwrite = &OnOverwrite;
        iface.Cleanup = &OnCleanup;
        iface.Close = &OnClose;
        iface.Read = &OnRead;
        iface.Write = &OnWrite;
        iface.Flush = &OnFlush;
        iface.GetFileInfo = &OnGetFileInfo;
        iface.SetBasicInfo = &OnSetBasicInfo;
        iface.SetFileSize = &OnSetFileSize;
        iface.CanDelete = &OnCanDelete;
        iface.Rename = &OnRename;
        iface.GetSecurity = &OnGetSecurity;
        iface.SetSecurity = &OnSetSecurity;
        iface.ReadDirectory = &OnReadDirectory;
        if (reparsePoints)
            iface.ResolveReparsePoints = (nint)(delegate* unmanaged[Cdecl]<nint, char*, uint, byte, nint, nint, nuint*, int>)&OnResolveReparsePoints;
        iface.GetReparsePoint = &OnGetReparsePoint;
        iface.SetReparsePoint = &OnSetReparsePoint;
        iface.DeleteReparsePoint = &OnDeleteReparsePoint;
        iface.GetStreamInfo = &OnGetStreamInfo;
        iface.GetDirInfoByName = &OnGetDirInfoByName;
        iface.Control = &OnControl;
        iface.GetEa = &OnGetEa;
        iface.SetEa = &OnSetEa;
        iface.DispatcherStopped = &OnDispatcherStopped;
    }

    // ═══════════════════════════════════════════
    //  [UnmanagedCallersOnly] Callbacks
    //  Each: Self(fs) → IFileSystem method → NTSTATUS or STATUS_PENDING
    // ═══════════════════════════════════════════

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetVolumeInfo(nint fs, FspVolumeInfo* pVi)
    {
        var self = Self(fs);
        try
        {
            int r = self._fs.GetVolumeInfo(out ulong total, out ulong free, out string label);
            if (r >= 0) { pVi->TotalSize = total; pVi->FreeSize = free; pVi->SetVolumeLabel(label); }
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetVolumeLabel(nint fs, char* label, FspVolumeInfo* pVi)
    {
        var self = Self(fs);
        try
        {
            string s = new(label);
            int r = self._fs.SetVolumeLabel(s, out ulong total, out ulong free);
            if (r >= 0) { pVi->TotalSize = total; pVi->FreeSize = free; pVi->SetVolumeLabel(s); }
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetSecurityByName(nint fs, char* fileName, uint* pAttr, nint sd, nuint* pSdSize)
    {
        var self = Self(fs);
        try
        {
            string name = new(fileName);
            // WinFsp calls this synchronously (no STATUS_PENDING path); block only if the
            // implementation's task did not complete synchronously.
            var task = self._fs.GetFileSecurityByName(name, pSdSize != null, CancellationToken.None);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (self.ReparsePoints && (r.Status == NtStatus.ObjectNameNotFound || r.Status == NtStatus.ObjectPathNotFound)
                && FspApi.FspFileSystemFindReparsePoint(fs, &OnGetReparsePointByName, 0, fileName, pAttr) != 0)
                return NtStatus.Reparse;
            if (r.Status >= 0 || r.Status == NtStatus.Reparse)
            {
                if (pAttr != null) *pAttr = r.FileAttributes;
                if (pSdSize != null) CopySecurityDescriptor(r.SecurityDescriptor, sd, pSdSize);
            }
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnCreate(nint fs, char* fileName, uint co, uint ga, uint fa, nint sd, ulong alloc,
        FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = new HandleState { FileName = new string(fileName) };
            byte[]? sdBytes = MakeSecurityDescriptor(sd);
            var task = self._fs.CreateFile(hs.FileName, co, ga, fa, sdBytes, alloc, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) { SetHandle(ctx, hs); *pFi = r.FileInfo; }
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnOpen(nint fs, char* fileName, uint co, uint ga, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = new HandleState { FileName = new string(fileName) };
            var task = self._fs.OpenFile(hs.FileName, co, ga, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) { SetHandle(ctx, hs); *pFi = r.FileInfo; }
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnOverwrite(nint fs, FspFullContext* ctx, uint fa, byte rfa, ulong alloc, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.OverwriteFile(fa, rfa != 0, alloc, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnCleanup(nint fs, FspFullContext* ctx, char* fileName, uint flags)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            hs.Info.CancelPendingOperations();
            string? name = fileName != null ? new string(fileName) : null;
            self._fs.Cleanup(name ?? hs.FileName, hs.Info, (CleanupFlags)flags);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnClose(nint fs, FspFullContext* ctx)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            self._fs.Close(hs.Info);
            hs.Info.DisposeResources();
        }
        catch { }
        finally { FreeHandle(ctx); }
    }

    // ── Read/Write: sync FS = zero-copy (kernel buffer passed directly)
    //                async FS = rent from PinnedBufferPool ──

    /// <summary>
    /// Thread-static native buffer wrapper. Avoids allocating MemoryManager per I/O.
    /// Safe because WinFsp dispatcher threads process one request at a time.
    /// Only used when <see cref="IFileSystem.SynchronousIo"/> is true.
    /// </summary>
    [ThreadStatic] private static NativeBufferMemory? t_nativeBuffer;

    private static NativeBufferMemory GetNativeBuffer(nint ptr, int length)
    {
        var buf = t_nativeBuffer ??= new NativeBufferMemory();
        buf.Reset(ptr, length);
        return buf;
    }

    /// <summary>
    /// Reusable MemoryManager that wraps a native pointer without owning it.
    /// IMPORTANT: Dispose is intentionally a no-op — this buffer is thread-static and reused.
    /// IFileSystem implementers must NOT dispose the Memory&lt;byte&gt; received in ReadFile/WriteFile.
    /// </summary>
    private sealed class NativeBufferMemory : MemoryManager<byte>
    {
        private nint _ptr;
        private int _length;

        public void Reset(nint ptr, int length) { _ptr = ptr; _length = length; }

        public override Span<byte> GetSpan() => new((void*)_ptr, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_ptr + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { /* no-op: thread-static, not owned */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnRead(nint fs, FspFullContext* ctx, nint buffer, ulong offset, uint length, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);

            if (self._synchronousIo)
            {
                // Zero-copy: wrap kernel buffer directly, pass to ReadFile
                var directBuf = GetNativeBuffer(buffer, (int)length);
                var task = self._fs.ReadFile(hs.FileName!, directBuf.Memory, offset, hs.Info, hs.Info.CancellationToken);
                var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
                *pBt = r.BytesTransferred;
                return r.Status;
            }

            // Zero-copy async: wrap kernel buffer directly — it stays valid until
            // FspFileSystemSendResponse is called, so ReadFile can write into it
            // on any thread. No pool rent/return, no extra memcpy.
            var asyncBuf = new NativeBufferMemory();
            asyncBuf.Reset(buffer, (int)length);
            var asyncTask = self._fs.ReadFile(hs.FileName!, asyncBuf.Memory, offset, hs.Info, hs.Info.CancellationToken);

            if (asyncTask.IsCompletedSuccessfully)
            {
                var r = asyncTask.Result;
                *pBt = r.BytesTransferred;
                return r.Status;
            }

            // STATUS_PENDING — must build a fresh response buffer (MEMFS pattern).
            // The dispatcher's Response may be invalidated after we return STATUS_PENDING.
            var hint = FspApi.FspFileSystemGetOperationContext()->Request->Hint;
            asyncTask.AsTask().ContinueWith(t =>
            {
                var rsp = new FspTransactRsp();
                rsp.Size = (ushort)sizeof(FspTransactRsp);
                rsp.Kind = FspTransactKind.Read;
                rsp.Hint = hint;
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        var r = t.Result;
                        rsp.IoStatusStatus = r.Status;
                        rsp.IoStatusInformation = r.BytesTransferred;
                    }
                    else if (t.IsCanceled)
                    {
                        rsp.IoStatusStatus = NtStatus.Cancelled;
                    }
                    else
                    {
                        rsp.IoStatusStatus = NtStatus.UnexpectedIoError;
                    }
                }
                catch
                {
                    rsp.IoStatusStatus = NtStatus.UnexpectedIoError;
                }
                FspApi.FspFileSystemSendResponse(fs, &rsp);
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnWrite(nint fs, FspFullContext* ctx, nint buffer, ulong offset, uint length,
        byte wteof, byte cio, uint* pBt, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);

            if (self._synchronousIo)
            {
                // Zero-copy: wrap kernel buffer directly, pass to WriteFile
                var directBuf = GetNativeBuffer(buffer, (int)length);
                var task = self._fs.WriteFile(hs.FileName!, directBuf.Memory, offset,
                    wteof != 0, cio != 0, hs.Info, hs.Info.CancellationToken);
                var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
                *pBt = r.BytesTransferred;
                if (r.Status >= 0) *pFi = r.FileInfo;
                return r.Status;
            }

            // Zero-copy async: kernel buffer stays valid until FspFileSystemSendResponse
            var asyncBuf = new NativeBufferMemory();
            asyncBuf.Reset(buffer, (int)length);

            var asyncTask = self._fs.WriteFile(hs.FileName!, asyncBuf.Memory, offset,
                wteof != 0, cio != 0, hs.Info, hs.Info.CancellationToken);

            if (asyncTask.IsCompletedSuccessfully)
            {
                var r = asyncTask.Result;
                *pBt = r.BytesTransferred;
                if (r.Status >= 0) *pFi = r.FileInfo;
                return r.Status;
            }

            // STATUS_PENDING — build fresh response (MEMFS pattern)
            var hint = FspApi.FspFileSystemGetOperationContext()->Request->Hint;
            asyncTask.AsTask().ContinueWith(t =>
            {
                var rsp = new FspTransactRsp();
                rsp.Size = (ushort)sizeof(FspTransactRsp);
                rsp.Kind = FspTransactKind.Write;
                rsp.Hint = hint;
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        var r = t.Result;
                        rsp.IoStatusStatus = r.Status;
                        rsp.IoStatusInformation = r.BytesTransferred;
                        if (r.Status >= 0) rsp.FileInfo = r.FileInfo;
                    }
                    else if (t.IsCanceled) { rsp.IoStatusStatus = NtStatus.Cancelled; }
                    else { rsp.IoStatusStatus = NtStatus.UnexpectedIoError; }
                }
                catch
                {
                    rsp.IoStatusStatus = NtStatus.UnexpectedIoError;
                }
                FspApi.FspFileSystemSendResponse(fs, &rsp);
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            *pFi = default;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── ReadDirectory: same pattern ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnReadDirectory(nint fs, FspFullContext* ctx, char* pattern, char* marker,
        nint buffer, uint length, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            string? pat = pattern != null ? new string(pattern) : null;
            string? mark = marker != null ? new string(marker) : null;
            var task = self._fs.ReadDirectory(hs.FileName!, pat, mark, buffer, length,
                hs.Info, hs.Info.CancellationToken);

            if (task.IsCompletedSuccessfully)
            {
                var r = task.Result;
                *pBt = r.BytesTransferred;
                return r.Status;
            }

            // STATUS_PENDING — build fresh response (MEMFS pattern)
            var hint = FspApi.FspFileSystemGetOperationContext()->Request->Hint;
            task.AsTask().ContinueWith(t =>
            {
                var rsp = new FspTransactRsp();
                rsp.Size = (ushort)sizeof(FspTransactRsp);
                rsp.Kind = FspTransactKind.QueryDirectory;
                rsp.Hint = hint;
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        rsp.IoStatusStatus = t.Result.Status;
                        rsp.IoStatusInformation = t.Result.BytesTransferred;
                    }
                    else if (t.IsCanceled) { rsp.IoStatusStatus = NtStatus.Cancelled; }
                    else { rsp.IoStatusStatus = NtStatus.UnexpectedIoError; }
                }
                catch
                {
                    rsp.IoStatusStatus = NtStatus.UnexpectedIoError;
                }
                FspApi.FspFileSystemSendResponse(fs, &rsp);
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Simple sync callbacks ──

    /// <summary>Shared sentinel for volume-level operations (no per-handle state).</summary>
    private static readonly FileOperationInfo s_volumeInfo = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnFlush(nint fs, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = ctx->UserContext2 != 0 ? H(ctx) : null;
            var info = hs?.Info ?? s_volumeInfo;
            var task = self._fs.FlushFileBuffers(hs?.FileName, info, info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetFileInfo(nint fs, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.GetFileInformation(hs.FileName!, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetBasicInfo(nint fs, FspFullContext* ctx, uint fa, ulong ct, ulong lat, ulong lwt, ulong cht, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.SetFileAttributes(hs.FileName!, fa, ct, lat, lwt, cht, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetFileSize(nint fs, FspFullContext* ctx, ulong sz, byte alloc, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.SetFileSize(hs.FileName!, sz, alloc != 0, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnCanDelete(nint fs, FspFullContext* ctx, char* fn)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.CanDelete(new string(fn), hs.Info, hs.Info.CancellationToken);
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnRename(nint fs, FspFullContext* ctx, char* fn, char* newFn, byte replace)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            string newName = new(newFn);
            var task = self._fs.MoveFile(new string(fn), newName, replace != 0, hs.Info, hs.Info.CancellationToken);
            int r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r >= 0) hs.FileName = newName;
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Security ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetSecurity(nint fs, FspFullContext* ctx, nint sd, nuint* pSdSize)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[]? sdBytes = pSdSize != null ? [] : null;
            int r = self._fs.GetFileSecurity(hs.FileName!, ref sdBytes, hs.Info);
            if (r >= 0 && pSdSize != null) CopySecurityDescriptor(sdBytes, sd, pSdSize);
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetSecurity(nint fs, FspFullContext* ctx, uint si, nint md)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] mdBytes = MakeSecurityDescriptor(md) ?? [];
            return self._fs.SetFileSecurity(hs.FileName!, si, mdBytes, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Reparse ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnResolveReparsePoints(nint fs, char* fileName, uint index, byte resolveLast,
        nint ioStatus, nint buffer, nuint* size)
    {
        var self = Self(fs);
        try
        {
            return FspApi.FspFileSystemResolveReparsePoints(fs, &OnGetReparsePointByName, 0,
                fileName, index, resolveLast, ioStatus, buffer, size);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetReparsePointByName(nint fs, nint context, char* fileName, byte isDirectory,
        nint buffer, nuint* size)
    {
        var self = Self(fs);
        try
        {
            int status = self._fs.GetReparsePointByName(new string(fileName), isDirectory != 0, out var data);
            if (status < 0) return status;
            if (!ValidReparseData(data)) return NtStatus.InvalidReparseData;
            if (buffer == 0) return status;
            if (isDirectory != 0) data = TrimSymlinkTargetSeparators(data!);
            return CopyReparseData(data, buffer, size);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint* pSz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[]? data = null;
            int r = self._fs.GetReparsePoint(new string(fn), ref data, hs.Info);
            if (r >= 0) return CopyReparseData(data, buf, pSz);
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint sz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] data = new byte[(int)sz];
            Marshal.Copy(buf, data, 0, data.Length);
            return self._fs.SetReparsePoint(new string(fn), data, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnDeleteReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint sz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] data = new byte[(int)sz];
            Marshal.Copy(buf, data, 0, data.Length);
            return self._fs.DeleteReparsePoint(new string(fn), data, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Streams / EA / IOCTL / Misc ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetStreamInfo(nint fs, FspFullContext* ctx, nint buf, uint len, uint* pBt)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.GetStreamInfo(hs.FileName!, buf, len, out uint bt, hs.Info); *pBt = bt; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetDirInfoByName(nint fs, FspFullContext* ctx, char* fn, FspDirInfo* pDi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.GetDirInfoByName(hs.FileName!, new string(fn), hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pDi = r.DirInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnControl(nint fs, FspFullContext* ctx, uint cc, nint inBuf, uint inLen, nint outBuf, uint outLen, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var input = new ReadOnlySpan<byte>((void*)inBuf, (int)inLen);
            var output = new Span<byte>((void*)outBuf, (int)outLen);
            int r = self._fs.DeviceControl(hs.FileName!, cc, input, output, out uint bt, hs.Info);
            *pBt = bt;
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetEa(nint fs, FspFullContext* ctx, nint ea, uint len, uint* pBt)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.GetEa(hs.FileName!, ea, len, out uint bt, hs.Info); *pBt = bt; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetEa(nint fs, FspFullContext* ctx, nint ea, uint len, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.SetEa(hs.FileName!, ea, len, out var fi, hs.Info); if (r >= 0) *pFi = fi; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDispatcherStopped(nint fs, byte normally)
    {
        try { Self(fs)._fs.DispatcherStopped(normally != 0); } catch { }
    }

    // ═══════════════════════════════════════════
    //  Security descriptor helpers
    // ═══════════════════════════════════════════

    private static void CopySecurityDescriptor(byte[]? sd, nint buffer, nuint* pSize)
    {
        if (sd != null && sd.Length > 0)
        {
            if ((int)*pSize < sd.Length) { *pSize = (nuint)sd.Length; return; }
            *pSize = (nuint)sd.Length;
            if (buffer != 0) Marshal.Copy(sd, 0, buffer, sd.Length);
        }
        else { *pSize = 0; }
    }

    private static byte[]? MakeSecurityDescriptor(nint ptr)
    {
        if (ptr == 0) return null;
        int len = (int)GetSecurityDescriptorLength(ptr);
        byte[] sd = new byte[len];
        Marshal.Copy(ptr, sd, 0, len);
        return sd;
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint pSecurityDescriptor);

    private static int CopyReparseData(byte[]? data, nint buffer, nuint* pSize)
    {
        if (!ValidReparseData(data)) return NtStatus.InvalidReparseData;
        if (data != null && data.Length > 0)
        {
            if (*pSize < (nuint)data.Length) { *pSize = (nuint)data.Length; return NtStatus.BufferTooSmall; }
            *pSize = (nuint)data.Length;
            if (buffer != 0) Marshal.Copy(data, 0, buffer, data.Length);
        }
        else { *pSize = 0; }
        return NtStatus.Success;
    }

    // WinFsp's native resolver joins the target to a remainder beginning with
    // a backslash without removing duplicate separators. Only change the
    // by-name resolution buffer; handle-based queries return the stored data.
    private static byte[] TrimSymlinkTargetSeparators(byte[] data)
    {
        var bytes = data.AsSpan();
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0xA000000C) return data;
        int start = 20 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int end = start + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        int originalEnd = end;
        while (end > start && bytes[end - 2] == (byte)'\\' && bytes[end - 1] == 0) end -= 2;
        if (end == originalEnd) return data;

        // Keep a root target absolute, and let the native resolver remove the
        // dot before it joins the remaining components.
        int length = end == start ? 4 : end - start;
        byte[] result = new byte[20 + length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result, 0xA000000C);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), (ushort)(result.Length - 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), (ushort)length);
        bytes.Slice(16, 4).CopyTo(result.AsSpan(16));
        if (end == start) { result[20] = (byte)'\\'; result[22] = (byte)'.'; }
        else bytes.Slice(start, length).CopyTo(result.AsSpan(20));
        return result;
    }

    // WinFsp's resolver dereferences tag-specific offsets; validate managed callback
    // results before exposing them to native code, even for existence-only probes.
    private static bool ValidReparseData(byte[]? data)
    {
        if (data == null || data.Length < 8 || data.Length > 16384) return false;
        var bytes = data.AsSpan();
        uint tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        // Third-party tags carry a GUID between the common header and payload;
        // ReparseDataLength excludes that GUID.
        int headerSize = (tag & 0x80000000u) != 0 ? 8 : 24;
        if (data.Length < headerSize ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != data.Length - headerSize) return false;
        int start = tag == 0xA0000003 ? 16 : tag == 0xA000000C ? 20 : 0;
        if (start == 0) return true;
        if (data.Length < start) return false;
        int a = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int b = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        int c = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        int d = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        return b != 0 && ((a | b | c | d) & 1) == 0 && a + b <= data.Length - start && c + d <= data.Length - start;
    }
}
