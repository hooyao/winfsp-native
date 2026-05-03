# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build (multi-TFM: net8.0 + net10.0)
dotnet build

# Run tests (requires WinFsp 2.x installed with Developer files)
dotnet test

# Run a single test class
dotnet test --filter "FullyQualifiedName~SyncTests"

# Pack NuGet package
dotnet pack src/WinFsp.Native/WinFsp.Native.csproj -c Release -o ./artifacts

# Run HelloFs example (mounts read-only FS at M:)
dotnet run --project examples/HelloFs
```

**Prerequisites:** .NET 8+ SDK, [WinFsp](https://winfsp.dev/rel/) 2.x (install with Developer files).

## Release Process

Push a tag matching `v*` to trigger the release workflow:
```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow builds, tests, packs, pushes to nuget.org (via `NUGET_API_KEY` secret), and creates a GitHub Release.

## Architecture

```
    ┌────────────────────────────────────────────────────────┐
    │                     User Code                          │
    │                                                        │
    │  ┌──── Choice A ──────┐    ┌──── Choice B ──────────┐  │
    │  │ IFileSystem         │    │ WinFspFileSystem       │  │
    │  │ (high-level)        │    │ (low-level raw API)    │  │
    │  │ ValueTask, string,  │    │ delegate/fptr → slot   │  │
    │  │ Memory<byte>        │    │ nint, char*, NTSTATUS  │  │
    │  └────────┬────────────┘    └──────────┬─────────────┘  │
    └───────────┼────────────────────────────┼────────────────┘
                │                            │
    ════════════╪═══════ HIGH ═══════════════╪════════════════
                │                            │
    ┌───────────▼────────────────┐           │
    │  FileSystemHost            │           │
    │  IFileSystem → callbacks   │           │
    │  STATUS_PENDING mgmt       │           │
    │  zero-copy buffer, cancel  │           │
    └───────────┬────────────────┘           │
                │ uses internally            │
    ════════════╪═══════ LOW ════════════════╪════════════════
                │                            │
    ┌───────────▼────────────────────────────▼───────────────┐
    │  WinFspFileSystem                                      │
    │  VolumeParams + InterfacePtr + Mount/Unmount           │
    └───────────┬────────────────────────────────────────────┘
                │ P/Invoke (LibraryImport)
    ┌───────────▼────────────────────────────────────────────┐
    │  Interop: FspApi + FspStructs + FspFileSystemInterface │
    └───────────┬────────────────────────────────────────────┘
                │ winfsp-x64.dll (runtime, GPLv3)
    ┌───────────▼───────────────┐
    │  WinFSP kernel driver     │
    └───────────────────────────┘
```

## Namespace Layout

| Namespace | Contains | When to use |
|---|---|---|
| `WinFsp.Native` | `IFileSystem`, `FileSystemHost`, `WinFspFileSystem`, `FileOperationInfo`, result types (`FsResult`, `CreateResult`, `ReadResult`, `WriteResult`, `ReadDirectoryResult`), `NtStatus`, `CleanupFlags`, `CreateOptions`, `FileNotify`, `FspFileInfo`, `FspDirInfo`, `FspStreamInfo`, `PinnedBufferPool` (internal) | `using WinFsp.Native;` — everything for IFileSystem implementors |
| `WinFsp.Native.Interop` | `FspApi` (internal), `FspFileSystemInterface`, `FspVolumeParams`, `FspVolumeInfo`, `FspFullContext`, `FspOperationContext`, `FspTransactReq`, `FspTransactRsp`, `FspTransactKind` | `using WinFsp.Native.Interop;` — only for low-level WinFspFileSystem users |

Consumer-facing types (`FspFileInfo`, `FspDirInfo`, `NtStatus`, `CleanupFlags`, `CreateOptions`) were deliberately promoted from Interop to the root namespace because they appear in `IFileSystem` method signatures.

## Key Design Decisions

### Zero managed allocation on hot paths
All FS callback methods (`OnRead`, `OnWrite`, `OnGetFileInfo`, etc.) must not allocate on the managed heap. This is enforced by:
- `ValueTask<T>` synchronous return (zero Task boxing)
- `NativeBufferMemory` wrapping kernel buffers directly (zero intermediate copy)
- `FileOperationInfo.Context` caching user objects (zero repeated lookups)
- `ReadDirectory` using native buffer with `WinFspFileSystem.AddDirInfo`/`EndDirInfo` (zero IEnumerable)

