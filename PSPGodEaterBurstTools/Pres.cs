using System.Text;

namespace PSPGodEaterBurstTools;

/// <summary>TOC 里的一条记录（32 字节）</summary>
public class TocEntry
{
    public int Group;
    public int Index;
    public long TocPos;
    public long Offset;
    public long Size;
    public long NameOffset;
    public long ChunkName;
    public List<string> ElementName = new();
}

/// <summary>
/// GEB 系 PRES(.res) 格式。
/// 物理布局：0x00 头(0x50) + 组表 8×8 + TOC(n×32) +
///           按 TOC 顺序逐条 [16 对齐数据][16 对齐名字区]，末尾 16 对齐补 0。
/// </summary>
public static class Pres
{
    /// <summary>rdp 块大小指数：本体 15（0x8000），DLC 数据包（-xdlc/-cdlc）4（0x10）。
    ///   必须是【可变字段】—— const 会在编译期把 BlockSize 内联进所有调用点，运行时切不过去。</summary>
    public static int RdpShift = 15;
    public static int BlockSize => 1 << RdpShift;
    public static readonly int[] SlotOrder = { 2, 4, 5, 6, 7, 1, 0 };
    public static readonly HashSet<int> InRes = new() { 2, 5, 6 };

    /// <summary>
    /// Sign==1（本 res 不引用 package.rdp）时，以下槽位退化为 res 内的「逻辑路径声明」：
    /// offset 是【文件内偏移】、size 恒 0、数据是 \0 终止的文本，和 G2/G5/G6 一样排在 body 里。
    /// 实测（GEB 全树 4460 个 Sign=1 的 res）：prx(G1) 50 条、refer(G4) 271 条，
    /// 全部 size==0 且 offset 落在文件内；res(G0)/rtbl(G7) 条目数为 0，故未纳入。
    ///   · prx   例：MS_001N_tora.res 的 G1[0] = "creature_vajra.bin"
    ///   · refer 例：NP_001_AP001.res 的 G4[0] = "data/GameData/.../NP_001_AP001.scr"
    /// </summary>
    public static readonly HashSet<int> Sign1TextSlots = new() { 1, 4 };
    public static readonly Dictionary<int, string> SlotName = new()
    {
        { 0, "res" }, { 1, "prx" }, { 2, "local" }, { 3, "" },
        { 4, "refer" }, { 5, "table" }, { 6, "string" }, { 7, "rtbl" },
    };

