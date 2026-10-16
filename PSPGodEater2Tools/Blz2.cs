using System.IO.Compression;

namespace PSPGodEater2Tools;

/// <summary>
/// blz2 —— **GE2 正式版**的 res 压缩格式（GE/GEB 用 gzip、GE2 体验版用 dflt）。
///
/// 段结构：
///   0x00  4  魔数 "blz2"（小写 ASCII 62 6C 7A 32，读作 u32 = 0x327A6C62）
///   之后重复 { u16 压缩块长度, 压缩块字节 } 直到段尾
///
/// ★ 块顺序 = **轮转**：原文件被切成 N 块（从**尾部**往头部切，每块 0x10000 = 64 KB，
///   **头部那块是余数**），压缩后写入文件的顺序是
///     [第 N-1 块(末块), 第 0 块, 第 1 块, …, 第 N-2 块]
///   解压时把读到的第 0 个块挪到末尾即可还原原始顺序。
///   （这与 GECV_EX 的 `BLZ2Utils` 行为一致；其轮转分支的写法是「1..n-1 再接 0」。）
///
/// ★ 压缩算法 = **raw deflate（无 zlib/gzip 包装），压缩级别 9**
///   实测（2026-10-06）：`zlib level 9 / wbits=-15` 可对 `tcretab-2-2nd.pgf`（20 块）
///   与 `article.kst`（6 块）**逐块 bit-identical 复现**游戏原生压缩流。
///   ⇒ 零改动往返时能拿回与原始完全一致的 blz2 段。
///
/// 注：块解压后的长度不写在块头里 —— 逐块 inflate 到流尾即自然结束，
///     「解压后总长」只记录在 res 的 TOC +0x1C（decsize）里。
/// </summary>
public static class Blz2
{
    /// <summary>魔数 "blz2" 的 u32 形式（小端读出的整数）</summary>
    public const uint Magic = 0x327A6C62;

    /// <summary>原始数据每块的字节数（尾→头切，最后留在头部的那块是余数）</summary>
    public const int BlockSize = 0x10000;

    public static readonly byte[] MagicBytes = { (byte)'b', (byte)'l', (byte)'z', (byte)'2' };

    public static bool IsBlz2(byte[] b) =>
        b != null && b.Length >= 6 &&
        b[0] == (byte)'b' && b[1] == (byte)'l' && b[2] == (byte)'z' && b[3] == (byte)'2';

    /// <summary>切成「尾→头、每块 0x10000、头部为余数」的块序列（返回顺序 = 原始文件顺序）</summary>
    public static List<byte[]> SplitPlain(byte[] data)
    {
        var list = new List<byte[]>();
        if (data.Length <= BlockSize) { list.Add(data); return list; }

        int rem = data.Length % BlockSize;          // 头部余数块
        int p = 0;
        if (rem > 0)
        {
            var b = new byte[rem];
            Array.Copy(data, 0, b, 0, rem);
            list.Add(b);
            p = rem;
        }
        while (p < data.Length)
        {
            var b = new byte[BlockSize];
            Array.Copy(data, p, b, 0, BlockSize);
            list.Add(b);
            p += BlockSize;
        }
        return list;
    }

    /// <summary>blz2 段解压：单块直解；多块按「读到的第 0 块挪到末尾」还原后逐块解并拼接</summary>
    public static byte[] Decompress(byte[] seg)
    {
        if (!IsBlz2(seg))
            throw new InvalidDataException(
                $"不是 blz2 段：{Convert.ToHexString(seg.AsSpan(0, Math.Min(4, seg.Length)))}");

        var blocks = new List<byte[]>();
        int off = 4;
        while (off < seg.Length)
        {
            if (off + 2 > seg.Length)
                throw new InvalidDataException($"blz2：尾部剩 {seg.Length - off} 字节，读不了 u16 块长");
            int n = seg[off] | (seg[off + 1] << 8);
            off += 2;
            if (off + n > seg.Length)
                throw new InvalidDataException($"blz2：块越界 off={off} n={n} 剩余={seg.Length - off}");
            blocks.Add(new byte[n]);
            Array.Copy(seg, off, blocks[^1], 0, n);
            off += n;
        }
        if (blocks.Count == 0)
            throw new InvalidDataException("blz2：没有任何数据块");

        // ★ 轮转还原：读到的 block[0] 是原文件的【末块】，挪到末尾
        byte[] result;
        if (blocks.Count == 1) result = Inflate(blocks[0]);
        else
        {
            using var ms = new MemoryStream();
            for (int i = 1; i < blocks.Count; i++) Write(ms, Inflate(blocks[i]));
            Write(ms, Inflate(blocks[0]));
            result = ms.ToArray();
        }
        return result;
    }

    /// <summary>压缩成 blz2 段：原文件切块（尾→头）→ 末块先写 → 每块 raw deflate level 9</summary>
    public static byte[] Compress(byte[] data)
    {
        var plain = SplitPlain(data);
        using var ms = new MemoryStream();
        ms.Write(MagicBytes, 0, 4);
        // ★ 写盘顺序：末块（plain[^1]）先写，其余按原序
        WriteBlock(ms, plain[^1]);
        for (int i = 0; i < plain.Count - 1; i++) WriteBlock(ms, plain[i]);
        return ms.ToArray();
    }

    static void WriteBlock(Stream s, byte[] raw)
    {
        byte[] c = Deflate9(raw);
        if (c.Length > ushort.MaxValue)
            throw new InvalidDataException(
                $"blz2：压缩块 {c.Length} 字节超出 u16 块长上限（原始块 {raw.Length} 字节）");
        s.WriteByte((byte)(c.Length & 0xFF));
        s.WriteByte((byte)(c.Length >> 8));
        s.Write(c, 0, c.Length);
    }

    /// <summary>
    /// raw deflate（无 zlib/gzip 包装），压缩级别 9 —— 与游戏原生一致。
    /// ★ 实测（2026-10-15）：level 9 / memLevel 8 / wbits -15，原生 zlib 对 96/96 块 bit-identical 复现；
    ///   .NET DeflateStream(SmallestSize) 仅 94/96（等价 memLevel 8 但实现与 C zlib 有差异）。
    ///   故改走 NativeZlib（zlib1.dll P/Invoke）。
    /// </summary>
    static byte[] Deflate9(byte[] data) => NativeZlib.RawDeflate9(data);

    static byte[] Inflate(byte[] comp)
    {
        using var ms = new MemoryStream(comp);
        using var df = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        df.CopyTo(outMs);
        return outMs.ToArray();
    }

    static void Write(MemoryStream ms, byte[] b) => ms.Write(b, 0, b.Length);
}
