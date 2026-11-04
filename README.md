# WinFsp.Native

[![NuGet](https://img.shields.io/nuget/v/WinFsp.Native.svg)](https://www.nuget.org/packages/WinFsp.Native)
[![CI](https://github.com/hooyao/winfsp-native/actions/workflows/ci.yml/badge.svg)](https://github.com/hooyao/winfsp-native/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Zero-alloc, AOT-ready WinFsp binding for modern .NET** — build user-mode file systems on Windows with native performance.

## Why WinFsp.Native?

|  | WinFsp.Native | [Official WinFsp.Net](https://www.nuget.org/packages/winfsp.net) |
|--|--|--|
| **Native AOT** | Full support (`IsAotCompatible`) | Not compatible (reflection-based) |
| **Target** | .NET 8+ | .NET Standard 2.0 / .NET Framework 3.5 |
| **Hot-path alloc** | Zero managed heap allocation | Allocates per callback |
| **P/Invoke** | `[LibraryImport]` source generator | `[DllImport]` with marshaling |
| **Async I/O** | `ValueTask<T>` + `STATUS_PENDING` | Synchronous only |
| **API style** | `IFileSystem` interface + `FileSystemHost` | Inheritance-based `FileSystemBase` |
| **Dependencies** | Zero NuGet dependencies | `Microsoft.Win32.Registry` + `System.IO.FileSystem.AccessControl` |

## Quick Start

```bash
dotnet add package WinFsp.Native
```

```csharp
using WinFsp.Native;

class MyFs : IFileSystem
{
    public bool SynchronousIo => true;

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = 1024 * 1024;
        freeSize = 512 * 1024;
        volumeLabel = "MyDrive";
        return NtStatus.Success;
    }

    // Implement other IFileSystem methods...
}

// Mount
var host = new FileSystemHost(new MyFs());
host.Mount("Z:");
```

## Two API Levels

### High-Level: `IFileSystem` + `FileSystemHost`

Implement the `IFileSystem` interface with familiar .NET types (`string`, `Memory<byte>`, `ValueTask<T>`). The `FileSystemHost` handles all WinFsp plumbing — mounting, callback dispatch, async `STATUS_PENDING`, buffer management, and cancellation.

```csharp
using WinFsp.Native;

// One using gets you everything: IFileSystem, FileSystemHost, NtStatus,
// FspFileInfo, CleanupFlags, CreateOptions, result types, etc.
```

### Low-Level: `WinFspFileSystem`

Direct function pointer access to all 64 WinFsp callback slots. Full control, zero abstraction overhead.

```csharp
using WinFsp.Native;
using WinFsp.Native.Interop; // FspVolumeParams, FspFileSystemInterface, etc.

var fs = new WinFspFileSystem();
fs.VolumeParams.SectorSize = 4096;
fs.Interface.Read = &OnRead;
fs.Mount("Z:");
```

## Reparse points

Set `FileSystemHost.ReparsePoints` in `IFileSystem.Init` and implement
`GetReparsePointByName` together with the handle-based get, set, and delete methods. The
by-name method must return the raw reparse buffer for a link, `NtStatus.NotAReparse` for
an ordinary node, and a not-found status for a missing node. The host calls WinFsp's native
find and resolve helpers so Windows follows links and handles cross-volume targets. The
by-name callback is synchronous and must return stable, immutable data. When name lookup
returns a missing-path status, the host checks for an intermediate reparse point before
returning the failure. File systems which do not enable reparse points retain their previous
behavior.

For a network filesystem that should be reachable only by its UNC prefix, set
`host.PrefixOnly = true` and `host.Prefix` before calling `host.Mount(null)`. Without
`PrefixOnly`, WinFsp interprets a null mount point as an automatic drive-letter mount.

## Prerequisites

- Windows 10/11
- [WinFsp](https://winfsp.dev/rel/) 2.x installed (with Developer files for building)
- .NET 8.0+ SDK

## Project Structure

```
src/WinFsp.Native/           # The binding library
tests/WinFsp.Native.Tests/   # Integration tests (mounts real WinFsp FS)
examples/HelloFs/             # Minimal read-only file system example
```

## Building

```bash
dotnet build
dotnet test
dotnet pack
```

## License

MIT — see [LICENSE](LICENSE).

**Runtime dependency:** [WinFsp](https://github.com/winfsp/winfsp) (GPLv3 with FLOSS exception). WinFsp must be installed on the target machine. This binding library does not redistribute WinFsp binaries.