    public static uint U32(byte[] b, long o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    public static int U16(byte[] b, long o) => b[o] | (b[o + 1] << 8);
    public static long Align16(long x) => (x + 15) & ~15L;

    public static string CStr(byte[] b, long o)
    {
        if (o < 0 || o >= b.Length) return "";
        long e = o;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.UTF8.GetString(b, (int)o, (int)(e - o));
    }

    public static bool IsGzip(byte[] b) => b.Length >= 3 && b[0] == 0x1F && b[1] == 0x8B && b[2] == 0x08;

    public static bool IsPres(byte[] b) =>
        b != null && b.Length >= 4 && b[0] == (byte)'P' && b[1] == (byte)'r' && b[2] == (byte)'e' && b[3] == (byte)'s';

    /// <summary>只读文件头 4 字节判断是否 Pres（避免整份读入）</summary>
    public static bool IsPresFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> h = stackalloc byte[4];
            return fs.ReadAtLeast(h, 4, false) == 4
                   && h[0] == (byte)'P' && h[1] == (byte)'r' && h[2] == (byte)'e' && h[3] == (byte)'s';
        }
        catch { return false; }
    }

    /// <summary>解析 TOC → 组表 + 条目列表</summary>
    public static (long[,] groups, List<TocEntry> entries) ParseToc(byte[] d)
    {
        long go = U32(d, 4);
        var groups = new long[8, 2];
        for (int i = 0; i < 8; i++)
        {
            groups[i, 0] = U32(d, go + i * 8);
            groups[i, 1] = U32(d, go + i * 8 + 4);
        }
        var list = new List<TocEntry>();
        for (int g = 0; g < 8; g++)
        {
            long off = groups[g, 0], cnt = groups[g, 1];
            for (long k = 0; k < cnt; k++)
            {
                long p = off + k * 32;
                if (p + 32 > d.Length) break;
                var e = new TocEntry
                {
                    Group = g, Index = (int)k, TocPos = p,
                    Offset = U32(d, p), Size = U32(d, p + 4),
                    NameOffset = U32(d, p + 8), ChunkName = U32(d, p + 0xC),
                };
                if (e.ChunkName > 0 && e.NameOffset > 0 && e.NameOffset + e.ChunkName * 4 <= d.Length)
                {
                    for (int j = 0; j < e.ChunkName; j++)
                    {
                        long sp = U32(d, e.NameOffset + j * 4);
                        e.ElementName.Add(sp < d.Length ? CStr(d, sp) : "");
                    }
                }
                list.Add(e);
            }
        }
        return (groups, list);
    }

    /// <summary>由 ElementName 还原文件名：E0 + (E1 非空 ? ".E1" : "")</summary>
    public static string NameOf(List<string> els)
    {
        if (els == null || els.Count == 0) return "";
        string basis = els[0];
        string ext = els.Count > 1 ? els[1] : "";
        return string.IsNullOrEmpty(ext) ? basis : basis + "." + ext;
    }

    public delegate byte[] DataReader(ResEntry item, int group, int gindex);

    /// <summary>rdp 侧条目在【新 rdp】上的位置：块号 + 该条目在 rdp 上占的字节数。未知返回 (-1,-1)</summary>
    public delegate (long block, long size) RdpLocator(int group, int gindex);

    /// <summary>由 list + 数据源重建 .res。res 内条目(G2/G5/G6)读数据，rdp 侧条目(G0/G1/G4/G7)查块位表。</summary>
    public static byte[] Build(ResList js, DataReader readData, RdpLocator locateRdp)
    {
        var byGroup = new SortedDictionary<int, List<(ResEntry it, int gi)>>();
        foreach (var it in js.Files)
        {
            int g = int.Parse(it.Group);
            if (!byGroup.TryGetValue(g, out var l)) byGroup[g] = l = new List<(ResEntry, int)>();
            l.Add((it, l.Count));
        }

        long grupOff = 0x10, tocStart = grupOff + 64;
        var gstart = new Dictionary<int, long>();
        long pos = tocStart;
        foreach (var g in SlotOrder)
        {
            if (!byGroup.ContainsKey(g)) continue;
            gstart[g] = pos;
            pos += byGroup[g].Count * 32L;
        }
        long tocEnd = pos, baseOff = tocEnd;

        var body = new MemoryStream();
        var recs = new List<(int g, int gi, long off, long size, long nameOff, long cn)>();

        foreach (var g in SlotOrder)
        {
            if (!byGroup.ContainsKey(g)) continue;
            foreach (var (it, gi) in byGroup[g])
            {
                bool isDummy = it.FileName == "dummy" || it.ElementName == null;
                long offset, size, cn;
                if (isDummy)
                {
                    offset = 0;
                    size = it.DummySize ?? 0;
                    cn = 0;
                }
                else
                {
                    // Sign==1 的 res 不引用 package.rdp：prx(G1)/refer(G4) 的 offset 是文件内偏移、
                    // size 恒为 0，数据是 \0 终止的文本（与 G2/G5/G6 一样排在 body 里）。
                    bool asText = js.Sign == 1 && Sign1TextSlots.Contains(g);
                    if (InRes.Contains(g) || asText)
                    {
                        byte[] data = readData(it, g, gi);
                        Pad16(body);
                        offset = baseOff + body.Length;
                        if (g == 6)                                  // string：补结尾 0x00；空串 size=0 不写任何字节
                        {
                            if (data.Length > 0) { body.Write(data); body.WriteByte(0); size = data.Length + 1; }
                            else size = 0;
                        }
                        else if (g == 5)                             // table：size = u32 个数
                        {
                            if (data.Length % 4 != 0)
                                throw new InvalidDataException($"table 数据长度须为 4 的倍数: {it.FileName} ({data.Length})");
                            body.Write(data); size = data.Length / 4;
                        }
                        else if (asText)                             // prx/refer 文本：补 \0，size 恒 0（长度靠 \0 终止）
                        {
                            body.Write(data); body.WriteByte(0); size = 0;
                        }
                        else { body.Write(data); size = data.Length; }
                    }
                    else                                             // 数据在 package.rdp：块位/大小来自 rdp 打包结果
                    {
                        var (nb, ns) = locateRdp(g, gi);
                        if (nb < 0 || ns < 0)
                            throw new InvalidDataException(
                                $"rdp 定位失败：{it.FileName}（组 {g} / 组内索引 {gi}）—— package.json 里缺少对它的引用记录");
                        offset = nb;
                        size = ns;
                    }
                    cn = it.ElementName.Count;
                }

                long nameOffset = 0;
                if (cn > 0)
                {
                    Pad16(body);
                    nameOffset = baseOff + body.Length;
                    long p = nameOffset + cn * 4;
                    var ptrs = new List<long>((int)cn);
                    foreach (var s in it.ElementName) { ptrs.Add(p); p += Encoding.UTF8.GetByteCount(s) + 1; }
                    foreach (var pv in ptrs) WriteU32(body, (uint)pv);
                    foreach (var s in it.ElementName) { body.Write(Encoding.UTF8.GetBytes(s)); body.WriteByte(0); }
                }
                recs.Add((g, gi, offset, size, nameOffset, cn));
            }
        }

        byte[] toc = new byte[tocEnd - tocStart];
        foreach (var r in recs)
        {
            long p = (gstart[r.g] - tocStart) + r.gi * 32L;
            WriteU32At(toc, p, (uint)r.off);
            WriteU32At(toc, p + 4, (uint)r.size);
            WriteU32At(toc, p + 8, (uint)r.nameOff);
            WriteU32At(toc, p + 0xC, (uint)r.cn);
        }

        byte[] head = new byte[0x50];
        WriteU32At(head, 0, (uint)js.Magic);
        WriteU32At(head, 4, (uint)grupOff);
        head[8] = (byte)js.Format;
        head[9] = (byte)js.Version;
        head[0x0A] = (byte)(js.Package & 0xFF);
        head[0x0B] = (byte)((js.Package >> 8) & 0xFF);
        int flag = js.Sign & 3;
        WriteU32At(head, 0x0C, (uint)flag);          // 高 30 位先清零，最后回填 ceckSum
        for (int g = 0; g < 8; g++)
        {
            long cnt = byGroup.TryGetValue(g, out var l2) ? l2.Count : 0;
            WriteU32At(head, grupOff + g * 8, cnt > 0 ? (uint)gstart[g] : 0);
            WriteU32At(head, grupOff + g * 8 + 4, (uint)cnt);
        }

        Pad16(body);
        var outBuf = new MemoryStream();
        outBuf.Write(head); outBuf.Write(toc); body.Position = 0; body.CopyTo(outBuf);
        byte[] result = outBuf.ToArray();

        // ceckSum：整份文件滚动 ck = b ^ t; t = b + ck，最后 (ck<<2)|flag 写回 0x0C
        uint t = 0, c = 0;
        foreach (byte b in result)
        {
            c = (uint)(b ^ t);
            t = (uint)(b + c);
        }
        uint final = ((c << 2) | (uint)flag) & 0xFFFFFFFFu;
        WriteU32At(result, 0x0C, final);
        return result;
    }

    // ---------------------------------------------------------- helpers
    static void Pad16(MemoryStream ms) { while (ms.Length % 16 != 0) ms.WriteByte(0); }
    static void WriteU32(Stream s, uint v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }
    public static void WriteU32At(byte[] b, long o, uint v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
