# WinFsp.Native — Building a Modern .NET WinFSP Binding from Scratch

This document records the complete process of writing a modern .NET 8+/10 binding library for WinFSP (Windows File System Proxy), including architecture design, implementation details, every pitfall encountered, and the final verified solutions.

Target audience: developers who want to replicate this binding library, or understand WinFSP .NET interop details.

---

## 1. Prerequisites

- Windows 10/11
- .NET 8+ SDK (or .NET 10 SDK for full feature set)
- [WinFSP](https://winfsp.dev/rel/) 2.x installed (with Developer files)
- Visual Studio Build Tools (for AOT publish — requires C++ linker)
- `vswhere.exe` in PATH (for AOT publish; usually at `C:\Program Files (x86)\Microsoft Visual Studio\Installer\`)

Verify WinFSP installation:
```powershell
# WinFSP Launcher service should be running
Get-Service WinFsp.Launcher
# Native memfs should work
& "C:\Program Files (x86)\WinFsp\bin\memfs-x64.exe" -m T:
# In another terminal: dir T:\ should show empty directory
```

---

## 2. Architecture

```
    ┌─────────────────────────────────────────────────────┐
    │                    User Code                         │
    │                                                      │
    │  ┌──── Choice A ────┐    ┌──── Choice B ───────────┐ │
    │  │ IFileSystem       │    │ WinFspFileSystem        │ │
    │  │ (high-level)      │    │ (low-level raw API)     │ │
    │  │ ValueTask,        │    │ delegate → nint slot    │ │
    │  │ string,           │    │ nint, char*, NTSTATUS   │ │
    │  │ Memory<byte>      │    │ FspFullContext*          │ │
    │  └────────┬──────────┘    └──────────┬──────────────┘ │
    └───────────┼──────────────────────────┼────────────────┘
                │                          │
    ════════════╪════════ HIGH ════════════╪════════════════
                │                          │
    ┌───────────▼────────────────┐         │
    │  FileSystemHost            │         │
    │  IFileSystem → callbacks   │         │
    │  STATUS_PENDING mgmt       │         │
    │  buffer pool, cancel       │         │
    └───────────┬────────────────┘         │
                │ uses internally          │
    ════════════╪════════ LOW ═════════════╪════════════════
                │                          │
    ┌───────────▼──────────────────────────▼────────────────┐
    │  WinFspFileSystem                                     │
    │  VolumeParams + InterfacePtr + Mount/Unmount          │
    └───────────┬───────────────────────────────────────────┘
                │ P/Invoke (LibraryImport)
    ┌───────────▼───────────────────────────────────────────┐
    │  Interop: FspApi + FspStructs + NtStatus              │
    └───────────┬───────────────────────────────────────────┘
                │ winfsp-x64.dll (runtime, GPLv3)
    ┌───────────▼───────────────┐
    │  WinFSP kernel driver     │
    └───────────────────────────┘
```

**Low-Level API** (`WinFspFileSystem`): Zero abstraction — user fills function pointers into a 64-slot struct and handles everything directly. Suitable for porting C/C++ WinFSP implementations or pursuing maximum performance.

**High-Level API** (`IFileSystem` + `FileSystemHost`): Dokan-style interface with `ValueTask` async. The framework handles `STATUS_PENDING`, buffer pooling, and cancellation. Suitable for normal development.

---

## 3. WinFSP Core Concepts (Must Understand Before Writing .NET Bindings)

### 3.1 Callback Table: FSP_FILE_SYSTEM_INTERFACE

WinFSP's core data structure. 64 function pointer slots, each 8 bytes (x64), totaling 512 bytes.

```
slot  0: GetVolumeInfo
slot  1: SetVolumeLabel
slot  2: GetSecurityByName     ← called during access checks
slot  3: Create                ← create new file/directory
slot  4: Open                  ← open existing file/directory
slot  5: Overwrite             ← truncate/overwrite file
slot  6: Cleanup               ← last handle closed
slot  7: Close                 ← all references released
slot  8: Read
slot  9: Write
slot 10: Flush
slot 11: GetFileInfo
slot 12: SetBasicInfo
slot 13: SetFileSize
slot 14: CanDelete
slot 15: Rename
slot 16: GetSecurity
slot 17: SetSecurity
slot 18: ReadDirectory
slot 19: ResolveReparsePoints
slot 20: GetReparsePoint
...
slot 27: CreateEx              ← extended Create (supports EA/reparse)
slot 28: OverwriteEx           ← extended Overwrite
...
slot 32: DispatcherStopped
slots 33-63: Reserved
```

Null slots → WinFSP automatically returns `STATUS_INVALID_DEVICE_REQUEST`.

### 3.2 Three Mandatory Callbacks (The Biggest Trap)

**Hard-coded check in fsop.c lines 907-910:**

```c
if ((0 == Interface->Create && 0 == Interface->CreateEx) ||
    0 == Interface->Open ||
    (0 == Interface->Overwrite && 0 == Interface->OverwriteEx))
    return STATUS_INVALID_DEVICE_REQUEST;
```

`Create` (or `CreateEx`) + `Open` + `Overwrite` (or `OverwriteEx`) **must all be non-null**.

Missing any one → drive mounts successfully (`Mount()` returns 0) but is **completely inaccessible**. `dir` / `type` / Explorer all return "Incorrect function" or "drive not found". WinFSP debug log shows `<<Create IoStatus=c0000010` (STATUS_INVALID_DEVICE_REQUEST).

**Symptoms are extremely obscure**: mount succeeds, dispatcher starts, drive letter appears — but no request ever reaches the Open callback. Easy to misdiagnose as a function pointer, struct layout, or .NET runtime issue.

**Even read-only FS requires:**
- `Create` → return `STATUS_OBJECT_NAME_NOT_FOUND` (don't allow creating new files)
- `Overwrite` → return `STATUS_MEDIA_WRITE_PROTECTED` (don't allow overwriting)

### 3.3 `FspFileSystemCreate` Behavior

```c
NTSTATUS FspFileSystemCreate(
    PWSTR DevicePath,               // "WinFsp.Disk" or "WinFsp.Net"
    const FSP_FSCTL_VOLUME_PARAMS*, // 504-byte config struct, copied by value
    const FSP_FILE_SYSTEM_INTERFACE*, // ⚠ Stores the pointer, does NOT copy contents!
    FSP_FILE_SYSTEM**
);
```

**Interface pointer is NOT copied**: the 512-byte interface struct memory you allocate must remain valid for the entire FS lifetime.

### 3.4 FSP_FILE_SYSTEM Memory Layout (x64, 792 bytes)

```
offset   0: UINT16 Version
offset   8: PVOID UserContext          ← your custom pointer
offset  16: WCHAR VolumeName[256]      ← 512 bytes! (not 128)
offset 528: HANDLE VolumeHandle
offset 536: EnterOperation*
offset 544: LeaveOperation*
offset 552: Operations[22]             ← internal, 176 bytes
offset 728: const INTERFACE* Interface ← pointer you passed in
offset 736: ...
```

**`VolumeName` is 512 bytes** (`FSP_FSCTL_VOLUME_NAME_SIZEMAX = (64+192)*2 = 512`), not the intuitive 128. This puts `Interface` at offset 728, not 344.

### 3.5 GetSecurityByName + PersistentAcls

| PersistentAcls | GetSecurityByName slot | Behavior |
|---|---|---|
| false | null | Skip access check, grant all |
| false | non-null | Calls callback, but skips check when `pSdSize=0` |
| true | non-null | Full Windows access check (requires real security descriptor) |
| true | null | Skip access check |

**Simple FS recommendation**: Don't set `PersistentAcls`, have `GetSecurityByName` return `*pAttr = attributes, *pSdSize = 0`.

### 3.6 Debug Logging

WinFSP has built-in detailed logging, defaults to Win32 `OutputDebugString` (requires DebugView to see). Redirect to stderr:

```csharp
WinFspFileSystem.SetDebugLogToStderr();        // Call before Mount()
fs.Mount("M:", debugLog: uint.MaxValue);        // Enable all logging
```

Log format:
```
HelloFs[TID=1234]: >>Create "\", FILE_OPEN, ...    ← request entering
HelloFs[TID=1234]: <<Create IoStatus=c0000010[0]   ← request returning (NTSTATUS)
```

---

## 4. Callback Mechanism: delegate vs function pointer

### 4.1 Two .NET → native callback approaches

| Approach | Syntax | AOT | Mechanism |
|------|------|-----|------|
| `[UnmanagedCallersOnly]` | `&StaticMethod` | ✅ | Compile-time reverse P/Invoke stub |
| `[UnmanagedFunctionPointer]` delegate | `Marshal.GetFunctionPointerForDelegate<T>(d)` | ✅ (generic version) | Runtime-generated thunk |

### 4.2 Our findings

Initially used `[UnmanagedCallersOnly]` function pointers to fill the interface struct. **Mount succeeded but all requests returned `c0000010`.**

After extensive debugging (hex dumping struct contents, verifying slot addresses, writing files inside callbacks for tracing), the root cause was confirmed: **the real issue was missing mandatory Create + Overwrite callbacks (§3.2), NOT an `[UnmanagedCallersOnly]` problem.**

During debugging we switched to delegate approach, which also verified working under AOT.

**The high-level `FileSystemHost` uses `[UnmanagedCallersOnly]` with `delegate* unmanaged[Cdecl]` function pointers** — the most efficient approach. The low-level `HelloFs` example demonstrates the delegate approach as an alternative.

### 4.3 AOT delegate usage

```csharp
// ✅ Correct: generic version, AOT safe
nint ptr = Marshal.GetFunctionPointerForDelegate<OpenDelegate>(openDelegate);

// ❌ Wrong: non-generic version, produces IL3050 warning under AOT
nint ptr = Marshal.GetFunctionPointerForDelegate((Delegate)openDelegate);
```

**Delegates must be pinned**, otherwise GC collects them and the function pointer becomes dangling:
```csharp
GCHandle.Alloc(openDelegate); // Only Alloc needed, not Pinned
```

---

## 5. Namespace Design

```
WinFsp.Native              ← root namespace (using WinFsp.Native; gives you everything)
WinFsp.Native.Interop      ← low-level P/Invoke structs and internal API
```

| Namespace | Types | Rationale |
|---|---|---|
| `WinFsp.Native` | `IFileSystem`, `FileSystemHost`, `WinFspFileSystem`, `FileOperationInfo`, result types, `NtStatus`, `CleanupFlags`, `CreateOptions`, `FspFileInfo`, `FspDirInfo`, `FspStreamInfo` | Everything a typical consumer touches. One `using` covers all normal usage. |
| `WinFsp.Native.Interop` | `FspApi` (internal), `FspFileSystemInterface`, `FspVolumeParams`, `FspVolumeInfo`, `FspFullContext`, `FspOperationContext`, `FspTransactReq/Rsp/Kind` | Raw interop types for `WinFspFileSystem` low-level API. Power users add a second `using`. |

Consumer-facing types (`FspFileInfo`, `FspDirInfo`, `FspStreamInfo`, `NtStatus`, `CleanupFlags`, `CreateOptions`) were promoted from Interop to the root namespace because they appear directly in `IFileSystem` method signatures.

---

## 6. STATUS_PENDING Async — Correct Implementation

### 6.1 The Bug (deadlock)

```csharp
// ❌ Wrong: reusing the dispatcher's Response pointer
var opCtx = FspApi.FspFileSystemGetOperationContext();
var response = opCtx->Response;
asyncTask.AsTask().ContinueWith(t => {
    response->IoStatusStatus = t.Result.Status;   // response may have been freed!
    FspApi.FspFileSystemSendResponse(fs, response);
}, TaskContinuationOptions.ExecuteSynchronously);
return NtStatus.Pending;
```

**Cause**: WinFsp dispatcher may free/reuse the original `FSP_FSCTL_TRANSACT_RSP` buffer after the callback returns `STATUS_PENDING`.

### 6.2 Correct Implementation (following WinFsp MEMFS MEMFS_SLOWIO pattern)

```csharp
// ✅ Correct: save Hint, build fresh stack-local Response
var hint = FspApi.FspFileSystemGetOperationContext()->Request->Hint;
asyncTask.AsTask().ContinueWith(t => {
    var rsp = new FspTransactRsp();              // new buffer
    rsp.Size = (ushort)sizeof(FspTransactRsp);   // required
    rsp.Kind = FspTransactKind.Read;             // Read/Write/QueryDirectory
    rsp.Hint = hint;                             // IRP correlation ID
    rsp.IoStatusStatus = t.Result.Status;
    rsp.IoStatusInformation = t.Result.BytesTransferred;
    FspApi.FspFileSystemSendResponse(fs, &rsp);
}, TaskContinuationOptions.ExecuteSynchronously);
return NtStatus.Pending;
```

**Key points**:
1. `Hint` is the IRP correlation ID — WinFsp uses it to match async responses to pending kernel IRPs
2. `Size`/`Kind` must be manually filled — unlike sync path where the dispatcher pre-fills them
3. `FspFileSystemSendResponse` can be called from any thread
4. Write responses need additional `rsp.FileInfo` — kernel needs updated metadata
5. Kernel buffer remains valid until `SendResponse` — async path can also do zero-copy

---

## 7. Zero-Copy I/O Design

`FileSystemHost` implements zero-copy for all three I/O paths:

```
Sync path (SynchronousIo=true):
  kernel buffer → ThreadStatic NativeBufferMemory wrap → ReadFile/WriteFile operates directly → return

Async sync-completed (SynchronousIo=false, IsCompletedSuccessfully=true):
  kernel buffer → new NativeBufferMemory() wrap → ReadFile/WriteFile operates directly → return

Async STATUS_PENDING (SynchronousIo=false, truly async):
  kernel buffer → new NativeBufferMemory() wrap → ReadFile/WriteFile on thread pool → SendResponse
```

**Key insight**: WinFsp's kernel buffer remains valid until `FspFileSystemSendResponse` is called, so even when ReadFile/WriteFile completes on another thread (STATUS_PENDING), operating directly on the kernel buffer is safe. No intermediate buffer copy needed.

The difference is `NativeBufferMemory` reuse:
- Sync path uses `[ThreadStatic]` singleton, `Reset()` and reuse (zero alloc)
- Async path must `new` (because thread-static would be overwritten by the next operation after callback returns)

---

## 8. Mount Point Format & Mount Manager

WinFsp's `FspFileSystemSetMountPointEx` chooses different mount strategies based on mount point string format:

| Format | Route | Behavior |
|------|------|------|
| `"X:"` | `FspPathIsDrive` → `DefineDosDevice` | Fast, but disk tools like ATTO **can't see it** |
| `"\\.\X:"` or `"\\?\X:"` | `FspPathIsMountmgrDrive` → Mount Manager | Registered via Volume Mount Manager, visible to all apps. **Requires admin** |
| `"X:\"` | ❌ crash (0xC0000005) | Triggers uninitialized Mount Manager path, **never use** |
| Directory path | `FspMountSet_Directory` | Reparse point junction |

### Test mounts must use UNC paths

Drive letter mounts that become zombie (process crash without unmount) cause Explorer and shell to hang when polling dead mount points, potentially freezing the entire system.

Solution: test mounts use UNC paths:
1. Set `host.Prefix = @"\winfsp-tests\sharename-{PID}"`
2. `host.Mount(null)` — WinFsp creates UNC share, no drive letter needed
3. PID suffix prevents stale share collisions from prior crashes

---

## 9. Pitfall Checklist (by time spent debugging)

| # | Pitfall | Symptom | Root Cause | Time Lost |
|---|---|---|---|---|
| 1 | Drive mounted but completely inaccessible | `dir M:\` → "Incorrect function" | Missing Create and/or Overwrite mandatory callbacks | Hours |
| 2 | `VolumeName` array size miscalculation | Interface pointer at wrong offset (344 vs 728) | `FSP_FSCTL_VOLUME_NAME_SIZEMAX = 512 bytes`, not 128 | 1 hour |
| 3 | WinFSP debug log invisible | No log output after mount | Defaults to `OutputDebugString`, need `SetDebugLogToStderr()` | 30 min |
| 4 | `Marshal.GetFunctionPointerForDelegate` AOT warning | IL3050: RequiresDynamicCode | Non-generic version not AOT safe, use `<T>` generic | 5 min |
| 5 | `SetMountPointEx("R:\")` crash | 0xC0000005 access violation | `"R:\"` triggers uninitialized Mount Manager path, use `"R:"` | 1 hour |
| 6 | STATUS_PENDING async I/O deadlock | Truly async ReadFile/WriteFile never responds | Reusing `OperationContext->Response` pointer after returning STATUS_PENDING | 3 hours |
| 7 | Drive letter test mounts freeze system | Explorer hangs after test process killed | Zombie `DefineDosDevice` mount, Explorer polls dead mount points | 2 hours |

---

## 10. AOT Compatibility Summary

| Component | AOT Strategy | Status |
|------|---------|---------|
| P/Invoke | `[LibraryImport]` source generator | ✅ |
| High-level callbacks | `[UnmanagedCallersOnly]` + `delegate* unmanaged[Cdecl]` | ✅ |
| Low-level callbacks | `[UnmanagedFunctionPointer]` + `Marshal.GetFunctionPointerForDelegate<T>` | ✅ |
| Structs | All blittable (`LayoutKind.Sequential` / `Explicit`, `fixed` arrays) | ✅ |
| DLL loading | `NativeLibrary.SetDllImportResolver` + Registry fallback | ✅ |
| Package | `<IsAotCompatible>true</IsAotCompatible>` | ✅ |
