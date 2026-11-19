namespace DownloadManager.TestServer;

/// <summary>
/// Deterministic, non-repeating test content: the byte at any offset is a pure function of (offset, seed).
/// A misplaced or duplicated segment cannot go unnoticed, and the "original file" never has to be stored.
/// </summary>
public static class TestData
{
    public static byte At(long index, int seed)
    {
        var h = ((ulong)(index >> 3) + (ulong)seed * 0x9E3779B1UL) * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return (byte)(h >> (int)((index & 7) * 8));
    }

    public static void Fill(Span<byte> buffer, long offset, int seed)
    {
        for (var i = 0; i < buffer.Length; i++) buffer[i] = At(offset + i, seed);
    }

    /// <summary>Returns -1 when the file is byte-for-byte identical to the expected content, otherwise the first differing offset.</summary>
    public static long FindFirstMismatch(string path, long expectedSize, int seed)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (fs.Length != expectedSize) return Math.Min(fs.Length, expectedSize);

        var actual = new byte[1 << 20];
        var expected = new byte[1 << 20];
        long offset = 0;
        while (offset < expectedSize)
        {
            var count = (int)Math.Min(actual.Length, expectedSize - offset);
            fs.ReadExactly(actual, 0, count);
            Fill(expected.AsSpan(0, count), offset, seed);
            var common = actual.AsSpan(0, count).CommonPrefixLength(expected.AsSpan(0, count));
            if (common < count) return offset + common;
            offset += count;
        }
        return -1;
    }
}