### Three I/O modes (all zero-copy)
- **Sync** (`SynchronousIo=true`): ThreadStatic `NativeBufferMemory` wraps kernel buffer, ~1ns overhead
- **SyncCompleted** (`SynchronousIo=false`, `ValueTask.IsCompletedSuccessfully`): new `NativeBufferMemory` per call, still zero-copy
- **TrueAsync** (`SynchronousIo=false`, `STATUS_PENDING`): kernel buffer stays valid until `SendResponse`, async completion on thread pool

### STATUS_PENDING async
Must build a fresh stack-allocated `FspTransactRsp` with `Size`/`Kind`/`Hint` fields. Do NOT reuse `OperationContext->Response` — it may be invalidated after returning `STATUS_PENDING`. Save `Request->Hint` before returning, echo it back in the response. This follows the WinFsp official MEMFS `MEMFS_SLOWIO` pattern.

### Cache-invalidation notifications

`FileSystemHost.Notify(filter, action, path)` wraps `FspFileSystemNotify` for invalidating the WinFsp kernel `FileInfo` cache after path-mutating user-mode operations. Required when `FileInfoTimeout > 0`, otherwise the kernel can serve stale `OpenFile` / `ReadFile` results from cache without consulting user-mode.

- **Single-shot, no `Begin/End` framing**: the framing API exists for atomically batching many notifications relative to concurrent renames. Adapters typically emit ≤ 2 notifications per mutation (rename = `RenamedOldName` + `RenamedNewName`) and do not need atomic multi-event ordering. The official `fuse.c` reference implementation in winfsp uses single-shot `Notify` for the same reason.
- **Case-insensitive normalization is the binding's job**: the WinFsp driver upper-cases internally for case-insensitive volumes and the cache key is the upper-cased form. `Notify` upper-cases the path in place inside its stack buffer when `CaseSensitiveSearch == false`. Callers always pass the user-supplied case unchanged.
- **Allocation-free hot path**: paths up to ~2030 chars are stack-allocated; longer paths fall back to `ArrayPool<byte>`.
- **Returns NTSTATUS**: callers (typically adapter mutators) decide error handling. The IRP that triggered the notification MUST NOT be failed because the notification failed — the user-mode mutation has already taken effect.

## WinFsp Pitfalls (hard-won knowledge)

- **Three mandatory callbacks**: `Create` (or `CreateEx`) + `Open` + `Overwrite` (or `OverwriteEx`) must ALL be non-null. Missing any one → mount succeeds but all I/O returns `STATUS_INVALID_DEVICE_REQUEST`. Extremely hard to diagnose.
- **`fileSystemName` must be `"NTFS"`** for elevated process compatibility.
- **`GetFileSecurityByName`** returning `securityDescriptor = null` (sdSize=0) makes WinFsp skip access checks.
- **`SetMountPointEx` crashes on `"R:\"` (trailing backslash)**. Use `"R:"` for `DefineDosDevice` or `"\\.\R:"` for Mount Manager.
- **Test mounts must use UNC paths** (`host.Prefix = @"\winfsp-tests\name"`; `host.Mount(null)`), not drive letters. Drive letter mounts become zombie on process crash and hang Explorer/entire system.
- **`FspFileSystemCreate` stores the Interface pointer, not a copy**. The 512-byte interface struct must remain valid for the FS lifetime.
- **Debug logging**: call `WinFspFileSystem.SetDebugLogToStderr()` before `Mount()`, then `mount(debugLog: uint.MaxValue)`.

## Test Architecture

Tests mount real WinFsp file systems via UNC paths (no drive letters) and exercise actual Windows file I/O:
- `TestMemFs` — lightweight in-memory `IFileSystem` supporting all three async modes
- `MountFixtureBase` + `IClassFixture` — mount once per mode, run all tests, then unmount
- UNC share names include PID suffix to avoid stale share collisions after crashes
- 30 tests (10 per mode): text I/O, binary I/O, 1MB files, concurrent 8-thread, streaming, truncate, overwrite, directory enumeration
