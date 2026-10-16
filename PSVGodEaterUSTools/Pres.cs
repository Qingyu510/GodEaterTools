using System.Text;

namespace PSVGodEaterUSTools;

/// <summary>TOC 里的一条记录（32 字节）</summary>
public class TocEntry
{
    public int Group;
    public int Index;
    public long TocPos;
    public long Offset;
    /// <summary>offset 高 4 位 = 数据来源类型：0xC=本 res 文件内 / 0x4=package.rdp / 0x5=data.rdp / 0=无(体验版/空)</summary>
    public int OffsetType;
    public long Size;
    /// <summary>+0x1C：解密（解压）后大小（GE2 正式版新增字段；体验版恒 0）</summary>
    public long DecSize;
    public long NameOffset;
    public long ChunkName;
    public List<string> ElementName = new();
}

/// <summary>
/// 更新补丁的 res「本体 res 指针」——头部 +0x14（来源，offset 高 4 位=类型 / 低 28 位=块号）
/// 与 +0x18（本体 res 字节数）这 8 字节。全 0 = 本体即自身。
/// 有值时：该 res 的【local(G2) 组 C/D 条目】不在自身文件内，而是索引到本体 res 的对应偏移
///   （实测补丁 god_menu.res：G2 的 D 条目偏移 0x1DD00~0x264E0 与本体 god_menu.res 完全一致，
///     且超出补丁自身 pkgSize 0x242C0 —— 数据只能在本体里）；
///   而 G5(table)/G6(string) 的 C/D 条目偏移两者不同，仍在补丁 res 自己的 body 里。
/// 因此「有指针 ⇒ 只有 local 组改指向本体」，其余组不受影响。
/// </summary>
public readonly struct BaseRef
{
    public readonly int Type;      // offset 高 4 位：4=package.rdp / 5=data.rdp / 6=patch.rdp
    public readonly long Offset;   // 低 28 位（4/5/6 时 = 块号）
    public readonly long Size;     // +0x18：本体 res 字节数
    public bool Present => Type != 0 || Offset != 0 || Size != 0;
    public BaseRef(int t, long off, long sz) { Type = t; Offset = off; Size = sz; }
}

/// <summary>
/// GE2 体验版 PRES(.res) 格式。
/// 物理布局：固定头 0x10 + 新增区 0x10（0x10 处 = res 包大小）+ 组表 8×8(@0x20) +
///           TOC(n×32, @0x60) + 按 TOC 顺序逐条 [16 对齐数据][16 对齐名字区]（16 对齐收尾）+
///           【Local 区】—— local(G2) 数据整体排在 res 包之后（起点 = res 包大小）。
/// 0x0C 仅低 2 位 Sign（CeckSum 已废弃，不计算、不回填）。
/// </summary>
public static class Pres
{
    public const int RdpShift = 11;      // ★ 本工具专用：rdp 块粒度 0x800（GECV/GEB 系为 0x8000 = 1<<15）
    public const int BlockSize = 1 << RdpShift;

    /// <summary>offset 高 4 位的来源类型（GE2 正式版 ver≥3 起启用）：
    ///   0xC —— 数据在本 res 文件内（低 28 位 = 文件内绝对偏移）；local(G2) 排在 res 包之后、table(G5) 在 body 里。
    ///          宿主是【system.res 本身】或【data.rdp 里的 res】时用 C（tbl/string 必定是 C）
    ///   0xD —— 语义与 0xC 完全相同（数据同样在本 res 文件内、低 28 位 = 文件内绝对偏移），
    ///          只是宿主 res 本身位于 package.rdp 时用它（如 god_menu.res 的 local 区）
    ///   0x4 —— 数据在 package.rdp（低 28 位 = 块号）
    ///   0x5 —— 数据在 data.rdp（低 28 位 = 块号，双 rdp 变革后新增）
    ///   0   —— 无类型（dummy 条目 / 体验版 ver2 全体条目）
    /// 约束：tbl / string 恒为 C；local(G2) 可为 C / D / 4 / 5；res / prx / refer / rtbl 只出 4 / 5</summary>
    public const int TypeRes = 0xC;
    public const int TypeResInPkg = 0xD;
    public const int TypePackage = 0x4;
    public const int TypeData = 0x5;
    /// <summary>0x6 —— 数据在【更新补丁 patch.rdp】上（低 28 位 = 块号）。
    ///   仅出现在更新补丁的 res 里（system_update.res 与它引用的补丁版 res）；
    ///   本体 package.rdp / data.rdp 的 res 不会出现此类型。</summary>
    public const int TypePatch = 0x6;
    /// <summary>0x3 —— EXCLUDE_RDP：文件【不在任何 rdp 内】，游戏按 ElementName[4] 的 "PATH=..." 从本地路径取。
    ///   实测（PSV god_m002_Movie.res 组 4 refer 共 19 条）：类型位 = 3、低 28 位 = 0（即 0x30000000），
    ///   而 +0x04 字节数与 +0x1C 解压大小【照常保留真值】—— 这点与 G1 self 的三项全 0 不同。</summary>
    public const int TypeExcludeRdp = 0x3;
    public static long Tag(int type, long v) => ((long)type << 28) | (v & 0x0FFFFFFFL);
    public static long Untag(long raw) => raw & 0x0FFFFFFFL;
    public static int TagOf(long raw) => (int)(raw >> 28);

    /// <summary>类型数值 → list 里的 Type 字符串（大写十六进制，不带 0x）：0xC→"C" / 0xD→"D" / 4→"4" / 5→"5" / 0→"0"</summary>
    public static string TypeStr(int t) => t.ToString("X");

    /// <summary>该来源类型的数据是否写在【本 res 文件内】（C=宿主在 system.res / data.rdp 的 res，D=宿主在 package.rdp 的 res）</summary>
    public static bool IsRes(int t) => t == TypeRes || t == TypeResInPkg;

    /// <summary>list 里的 Type 字符串 → 类型数值；空 / 非法返回 fallback</summary>
    public static int ParseType(string s, int fallback = 0) =>
        !string.IsNullOrEmpty(s)
        && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : fallback;

    /// <summary>该来源类型的数据是否落在 rdp（package.rdp = 4 / data.rdp = 5 / patch.rdp = 6）上</summary>
    public static bool IsRdp(int t) => t == TypePackage || t == TypeData || t == TypePatch;

    /// <summary>该来源类型是否落在【更新补丁 patch.rdp】（Type 6）上</summary>
    public static bool IsPatch(int t) => t == TypePatch;

    /// <summary>解析 res 头部 +0x14 / +0x18 的「本体 res 指针」</summary>
    public static BaseRef ReadBaseRef(byte[] d)
    {
        uint raw = U32(d, 0x14);
        return new BaseRef((int)(raw >> 28), raw & 0x0FFFFFFFL, U32(d, 0x18));
    }

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
    /// <summary>组号 → 槽位名（★ PSV：G1 由 PSP 的 prx 变为 self）。</summary>
    public static readonly Dictionary<int, string> SlotName = new()
    {
        { 0, "res" }, { 1, "self" }, { 2, "local" }, { 3, "" },
        { 4, "refer" }, { 5, "table" }, { 6, "string" }, { 7, "rtbl" },
    };

    /// <summary>★ PSV：G1（self）的声明条目 —— 该文件不在任何 rdp 内，游戏按本地路径取，
    ///   TOC 的 offset / size / +0x1C 解压大小三项恒 0。
    ///   实测样本 god_m002_Movie.res：G1[0] = ['god_m002_Movie','self','']，TOC 三项全 0。</summary>
    public static bool IsSelfDecl(int g, List<string> els) =>
        g == 1 && els != null && els.Count >= 2 && els[1] == "self";

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

