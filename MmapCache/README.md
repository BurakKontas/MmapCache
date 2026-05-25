# MmapCache (LSM Engine)

[![NuGet](https://img.shields.io/nuget/v/MmapCache.svg)](https://www.nuget.org/packages/MmapCache)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com)

A high-performance LSM-based storage engine for .NET.  
Data lives in off-heap immutable `mmap` Segment files (`.sst`) — not the GC heap — with an active Write-Ahead Log (`WAL`) for crash-recovery and a highly concurrent MemTable for active modifications.

---

## Table of Contents

- [Why MmapCache LSM?](#why-mmapcache-lsm)
- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration Reference](#configuration-reference)
- [How It Works](#how-it-works)
  - [WAL and Crash Recovery](#wal-and-crash-recovery)
  - [Flush and Immutability](#flush-and-immutable-sstables)
  - [Compaction](#compaction)
- [Performance Notes](#performance-notes)
- [Contributing](#contributing)
- [License](#license)

---

## Why MmapCache LSM?

Most in-process caches and embedded stores suffer from:

- **GC fragmentation** — constantly overwriting objects creates holes in the managed heap causing huge compaction pauses.
- **Data volatility** — standard caching means losing all mutations if the target process crashes.
- **Reload lockouts** — having to rebuild your exact state from databases periodically is expensive and painful.

By restructuring the system strictly with an LSM architecture, every mutation is recorded purely with append-friendly I/O to a `WAL` and `MemTable`. When size triggers are reached, the memory is locked, serialized continuously off-heap into an SSTable mapped strictly with OS memory-mapping (`mmap`), and handles are efficiently GC'd during background compactions, effectively providing **Zero Memory Leaks** over time.

---

## Features

| | |
|---|---|
| **Off-heap storage** | Immutable segment files (`.sst`) are `mmap`'d — zero GC pressure regardless of dataset size. |
| **Write-Ahead Log (WAL)** | Append-only sequential I/O ensures durable crashes and recovery. |
| **Active MemTable**| `ConcurrentDictionary` buffers live I/O so you don't stall disk flush pipelines. |
| **Background Compaction** | Garbage collects dead variables and tombstones (deletions) without pausing operations. |
| **Warm Restart** | Rebuilds the memory table exclusively by sniffing the index markers of existing SSTables without rebuilding payloads from scratch. |

---

## Installation

```
dotnet add package MmapCache
```

Targets **net8.0**, **net9.0** and **net10.0**.

> ⚠️ MmapCache is released under the **GPL-3.0** license. If you use it in a closed-source product, review the license terms carefully.

---

## Quick Start

```csharp
using MmapCache.Cache;
using MmapCache.Config;
using System.Text.Json;

// ── 1. Initialize once at application startup ─────────────────────────────────
MmapCacheManager.Initialize(
    basePath : "/var/cache/myapp");  // LSM log and sst files are securely written here

// ── 2. Describe what to store ─────────────────────────────────────────────────
var productDef = new MmapCacheDefinition<Product>
{
    Name = "products",
    // Supplier used for initial bootstrapping if index is entirely clean/empty
    Supplier = () => db.Products.Select(p => (p.Id.ToString(), p)),
    Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
    Deserializer = b => JsonSerializer.Deserialize<Product>(b)!
};

// ── 3. Register Engine Instance ───────────────────────────────────────────────
// Loads from on-disk SSTables instantly, replays WAL if recovering from crash.
MmapCacheManager.Instance.Register(productDef);

// ── 4. Active Mutability (Put/Delete) ─────────────────────────────────────────
// Mutations are immediately visible and durably piped to the active WAL
MmapCacheManager.Instance.Put("products", "product_42", new Product("product_42", "Updated!", 9.99m, 50));
MmapCacheManager.Instance.Delete("products", "product_99");

// ── 5. Concurrent Reads (lock-free) ───────────────────────────────────────────
var product = MmapCacheManager.Instance.Get<Product>("products", "product_42");

if (MmapCacheManager.Instance.TryGet<Product>("products", "product_42", out var p))
    Console.WriteLine(p!.Name);

// ── 6. Dispose on shutdown ────────────────────────────────────────────────────
// Safely closes current memory map handles and active WAL flush buffers
await MmapCacheManager.Instance.DisposeAsync();
```

---

## Configuration Reference

`MmapCacheDefinition<TValue>` accepts the following properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | *(required)* | Logical name; also used as the subdirectory for the specific Cache LSM Engine. |
| `Supplier` | `Func<IEnumerable<(string, TValue)>>` | *(optional)* | Only runs if the engine boots from an empty state (no existing records on disk) to seed initial segments. |
| `Serializer` | `Func<TValue, byte[]>` | *(required)* | Converts a value to bytes for write operations. |
| `Deserializer` | `Func<byte[], TValue>` | *(required)* | Converts bytes back to a value on read. |

*(Note: Certain legacy shard size variables remain gracefully ignored to preserve backward compatibility.)*

---

## How It Works

### WAL and Crash Recovery
When inserting new values, records are packaged symmetrically onto a `wal_XX.log` with a `CRC32` checksum footprint. If the cache dies unpredictably, the engine parses `.sst` segment offsets rapidly and dynamically walks forward along existing WALs up until bad frames to guarantee exactly synchronized internal checkpoints. 

### Flush and Immutable SSTables
Whenever the `MemTable` surpasses a defined threshold (i.e. `64MB`), operations swap the structure into a background flush task. Flushed tables output precisely tracked `segment_N.sst` files. Because writes to the backend are only handled dynamically when buffers fill, the application maintains continuous low-latency responses.

### Compaction
Since MemTable writes only sequentially produce Segment SSTables, modifying similar values creates logical duplicates in multiple Segment instances mapping over time. A `Compactor` runs asynchronously to trace overlaps and resolve tombstones dynamically without interrupting concurrent Get actions over the exact same references.

---

## Performance Notes

- **Reads are lock-free.** `TryGet` increments an `int` reader counter with `Interlocked`, reads from the mmap accessor, then decrements. There is no `Monitor`, `SemaphoreSlim`, or `ReaderWriterLock` on the read path.
- **L1 hit cost** is a single `MemoryCache.TryGetValue` call — effectively a dictionary lookup.
- **L2 hit cost** is a FasterKV lookup (hash table + possible log-device read) followed by a `MemoryMappedViewAccessor.ReadArray`.
- **Reload does not block reads.** The build runs concurrently; the pointer swap is a single `Interlocked.Exchange`. Readers on the old version finish normally and the old version is only disposed after the last one exits.
- **GC impact is minimal.** The only managed allocation per read is the deserialized `TValue` object. Shard data, index buckets, and reader counters live outside the managed heap.

---

## Contributing

Pull requests are welcome. For significant changes, please open an issue first to discuss the approach.

```bash
git clone https://github.com/BurakKontas/MmapCache
cd MmapCache
dotnet build
dotnet test MmapCache.Tests/MmapCache.Tests.csproj -v normal
```

Please note that contributions are subject to the terms of the GPL-3.0 license — by submitting a pull request you agree that your contribution will be distributed under the same license.

---

## License

Copyright (C) 2026 Arda Burak Kontaş

MmapCache is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](LICENSE) as published by the Free Software Foundation.

This program is distributed in the hope that it will be useful, but **without any warranty**; without even the implied warranty of merchantability or fitness for a particular purpose. See the GNU General Public License for more details.