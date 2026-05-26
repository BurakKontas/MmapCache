# MmapCache (LSM Engine)

**An off-heap LSM storage engine for .NET with active mutation support and zero-downtime compaction.**

MmapCache stores data in off-heap memory-mapped (`mmap`) segment files utilizing a lock-free LSM (Log-Structured Merge-tree) architecture. It fully supports WAL (Write-Ahead Log) crash-recovery, active live mutations, automatic background compaction, and completely lock-free segment reads.

---

## Table of Contents

* [Why MmapCache LSM?](https://www.google.com/search?q=%23why-mmapcache-lsm)
* [Features](https://www.google.com/search?q=%23features)
* [Installation](https://www.google.com/search?q=%23installation)
* [Quick Start](https://www.google.com/search?q=%23quick-start)
* [Configuration Reference](https://www.google.com/search?q=%23configuration-reference)
* [How It Works](https://www.google.com/search?q=%23how-it-works)
* [WAL and Crash Recovery](https://www.google.com/search?q=%23wal-and-crash-recovery)
* [Flush and Immutable SSTables](https://www.google.com/search?q=%23flush-and-immutable-sstables)
* [Compaction](https://www.google.com/search?q=%23compaction)
* [TTL & Lazy Expiration](https://www.google.com/search?q=%23ttl--lazy-expiration)
* [Reload & Bootstrapping](https://www.google.com/search?q=%23reload--bootstrapping)


* [Performance & Stress-Test Benchmarks](https://www.google.com/search?q=%23performance--stress-test-benchmarks)
* [Contributing](https://www.google.com/search?q=%23contributing)
* [License](https://www.google.com/search?q=%23license)

---

## Why MmapCache LSM?

Most in-process caches and embedded stores suffer from:

* **GC Fragmentation & Pauses** — Constantly overwriting objects creates holes in the managed heap, leading to severe garbage collection compaction cycles.
* **Data Volatility** — Standard caching layers lose all mutated state instantly if the target process crashes or suffers an unhandled termination.
* **Reload Lockouts** — Having to periodically rebuild large data structures entirely from external databases is expensive, slow, and resource-heavy.

By restructuring the system strictly with an LSM architecture, every mutation is recorded purely with append-friendly I/O to a sequential `WAL` and a memory-efficient `Radix Tree MemTable`. When size triggers are reached, the memory is locked, serialized continuously off-heap into an SSTable mapped strictly via OS memory-mapping (`mmap`), and native pointers are safely recycled. This effectively guarantees **Zero Managed Memory Leaks** and deterministic overhead over time.

---

## Features

| Feature | Description |
| --- | --- |
| **Off-Heap Storage** | Immutable segment files (`.sst`) are directly `mmap`'d — ensuring zero GC pressure regardless of dataset size. |
| **Concurrent Radix Tree** | Global indexing and MemTables use a highly optimized Prefix Tree (Trie), reducing RAM usage drastically via prefix sharing (e.g., `user_1`, `user_2`) and providing native lexicographical sorting. |
| **Bloom Filter** | A spin-locked Counting Bloom Filter sits in front of read requests. Non-existent keys skip trie traversal and disk I/O entirely. |
| **Write-Ahead Log (WAL)** | Append-only sequential I/O ensures durable crashes and fast recovery using stateful frame synchronization. |
| **Background Compaction** | Asynchronously garbage collects obsolete variables and tombstones (deletions) without pausing active operations. |
| **Warm Bootstrapping** | Rebuilds the memory index layout instantly by sniffing the header/footer markers of existing SSTables without reloading raw payloads into the managed heap. |

---

## Installation

```bash
dotnet add package MmapCache

```

Targets **net8.0**, **net9.0**, and **net10.0**.

> ⚠️ MmapCache is released under the **GPL-3.0** license. If you intend to use it inside a closed-source commercial product, review the license terms carefully.

---

## Quick Start

```csharp
using MmapCache.Cache;
using MmapCache.Config;
using System.Text.Json;

// ── 1. Initialize once at application startup ─────────────────────────────────
MmapCacheManager.Initialize(
    basePath : "/var/cache/myapp");  // LSM log and sst files are written here

// ── 2. Describe what to store ─────────────────────────────────────────────────
var productDef = new MmapCacheDefinition<Product>
{
    Name = "products",
    // Supplier runs ONLY if the engine boots from an empty/clean disk state
    Supplier = () => db.Products.Select(p => (p.Id.ToString(), p)),
    Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
    Deserializer = b => JsonSerializer.Deserialize<Product>(b)!,
    Ttl = TimeSpan.FromHours(1)
};

// ── 3. Register Engine Instance ───────────────────────────────────────────────
// Restores from on-disk SSTables instantly, replays WAL if recovering from a crash.
MmapCacheManager.Instance.Register(productDef);

// ── 4. Active Mutability (Put/Delete) ─────────────────────────────────────────
// Mutations are immediately visible and durably piped to the active WAL
MmapCacheManager.Instance.Put("products", "product_42", new Product("product_42", "Updated!", 9.99m, 50));
MmapCacheManager.Instance.Delete("products", "product_99");

// ── 5. Concurrent Reads (lock-free logic) ─────────────────────────────────────
var product = MmapCacheManager.Instance.Get<Product>("products", "product_42");

if (MmapCacheManager.Instance.TryGet<Product>("products", "product_42", out var p))
    Console.WriteLine(p!.Name);

// ── 6. Dispose on shutdown ────────────────────────────────────────────────────
// Safely unmaps memory handles and flushes remaining active WAL stream buffers
await MmapCacheManager.Instance.DisposeAsync();

```

---

## Configuration Reference

`MmapCacheDefinition<TValue>` accepts the following core properties:

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Name` | `string` | *(Required)* | Logical name; used as the subdirectory for the specific Cache LSM Engine instance. |
| `Supplier` | `Func<IEnumerable<(string, TValue)>>` | *(Optional)* | Lazy provider to seed data if the engine starts empty. |
| `Serializer` | `Func<TValue, byte[]>` | *(Required)* | Converts your model to bytes for write operations. |
| `Deserializer` | `Func<byte[], TValue>` | *(Required)* | Converts raw bytes back into the object model on read. |
| `Ttl` | `TimeSpan` | `1 Hour` | Background refresh interval and rolling threshold for lazy expiration. |
| `RadixTreeCapacity` | `int` | `1,000,000` | Sizing for the unmanaged trie node index map to prevent costly hot reallocations. |
| `MemTableFlushThresholdBytes` | `int` | `67,108,864 (64 MB)` | The size threshold of the active native memory arena before swapping buffers and initiating an asynchronous background SSTable flush task. |

---

## How It Works

### WAL and Crash Recovery

When inserting new values, records are structured and packaged sequentially onto a `wal_XX.log` with a `CRC32` checksum footprint. If the cache terminates unpredictably, the engine parses `.sst` segment offsets rapidly and walks forward along the WAL frames up to the last valid bitwise block to guarantee exact state synchronization.

### Flush and Immutable SSTables

Whenever the active `MemTable` surpasses its memory threshold, operations swap the active buffer out into an asynchronous background flush task. Flushed tables output precisely structured `segment_N.sst` files. Because our unmanaged Radix Tree naturally maintains data in lexicographical order, SSTable segments are dumped to disk sequentially, eliminating expensive in-memory sorting overheads.

### Compaction

Since MemTable writes sequentially produce separate SSTable files, modifying similar keys creates duplicate logical offsets across multiple segments over time. An asynchronous `Compactor` thread periodically scans these segments, merges overlapping key spaces, resolves tombstones (deletions), and atomically updates the unmanaged index mapping without interrupting concurrent `Get` actions.

### TTL & Lazy Expiration

Time-To-Live (TTL) is handled seamlessly without imposing structural overhead on the underlying LSM engine. `MmapCacheManager` prefixes the serialized byte array with an 8-byte `expireTicks` timestamp. The Radix Tree and Bloom Filter remain fully agnostic to this, storing and retrieving raw payloads efficiently. When a record is fetched (`Get`), the timestamp is evaluated in real-time. If expired, the record is immediately dropped via a lazy-delete mechanism (`Tombstone`), making it logically invisible.

### Reload & Bootstrapping

When `ReloadAsync` is invoked, it completely resets the engine state with zero downtime. The Radix Tree, Bloom Filter, and active MemTables are instantly cleared from RAM, while stale `.sst` and `.log` files are wiped from the disk. The engine immediately re-hydrates itself by streaming fresh, up-to-date data directly from the user-defined `Supplier`.

---

## Performance & Stress-Test Benchmarks

The metrics below represent the performance of the off-heap unmanaged engine under high-concurrency stress testing. The writes are executed in consecutive chunks (Super Chunks), and each batch triggers deep native cleanup routines. Configurations include dynamically scaling `RadixTreeCapacity` matching the payload size paired with aggressive/small `FlushThreshold` constraints.

### Scalability Measurement Ledger (`StressTest_Scale_Measurements`)

| Record Count | RadixTree Capacity | Chosen Flush Threshold | Write Throughput | Write Duration | Read Throughput | **Avg. Read Latency** | Read Duration | Total Disk Footprint | Post-Write Managed Heap | Post-Write Working Set | Post-Read Working Set | GC Collections (Gen0 / Gen1 / Gen2) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **1 K** | 2,000 | 64 MB | 26,595 / s | 37 ms | 108,236 / s | **9.23 µs** | 9 ms | 0.11 MB | 0.94 MB | 62.13 MB | 62.33 MB | 0 / 0 / 0 |
| **10 K** | 15,000 | 64 MB | 97,726 / s | 102 ms | 339,699 / s | **2.94 µs** | 29 ms | 1.16 MB | 1.00 MB | 33.39 MB | 39.49 MB | 0 / 0 / 0 |
| **100 K** | 150,000 | 32 MB | 118,904 / s | 841 ms | 381.809 / s | **2.61 µs** | 261 ms | 11.89 MB | 1.00 MB | 64.32 MB | 71.30 MB | 5 / 1 / 0 *(Incl. Read)* |
| **1 M** | 1,500,000 | 32 MB | 164,657 / s | 6,073 ms | 604,487 / s | **1.65 µs** | 1,654 ms | 118.40 MB | 35.01 MB | 187.37 MB | 246.85 MB | 70 / 4 / 2 *(Incl. Read)* |
| **5 M** | 7,000,000 | 16 MB | 123,679 / s | 40,427 ms | 664,734 / s | **1.50 µs** | 7.521 ms | 602.93 MB | 1.27 MB | **5.01 MB** | **7.95 MB** | 368 / 59 / 52 *(Incl. Read)* |
| **10 M** | 12,500,000 | 16 MB | 112,860 / s | 88.605 ms | 617,602 / s | **1.61 µs** | 16,191 ms | 1,208.06 MB | 1.28 MB | **8.57 MB** | **7.12 MB** | 753 / 118 / 107 *(Incl. Read)* |
| **50 M** | 65,000,000 | 4 MB | 104,006 / s | 480,743 ms | 523,782 / s | **1.80 µs** | 95,459 ms | 6,167.15 MB | 2.14 MB | **8.38 MB** | **4.23 MB** | 4210 / 827 / 740 *(Incl. Read)* |
| **100 M** | 115,000,000 | 2 MB | 84,558 / s | 1,182,625 ms | 533,589 / s | **1.87 µs** | 187,410 ms | 12,366.03 MB | 4.71 MB | **4.36 MB** | **4.48 MB** | 8563 / 1667 / 1473 *(Incl. Read)* |

### Architectural Insights & Analysis

1. **Memory Stabilization Success (Working Set)**
By leveraging chunked write boundaries past 1M records and implementing a completely off-heap unmanaged `ConcurrentRadixTree`, the operating system physical RAM footprint (**Working Set**) flatlines and pins tightly to **~4.5 MB**. Even as the dataset grows from **5 Million to 100 Million entries (a 20x scale increase)**, memory use does not drift.
2. **Deterministic O(1)-like Read Latency**
While total read duration naturally scales with higher key sizes due to processing volume, the record-specific **Average Read Latency remains practically linear between 1.65 µs and 1.87 µs**. Pointer hopping on the unmanaged memory layout alongside front-facing Bloom Filter evaluations keeps reading speeds isolated from scale degradation.
3. **Flush Threshold Backpressure**
Intentionally lowering the `FlushThreshold` at high volumes (dropping to 2MB at 100M keys) bounds memory overhead but introduces more frequent disk write (I/O) switching. This bounds write throughput to around ~84K operations per second, representing a clean engineering tradeoff between memory safety and maximum disk ingestion speeds.
4. **Zero LOH or Managed Bloat**
Despite processing billions of transient transformations totaling roughly ~134 GB of net allocated throughput during the 100M sequence, unmanaged pointer mappings keep the Large Object Heap (LOH) empty (~0.09 MB baseline). Aggressive collection cycles cleanly sweep out managed residue, returning the application back to a pristine **~1.02 MB working set baseline** following each runtime test execution.

---

## Performance Notes

* **Early Read Exits:** Read requests (`Get`) first pass through an internal **Bloom Filter**. If a key does not exist, the engine returns immediately without traversing the tree or touching the disk.
* **Prefix Memory Sharing:** By replacing traditional Hash Maps with a `ConcurrentRadixTree`, keys with common prefixes (like `session_xyz`, `session_abc`) share memory nodes. This prevents massive string allocation overheads and reduces the managed heap footprint significantly.
* **Lock-Free Reads:** Read traversals down the Radix Tree do not use heavy locking mechanisms (like `ReaderWriterLock` or `SemaphoreSlim`). The node structural integrity is maintained safely, allowing thousands of concurrent reads.
* **GC Impact is Minimal:** The only managed allocation per read is the deserialized `TValue` object. Shard data and binary payloads are sliced directly from the `MemoryMappedViewAccessor` via zero-copy `ReadOnlySpan<byte>`.

---

## Contributing

Pull requests are welcome. For significant architectural adjustments, please open an issue first to discuss your proposed strategy.

```bash
git clone https://github.com/BurakKontas/MmapCache
cd MmapCache
dotnet build
dotnet test MmapCache.Tests/MmapCache.Tests.csproj -v normal

```

Please note that contributions are subject to the terms of the GPL-3.0 license — by submitting a pull request you agree that your contribution will be distributed under the same license terms.

---

## License

Copyright (C) 2026 Arda Burak Kontaş

MmapCache is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](https://www.google.com/search?q=https://www.gnu.org/licenses/gpl-3.0.html) as published by the Free Software Foundation.

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.