    /// <summary>
    /// 本工具处理之包的 res 压缩头：4 字节魔数 "dflt" + raw deflate 流（无 zlib / gzip 包装）。
    /// 实测（2026-10-05，package.rdp 4,030,464 B / system.res 1,262,240 B 的包）：
    ///   god_menu.res 段 = 134,193 B，从偏移 4 起用 raw deflate 解压得 242,912 B 的完整 PRES
    ///   （头 `Pres` + groupOffset 0x10，Sign=3），deflate 流正好用尽整段 ⇒ 头仅 4 字节、无尾部字段。
    /// 该包 rdp 侧 58 条中 dflt 仅 1 条（god_menu.res），而 gzip 为 0 条 ⇒ 本包以 dflt 取代 gzip。
    /// </summary>
    public static readonly byte[] DfltMagic = { (byte)'d', (byte)'f', (byte)'l', (byte)'t' };

    public static bool IsDflt(byte[] b) =>
        b != null && b.Length >= 5 &&
        b[0] == (byte)'d' && b[1] == (byte)'f' && b[2] == (byte)'l' && b[3] == (byte)'t';

    /// <summary>GE2 Rage Burst 系的压缩段 blz4（魔数 "blz4" 小写，u32 读作 0x347A6C62）—— 见 Blz4.cs</summary>
    public static bool IsBlz4(byte[] b) => Blz4.IsBlz4(b);

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

    // ============================================================ ★ US 美版：多语言分区
    /// <summary>语言分区的固定顺序（+0x1C ≥ 2 时按此顺序写分区表 / 落解包目录）。</summary>
    public static readonly string[] LangOrder = { "en", "fr", "it", "de", "es", "ru" };

    /// <summary>头部 +0x1C：语言分区数。
    ///   0 = 无该字段（日版）；1 = 单分区（美版，此时 +0x04 的组偏移已被废弃置 0）；≥2 = 分区表在 0x20。</summary>
    public static int LangCount(byte[] d) => (int)U32(d, 0x1C);

    /// <summary>分区序号 → 目录名（en/fr/it/de/es/ru），超出范围用 L&lt;n&gt;。</summary>
    public static string LangName(int i) => i >= 0 && i < LangOrder.Length ? LangOrder[i] : "L" + i;

    /// <summary>分区表：(组表绝对偏移, 分区字节长)。
    ///   · +0x1C ≥ 2 → @0x20 的 N × 8 字节（u32 offset + u32 length），首项即分区 0 的组表位置；
    ///   · 否则单分区 = [组表起点, res 包大小)，组表起点 = +0x04（美版废弃置 0 时退回 0x20）。
    /// 实测：US system.res 6 分区 @0x50/0x58E0/…；1.res 6 分区 @0x50/0x1590/…；2.res 单分区 @0x20。</summary>
    public static List<(long Off, long Len)> Partitions(byte[] d)
    {
        int lc = LangCount(d);
        uint pkg = U32(d, 0x10);
        var r = new List<(long, long)>();
        if (lc >= 2)
        {
            for (int i = 0; i < lc; i++)
                r.Add(((long)U32(d, 0x20 + i * 8), (long)U32(d, 0x24 + i * 8)));
            return r;
        }
        long go = U32(d, 4);
        if (go == 0) go = 0x20;
        r.Add((go, pkg > go ? pkg - go : 0));
        return r;
    }

    /// <summary>解析 TOC → 组表 + 条目列表</summary>
    public static (long[,] groups, List<TocEntry> entries) ParseToc(byte[] d)
    {
        long go = U32(d, 4);
        // ★ 美版（+0x1C 有语言分区数）：+0x04 的组偏移被废弃置 0 —— 单分区时组表固定 0x20。
        if (go == 0) go = 0x20;
        return ParseToc(d, go);
    }

