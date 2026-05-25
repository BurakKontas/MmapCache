# MmapCache

[![NuGet](https://img.shields.io/nuget/v/MmapCache.svg)](https://www.nuget.org/packages/MmapCache)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4)](https://dotnet.microsoft.com)

Zero-downtime, memory-mapped cache for .NET.  
Data lives in off-heap `mmap` shard files — not the GC heap — with an atomic version-swap reload model and a warm-restart path that resumes from disk without touching the data source.

---

## Table of Contents

- [Why MmapCache?](#why-mmapcache)
- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration Reference](#configuration-reference)
- [How It Works](#how-it-works)
  - [Build & Reload Pipeline](#build--reload-pipeline)
  - [Warm Restart](#warm-restart)
  - [Old Version Cleanup](#old-version-cleanup)
  - [Disk Layout](#disk-layout)
- [Performance Notes](#performance-notes)
- [Contributing](#contributing)
- [License](#license)

---

## Why MmapCache?

Most in-process caches store everything on the managed heap, which means:

- **GC pressure** grows linearly with dataset size — large caches cause long pauses
- **Reload blackouts** — clearing and refilling a cache leaves a window where reads miss
- **Cold restarts** — every process restart hits the database from scratch

MmapCache sidesteps all three problems: data lives in memory-mapped files outside the GC heap, new versions are built fully before an atomic pointer swap, and on restart the process re-opens existing shard files if they are still within TTL — no database round-trip needed.

---

## Features

| | |
|---|---|
| **Off-heap storage** | Shard files are `mmap`'d — zero GC pressure regardless of dataset size |
| **Zero-downtime reload** | New version is fully built before the atomic pointer swap; readers never see a partial state |
| **Warm restart** | On startup, if a version on disk is younger than its TTL, it is re-opened and re-indexed locally — no Supplier call, no database round-trip |
| **Safe old-version cleanup** | Retired versions are deleted from disk only after every in-flight read has finished — no dangling `mmap` handles |
| **L1 memory cache** | Optional hot-key `MemoryCache` layer in front of the shards for sub-microsecond repeated reads |
| **Off-heap index** | FasterKV-backed key → location index; hash table lives outside the GC heap |
| **Multi-shard** | Dataset is split across N shard files automatically; shard count scales with row count |
| **Dynamic sizing** | When `DynamicSizing = true`, max key/value lengths are computed from the data so no byte is wasted on disk |

---

## Installation

```
dotnet add package MmapCache
```

Targets **net8.0** and **net9.0**.

> ⚠️ MmapCache is released under the **GPL-3.0** license. If you use it in a closed-source product, review the license terms carefully.

---

## Quick Start

```csharp
using MmapCache;
using MmapCache.Config;
using System.Text.Json;

// ── 1. Initialize once at application startup ─────────────────────────────────
var manager = MmapCacheManager.Initialize(
    basePath : "/var/cache/myapp",   // shard files are written here
    ttlCheck : TimeSpan.FromSeconds(30));

// ── 2. Describe what to cache ─────────────────────────────────────────────────
var productDef = new MmapCacheDefinition<Product>
{
    Name          = "products",
    Supplier      = () => db.Products.Select(p => (p.Id.ToString(), p)),
    Serializer    = p => JsonSerializer.SerializeToUtf8Bytes(p),
    Deserializer  = b => JsonSerializer.Deserialize<Product>(b)!,
    Ttl           = TimeSpan.FromMinutes(30),
    DynamicSizing = true,
    L1MaxSize     = 10_000,
    L1Ttl         = TimeSpan.FromMinutes(1),
};

// ── 3. Register ───────────────────────────────────────────────────────────────
// Loads from disk if data is still fresh, otherwise calls Supplier.
manager.Register(productDef);

// ── 4. Read (lock-free) ───────────────────────────────────────────────────────
var product = manager.Get<Product>("products", "product_42");

if (manager.TryGet<Product>("products", "product_99", out var p))
    Console.WriteLine(p!.Name);

// ── 5. Manual reload (e.g. triggered by a message queue event) ────────────────
await manager.ReloadAsync<Product>("products");

// ── 6. Dispose on shutdown ────────────────────────────────────────────────────
await manager.DisposeAsync();
```

---

## Configuration Reference

`MmapCacheDefinition<TValue>` accepts the following properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | *(required)* | Logical name; also used as the subdirectory under `basePath` |
| `Supplier` | `Func<IEnumerable<(string, TValue)>>` | *(required)* | Provides the full dataset on every reload; `yield return` is encouraged to avoid loading everything into memory upfront |
| `Serializer` | `Func<TValue, byte[]>` | *(required)* | Converts a value to bytes for shard storage |
| `Deserializer` | `Func<byte[], TValue>` | *(required)* | Converts bytes back to a value on read |
| `Ttl` | `TimeSpan` | `1 hour` | Data age after which a background reload is triggered |
| `DynamicSizing` | `bool` | `true` | When `true`, scans rows once to determine max key/value lengths before allocating shard files |
| `MaxKeyBytes` | `int` | `256` | Used only when `DynamicSizing = false` |
| `MaxValueBytes` | `int` | `4096` | Used only when `DynamicSizing = false` |
| `ShardCapacity` | `long` | `200 000` | Maximum records per shard file |
| `IndexShardCount` | `int` | `16` | Number of FasterKV index shards |
| `L1MaxSize` | `int` | `10 000` | Hot-key MemoryCache entry limit; `0` disables L1 |
| `L1Ttl` | `TimeSpan?` | `null` | Per-entry expiry inside L1; `null` means LRU-only eviction |

---

## How It Works

### Build & Reload Pipeline

```
manager.Register(def)  /  TTL timer fires
          │
          ▼
  TryLoadExistingVersionAsync()
          │
          ├─ meta.json found & age < TTL?
          │       YES → open mmap shard files
          │             rebuild FasterKV index from shards  (no Supplier call)
          │             resume ──────────────────────────────────────────────┐
          │        NO → Supplier()                                           │
          │             dynamic sizing scan                                  │
          │             allocate CacheShard files                            │
          │             write all records                                    │
          │             write meta.json  ◄── written last;                  │
          │                                  an incomplete build has no meta │
          ▼                                                                  │
      slot.Swap(newVersion)  ◄─────────────────────────────────────────────┘
          │
          └─ oldVersion != null?
                  Task.Run → RetireAsync(oldVersion)
```

### Warm Restart

Every completed build writes a `meta.json` file to its version directory. On the next `Register()` call, the manager:

1. Finds the newest version directory with a readable `meta.json`.
2. Checks whether `createdAt + TTL > DateTime.UtcNow`.
3. If fresh: opens the existing shard files via `mmap` and scans them to rebuild the FasterKV index. The `Supplier` is **not** called — this is typically 10–100× faster than a database query.
4. If stale (or no version exists): falls through to a full build.

Orphaned or incomplete version directories (missing `meta.json`, or age ≥ TTL) are deleted during this scan.

### Old Version Cleanup

`RetireAsync` runs in a background `Task.Run` off the hot path:

```
RetireAsync(oldVersion)
  │
  ├── _dead = true             ← new readers are rejected immediately
  │
  ├── spin while _readers > 0  ← wait for every in-flight read to exit
  │
  ├── Dispose()                ← release MemoryMappedFile handles, FasterKV devices
  │
  └── Directory.Delete(VersionDir, recursive: true)
                               ← safe: no OS handle is open at this point
```

### Disk Layout

```
/var/cache/myapp/
└── products/
    └── v1716681234567/            ← version directory (Unix ms timestamp)
        ├── meta.json              ← build metadata (createdAt, sizing, shard count)
        ├── shard_0000.bin         ← memory-mapped shard file
        ├── shard_0001.bin
        └── index/
            ├── index_0000.log     ← FasterKV log device
            └── ...
```

On a clean shutdown there is always exactly one version directory per cache. Old directories are deleted automatically after retire.

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
git clone https://github.com/YourName/MmapCache
cd MmapCache
dotnet build
dotnet run --project samples/MmapCache.Demo
```

Please note that contributions are subject to the terms of the GPL-3.0 license — by submitting a pull request you agree that your contribution will be distributed under the same license.

---

## License

Copyright (C) 2026 Arda Burak Kontaş

MmapCache is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](LICENSE) as published by the Free Software Foundation.

This program is distributed in the hope that it will be useful, but **without any warranty**; without even the implied warranty of merchantability or fitness for a particular purpose. See the GNU General Public License for more details.