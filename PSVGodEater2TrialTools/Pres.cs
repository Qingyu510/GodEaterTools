using System.Text;

namespace PSVGodEater2TrialTools;

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
/// GE2 体验版 PRES(.res) 格式。
/// 物理布局：固定头 0x10 + 新增区 0x10（0x10 处 = res 包大小）+ 组表 8×8(@0x20) +
///           TOC(n×32, @0x60) + 按 TOC 顺序逐条 [16 对齐数据][16 对齐名字区]（16 对齐收尾）+
///           【Local 区】—— local(G2) 数据整体排在 res 包之后（起点 = res 包大小）。
/// 0x0C 仅低 2 位 Sign（CeckSum 已废弃，不计算、不回填）。
/// PSV 另有两条「不在 rdp 内」的条目形态：EXCLUDE_RDP（区块值 -1、size 照常）与 G1 self（offset/size 恒 0），
///   见 IsExcludeRdp / IsSelfDecl（样本 god_m002_Movie.res 实测）。
/// </summary>
public static class Pres
{
    public const int RdpShift = 11;      // ★ 本工具专用：rdp 块粒度 0x800（GECV/GEB 系为 0x8000 = 1<<15）
    public const int BlockSize = 1 << RdpShift;
    public static readonly int[] SlotOrder = { 2, 4, 5, 6, 7, 1, 0 };
    /// <summary>数据存于本 res 内的组：local(2) / table(5) / string(6)。
    ///   GE2 体验版：local 的数据在【res 包之后（文件尾）】，table/string 仍在 body。</summary>
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

    // ------------------------------------------------ PSV 差异（样本 god_m002_Movie.res 实测）
    /// <summary>★ PSV：`ElementName[3] == "EXCLUDE_RDP"` ⇒ 该文件被排除在 rdp 之外
    ///   （真实数据在本地路径，见 ElementName[4] 的 `PATH=...`）。
    ///   表现：TOC 的区块值 = **-1**（0xFFFFFFFF），size **照常记录**真实文件大小。
    ///   实测样本：G4 `['ge2_trial_op','mp4','','EXCLUDE_RDP','PATH=data/GameData/movie/OP_ED/ge2_trial_op.mp4']`
    ///   → offset=0xFFFFFFFF、size=100380541。</summary>
    public const string ExcludeRdp = "EXCLUDE_RDP";

    public static bool IsExcludeRdp(List<string> els) =>
        els != null && els.Count >= 4 && els[3] == ExcludeRdp;

    /// <summary>★ PSV：G1 由 PSP 的 `prx` 变为 `self`。self 不存在于 rdp 包内（走本地路径），
    ///   因此该条目【只记录 ElementName】，offset / size 恒 0。
    ///   实测样本：G1 `['god_m002_Movie','self','']` → offset=0、size=0。</summary>
    public static bool IsSelfDecl(int g, List<string> els) =>
        g == 1 && els != null && els.Count >= 2 && els[1] == "self";

    static long ParseSize(string s) => long.TryParse(s, out var v) ? v : 0;

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

    /// <summary>rtbl 条目的槽位数（= 重建后 rtbl 文件字节数 ÷ 0x80）。
    ///   用于把它写回 res 时，在名字区之后生成对应长度的「槽位 bit 表」。未知返回 -1。</summary>
    public delegate int RtblSlotsProvider(string exportPath);

