using System.IO.Hashing;

namespace MmapCache.Lsm;

public class WalWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    public WalWriter(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        _writer = new BinaryWriter(_stream);
    }

    public void Append(byte opType, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        // Format: [CRC32:4] [OpType:1] [KeyLen:4] [ValueLen:4] [Key] [Value]
        int totalBodyLen = 1 + 4 + 4 + key.Length + value.Length;
        Span<byte> buffer = stackalloc byte[totalBodyLen];
        
        buffer[0] = opType; // 0 = Put, 1 = Delete
        BitConverter.TryWriteBytes(buffer.Slice(1, 4), key.Length);
        BitConverter.TryWriteBytes(buffer.Slice(5, 4), value.Length);
        key.CopyTo(buffer.Slice(9));
        if (value.Length > 0) value.CopyTo(buffer.Slice(9 + key.Length));

        uint crc = Crc32.HashToUInt32(buffer);
        _writer.Write(crc);
        _writer.Write(buffer);
    }

    public void Flush()
    {
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}