    /// <summary>按指定组表偏移解析（多语言分区逐分区调用，分区表见 Partitions）</summary>
    public static (long[,] groups, List<TocEntry> entries) ParseToc(byte[] d, long go)
    {
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
                long rawOff = U32(d, p);
                var e = new TocEntry
                {
                    Group = g, Index = (int)k, TocPos = p,
                    Offset = rawOff & 0x0FFFFFFFL,          // ★ 低 28 位才是真实值（高 4 位是来源类型）
                    OffsetType = (int)(rawOff >> 28),
                    Size = U32(d, p + 4),
                    DecSize = U32(d, p + 0x1C),
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

    /// <summary>rdp 侧条目在【新 rdp】上的位置：来源类型（TypePackage / TypeData）+ 块号 + 该条目在 rdp 上占的字节数。
    ///   未知返回 (-1,-1,-1)</summary>
    public delegate (int type, long block, long size) RdpLocator(int group, int gindex);

    /// <summary>★ US 美版多语言分区版 rdp 定位：多一个 lang 维度（0 = en）。
    ///   六分区可能为【同一 (组, 组内序号) 槽位】各调一个不同的 rdp 块 —— 实测 1.res 的 G2[40]
    ///   Monitor_01 六分区分别占 4650 / 5189 / 5196 / 5202 / 5209 / 5216 块，故 key 必须能区分分区，
    ///   否则六条 ref 会互相覆盖，六个分区全指到同一块（实测就是这样打错的）。</summary>
    public delegate (int type, long block, long size) RdpLocatorL(int lang, int group, int gindex);

    /// <summary>★ 多语言分区版「还原后大小」：多一个 lang 维度（0 = en）。</summary>
    public delegate long? DecLenProviderL(int lang, int group, int gindex, ResEntry it);

    /// <summary>rtbl 条目的槽位数（= 重建后 rtbl 文件字节数 ÷ 0x200）。
    ///   用于把它写回 res 时，在名字区之后生成对应长度的「槽位 bit 表」。未知返回 -1。</summary>
    public delegate int RtblSlotsProvider(string exportPath);

    /// <summary>条目【还原后（解压后）的大小】—— 覆盖 list 里记录的 decsize。
    ///   编辑过压缩文件后明文长度会变，TOC +0x1C 必须跟着改，否则游戏分配的空间不对。
    ///   返回 null 表示「沿用 list 的 decsize」。</summary>
    public delegate long? DecLenProvider(int group, int gindex, ResEntry it);

    /// <summary>把 res 【内部】条目的明文转成实际写进 res 的字节。
    ///   ★ res 内部条目（Type C/D）的 Compression 标记与 rdp 侧条目是两套独立标记：
    ///     Type 只回答「数据躺在哪张 rdp / 是否在本 res 内」，压缩与否只看 list 的
    ///     Compression + Compress。正式版的 local(G2) 就是 Type=C 且 Compress=blz4。
    ///   传入 plain = 解压后的明文，返回应写进 res 的字节（压缩态；未压缩则原样返回）。</summary>
    public delegate byte[] ResInnerEncoder(int group, int gindex, ResEntry it, byte[] plain);

    /// <summary>rdp 侧条目的【就地重建】钩子（可选，DLC 打包用）：(组,组内索引,ResEntry) → 重建后的字节 / null。
    ///   ★ 6 / 4 / 5 类型条目若它本身就是一个 res（补丁里被替换的 res），必须按它自己的 list 重建，
    ///   否则 res 内的 4/5/C/D 索引与 +0x14/+0x18 仍是旧值 —— 游戏会找不到本体里的文件。
    ///   返回 null 表示不干预（走普通 locateRdp 路径）。</summary>
    public delegate byte[] RdpRebuilder(int group, int gindex, ResEntry it);
    /// <summary>★ 从【原始 res】按 TOC 取某个条目的原始字节段（不解压）—— DLC 打包 ext 命中 C/D 时用。</summary>
    public delegate byte[] OrigSegmentProvider(int group, int gindex, ResEntry it);

    /// <summary>★ 多语言分区版的原始段提供者：多一个 lang 维度（0 = en）。
    ///   六分区的 TOC 各自独立，同一 (组, 组内序号) 槽位在不同分区可能落在不同的原始偏移上，
    ///   故取原始段必须带分区号（否则 p&gt;0 会误取分区 0 的字节）。</summary>
    public delegate byte[] OrigSegmentProviderL(int lang, int group, int gindex, ResEntry it);

    /// <summary>由 list + 数据源重建 .res。res 内条目(G2/G5/G6)读数据，rdp 侧条目(G0/G1/G4/G7)查块位表。</summary>
    /// <param name="ext">★ DLC 打包的索引覆盖表（可选）：(语言分区, 组, 组内索引) → (显式offset, 显式size,
    ///   显式解压大小)。key 带 lang 维度以支持 US 美版多语言分区（单分区 res 恒用 lang = 0）。
    ///   命中项用显式值写 TOC，不读数据、不写 body/localPend —— 用于「C/D 指向本体时 offset 用 list 显式值、
    ///   不写数据」以及「4/5 引用本体时按基准 list 修正块号与大小」。仅 typed(ver≥3) 生效。</param>
    /// <param name="baseRef">★ 本体 res 指针（可选）：非 null 时把 +0x14（类型+块号）/ +0x18（字节数）写回头部。</param>
    /// <param name="rebuildRdp">★ rdp 侧 res 条目的就地重建钩子（可选，见 RdpRebuilder）。</param>
    public static byte[] Build(ResList js, DataReader readData, RdpLocator locateRdp,
                              RtblSlotsProvider rtblSlots = null, DecLenProvider decLen = null,
                              ResInnerEncoder encodeInner = null,
                              Dictionary<(int lang, int g, int gi), (long Offset, long Size, long DecSize)> ext = null,
                              BaseRef? baseRef = null, RdpRebuilder rebuildRdp = null,
                              OrigSegmentProvider origSeg = null)
    {
        bool dbg = Environment.GetEnvironmentVariable("GE2RB_DBG") == "1";
        var byGroup = new SortedDictionary<int, List<(ResEntry it, int gi)>>();
        foreach (var it in js.Files)
        {
            int g = int.Parse(it.Group);
            if (!byGroup.TryGetValue(g, out var l)) byGroup[g] = l = new List<(ResEntry, int)>();
            l.Add((it, l.Count));
        }

        long grupOff = 0x20, tocStart = grupOff + 64;      // ★ GE2 体验版：组表偏移 0x20，TOC 从 0x60 起
        // ★ 组在 TOC / body 里的物理顺序：以 list 的 Groups 为准（实测并非恒按组号升序：
        //   god_menu.res = 2,4,5,6,7,1,0 而 Field.res = 2,5,4），缺失 / 为空时退回默认顺序。
        IReadOnlyList<int> order = js.Groups != null && js.Groups.Count > 0
                                 ? js.Groups : (IReadOnlyList<int>)SlotOrder;
        var gstart = new Dictionary<int, long>();
        long pos = tocStart;
        foreach (var g in order)
        {
            if (!byGroup.ContainsKey(g)) continue;
            gstart[g] = pos;
            pos += byGroup[g].Count * 32L;
        }
        long tocEnd = pos, baseOff = tocEnd;

        var body = new MemoryStream();
        var recs = new List<(int g, int gi, long off, long size, long nameOff, long cn, int otype, long? dec)>();
        // ★ GE2 正式版（ver≥3）：offset 的高 4 位是数据来源类型，list 里以 Type 字段原样记录
        //   （"C"=本 res 文件内 / "4"=package.rdp / "5"=data.rdp / "0"=空条目），并且 +0x1C 记录解密大小。
        //   体验版（ver2）两者都没有：list 无 Type 字段，退回按组判定、按纯偏移写。
        bool typed = js.Version >= 3;
        // ★ local(G2) 的数据不在 body，先攒起来，等 body 定长后写到整个 res 包之后（文件尾）
        var localPend = new List<(int gi, byte[] data)>();
        // ★ res 内部条目写进 res 时的【明文长度】—— TOC +0x1C 记的是解压后大小，
        //   编辑过压缩文件后明文会变长/变短，必须用真值而不是 list 里的旧 decsize。
        var innerPlainLen = new Dictionary<(int g, int gi), int>();
        // ★ ext 覆盖项的显式解压大小（+0x1C）—— DLC 打包的 4/5/C/D 指向本体时，decsize 来自基准 list
        var decOverride = new Dictionary<(int g, int gi), long>();

        int nextGi = -1;
        // 把「本 res 文件内」的数据写进 body，返回 TOC 里的 size：
        //   table(G5) 记 u32 个数；string(G6) 含结尾 0x00；prx/refer 文本(asText) 恒 0（长度靠 \0 终止）；其余记字节数。
        long PutInBody(int g, byte[] data, bool asText, out long off)
        {
            if (dbg) Console.Error.WriteLine($"[PB] {js.Filename} g={g} gi={nextGi} len={data.Length} bodyLen={body.Length}");
            Pad16(body);
            off = baseOff + body.Length;
            if (g == 6)
            {
                if (data.Length == 0) return 0;
                body.Write(data); body.WriteByte(0); return data.Length + 1;
            }
            if (g == 5)
            {
                if (data.Length % 4 != 0)
                    throw new InvalidDataException($"table 数据长度须为 4 的倍数（{data.Length} 字节）");
                body.Write(data); return data.Length / 4;
            }
            if (asText) { body.Write(data); body.WriteByte(0); return 0; }
            body.Write(data); return data.Length;
        }

        foreach (var g in order)
        {
            if (!byGroup.ContainsKey(g)) continue;
            foreach (var (it, gi) in byGroup[g])
            {
                nextGi = gi;
                bool isDummy = it.FileName == "dummy" || it.ElementName == null;
                long offset, size, cn;
                int otype = ParseType(it.Type, 0);               // offset 来源类型（list 的 Type 字段）
                if (isDummy)
                {
                    offset = 0;
                    size = it.DummySize ?? 0;
                    cn = 0;
                }
                else if (typed)
                {
                    // ★ ext 覆盖（DLC 打包）：命中即用显式 offset / size / 解压大小，且【不读数据、不写 body】。
                    //   场景 —— 更新补丁（-cdlc）里带 BaseExportPath 的 res：
                    //     · local(G2) 的 C/D 索引到【本体 res】内部，offset/size 必须是本体 res 里的真值，
                    //       数据不在本 res 内（不落盘），所以既不能 readData 也不能进 localPend；
                    //     · 4 / 5 引用本体 rdp 块位，块号/大小由基准 list（x=1 本体 / x=2 重打包后）给出。
                    if (ext != null && ext.TryGetValue((0, g, gi), out var ex))
                    {
                        // ★ C/D 且原始 res 里能取到该条目的数据段（off+size 不超文件长）⇒ 原样搬回：
                        //   DLC 的 local(G2) C/D 数据有一部分就在自己文件里（offset 与本体一致，
                        //   从本体照搬），只写索引不写数据会让游戏读到空洞。
                        if (Pres.IsRes(otype) && origSeg != null)
                        {
                            byte[] seg = origSeg(g, gi, it);
                            if (seg != null)
                            {
                                if (g == 2)
                                {
                                    // local(G2)：整块写到 res 包之后，offset 由 localPend 回填
                                    //   （body 与原始一致时回填值即原始 offset）
                                    if (dbg) Console.Error.WriteLine($"[LP] {js.Filename} g={g} gi={gi} len={seg.Length} (ext)");
                                    localPend.Add((gi, seg));
                                    offset = 0;
                                    size = seg.Length;
                                }
                                else size = PutInBody(g, seg, false, out offset);
                                decOverride[(g, gi)] = ex.DecSize;   // 解压大小按基准 list
                                cn = it.ElementName?.Count ?? 0;
                                goto afterData;
                            }
                        }
                        // 4/5（数据在本体 rdp）或 C/D 数据确实不在本 res 内（off+size 超文件长）：
                        //   只写 TOC 索引，不读数据、不写 body。
                        offset = ex.Offset;
                        size = ex.Size;
                        decOverride[(g, gi)] = ex.DecSize;
                        cn = it.ElementName?.Count ?? 0;
                        goto afterData;                 // ★ 跳过下面的取数/写 body 逻辑（名字区仍照常写）
                    }
                    // ★ GE2 正式版：数据来源【完全由 list 的 Type 决定】——
                    //   C —— 数据在本 res 文件内（低 28 位 = 文件内绝对偏移），宿主是 system.res / data.rdp 里的 res：
                    //         local(G2) 排在 res 包之后（文件尾），table(G5) / string(G6) 排在 body 里
                    //   D —— 同 C（数据同样在本 res 文件内），只是宿主 res 本身躺在 package.rdp 里
                    //   4 —— 数据在 package.rdp（块号 / 字节数取自 package.json 的块表）
                    //   5 —— 数据在 data.rdp（块号 / 字节数取自 data.json 的块表）
                    //   0 —— 空条目（无数据，offset / size 均写 0）
                    if (IsRdp(otype))
                    {
                        // ★ rebuildRdp（DLC 打包）：该条目本身是一个 res、且要按它自己的 list 重建时，
                        //   用重建后的字节（size / 解压大小随之变化），块号仍取本轮 rdp 的新块位。
                        byte[] rb2 = rebuildRdp?.Invoke(g, gi, it);
                        var (lt, rb, rs) = locateRdp(g, gi);
                        if (rb < 0 || rs < 0)
                            throw new InvalidDataException(
                                $"rdp 定位失败：{it.FileName}（组 {g} / 组内索引 {gi} / Type \"{it.Type}\"）—— "
                              + "package.json / data.json 里缺少对它的引用记录");
                        // ★ Type 必须与它实际登记的 rdp 一致（改不了数据躺在那张 rdp 上，写错直接拦下）
                        if (lt >= 0 && lt != otype)
                            throw new InvalidDataException(
                                $"Type 不符：{it.FileName}（组 {g} / 组内索引 {gi}）list 写 \"{it.Type}\"，"
                              + $"但它登记在 \"{TypeStr(lt)}\"（{(lt == TypeData ? "data.rdp" : "package.rdp")}）上");
                        offset = rb;
                        size = rs;
                        if (rb2 != null)
                            // 只覆盖 +0x1C 的解压大小（重建后的明文长度）；压缩态 size 仍取 rdp 里的 rs
                            decOverride[(g, gi)] = rb2.Length;
                    }
                    else if (IsRes(otype))                        // C / D：数据在本 res 文件内
                    {
                        byte[] plain = readData(it, g, gi);
                        // ★ res 内部条目同样可能是压缩的（GE2RB 的 local 就是 blz4）——
                        //   必须压回压缩态写进 res，否则 res 体积暴涨（god_m000_Boot 32K→223K）。
                        //   encodeInner 优先复用原始压缩字节，保证零改动往返逐字节一致。
                        byte[] data = encodeInner != null ? encodeInner(g, gi, it, plain) : plain;
                        innerPlainLen[(g, gi)] = plain.Length;     // +0x1C 记【明文】长度
                        if (g == 2)
                        {
                            // local(G2)：数据不在 body，整块写到整个 res 包之后（文件尾），offset 等 body 定长后回填
                            if (dbg) Console.Error.WriteLine($"[LP] {js.Filename} g={g} gi={gi} len={data.Length}");
                            localPend.Add((gi, data));
                            offset = 0;
                            size = data.Length;
                        }
                        else { if (dbg) Console.Error.WriteLine($"[PB2] {js.Filename} g={g} gi={gi} len={data.Length}"); size = PutInBody(g, data, false, out offset); }
                    }
                    else if (otype == TypeExcludeRdp)             // ★ PSV：EXCLUDE_RDP（不在任何 rdp 内）
                    {
                        offset = 0;                               // 低 28 位恒 0（写回时 Tag(3,0) = 0x30000000）
                        size = it.Size ?? 0;                      // ★ 字节数照 list 保留
                        decOverride[(g, gi)] = it.DecSize ?? 0;   // ★ 解压大小同样照 list 保留
                    }
                    else if (IsSelfDecl(g, it.ElementName))       // ★ PSV：G1 self（不在 rdp 内，仅记名字）
                    {
                        offset = 0;                               // 索引 / 大小 / 解压大小三项恒 0
                        size = 0;
                        decOverride[(g, gi)] = 0;
                    }
                    else                                          // Type 0：空条目
                    {
                        offset = 0;
                        size = 0;
                    }
                    cn = it.ElementName.Count;
                }
                else
                {
                    // 体验版（ver<3）：TOC 里没有类型位，退回按组判定 ——
                    //   local(G2) 在文件尾、table(G5)/string(G6) 在 body、Sign==1 的 prx(G1)/refer(G4) 文本也在 body；
                    //   其余条目在 rdp（块位 / 大小来自 rdp 打包结果）。
                    var (rt, rb, rs) = locateRdp(g, gi);
                    bool onRdp = rb >= 0 && rs >= 0;
                    bool asText = !onRdp && js.Sign == 1 && Sign1TextSlots.Contains(g);
                    if (onRdp) { otype = rt; offset = rb; size = rs; }
                    else
                    {
otype = TypeRes;
                    byte[] plain = readData(it, g, gi);
                    byte[] data = encodeInner != null ? encodeInner(g, gi, it, plain) : plain;
                    innerPlainLen[(g, gi)] = plain.Length;
                    if (g == 2 && !asText)
                        {
                            localPend.Add((gi, data));
                            offset = 0;
                            size = data.Length;
                        }
                        else size = PutInBody(g, data, asText, out offset);
                    }
                    cn = it.ElementName.Count;
                }
                afterData:;        // ★ ext 覆盖命中时从 typed 分支跳到这里（名字区照常写）

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
                    if (dbg && (js.Filename ?? "").Contains("god_menu"))
                        Console.Error.WriteLine($"[NAME] g={g} gi={gi} cn={cn} file={it.FileName} exp={it.ExportPath} bodyStart={nameRel}");
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
                    //   实测（god_menu.res 276 条 + 体验版 god_menu.res 34 条，块长与位串全对上，零例外）。
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
                // ★ res 内部条目的 decsize =它的明文长度（编辑过压缩文件后要跟着变）；
                //   其余条目用 rdp 侧登记的真值，或沿用 list 的 decsize。
                // ★ tbl(5)/string(6)/rtbl(7) 的 +0x1C 恒为0：这几类数据本来就在 res 包内、游戏不压缩它们，
                //   官方文件里该字段就是 0（rtbl 同理，见 Packer 里 rdp 侧对 5/6/7 的同款排除）。
                //   ★ 必须排在 decOverride 之前 —— 否则 DLC 打包时 4/5 的 ext 会把本体块表的 rawSize
                //     灌进组 7 rtbl 条目的 +0x1C（实测 god_menu.res 的 34176 被误写）。
                long? decV = g is 5 or 6 or 7 ? 0
                            : decOverride.TryGetValue((g, gi), out var dov) ? dov
                            : innerPlainLen.TryGetValue((g, gi), out var ipl) ? ipl
                            : (decLen != null ? decLen(g, gi, it) : null);
                recs.Add((g, gi, offset, size, nameOffset, cn, otype, decV ?? it.DecSize));
            }
        }

        Pad16(body);
        long pkgSize = baseOff + body.Length;          // ★ res 包大小（不含 Local 数据）
        if (dbg) Console.Error.WriteLine($"[BUILD] {js.Filename} body={body.Length} pkgSize={pkgSize}");

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
            WriteU32At(toc, p, (uint)(typed ? Tag(r.otype, off) : off));   // ★ 正式版：高 4 位 = 数据来源类型
            WriteU32At(toc, p + 4, (uint)r.size);
            WriteU32At(toc, p + 8, (uint)r.nameOff);
            WriteU32At(toc, p + 0xC, (uint)r.cn);
            WriteU32At(toc, p + 0x1C, (uint)(r.dec ?? 0));                 // ★ +0x1C 解密后大小（体验版恒 0）
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
        // ★ +0x14 / +0x18：本体 res 指针（更新补丁专用）。
        //   有值 ⇒ 该 res 的 local(G2) 组 C/D 条目索引到【本体 res】内部的偏移（数据不在本 res 内）。
        //   值由调用方按基准 list 给出（本体块号 / 本体 res 字节数）；全 0 = 本体即自身。
        if (baseRef.HasValue && baseRef.Value.Present)
        {
            WriteU32At(head, 0x14, (uint)Tag(baseRef.Value.Type, baseRef.Value.Offset));
            WriteU32At(head, 0x18, (uint)baseRef.Value.Size);
        }
        for (int g = 0; g < 8; g++)
        {
            long cnt = byGroup.TryGetValue(g, out var l2) ? l2.Count : 0;
            WriteU32At(head, grupOff + g * 8, cnt > 0 ? (uint)gstart[g] : 0);
            WriteU32At(head, grupOff + g * 8 + 4, (uint)cnt);
        }

        var outBuf = new MemoryStream();
        outBuf.Write(head); outBuf.Write(toc); body.Position = 0; body.CopyTo(outBuf);
        localMs.Position = 0; localMs.CopyTo(outBuf);
        if (dbg && (js.Filename ?? "").Contains("god_menu"))
        {
            var ba = outBuf.ToArray();
            Console.Error.WriteLine($"[ASM] head={head.Length} toc={toc.Length} body={body.Length} localMs={localMs.Length} total={ba.Length} pkgSize={pkgSize}");
            Console.Error.WriteLine($"[ASM] body末64B = {Convert.ToHexString(ba.AsSpan((int)(0x60 + toc.Length + body.Length - 64), 64))}");
            Console.Error.WriteLine($"[ASM] localMs头64B = {Convert.ToHexString(ba.AsSpan((int)pkgSize, 64))}");
            Console.Error.WriteLine($"[ASM] localMs尾64B = {Convert.ToHexString(ba.AsSpan(ba.Length - 64, 64))}");
            return ba;
        }
        return outBuf.ToArray();
    }

    // ============================================================ ★ US 美版：多语言分区重建
    /// <summary>
    /// ★ US 美版多语言分区 res 的重建（头部 +0x1C = 分区数 ≥ 2，分区表见 Partitions）。
    ///   与 Build 的差异（2026-10-15 对美版 system.res / system_update.res / 1.res 逐字节实测反推）：
    ///     · 头部 0x20 起是 N×8 分区表（组表绝对偏移 + 分区字节长）；+0x04 恒 0；
    ///     · 每分区自带【组表 8×8 → TOC → body】：TOC 的组块顺序 = js.Groups（按组表偏移升序），
    ///       body 的组顺序恒为 SlotOrder { 2,4,5,6,7,1,0 }，条目内顺序为 [16 对齐数据][16 对齐名字区]；
    ///     · local(G2) 的 C 类数据不进分区，而是排在整个 res 包之后（pkgSize 起）的【内容池】；
    ///       同一槽位跨分区内容相同时只落一份，各分区 TOC 指向同一偏移；
    ///     · 内容池写入顺序：按槽位序号走 —— 共享槽位写一份；连续一段「各语言内容互不相同」的槽位
    ///       按【语言优先、段内槽位序】写（实测 system.res 反推：l30/l31 与 l60/l61 两个连号段的
    ///       池顺序均为 en·l30, en·l31, fr·l30, fr·l31, …）。
    ///   跨分区按 (组, 组内序号) 逐槽对齐 —— 各分区的组内条目数必须一致。
    ///
    ///   ★ DLC 打包（-cdlc）扩展（全部可选，默认 null ⇒ 与本体重建逐字节一致）：
    ///     · ext（(lang, 组, 组内序号) → 显式 offset/size/decsize）：4/5 按基准块表、C/D 按基准 res list；
    ///     · baseRef：把 +0x14/+0x18 本体指针写回头部；
    ///     · rebuildRdp：rdp 侧 res 条目就地重建（取重建后长度写 +0x1C）；
    ///     · origSeg：ext 命中的 C/D 若数据在【本 res】内则原样搬回（g2 进内容池、其余进分区 body）。
    ///   ⇒ 与 Build(单分区) 的同名参数一一对应，仅多 lang 维度。
    /// </summary>
    public static byte[] BuildMulti(ResList js, DataReader readData,
                                    RdpLocatorL locateRdp,
                                    RtblSlotsProvider rtblSlots = null, DecLenProviderL decLen = null,
                                    ResInnerEncoder encodeInner = null,
                                    Dictionary<(int lang, int g, int gi), (long Offset, long Size, long DecSize)> ext = null,
                                    BaseRef? baseRef = null, RdpRebuilder rebuildRdp = null,
                                    OrigSegmentProviderL origSeg = null)
    {
        int L = js.LangCount ?? 1;
        if (L < 1) throw new InvalidDataException($"BuildMulti 需要 LangCount ≥ 1（当前 {L}）");
        // ★ 单分区（+0x1C = 1，如 2.res 这种美版单分区 res）：【没有】分区表，组表固定在 0x20；
        //   头部仍是 +0x1C = 1、+0x04 = 0。只有 L ≥ 2 才在 0x20 起写 N×8 分区表。
        long headSize = L >= 2 ? 0x20 + L * 8L : 0x20L;

        // ---- ① 按分区归并条目（保持 list 里各组内的顺序 = 组内序号）----
        var pg = new List<Dictionary<int, List<ResEntry>>>();
        for (int i = 0; i < L; i++) pg.Add(new Dictionary<int, List<ResEntry>>());
        foreach (var it in js.Files)
        {
            int li = it.Lang ?? 0;
            if (li < 0 || li >= L) throw new InvalidDataException($"Lang 越界（{li}）：{it.FileName}");
            int g0 = int.Parse(it.Group);
            if (!pg[li].TryGetValue(g0, out var l2)) pg[li][g0] = l2 = new List<ResEntry>();
            l2.Add(it);
        }
        foreach (var kv in pg[0])
            for (int i = 1; i < L; i++)
            {
                int c1 = pg[i].TryGetValue(kv.Key, out var l3) ? l3.Count : 0;
                if (c1 != kv.Value.Count)
                    throw new InvalidDataException(
                        $"分区 {LangName(i)} 的 {SlotName[kv.Key]} 组条目数 {c1} ≠ 分区 0 的 {kv.Value.Count}"
                      + "（多语言分区必须逐槽对齐）");
            }

        IReadOnlyList<int> order = js.Groups != null && js.Groups.Count > 0
                                 ? js.Groups : (IReadOnlyList<int>)SlotOrder;
        if (Environment.GetEnvironmentVariable("GE2RB_DBG") == "1")
            for (int i = 0; i < L; i++)
                Console.Error.WriteLine($"[BM] p={i} L={L} G2={(pg[i].TryGetValue(2, out var zz) ? zz.Count : 0)}"
                    + $" G2[40]={(pg[i].TryGetValue(2, out var z9) && z9.Count > 40 ? z9[40].FileName + "|" + z9[40].Type + "|" + z9[40].Lang : "?")}");

        // ---- ② 逐分区构建 body（名字区 + 分区内数据）；local C 攒进内容池 ----
        var bodies = new List<MemoryStream>(L);
        var relOff = new List<Dictionary<(int g, int gi), long>>(L);   // 分区内数据的相对偏移
        var relLen = new List<Dictionary<(int g, int gi), long>>(L);   // 分区内数据的 TOC +0x04
        // 内容池候选：gi → 各分区写入的字节（仅 local(G2) 的 C 类条目）
        var poolList = new List<byte[][]>();                            // poolList[gi][p]（null = 该分区无此槽）
        var poolDec = new List<long[]>();
        // ★ DLC 打包：ext / rebuildRdp 给出的显式 +0x1C（解压后大小）—— 优先于 decLen / poolDec。
        var decOv = new Dictionary<(int p, int g, int gi), long>();
        int nLocal = pg[0].TryGetValue(2, out var l20) ? l20.Count : 0;
        for (int i = 0; i < nLocal; i++) { poolList.Add(new byte[L][]); poolDec.Add(new long[L]); }

        for (int p = 0; p < L; p++)
        {
            var body = new MemoryStream();
            var rel = new Dictionary<(int, int), long>();
            var rln = new Dictionary<(int, int), long>();
            foreach (var g in SlotOrder)
            {
                if (!pg[p].TryGetValue(g, out var lst)) continue;
                for (int gi = 0; gi < lst.Count; gi++)
                {
                    var it = lst[gi];
                    bool isDummy = it.FileName == "dummy" || it.ElementName == null;
                    int otype = ParseType(it.Type, 0);
                    // ★ DLC 打包：ext 命中 ⇒ 显式 offset/size/decsize，不读数据（数据在本体 rdp / 本体 res 里）；
                    //   例外：ext 命中的 C/D 若用 origSeg 能从【本 res 原始字节】取到数据段，则原样搬回
                    //   （g2 进内容池、其余进分区 body），否则只写 TOC 索引（同 Build 的单分区逻辑）。
                    (long Offset, long Size, long DecSize) ex0 = default;
                    bool extHit = !isDummy && ext != null && ext.TryGetValue((p, g, gi), out ex0);
                    if (extHit && Pres.IsRes(otype) && origSeg != null)
                    {
                        byte[] seg = origSeg(p, g, gi, it);
                        if (seg != null)
                        {
                            if (g == 2) { poolList[gi][p] = seg; poolDec[gi][p] = ex0.DecSize; }
                            else
                            {
                                Pad16(body);
                                rel[(g, gi)] = body.Length;
                                rln[(g, gi)] = g == 5 ? seg.Length / 4 : (g == 6 ? seg.Length + 1 : seg.Length);
                                body.Write(seg);
                                if (g == 6) body.WriteByte(0);
                            }
                            decOv[(p, g, gi)] = ex0.DecSize;
                        }
                        else decOv[(p, g, gi)] = ex0.DecSize;
                    }
                    else if (extHit)
                    {
                        decOv[(p, g, gi)] = ex0.DecSize;      // 4/5 或数据不在本 res 内的 C/D：只写 TOC
                    }
                    else if (!isDummy && otype == TypeRes && g == 2)
                    {
                        // local(G2) 的 C 类：数据进【内容池】，不进分区 body（名字区照写）
                        byte[] plain = readData(it, g, gi);
                        byte[] enc = encodeInner != null ? encodeInner(g, gi, it, plain) : plain;
                        poolList[gi][p] = enc;
                        poolDec[gi][p] = plain.Length;
                    }
                    else if (!isDummy && otype == TypeRes)
                    {
                        // table(5)/string(6)/rtbl(7) 等在 res 内的数据：写进分区 body
                        byte[] plain = readData(it, g, gi);
                        byte[] enc = encodeInner != null ? encodeInner(g, gi, it, plain) : plain;
                        Pad16(body);
                        rel[(g, gi)] = body.Length;
                        rln[(g, gi)] = g == 5 ? enc.Length / 4 : (g == 6 ? enc.Length + 1 : enc.Length);
                        body.Write(enc);
                        if (g == 6) body.WriteByte(0);
                    }
                    // 名字区（dummy / rtbl 只写前 4 项以外的规则同 Build）
                    int cn = it.ElementName?.Count ?? 0;
                    if (rtblSlots != null && Rtbl.IsPath(it.ExportPath) && cn > 4) cn = 4;
                    if (cn > 0)
                    {
                        Pad16(body);
                        long nameRel = body.Length;
                        long q = body.Length + cn * 4L;    // 相对；下面整体加 base 后写回
                        var ptrs = new List<long>((int)cn);
                        for (int j = 0; j < cn; j++)
                        {
                            ptrs.Add(q);
                            q += Encoding.UTF8.GetByteCount(it.ElementName[j]) + 1;
                        }
                        foreach (var pv in ptrs) WriteU32(body, (uint)pv);   // 先写相对，装配时再加 base
                        for (int j = 0; j < cn; j++)
                        {
                            body.Write(Encoding.UTF8.GetBytes(it.ElementName[j]));
                            body.WriteByte(0);
                        }
                        rel[(g, -1 - gi)] = nameRel;                        // 名字区相对偏移单独记键
                        long nl = body.Length - nameRel;
                        if (rtblSlots != null && Rtbl.IsPath(it.ExportPath))
                        {
                            int slots = rtblSlots(it.ExportPath);
                            if (slots <= 0) throw new InvalidDataException($"rtbl 槽位数为 0：{it.ExportPath}");
                            int c8 = (slots + 7) / 8;
                            var bits = ParseBits(it.ElementName, 4, slots);
                            var bitBuf = new byte[c8];
                            for (int s = 0; s < slots; s++)
                                if (bits[s]) bitBuf[s >> 3] |= (byte)(1 << (s & 7));
                            body.Write(bitBuf, 0, c8);
                            long target = nameRel + Align16(nl + c8 + 1);
                            while (body.Length < target) body.WriteByte(0);
                        }
                    }
                }
            }
            Pad16(body);
            bodies.Add(body); relOff.Add(rel); relLen.Add(rln);
        }

        // ---- ③ 分区起点与 pkgSize ----
        //   ★ 分区长度 = 组表(64) + TOC(Σ 各组条目数×32，16 对齐) + body(已 16 对齐)。
        //     TOC 长度只取决于各组的条目数，与 baseOf 无关 ⇒ 可以先算，再定各分区起点。
        var tocLen = new long[L];
        for (int p = 0; p < L; p++)
        {
            long tl = 0;
            foreach (var g in order) if (pg[p].TryGetValue(g, out var l4)) tl += l4.Count * 32L;
            tocLen[p] = Align16(tl);
        }
        var baseOf = new long[L];
        var partLen = new long[L];
        var bodyBase = new long[L];                           // 分区内 body 的起始绝对偏移
        long cur = headSize;
        for (int p = 0; p < L; p++)
        {
            baseOf[p] = cur;
            partLen[p] = 64 + tocLen[p] + bodies[p].Length;
            bodyBase[p] = baseOf[p] + 64 + tocLen[p];
            cur += partLen[p];
        }
        long pkgSize = cur;                                   // = 最后一个分区末尾（已 16 对齐）

        // ---- ④ 内容池：共享槽位写一份；差异段按【语言优先、段内槽位序】写 ----
        //   ★ 复用规则（实测 god_menu.res / god_game.res 官方池序，5/5 中 4 个 res 完全吻合）：
        //     ① p>0 且与【分区 0 同槽位】字节相同 ⇒ 复用分区 0 的偏移；
        //     ② 否则按 **(ElementName, 语言)** 查缓存 ⇒ 命中且字节相同 ⇒ 复用其偏移（跨槽位去重）；
        //        ★ 必须用【res 名字区的 ElementName】而非 list 的 ExportPath ——
        //          解包侧会给重名条目加 `_0001` 后缀（ExportPath 不同），但官方去重按 ElementName
        //          （实测 system.res：G2[62] `system/plane_rect.gxt` 与 G2[67]
        //           `system/plane_rect_0001.gxt` 的 ElementName 同为 plane_rect|gxt|，
        //           官方合并到同一池偏移 0x6C9D0；用 ExportPath 会多写一份 ⇒ 池 +80）。
        //     ③ 都不命中 ⇒ 写一份并登记缓存。
        //   （注：官方偶有「同名同内容却未合并」的实例身份差异，仅靠 res 结构无法推断；
        //     如需零改动 byte-identical，走「未修改 res 直通 / 复用原始段」而非依赖重压。）
        var poolName = new string[poolList.Count];
        if (pg[0].TryGetValue(2, out var l2n))
            for (int i = 0; i < l2n.Count && i < poolName.Length; i++)
                poolName[i] = l2n[i].ElementName != null
                            ? string.Join("|", l2n[i].ElementName) : "";
        var poolOff = new List<long[]>(poolList.Count);        // gi → 各分区的池偏移
        for (int i = 0; i < poolList.Count; i++) poolOff.Add(new long[L]);
        long pos = pkgSize;
        var poolMs = new MemoryStream();
        var poolCache = new Dictionary<(string path, int lang), (byte[] data, long off)>();
        long PoolWrite(byte[] b)
        {
            pos = Align16(pos);
            long need = pos - (pkgSize + poolMs.Length);
            if (need > 0) poolMs.Write(new byte[need], 0, (int)need);
            long off = pos;
            poolMs.Write(b, 0, b.Length);
            pos += b.Length;
            return off;
        }
        long PoolEmit(int gi, int p, byte[] v)
        {
            if (p > 0 && poolList[gi][0] != null && v.AsSpan().SequenceEqual(poolList[gi][0]))
                return poolOff[gi][0];                                        // ① 同槽位与分区 0 同字节
            var key = (poolName[gi] ?? "", p);
            if (poolCache.TryGetValue(key, out var c) && c.data.AsSpan().SequenceEqual(v))
                return c.off;                                                 // ② 同名同语言同字节
            long o = PoolWrite(v);                                            // ③ 写一份
            if (!poolCache.ContainsKey(key)) poolCache[key] = (v, o);
            return o;
        }
        for (int gi = 0; gi < poolList.Count; gi++)
        {
            var arr = poolList[gi];
            var first = Array.Find(arr, x => x != null);
            if (first == null) continue;                       // 全体为空（dummy）
            if (!HasLangDiff(arr))
            {
                // 共享槽位：一份，所有分区指向它
                long o = PoolEmit(gi, 0, first);
                for (int p = 0; p < L; p++) if (arr[p] != null) poolOff[gi][p] = o;
                continue;
            }
            // 差异段：连续且各语言内容不同的槽位，按【语言优先、段内槽位序】写
            int runEnd = gi;
            while (runEnd + 1 < poolList.Count && HasLangDiff(poolList[runEnd + 1])) runEnd++;
            for (int p = 0; p < L; p++)
                for (int k = gi; k <= runEnd; k++)
                {
                    var bk = poolList[k]?[p];
                    if (bk == null) continue;
                    poolOff[k][p] = PoolEmit(k, p, bk);
                }
            gi = runEnd;
        }
        {
            long need = Align16(pos) - (pkgSize + poolMs.Length);
            if (need > 0) poolMs.Write(new byte[need], 0, (int)need);
        }
        bool HasLangDiff(byte[][] arr)
        {
            var f = Array.Find(arr, x => x != null);
            if (f == null) return false;
            foreach (var x in arr) if (x != null && !x.AsSpan().SequenceEqual(f)) return true;
            return false;
        }

        // ---- ⑤ 写 TOC（分区内偏移 = body 起点 + rel；local = 池偏移；其余走 locateRdp）----
        //   ★ 六分区【共用】的槽位（同一个 ExportPath ⇒ 同一个 rdp 块）只在分区 0 查一次块表：
        //     共用槽位在 package.json 里只登记分区 0 的那条 ref，其余分区直接复用结果，
        //     否则分区 1..5 会 miss 而抛「rdp 定位失败」（实测 1.res 的 Article_01 就是这样）。
        var sharedRdp = new Dictionary<string, (int t, long off, long sz)>(StringComparer.OrdinalIgnoreCase);
        var sharedDec = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var tocMs = new List<MemoryStream>(L);
        for (int p = 0; p < L; p++)
        {
            var toc = new MemoryStream();
            long gcur = baseOf[p] + 64;                        // 组表之后
            foreach (var g in order)
            {
                if (!pg[p].TryGetValue(g, out var lst)) continue;
                for (int gi = 0; gi < lst.Count; gi++)
                {
                    var it = lst[gi];
                    bool isDummy = it.FileName == "dummy" || it.ElementName == null;
                    int otype = ParseType(it.Type, 0);
                    long offset, size, cn = it.ElementName?.Count ?? 0;
                    (long Offset, long Size, long DecSize) ex2 = default;
                    bool extHit2 = !isDummy && ext != null && ext.TryGetValue((p, g, gi), out ex2);
                    if (isDummy) { offset = 0; size = it.DummySize ?? 0; cn = 0; }
                    else if (extHit2)
                    {
                        // ★ DLC 打包：ext 命中 —— C/D 若已把数据搬回（内容池 / 分区 body）则用其新位置，
                        //   否则用 ext 给的显式 offset/size（4/5 指本体 rdp；C/D 数据在本体 res 内、只写索引）。
                        if (otype == TypeRes && g == 2 && poolList[gi][p] != null)
                        { offset = poolOff[gi][p]; size = poolList[gi][p].Length; }
                        else if (otype == TypeRes && relOff[p].TryGetValue((g, gi), out var r1))
                        { offset = bodyBase[p] + r1; size = relLen[p][(g, gi)]; }
                        else { offset = ex2.Offset; size = ex2.Size; }
                    }
                    else if (IsRdp(otype))
                    {
                        if (p > 0 && it.ExportPath != null && sharedRdp.TryGetValue(it.ExportPath, out var s0))
                        { otype = s0.t; offset = s0.off; size = s0.sz; }   // ★ 共用槽位：复用分区 0 的定位
                        else
                        {
                            // ★ DLC 打包：该 rdp 侧条目本身是一个要重建的 res ⇒ 取重建后的长度写 +0x1C
                            //   （压缩态 size 仍取 rdp 里的 rs；重建后 res 的明文长度会变）
                            byte[] rb2 = rebuildRdp?.Invoke(g, gi, it);
                            var (lt, rb, rs) = locateRdp(p, g, gi);
                            if (rb < 0 || rs < 0)
                                throw new InvalidDataException(
                                    $"rdp 定位失败：{it.FileName}（组 {g} / 组内序号 {gi} / Type \"{it.Type}\"）");
                            if (lt >= 0 && lt != otype)
                                throw new InvalidDataException(
                                    $"Type 不符：{it.FileName}（组 {g}）list 写 \"{it.Type}\"，实际登记在 \"{TypeStr(lt)}\"");
                            offset = rb; size = rs;
                            if (rb2 != null) decOv[(p, g, gi)] = rb2.Length;
                            if (p == 0 && it.ExportPath != null) sharedRdp[it.ExportPath] = (otype, rb, rs);
                        }
                    }
                    else if (otype == TypeRes)
                    {
                        if (g == 2) { offset = poolOff[gi][p]; size = poolList[gi][p]?.Length ?? 0; }
                        else if (relOff[p].TryGetValue((g, gi), out var r0))
                        { offset = bodyBase[p] + r0; size = relLen[p][(g, gi)]; }
                        else { offset = 0; size = 0; }
                    }
                    else if (otype == TypeExcludeRdp) { offset = 0; size = it.Size ?? 0; }
                    else if (IsSelfDecl(g, it.ElementName)) { offset = 0; size = 0; }
                    else { offset = 0; size = 0; }
                    // 名字区偏移（写【相对值】，⑥ 装配时统一 + base 变绝对）
                    long nameOff = 0;
                    int nName = (int)cn;
                    if (rtblSlots != null && Rtbl.IsPath(it.ExportPath) && nName > 4) nName = 4;
                    if (nName > 0 && relOff[p].TryGetValue((g, -1 - gi), out var nr)) nameOff = nr;
                    // +0x1C 解压后大小：tbl/string/rtbl 恒 0；ext / 重建 res 的显式值优先；
                    //   local C 用【本分区】的明文长度；其余沿用 list / 覆盖
                    long? decV = g is 5 or 6 or 7 ? 0
                               : decOv.TryGetValue((p, g, gi), out var dov) ? dov
                               : g == 2 && otype == TypeRes ? poolDec[gi][p]
                               : (decLen != null ? decLen(p, g, gi, it) : null);
                    long dec = decV ?? it.DecSize ?? 0;
                    // ★ 共用槽位的 +0x1C 同样复用分区 0 的真值（各分区该槽位就是同一份数据）
                    if (it.ExportPath != null)
                    {
                        if (p == 0) sharedDec[it.ExportPath] = dec;
                        else if (sharedDec.TryGetValue(it.ExportPath, out var d0)) dec = d0;
                    }
                    WriteU32(toc, (uint)Tag(otype, offset));
                    WriteU32(toc, (uint)size);
                    WriteU32(toc, (uint)nameOff);
                    WriteU32(toc, (uint)nName);
                    WriteU32(toc, 0); WriteU32(toc, 0);
                    WriteU32(toc, 0);
                    WriteU32(toc, (uint)dec);          // ★ +0x1C = 解密（解压）后大小
                }
                gcur += lst.Count * 32L;
            }
            Pad16(toc);
            tocMs.Add(toc);
        }

        // ---- ⑥ 装配：头 + 分区表 + 各分区（组表 + TOC + body）+ 内容池 ----
        var head = new byte[headSize];
        WriteU32At(head, 0, (uint)js.Magic);
        WriteU32At(head, 4, 0);                                // ★ 美版：+0x04 废弃置 0
        head[8] = (byte)js.Format; head[9] = (byte)js.Version;
        head[0x0A] = (byte)(js.Package & 0xFF); head[0x0B] = (byte)((js.Package >> 8) & 0xFF);
        WriteU32At(head, 0x0C, (uint)(js.Sign & 3));
        WriteU32At(head, 0x10, (uint)pkgSize);
        WriteU32At(head, 0x14, 0); WriteU32At(head, 0x18, 0);
        // ★ +0x14 / +0x18：本体 res 指针（更新补丁专用）—— 有值 ⇒ 该 res 的 local(G2) 组 C/D
        //   条目索引到【本体 res】内部的偏移（数据不在本 res 内）。同 Build 的单分区写法。
        if (baseRef.HasValue && baseRef.Value.Present)
        {
            WriteU32At(head, 0x14, (uint)Tag(baseRef.Value.Type, baseRef.Value.Offset));
            WriteU32At(head, 0x18, (uint)baseRef.Value.Size);
        }
        WriteU32At(head, 0x1C, (uint)L);                       // ★ 语言分区数（1 = 单分区，无分区表）
        for (int p = 0; L >= 2 && p < L; p++)
        {
            WriteU32At(head, 0x20 + p * 8, (uint)baseOf[p]);
            WriteU32At(head, 0x24 + p * 8, (uint)partLen[p]);  // ★ 分区总长 = 组表 + TOC + body
        }

        var outBuf = new MemoryStream();
        outBuf.Write(head);
        for (int p = 0; p < L; p++)
        {
            // 组表
            long gcur = baseOf[p] + 64;
            var gtab = new byte[64];
            foreach (var g in order)
            {
                if (!pg[p].TryGetValue(g, out var lst)) continue;
                WriteU32At(gtab, g * 8, (uint)gcur);
                WriteU32At(gtab, g * 8 + 4, (uint)lst.Count);
                gcur += lst.Count * 32L;
            }
            outBuf.Write(gtab);
            // TOC：+0x08 名字区偏移（⑤ 里写的是【body 内相对值】→ 就地 + body 起点 变绝对）
            var tb = tocMs[p].ToArray();
            for (int k = 0; k + 32 <= tb.Length; k += 32)
            {
                uint cn2 = (uint)(tb[k + 0x0C] | (tb[k + 0x0D] << 8) | (tb[k + 0x0E] << 16) | (tb[k + 0x0F] << 24));
                if (cn2 == 0) continue;                        // 无名字区（dummy / rtbl 无槽位名）
                uint no = (uint)(tb[k + 8] | (tb[k + 9] << 8) | (tb[k + 10] << 16) | (tb[k + 11] << 24));
                WriteU32At(tb, k + 8, (uint)(no + bodyBase[p]));
            }
            outBuf.Write(tb);
            // body：名字区内的【字符串指针表】同样写的是 body 内相对值 → 就地 + body 起点 变绝对
            var bb = bodies[p].ToArray();
            foreach (var g in order)
            {
                if (!pg[p].TryGetValue(g, out var lst)) continue;
                for (int gi = 0; gi < lst.Count; gi++)
                {
                    int cn = lst[gi].ElementName?.Count ?? 0;
                    if (rtblSlots != null && Rtbl.IsPath(lst[gi].ExportPath) && cn > 4) cn = 4;
                    if (cn == 0) continue;
                    if (!relOff[p].TryGetValue((g, -1 - gi), out var nr)) continue;
                    for (int j = 0; j < cn; j++)
                    {
                        int q = (int)(nr + j * 4);
                        uint v = (uint)(bb[q] | (bb[q + 1] << 8) | (bb[q + 2] << 16) | (bb[q + 3] << 24));
                        WriteU32At(bb, q, (uint)(v + bodyBase[p]));
                    }
                }
            }
            outBuf.Write(bb);
        }
        poolMs.Position = 0; poolMs.CopyTo(outBuf);
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
