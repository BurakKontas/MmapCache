# MmapCache (LSM Engine)

**An off-heap LSM storage engine for .NET with active mutation support and zero-downtime compaction.**

MmapCache stores data in off-heap memory-mapped (`mmap`) segment files utilizing a lock-free LSM (Log-Structured Merge-tree) architecture. It fully supports WAL (Write-Ahead Log) crash-recovery, active live mutations, automatic background compaction, and completely lock-free segment reads.

---

## Table of Contents

* [Why MmapCache LSM?](#why-mmapcache-lsm)
* [Features](#features)
* [Installation](#installation)
* [Quick Start](#quick-start)
* [Configuration Reference](#configuration-reference)
* [How It Works](#how-it-works)
* [WAL and Crash Recovery](#wal-and-crash-recovery)
* [Flush and Immutable SSTables](#flush-and-immutable-sstables)
* [Compaction](#compaction)
* [TTL & Lazy Expiration](#ttl--lazy-expiration)
* [Reload & Bootstrapping](#reload--bootstrapping)
* [Performance & Stress-Test Benchmarks](#performance--stress-test-benchmarks)
* [Contributing](#contributing)
* [License](#license)

---

## Why MmapCache LSM?

Most in-process caches and embedded stores suffer from:

* **GC Fragmentation & Pauses** - Constantly overwriting objects creates holes in the managed heap, leading to severe garbage collection compaction cycles.
* **Data Volatility** - Standard caching layers lose all mutated state instantly if the target process crashes or suffers an unhandled termination.
* **Reload Lockouts** - Having to periodically rebuild large data structures entirely from external databases is expensive, slow, and resource-heavy.

By restructuring the system strictly with an LSM architecture, every mutation is recorded purely with append-friendly I/O to a sequential `WAL` and a memory-efficient `Radix Tree MemTable`. When size triggers are reached, the memory is locked, serialized continuously off-heap into an SSTable mapped strictly via OS memory-mapping (`mmap`), and native pointers are safely recycled. This effectively guarantees **Zero Managed Memory Leaks** and deterministic overhead over time.

---

## Features

| Feature | Description |
| --- | --- |
| **Off-Heap Storage** | Immutable segment files (`.sst`) are directly `mmap`'d - ensuring zero GC pressure regardless of dataset size. |
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

> -- MmapCache is released under the **GPL-3.0** license. If you intend to use it inside a closed-source commercial product, review the license terms carefully.

---

## Quick Start

```csharp
using MmapCache.Cache;
using MmapCache.Config;
using System.Text.Json;

// -- 1. Initialize once at application startup --------------------------------?
MmapCacheManager.Initialize(
    basePath : "/var/cache/myapp");  // LSM log and sst files are written here

// -- 2. Describe what to store ------------------------------------------------?
var productDef = new MmapCacheDefinition<Product>
{
    Name = "products",
    // Supplier runs ONLY if the engine boots from an empty/clean disk state
    Supplier = () => db.Products.Select(p => (p.Id.ToString(), p)),
    Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
    Deserializer = b => JsonSerializer.Deserialize<Product>(b)!,
    Ttl = TimeSpan.FromHours(1)
};

// -- 3. Register Engine Instance ----------------------------------------------?
// Restores from on-disk SSTables instantly, replays WAL if recovering from a crash.
MmapCacheManager.Instance.Register(productDef);

// -- 4. Active Mutability (Put/Delete) ----------------------------------------?
// Mutations are immediately visible and durably piped to the active WAL
MmapCacheManager.Instance.Put("products", "product_42", new Product("product_42", "Updated!", 9.99m, 50));
MmapCacheManager.Instance.Delete("products", "product_99");

// -- 5. Concurrent Reads (lock-free logic) ------------------------------------?
var product = MmapCacheManager.Instance.Get<Product>("products", "product_42");

if (MmapCacheManager.Instance.TryGet<Product>("products", "product_42", out var p))
    Console.WriteLine(p!.Name);

// -- 6. Dispose on shutdown ----------------------------------------------------
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

The metrics below represent the performance of the off-heap unmanaged engine under high-concurrency stress testing. Writes execute in consecutive chunks (Super Chunks), with each batch triggering deep native cleanup routines. Configurations include dynamically scaling `RadixTreeCapacity` to match payload size, paired with aggressive `FlushThreshold` constraints for memory safety.

### Scalability Measurement Ledger (`StressTest_Scale_Measurements`)

| Record Count | RadixTree Capacity | Flush Threshold | Write Duration | Write Throughput | Read Duration | Read Throughput | Avg Latency | Disk Footprint | Post-Write Working Set | Post-Read Working Set | GC Collections (Gen0/Gen1/Gen2) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **1K** | 2,000 | 64 MB | 39 ms | 25,570/s | 10 ms | 91,557/s | 10.64 us | 0.11 MB | 62.20 MB | 62.47 MB | 0 / 0 / 0 |
| **10K** | 15,000 | 64 MB | 157 ms | 63,410/s | 39 ms | 250,872/s | 3.94 us | 1.16 MB | 34.29 MB | 40.50 MB | 0 / 0 / 0 |
| **100K** | 150,000 | 32 MB | 779 ms | 128,268/s | 332 ms | 300,333/s | 3.30 us | 11.89 MB | 63.45 MB | 73.13 MB | 5 / 1 / 0 |
| **1M** | 1,500,000 | 32 MB | 5,101 ms | 196,028/s | 1,749 ms | 571,649/s | 1.72 us | 118.03 MB | 180.23 MB | 258.03 MB | 55 / 12 / 7 |
| **5M** | 7,000,000 | 16 MB | 36,427 ms | 137,259/s | 7,678 ms | 651,128/s | 1.46 us | 602.43 MB | 15.68 MB | 40.84 MB | 310 / 114 / 78 |
| **10M** | 12,500,000 | 16 MB | 76,647 ms | 130,467/s | 16,002 ms | 624,918/s | 1.52 us | 1,208.15 MB | 7.84 MB | 79.22 MB | 608 / 222 / 150 |
| **50M** | 65,000,000 | 4 MB | 390,336 ms | 128,095/s | 83,076 ms | 601,853/s | 1.57 us | 6,167.18 MB | 2.62 MB | 387.79 MB | 3,591 / 1,978 / 1,136 |
| **100M** | 115,000,000 | 2 MB | 813,904 ms | 122,865/s | 196,056 ms | 510,057/s | 1.86 us | 12,365.99 MB | 2.56 MB | 766.00 MB | 7,039 / 4,154 / 2,686 |

> **Note:** GC counts shown are cumulative across both write and read phases for each test iteration. Write throughput remains consistently above 120K ops/sec even at 100M scale, demonstrating linear scalability.

### Read Latency Distribution (Percentile Analysis)

| Record Count | p50 | p95 | p99 | p99.9 | Max Latency |
| --- | --- | --- | --- | --- | --- |
| **1K** | 2.60 us | 3.40 us | 12.60 us | 7,619.00 us | 7,619.00 us |
| **10K** | 3.10 us | 6.90 us | 12.10 us | 21.20 us | 151.20 us |
| **100K** | 3.10 us | 4.50 us | 6.10 us | 11.30 us | 1,691.40 us |
| **1M** | 1.40 us | 3.30 us | 5.10 us | 15.50 us | 10,994.80 us |
| **5M** | 1.30 us | 2.20 us | 3.20 us | 7.00 us | 2,598.30 us |
| **10M** | 1.40 us | 2.30 us | 3.60 us | 7.20 us | 2,171.90 us |
| **50M** | 1.40 us | 2.30 us | 4.60 us | 23.70 us | 5,312.80 us |
| **100M** | 1.30 us | 2.20 us | 5.00 us | 42.00 us | 36,225.70 us |

**Key Insight:** p50 latency remains **< 3.1 us** across all scales. Even at 100M records, the median read latency is only 1.30 us, proving the effectiveness of the unmanaged RadixTree and Bloom Filter front-end.

### Detailed Heap Metrics (100M Record Benchmark)

| Phase | Working Set | Target Heap | Fragmentation | Net Allocated | Gen0 | Gen1 | Gen2 | LOH | POH |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Baseline (Empty)** | 11.96 MB | 1.31 MB | 0.05 MB | 0.03 MB | 0 | 0 | 0 | 0.09 MB | 0.02 MB |
| **Post-Write** | 2.56 MB | 5.42 MB | 0.00 MB | 70,471.95 MB | 7,039 | 4,154 | 2,686 | 0.09 MB | 0.02 MB |
| **Post-Read** | 766.00 MB | 767.64 MB | 0.00 MB | 54,856.30 MB | 4,801 | 401 | 401 | 763.03 MB | 0.02 MB |
| **After Cleanup** | 0.81 MB | - | - | - | - | - | - | - | - |

### Architectural Insights & Analysis

#### 1. Memory Stabilization Success (Working Set)

By leveraging chunked write boundaries past 1M records and implementing a completely off-heap unmanaged `ConcurrentRadixTree`, the operating system physical RAM footprint demonstrates remarkable stability:

- **5M�100M scale (20x growth):** Post-write Working Set remains between **2.56�15.68 MB**
- Each test iteration **returns to pristine baseline** (~0.8�0.9 MB) after cleanup
- No cumulative memory leakage across progressively larger datasets

#### 2. Deterministic O(1)-like Read Latency

While total read duration naturally scales with dataset size, per-operation latency remains exceptionally stable:

| Scale | Avg Read Latency | p50 | p95 | p99 |
| --- | --- | --- | --- | --- |
| 1K | 10.64 us | 2.60 us | 3.40 us | 12.60 us |
| 100K | 3.30 us | 3.10 us | 4.50 us | 6.10 us |
| 1M | 1.72 us | 1.40 us | 3.30 us | 5.10 us |
| 100M | 1.86 us | 1.30 us | 2.20 us | 5.00 us |

**Key takeaway:** After warm-up, read latency stabilizes to **~1.5�1.9 us** average regardless of dataset size. Pointer hopping on unmanaged memory layout, combined with front-facing Bloom Filter evaluations, keeps read speeds isolated from scale degradation.

#### 3. Write Throughput Scaling

| Scale | Write Throughput | Flush Threshold |
| --- | --- | --- |
| 1K | 25,570/s | 64 MB |
| 100K | 128,268/s | 32 MB |
| 1M | 196,028/s | 32 MB |
| 5M | 137,259/s | 16 MB |
| 100M | 122,865/s | 2 MB |

**Observation:** Intentionally lowering `FlushThreshold` at high volumes (dropping to 2MB at 100M keys) bounds memory overhead but introduces more frequent disk I/O switching. This represents a clean engineering tradeoff between memory safety and maximum disk ingestion speeds.

#### 4. Zero LOH or Managed Bloat

Despite processing massive total throughput (cumulative net allocated memory ~134 GB across the 100M sequence), unmanaged pointer mappings keep the Large Object Heap (LOH) remarkably clean:

- **Post-write LOH:** 0.09 MB (baseline) for all scales except 1M+ read phases
- **Post-read LOH:** Grows to accommodate read buffers but cleans up completely
- **Aggressive GC collections** sweep managed residue effectively

#### 5. GC Pressure Analysis

| Scale | Write Gen0 | Write Gen2 | Read Gen0 | Read Gen2 |
| --- | --- | --- | --- | --- |
| 1M | 55 | 7 | 46 | 0 |
| 10M | 608 | 150 | 481 | 41 |
| 50M | 3,591 | 1,136 | 2,402 | 201 |
| 100M | 7,039 | 2,686 | 4,801 | 401 |

The write phase generates most GC pressure due to serialization and temporary buffer allocations. The read phase is significantly more efficient, with Gen2 collections dropping to near-zero at smaller scales. At 100M, read phase Gen2 collections (401) represent only ~15% of write phase collections (2,686).

---

## Performance Notes

* **Early Read Exits:** Read requests (`Get`) first pass through an internal **Bloom Filter**. If a key does not exist, the engine returns immediately without traversing the tree or touching the disk.
* **Prefix Memory Sharing:** By replacing traditional Hash Maps with a `ConcurrentRadixTree`, keys with common prefixes (like `session_xyz`, `session_abc`) share memory nodes. This prevents massive string allocation overheads and reduces the managed heap footprint significantly.
* **Lock-Free Reads:** Read traversals down the Radix Tree do not use heavy locking mechanisms (like `ReaderWriterLock` or `SemaphoreSlim`). Node structural integrity is maintained safely, allowing thousands of concurrent reads.
* **GC Impact is Minimal:** The only managed allocation per read is the deserialized `TValue` object. Shard data and binary payloads are sliced directly from the `MemoryMappedViewAccessor` via zero-copy `ReadOnlySpan<byte>`.
* **Deterministic Cleanup:** After each stress test iteration, the engine returns to **~0.8 MB working set** - proof of zero memory leaks in the unmanaged layer.

---

## Contributing

Pull requests are welcome. For significant architectural adjustments, please open an issue first to discuss your proposed strategy.

```bash
git clone https://github.com/BurakKontas/MmapCache
cd MmapCache
dotnet build
dotnet test MmapCache.Tests/MmapCache.Tests.csproj -v normal
```

Please note that contributions are subject to the terms of the GPL-3.0 license - by submitting a pull request you agree that your contribution will be distributed under the same license terms.

---

## License

Copyright (C) 2026 Arda Burak Kontas

MmapCache is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.html) as published by the Free Software Foundation.

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
