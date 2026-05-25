using System.Collections.Concurrent;

namespace MmapCache.Lsm;

public class Compactor
{
    // A simplified compactor that merges two or more segments into one.
    public void Compact(ConcurrentDictionary<int, Segment> activeSegments, ConcurrentDictionary<string, IndexRecord> globalIndex, string dir, int newSegmentId)
    {
        // 1. Identify segments to merge (in a real system, Size-Tiered or Leveled rules apply)
        var segmentsToMerge = activeSegments.Values.ToList();
        if (segmentsToMerge.Count < 2) return;

        string newSstPath = Path.Combine(dir, $"segment_{newSegmentId}.sst");
        long currentOffset = 0;

        using (var fs = new FileStream(newSstPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            // We iterate over the global index to find what is currently mapped to these segments
            var keysInSegments = globalIndex
                .Where(kv => !kv.Value.IsMemTable && segmentsToMerge.Any(s => s.SegmentId == kv.Value.SegmentId))
                .ToList();

            foreach (var kvp in keysInSegments)
            {
                var segment = activeSegments[kvp.Value.SegmentId];
                var valueBytes = segment.ReadValue(kvp.Value.Offset, kvp.Value.Length).ToArray();
                var kBytes = System.Text.Encoding.UTF8.GetBytes(kvp.Key);

                long recordOffset = currentOffset;

                bw.Write(false); // isDeleted = false (tombstones are dropped during compaction)
                bw.Write(kBytes.Length);
                bw.Write(valueBytes.Length);
                bw.Write(kBytes);
                bw.Write(valueBytes);

                long valOffset = recordOffset + 9 + kBytes.Length;

                // Update index
                globalIndex[kvp.Key] = new IndexRecord
                {
                    IsMemTable = false,
                    SegmentId = newSegmentId,
                    Offset = valOffset,
                    Length = valueBytes.Length
                };

                currentOffset += 9 + kBytes.Length + valueBytes.Length;
            }
        }

        var newSegment = new Segment(newSegmentId, newSstPath);
        activeSegments[newSegmentId] = newSegment;

        // Cleanup old segments
        foreach (var oldSeg in segmentsToMerge)
        {
            activeSegments.TryRemove(oldSeg.SegmentId, out _);
            oldSeg.Dispose();
            File.Delete(Path.Combine(dir, $"segment_{oldSeg.SegmentId}.sst"));
        }
    }
}