    /// <summary>由 list + 数据源重建 .res。res 内条目(G2/G5/G6)读数据，rdp 侧条目(G0/G1/G4/G7)查块位表。</summary>
    public static byte[] Build(ResList js, DataReader readData, RdpLocator locateRdp,
                              RtblSlotsProvider rtblSlots = null)
    {
        var byGroup = new SortedDictionary<int, List<(ResEntry it, int gi)>>();
        foreach (var it in js.Files)
        {
            int g = int.Parse(it.Group);
            if (!byGroup.TryGetValue(g, out var l)) byGroup[g] = l = new List<(ResEntry, int)>();
            l.Add((it, l.Count));
        }

        long grupOff = 0x20, tocStart = grupOff + 64;      // ★ GE2 体验版：组表偏移 0x20，TOC 从 0x60 起
        var gstart = new Dictionary<int, long>();
        long pos = tocStart;
        // ★ 组在 TOC / body 里的物理顺序：以 list 的 Groups 为准（实测并非恒按组号升序：
        //   god_menu.res = 2,4,5,6,7,1,0 而 Field.res = 2,5,4），缺失 / 为空时退回默认顺序。
        IReadOnlyList<int> order = js.Groups != null && js.Groups.Count > 0
                                 ? js.Groups : (IReadOnlyList<int>)SlotOrder;
        foreach (var g in order)
        {
            if (!byGroup.ContainsKey(g)) continue;
            gstart[g] = pos;
            pos += byGroup[g].Count * 32L;
        }
        long tocEnd = pos, baseOff = tocEnd;

        var body = new MemoryStream();
        var recs = new List<(int g, int gi, long off, long size, long nameOff, long cn)>();
        // ★ GE2 体验版：local(G2) 数据不在 body，先攒起来，等 body 定长后写到整个 res 包之后
        var localPend = new List<(int gi, byte[] data)>();

        foreach (var g in order)
        {
            if (!byGroup.ContainsKey(g)) continue;
            foreach (var (it, gi) in byGroup[g])
            {
                bool isDummy = it.FileName == "dummy" || it.ElementName == null;
                bool isExcl = !isDummy && IsExcludeRdp(it.ElementName);             // ★ PSV：排除在 rdp 外
                bool isSelf = !isDummy && !isExcl && IsSelfDecl(g, it.ElementName); // ★ PSV：G1 self 声明
                long offset, size, cn;
                if (isDummy)
                {
                    offset = 0;
                    size = it.DummySize ?? 0;
                    cn = 0;
                }
                else if (isExcl)
                {
                    // ★ PSV EXCLUDE_RDP：文件不在 rdp 内 ⇒ 区块值 -1，size 按 list 写好的值回填
                    offset = 0xFFFFFFFFL;
                    size = ParseSize(it.Size);
                    cn = it.ElementName.Count;
                }
                else if (isSelf)
                {
                    // ★ PSV G1 self：不在 rdp 内、仅记录 ElementName ⇒ offset / size 恒 0
                    offset = 0;
                    size = 0;
                    cn = it.ElementName.Count;
                }
                else
                {
                    // Sign==1 的 res 不引用 package.rdp：prx(G1)/refer(G4) 的 offset 是文件内偏移、
                    // size 恒为 0，数据是 \0 终止的文本（与 G2/G5/G6 一样排在 body 里）。
                    bool asText = js.Sign == 1 && Sign1TextSlots.Contains(g);
                    if (InRes.Contains(g) || asText)
                    {
                        byte[] data = readData(it, g, gi);
                        if (g == 2 && !asText)
                        {
                            // ★ GE2 体验版：local(G2) 数据不在 body，改写到整个 res 包之后（文件尾）。
                            //   body 里只留名字区；offset 等 body 定长（= res 包大小）后再回填。
                            localPend.Add((gi, data));
                            offset = 0;
                            size = data.Length;
                        }
                        else
                        {
                            Pad16(body);
                            offset = baseOff + body.Length;
                            if (g == 6)                              // string：补结尾 0x00；空串 size=0 不写任何字节
                            {
                                if (data.Length > 0) { body.Write(data); body.WriteByte(0); size = data.Length + 1; }
                                else size = 0;
                            }
                            else if (g == 5)                         // table：size = u32 个数
                            {
                                if (data.Length % 4 != 0)
                                    throw new InvalidDataException($"table 数据长度须为 4 的倍数: {it.FileName} ({data.Length})");
                                body.Write(data); size = data.Length / 4;
                            }
                            else if (asText)                         // prx/refer 文本：补 \0，size 恒 0（长度靠 \0 终止）
                            {
                                body.Write(data); body.WriteByte(0); size = 0;
                            }
                            else { body.Write(data); size = data.Length; }
                        }
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

                // ★ rtbl(组 7) 条目的名字区固定只写【前 4 项】—— 第 5 项是「槽位 bit 表」，
                //   它不作为字符串写进 res，而是紧跟在名字区之后的一段二进制（见下）。
                int nName = it.ElementName?.Count ?? 0;
                if (rtblSlots != null && Rtbl.IsPath(it.ExportPath) && nName > 4) nName = 4;
                cn = nName;

                long nameOffset = 0;
                if (cn > 0)
                {
                    Pad16(body);
                    long nameRel = body.Length;                          // 名字区在 body 内的起点
                    nameOffset = baseOff + nameRel;
                    long p = nameOffset + cn * 4;
                    var ptrs = new List<long>((int)cn);
                    for (int j = 0; j < cn; j++) { ptrs.Add(p); p += Encoding.UTF8.GetByteCount(it.ElementName[j]) + 1; }
                    foreach (var pv in ptrs) WriteU32(body, (uint)pv);
                    for (int j = 0; j < cn; j++) { body.Write(Encoding.UTF8.GetBytes(it.ElementName[j])); body.WriteByte(0); }
                    long nl = body.Length - nameRel;

                    // ★ rtbl：名字区之后紧跟「槽位 bit 表」—— 每槽 1 bit（bit=1 表示该槽有引用），
                    //   长度 = ceil(槽位数 ÷ 8)，位序 LSB first（第 s 槽 = byte[s/8] 的 bit(s%8)）；
                    //   整块（名字区 + bit 表）再补齐到 al16(nl + c8 + 1) —— 至少留 1 字节，再 16 对齐。
                    if (rtblSlots != null && Rtbl.IsPath(it.ExportPath))
                    {
                        int slots = rtblSlots(it.ExportPath);
                        if (slots <= 0)
                            throw new InvalidDataException($"rtbl 槽位数为 0：{it.ExportPath}");
                        int c8 = (slots + 7) / 8;
                        var bits = ParseBits(it.ElementName, 4, slots);   // ★ 第 5 项 = ElementName[4]
                        var bitBuf = new byte[c8];
                        for (int s = 0; s < slots; s++)
                            if (bits[s]) bitBuf[s >> 3] |= (byte)(1 << (s & 7));
                        body.Write(bitBuf, 0, c8);
                        long target = nameRel + Align16(nl + c8 + 1);
                        while (body.Length < target) body.WriteByte(0);
                    }
                }
                recs.Add((g, gi, offset, size, nameOffset, cn));
            }
        }

        Pad16(body);
        long pkgSize = baseOff + body.Length;          // ★ res 包大小（不含 Local 数据）

        // ★ Local 区：紧跟整个 res 包之后，16 对齐首尾相接（GE2 体验版把 local 搬到文件尾）
        var localMs = new MemoryStream();
        var localOff = new Dictionary<int, long>();
        long lp = pkgSize;
        foreach (var (gi, data) in localPend)
        {
            lp = Align16(lp);
            long need = lp - (pkgSize + localMs.Length);
            if (need > 0) localMs.Write(new byte[need], 0, (int)need);
            localOff[gi] = lp;
            localMs.Write(data, 0, data.Length);
            lp += data.Length;
        }
        {
            long need = Align16(lp) - (pkgSize + localMs.Length);
            if (need > 0) localMs.Write(new byte[need], 0, (int)need);
        }

        byte[] toc = new byte[tocEnd - tocStart];
        foreach (var r in recs)
        {
            long p = (gstart[r.g] - tocStart) + r.gi * 32L;
            long off = r.g == 2 && localOff.TryGetValue(r.gi, out var lo) ? lo : r.off;   // local 的 offset 在包后
            WriteU32At(toc, p, (uint)off);
            WriteU32At(toc, p + 4, (uint)r.size);
            WriteU32At(toc, p + 8, (uint)r.nameOff);
            WriteU32At(toc, p + 0xC, (uint)r.cn);
        }

        byte[] head = new byte[0x60];
        WriteU32At(head, 0, (uint)js.Magic);
        WriteU32At(head, 4, (uint)grupOff);
        head[8] = (byte)js.Format;
        head[9] = (byte)js.Version;
        head[0x0A] = (byte)(js.Package & 0xFF);
        head[0x0B] = (byte)((js.Package >> 8) & 0xFF);
        WriteU32At(head, 0x0C, (uint)(js.Sign & 3));  // ★ 仅低 2 位 Sign（CeckSum 已废弃，不再计算）
        WriteU32At(head, 0x10, (uint)pkgSize);        // ★ res 包大小（不含 Local 数据）
        for (int g = 0; g < 8; g++)
        {
            long cnt = byGroup.TryGetValue(g, out var l2) ? l2.Count : 0;
            WriteU32At(head, grupOff + g * 8, cnt > 0 ? (uint)gstart[g] : 0);
            WriteU32At(head, grupOff + g * 8 + 4, (uint)cnt);
        }

        var outBuf = new MemoryStream();
        outBuf.Write(head); outBuf.Write(toc); body.Position = 0; body.CopyTo(outBuf);
        localMs.Position = 0; localMs.CopyTo(outBuf);
        return outBuf.ToArray();
    }

    // ---------------------------------------------------------- helpers
    /// <summary>把 ElementName 第 idx 项里的位串解析成 bool[slots]（rtbl 的槽位 bit 表用）。
    ///   位串形如 "1,0,1,…" —— 逗号只为可读性，任何非 '0'/'1' 字符一律忽略；
    ///   缺失 / 不足 slots 的位按 false（0）补齐 ⇒「rtbl 变大后多出来的槽默认为 0」。</summary>
    static bool[] ParseBits(List<string> els, int idx, int slots)
    {
        var r = new bool[slots];
        if (els == null || els.Count <= idx || slots <= 0) return r;
        string s = els[idx] ?? "";
        int n = 0;
        foreach (char ch in s)
        {
            if (ch == '1') { if (n < slots) r[n] = true; n++; }
            else if (ch == '0') n++;
        }
        return r;
    }

    static void Pad16(MemoryStream ms) { while (ms.Length % 16 != 0) ms.WriteByte(0); }
    static void WriteU32(Stream s, uint v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }
    public static void WriteU32At(byte[] b, long o, uint v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
