using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PS4GodEaterSeriesTools;

/// <summary>
/// blz4 —— GE2 Rage Burst（Ge2rbResTool）系的 res 压缩格式，取代原 blz2（GE2 正式版）。
/// 实现镜像自 Ge2rbResTool.Blz4（用户提供），算法保持逐字一致：
///   压缩算法 = <b>zlib 包装的 deflate</b>（ZLibStream，CompressionLevel.Optimal）
///   —— 注意与 blz2 的 raw deflate（无包装，SmallestSize）不同，不要混用。
///
/// 段结构：
///   0x00  4  魔数 "blz4"（小写 ASCII 62 6C 7A 34，读作 u32 = 0x347A6C62）
///   0x04  4  解压后总大小 decompSize（i32 LE）
///   0x08  8  保留（全零）
///   0x10  16 原始数据 MD5
///   0x20  .. 重复 { u16 压缩块长度, zlib 压缩块 } 直到段尾
///
/// ★ 块切分 = 头→尾、首块为余数（data.Length % 0x10000，整除则整块），每块 64KB
/// ★ 块写入顺序 = 轮转：[末块, 首块, 中间块 1..N-2]
///   （与 blz2 的 [末块, 首块, 中...] 同构；blz2 是尾→头切、首块余数写在最前附近）
/// ★ 解压顺序 = chunk[1], chunk[2], …, chunk[N-1], chunk[0]，拼接后总长须等于 decompSize
/// </summary>
public static class Blz4
{
    /// <summary>魔数 "blz4" 的 u32 形式（小端读出的整数）</summary>
    public const uint Magic = 0x347A6C62;

    /// <summary>原始数据每块的字节数（首块为余数）</summary>
    public const int BlockSize = 0x10000;

    /// <summary>段头长度：magic(4) + decompSize(4) + reserved(8) + MD5(16)</summary>
    public const int HeaderSize = 32;

    public static readonly byte[] MagicBytes = { (byte)'b', (byte)'l', (byte)'z', (byte)'4' };

    public static bool IsBlz4(byte[] b) =>
        b != null && b.Length >= HeaderSize &&
        b[0] == (byte)'b' && b[1] == (byte)'l' && b[2] == (byte)'z' && b[3] == (byte)'4';

    /// <summary>blz4 段解压：按轮转序 [1..N-1, 0] 逐块 inflate 后拼接，总长校验 decompSize</summary>
    public static byte[] Decompress(byte[] raw)
    {
        if (!IsBlz4(raw))
            throw new InvalidDataException(
                $"不是 blz4 段：{Convert.ToHexString(raw.AsSpan(0, Math.Min(4, raw.Length)))}");

        int decompSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        if (decompSize <= 0)
            throw new InvalidDataException($"blz4：非法 decompSize {decompSize}");

        // 只记偏移不复制：[u16 size][zlib data] 循环到段尾
        var chunks = new List<(int Offset, int Size)>();
        int pos = HeaderSize;
        while (pos < raw.Length)
        {
            if (pos + 2 > raw.Length)
                throw new InvalidDataException($"blz4：尾部剩 {raw.Length - pos} 字节，读不了 u16 块长");
            int size = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(pos));
            pos += 2;
            if (pos + size > raw.Length)
                throw new InvalidDataException($"blz4：块越界 off={pos} n={size} 剩余={raw.Length - pos}");
            chunks.Add((pos, size));
            pos += size;
        }
        if (chunks.Count == 0)
            throw new InvalidDataException("blz4：没有任何数据块");

        // 解压顺序：chunk[1..N-1] 接 chunk[0]
        byte[] result = new byte[decompSize];
        int offset = 0;
        for (int i = 1; i <= chunks.Count; i++)
        {
            var (chunkOff, chunkSize) = chunks[i % chunks.Count];
            offset += Inflate(raw, chunkOff, chunkSize, result.AsSpan(offset));
        }
        if (offset != decompSize)
            throw new InvalidDataException($"blz4：解压总长不符，期望 {decompSize} 实得 {offset}");
        return result;
    }

    /// <summary>压缩成 blz4 段：32B 头（magic + decompSize + 保留 + MD5）+ 轮转块序 [末块, 首块, 中...]</summary>
    public static byte[] Compress(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("blz4：不能压缩空数据");

        int firstBlockSize = data.Length % BlockSize;
        if (firstBlockSize == 0) firstBlockSize = BlockSize;
        int blockCount = (data.Length - firstBlockSize) / BlockSize + 1;

        using var ms = new MemoryStream(HeaderSize + data.Length);
        using var wr = new BinaryWriter(ms);

        wr.Write(Magic);
        wr.Write(data.Length);
        wr.Write(new byte[8]);
        wr.Write(MD5.HashData(data));

        if (blockCount == 1)
        {
            WriteChunk(wr, Deflate(data));
        }
        else
        {
            int lastBlockStart = firstBlockSize + (blockCount - 2) * BlockSize;
            WriteChunk(wr, Deflate(data.AsSpan(lastBlockStart)));
            WriteChunk(wr, Deflate(data.AsSpan(0, firstBlockSize)));
            for (int i = 1; i < blockCount - 1; i++)
                WriteChunk(wr, Deflate(data.AsSpan(firstBlockSize + (i - 1) * BlockSize, BlockSize)));
        }
        wr.Flush();
        return ms.ToArray();
    }

    static void WriteChunk(BinaryWriter wr, byte[] chunk)
    {
        if (chunk.Length > ushort.MaxValue)
            throw new InvalidDataException(
                $"blz4：压缩块 {chunk.Length} 字节超出 u16 块长上限");
        wr.Write((ushort)chunk.Length);
        wr.Write(chunk);
    }

    /// <summary>zlib 包装 inflate 单块到 dst，返回写入字节数</summary>
    static int Inflate(byte[] src, int srcOffset, int srcSize, Span<byte> dst)
    {
        using var zlib = new ZLibStream(new MemoryStream(src, srcOffset, srcSize), CompressionMode.Decompress);
        int total = 0;
        while (total < dst.Length)
        {
            int n = zlib.Read(dst[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>zlib 包装 deflate —— ★ 实测（2026-10-15）：游戏原生 blz4 段由 zlib level 9 / memLevel 8 产出，
    /// 已改用原生 C zlib（NativeZlib.ZlibDeflate9）P/Invoke，可对全部真实段逐字节复现 ⇒ 零改动往返 bit-identical。</summary>
    static byte[] Deflate(ReadOnlySpan<byte> src) => NativeZlib.ZlibDeflate9(src.ToArray());
}
