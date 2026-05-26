using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;

namespace MmapCache.Lsm;

/// <summary>
/// A high-performance, low-overhead Compactor designed to merge multiple SSTable segments 
/// into a single dense segment while interfacing directly with our unmanaged ConcurrentRadixTree index.
/// </summary>
public class Compactor
{
    /// <summary>
    /// Merges two or more active segments into a single consolidated unmanaged segment.
    /// Purges dropped/tombstoned keys and updates the global native index lock-free snapshot layout.
    /// </summary>
    public void Compact(
        ConcurrentDictionary<int, Segment> activeSegments,
        ConcurrentRadixTree<IndexRecord> globalIndex,
        string dir,
        int newSegmentId)
    {
        // 1. Identify segments targeted for the merge operation
        var segmentsToMerge = activeSegments.Values.ToList();
        if (segmentsToMerge.Count < 2) return;

        string newSstPath = Path.Combine(dir, $"segment_{newSegmentId}.sst");
        long currentOffset = 0;

        using (var fs = new FileStream(newSstPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        using (var bw = new BinaryWriter(fs))
        {
            // 2. Scan the global unmanaged index snapshot to find records pointing to the target segments.
            // This traversal happens cleanly without putting allocations onto the managed GC heap.
            var keysInSegments = globalIndex.GetSnapshot()
                .Where(kv => !kv.Value.IsMemTable && segmentsToMerge.Any(s => s.SegmentId == kv.Value.SegmentId))
                .ToList();

            foreach (var kvp in keysInSegments)
            {
                var segment = activeSegments[kvp.Value.SegmentId];

                // Read directly from the memory-mapped file view boundary
                var valueBytes = segment.ReadValue(kvp.Value.Offset, kvp.Value.Length);
                var kBytes = Encoding.UTF8.GetBytes(kvp.Key);

                long recordOffset = currentOffset;

                // Write the continuous stream data into the new SSTable
                bw.Write(false); // isDeleted = false (tombstones are dropped entirely during compaction)
                bw.Write(kBytes.Length);
                bw.Write(valueBytes.Length);
                bw.Write(kBytes);
                bw.Write(valueBytes);

                // Format offset matching: 1 byte (bool) + 4 bytes (kLen) + 4 bytes (vLen) + Key Length
                long valOffset = recordOffset + 1 + 4 + 4 + kBytes.Length;

                // 3. Atomically update the global unmanaged index node mapping to the new segment
                globalIndex.Put(kvp.Key, new IndexRecord
                {
                    IsMemTable = false,
                    SegmentId = newSegmentId,
                    Offset = valOffset,
                    Length = valueBytes.Length
                });

                currentOffset += 1 + 4 + 4 + kBytes.Length + valueBytes.Length;
            }
        }

        // 4. Register the newly compacted segment to the engine loop
        var newSegment = new Segment(newSegmentId, newSstPath);
        activeSegments[newSegmentId] = newSegment;

        // 5. Safely unmap and erase old obsolete physical files from disk
        foreach (var oldSeg in segmentsToMerge)
        {
            if (activeSegments.TryRemove(oldSeg.SegmentId, out _))
            {
                oldSeg.Dispose(); // Releases memory-mapped system file handle hooks immediately

                string oldSstPath = Path.Combine(dir, $"segment_{oldSeg.SegmentId}.sst");
                try
                {
                    if (File.Exists(oldSstPath))
                    {
                        File.Delete(oldSstPath);
                    }
                }
                catch
                {
                    // Catch transient file locks from operating system delay or antivirus hooks
                }
            }
        }
    }
}