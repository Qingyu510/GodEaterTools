namespace PS4GodEaterSeriesTools;

/// <summary>工作目录约定 + 路径解析（2.new 覆盖 1.out）</summary>
public static class Ws
{
    public const string OutDir = "1.out";
    public const string NewDir = "2.new";
    public const string TempDir = "3.temp";
    public const string PackDir = "4.pack";
    /// <summary>★ DLC 打包（-cdlc）产物目录：重打包的 patch.rdp + 重建的 system_update.res + 新 list</summary>
    public const string DlcPackDir = "5.dlcpack";

    public static string ToOs(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
    public static string ToRel(string p) => p.Replace('\\', '/');

    /// <summary>数据文件：2.new/&lt;rel&gt; 优先，其次 1.out/&lt;rel&gt;</summary>
    public static string ResolveData(string cwd, string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        string a = Path.Combine(cwd, NewDir, ToOs(rel));
        if (File.Exists(a)) return a;
        string b = Path.Combine(cwd, OutDir, ToOs(rel));
        return File.Exists(b) ? b : null;
    }

    /// <summary>list 文件：2.new/list/&lt;rel&gt; 优先，其次 1.out/list/&lt;rel&gt;</summary>
    public static string ResolveList(string cwd, string rel, string listDir = "list")
    {
        string a = Path.Combine(cwd, NewDir, listDir, ToOs(rel));
        if (File.Exists(a)) return a;
        string b = Path.Combine(cwd, OutDir, listDir, ToOs(rel));
        return File.Exists(b) ? b : null;
    }

    /// <summary>从 res 的 exportPath（相对 1.out）推出它的 list 相对路径
    /// system/god_menu.res → system/god_menu.json ; system.res → system.json</summary>
    public static string ListRelOf(string resExportPath)
    {
        string p = ToRel(resExportPath);
        int i = p.LastIndexOf('/');
        string dir = i < 0 ? "" : p[..i];
        string stem = Path.GetFileNameWithoutExtension(p);
        return dir.Length == 0 ? stem + ".json" : dir + "/" + stem + ".json";
    }

    /// <summary>相对路径（用 / 分隔），失败返回文件名</summary>
    public static string Rel(string baseDir, string full)
    {
        try
        {
            string r = Path.GetRelativePath(baseDir, full);
            return ToRel(r);
        }
        catch { return ToRel(full); }
    }

    public static string Sanitize(string name)
    {
        foreach (char c in "<>:\"|?*") name = name.Replace(c, '_');
        return name.Length == 0 ? "_" : name;
    }

    /// <summary>重名去重：name → name_0001</summary>
    public static string Dedup(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        string b = Path.GetFileNameWithoutExtension(name);
        string x = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            string c = $"{b}_{i:D4}{x}";
            if (used.Add(c)) return c;
        }
    }
}

/// <summary>
/// ★ 本体索引器：把更新补丁（DLC）里的 Type + Offset 映射回【本体已解包产物】里的 exportPath。
///
/// 数据来源（-xdlc 的前置要求，必须存在）：
///   &lt;cwd&gt;/1.out/list/package.json  package.rdp 块表 → (4, 块号) → exportPath
///   &lt;cwd&gt;/1.out/list/data.json     data.rdp   块表 → (5, 块号) → exportPath
///   &lt;cwd&gt;/1.out/list/system.json  仅作存在性校验（本体顶层 res 的 list）
///   以及 &lt;cwd&gt;/1.out/list/&lt;各 res 的 list&gt;（懒加载，只在需要匹配 C/D 条目时才读）
///
/// 两个用途：
///   ① res 级 —— 补丁 res 头部 +0x20 的 (BaseType, BaseOffset) 就是本体 res 的块号，
///      查块表即得 BaseExportPath（如 "system/god_menu.res"）；
///   ② 条目级 —— DLC 模式跳过的条目据 Type 走两条路：
///      Type 4/5 → 按 (类型, 块号) 查本体块表；
///      Type C/D → 在本体【对应 res 的 list】里按 (Group, Type, Offset) 三元组匹配（回退到忽略 Group）。
/// </summary>
public sealed class BaseIndex
{
    readonly string _cwd;
    /// <summary>本体块表：(offset 类型 4/5, 块号) → 该块在本体解包产物里的 exportPath</summary>
    readonly Dictionary<(int, long), (string ExportPath, bool Compressed, string Compress)> _blk = new();
    /// <summary>本体各 res 的 list（懒加载）：resExportPath → ResList</summary>
    readonly Dictionary<string, ResList> _resList = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本体各 res 的条目索引（懒加载）：resExportPath → EntryIdx</summary>
    readonly Dictionary<string, EntryIdx> _resIdx = new(StringComparer.OrdinalIgnoreCase);

    public bool Loaded { get; private set; }
    public int BlockCount => _blk.Count;

    /// <param name="listRoot">基准 list 根目录（绝对路径）。x=1 → 1.out/list，x=2 → 4.pack/list。</param>
    public BaseIndex(string cwd, string listRoot = null)
    {
        _cwd = cwd;
        _listRoot = listRoot;
    }

    string _listRoot;
    /// <summary>本体的 list 根目录（x=1 → 1.out/list；x=2 → 4.pack/list）</summary>
    public string ListRoot => _listRoot ?? Path.Combine(_cwd, Ws.OutDir, "list");

    /// <summary>★ -xdlc 的前置条件：基准 list 根目录下必须有 system.json 与 package.json</summary>
    public static bool HasBaseList(string cwd, out string why, string listRoot = null)
    {
        string root = listRoot ?? Path.Combine(cwd, Ws.OutDir, "list");
        string a = Path.Combine(root, "system.json"), b = Path.Combine(root, "package.json");
        bool okA = File.Exists(a), okB = File.Exists(b);
        if (okA && okB) { why = null; return true; }
        string rel = Ws.Rel(cwd, root);
        why = !okA && !okB
            ? $"缺少 {rel}/system.json 与 {rel}/package.json —— 请先在本体目录执行 -x 解包"
            : (!okA ? $"缺少 {rel}/system.json —— 请先在本体目录执行 -x 解包"
                    : $"缺少 {rel}/package.json —— 请先在本体目录执行 -x 解包");
        return false;
    }

    /// <summary>载入本体块表（package.json + data.json）。可重复调用，只真正载入一次。</summary>
    public void Load()
    {
        if (Loaded) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n1 = LoadBlocks(Path.Combine(ListRoot, "package.json"), Pres.TypePackage);
        int n2 = LoadBlocks(Path.Combine(ListRoot, "data.json"), Pres.TypeData);
        Loaded = true;
        Console.WriteLine($"  本体索引 : package.json {n1} 块 / data.json {n2} 块  ({sw.Elapsed.TotalSeconds:F1}s)");
    }

    int LoadBlocks(string path, int type)
    {
        if (!File.Exists(path)) return 0;
        var pl = JsonIo.Load<PackageList>(path);
        if (pl?.Entries == null) return 0;
        int n = 0;
        foreach (var e in pl.Entries)
        {
            n++;
            _blkEntries[(type, e.Block)] = e;
            if (!string.IsNullOrEmpty(e.ExportPath))
            {
                _blk[(type, e.Block)] = (e.ExportPath, e.Compressed, e.Compress);
                if (!_blkByPath.ContainsKey(e.ExportPath)) _blkByPath[e.ExportPath] = (type, e.Block);
            }
        }
        return n;
    }

    /// <summary>本体块号 → exportPath（type 取 Pres.TypePackage=4 / Pres.TypeData=5）</summary>
    public string ExportOfBlock(int type, long block)
        => _blk.TryGetValue((type, block), out var p) ? p.ExportPath : null;

    /// <summary>本体块号 → (exportPath, 是否压缩, 压缩格式)。★ 供 -cdlc 按基准 list 修正 4/5 的大小/解压大小。</summary>
    public (string ExportPath, bool Compressed, string Compress)? BlockInfo(int type, long block)
        => _blk.TryGetValue((type, block), out var v) ? v : null;

    /// <summary>★ 本体块表按 exportPath 反查：(类型, 块号, size, 压缩态, 压缩格式)。
    ///   -cdlc 用它把「基准 list 里的 4/5 条目」换成正确的本体块号与大小。
    ///   一个 exportPath 可能有多条（别名 / 多层级引用），取第一条即可（同一文件在本体只占一块）。</summary>
    public (int Type, long Block, long Size, bool Compressed, string Compress)? BlockOfExport(string exportPath)
    {
        if (string.IsNullOrEmpty(exportPath)) return null;
        if (!_blkByPath.TryGetValue(exportPath, out var key)) return null;
        var e = ExportEntryOf(key.Item1, key.Item2);
        var bi = BlockInfo(key.Item1, key.Item2);
        return (key.Item1, key.Item2, e?.Size ?? 0, bi?.Compressed ?? false, bi?.Compress);
    }
    readonly Dictionary<(int, long), PackageEntry> _blkEntries = new();
    /// <summary>exportPath → (类型, 块号)，LoadBlocks 时建，供 BlockOfExport O(1) 反查</summary>
    readonly Dictionary<string, (int, long)> _blkByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>基准 list 的块表条目（(类型, 块号) → PackageEntry，由 LoadBlocks 填好）—— 供 -cdlc 取 size / rawSize。</summary>
    public PackageEntry ExportEntryOf(int type, long block)
        => _blkEntries.TryGetValue((type, block), out var e) ? e : null;

    /// <summary>本体某个 res 的 list（懒加载；找不到返回 null）</summary>
    public ResList ResListOf(string resExportPath)
    {
        if (string.IsNullOrEmpty(resExportPath)) return null;
        if (_resList.TryGetValue(resExportPath, out var v)) return v;
        string p = Path.Combine(ListRoot, Ws.ToOs(Ws.ListRelOf(resExportPath)));
        ResList rl = null;
        if (File.Exists(p))
        {
            try { rl = JsonIo.Load<ResList>(p); }
            catch (Exception ex) { Console.WriteLine($"  !! 本体 res list 读取失败 {resExportPath} : {ex.Message}"); rl = null; }
        }
        _resList[resExportPath] = rl;
        return rl;
    }

    /// <summary>本体某个 res 的条目索引（懒加载）：(Group,Type,Offset) 与 (Type,Offset) 两套键</summary>
    EntryIdx EntryIdxOf(string resExportPath)
    {
        if (string.IsNullOrEmpty(resExportPath)) return null;
        if (_resIdx.TryGetValue(resExportPath, out var v)) return v;
        var idx = new EntryIdx();
        var rl = ResListOf(resExportPath);
        if (rl?.Files != null)
        {
            foreach (var it in rl.Files)
            {
                if (string.IsNullOrEmpty(it.ExportPath)) continue;      // dummy / 空条目没有路径，不参与匹配
                if (!TryParseOff(it.Offset, out long off)) continue;
                string ty = it.Type ?? "";
                int g = int.TryParse(it.Group, out var gg) ? gg : -1;
                if (!idx.ByGroup.ContainsKey((g, ty, off))) idx.ByGroup[(g, ty, off)] = it;
                if (!idx.ByTypeOff.ContainsKey((ty, off))) idx.ByTypeOff[(ty, off)] = it;
                string nm = (it.ElementName != null && it.ElementName.Count > 0) ? (it.ElementName[0] ?? "") : "";
                if (nm.Length > 0)
                {
                    var nk = (g, nm.ToLowerInvariant());
                    if (!idx.ByName.ContainsKey(nk)) idx.ByName[nk] = it;
                }
            }
        }
        _resIdx[resExportPath] = idx;
        return idx;
    }

    /// <summary>DLC 条目（Type C/D/4/5）→ 本体 exportPath（精简版，详见 EntryOf）</summary>
    public string ExportOfEntry(string baseResExportPath, int group, string type, long offset)
        => EntryOf(baseResExportPath, group, type, offset)?.ExportPath;

    /// <summary>
    /// DLC 条目（Type C/D/4/5）→ 本体对应 list 里的完整条目（含 Compression / Compress / ExportPath）。
    /// 先按 (Group, Type, Offset) 精确匹配，miss 再回退到 (Type, Offset)。
    /// 解包时用来把跳过条目的 Compression 状态复制成本体的真实值。
    /// </summary>
    public ResEntry EntryOf(string baseResExportPath, int group, string type, long offset)
    {
        var idx = EntryIdxOf(baseResExportPath);
        if (idx == null) return null;
        string ty = type ?? "";
        if (idx.ByGroup.TryGetValue((group, ty, offset), out var a)) return a;
        if (idx.ByTypeOff.TryGetValue((ty, offset), out var b)) return b;
        return null;
    }

    /// <summary>
    /// ★ 按 (组, 名字小写) 匹配本体条目。用于 DLC 打包时本体已经过 -c 重打包的场景：
    ///   此时本体 res 的内部偏移全变，(组,类型,偏移) 会 100% 失配；名字却是稳定的
    ///   （DLC 只为「本次新增的文件」重设名字），故作为首选键、偏移键作回退。
    /// </summary>
    public ResEntry EntryByName(string baseResExportPath, int group, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var idx = EntryIdxOf(baseResExportPath);
        if (idx == null) return null;
        return idx.ByName.TryGetValue((group, name.ToLowerInvariant()), out var e) ? e : null;
    }

    /// <summary>
    /// ★ 原版（-c 重打包【之前】）的 list 根目录。仅 -cdlc 2 时与 ListRoot 不同：
    ///   ListRoot = 4.pack/list（重打包后），这里 = 1.out/list（原版）。
    ///   用途：更新补丁（DLC）的 list_update 里，C/D 条目记的 offset 属于【原版 res 空间】的
    ///   数据偏移；靠它把该 offset 反查成【组内序号】，再到重打包后的 list 里取同序号条目。
    ///   依据：-c 重打包只改条目内的偏移 / 大小，不改组内条目顺序 —— 实测 god_m001_Title.res 的
    ///   1.out/list 与 4.pack/list 组内同序号条目 100% 同名同文件（393/393、253/253、19/19…）。
    /// </summary>
    public string OriginListRoot { get; set; }

    readonly Dictionary<string, ResList> _resListOrigin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原版 list 根目录下的本体 res list（懒加载）。OriginListRoot 为空 → 退回 ResListOf。</summary>
    ResList ResListOfOrigin(string resExportPath)
    {
        if (string.IsNullOrEmpty(OriginListRoot)) return ResListOf(resExportPath);
        if (string.IsNullOrEmpty(resExportPath)) return null;
        if (_resListOrigin.TryGetValue(resExportPath, out var v)) return v;
        string p = Path.Combine(OriginListRoot, Ws.ToOs(Ws.ListRelOf(resExportPath)));
        ResList rl = null;
        if (File.Exists(p))
        {
            try { rl = JsonIo.Load<ResList>(p); } catch { rl = null; }
        }
        _resListOrigin[resExportPath] = rl;
        return rl;
    }

    /// <summary>
    /// ★ 在原版 list 的指定组里，按数据偏移反查【组内序号】（0 基；唯一命中才返回，否则 -1）。
    ///   用来把 DLC 条目里记的「原版空间 offset」翻译成组内序号 —— 这是本体经 -c 重打包后
    ///   唯一稳定的映射键。★ 不能用 (组, 名字)：本体里同基名条目极多（Trg_Circle.gim ×5、
    ///   Face.gim ×2），甚至有同基名不同扩展名的兄弟（Save_Data_Import_Info_Restrict 同时有
    ///   .gim 与 .ssq），按名字只会「取第一条」，实测 369 条 C/D 里 83 条取错。
    /// </summary>
    public int SeqOfOffsetOrigin(string baseResExportPath, int group, long offset)
    {
        var rl = ResListOfOrigin(baseResExportPath);
        if (rl?.Files == null) return -1;
        int n = 0, hit = -1;
        foreach (var it in rl.Files)
        {
            if (!int.TryParse(it.Group, out var gg) || gg != group) continue;
            int seq = n++;
            if (TryParseOff(it.Offset, out long o) && o == offset)
            {
                if (hit >= 0) return -1;              // 该偏移在组内不唯一 → 不可用
                hit = seq;
            }
        }
        return hit;
    }

    /// <summary>★ 本体 res 指定组的第 seq 条条目（组内物理顺序，0 基）—— 与 SeqOfOffsetOrigin 配对使用。</summary>
    public ResEntry EntryBySeq(string baseResExportPath, int group, int seq)
    {
        if (seq < 0) return null;
        var rl = ResListOf(baseResExportPath);
        if (rl?.Files == null) return null;
        int n = 0;
        foreach (var it in rl.Files)
        {
            if (!int.TryParse(it.Group, out var gg) || gg != group) continue;
            if (n++ == seq) return it;
        }
        return null;
    }

    /// <summary>list 里的 Offset 字符串 → long。★兼容两代格式：纯十进制（新版）与 0x 前缀十六进制（旧版）。</summary>
    public static bool TryParseOff(string s, out long v)
    {
        v = 0;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out v);
        return long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    sealed class EntryIdx
    {
        public readonly Dictionary<(int, string, long), ResEntry> ByGroup = new();
        public readonly Dictionary<(string, long), ResEntry> ByTypeOff = new();
        /// <summary>★ (组, 名字小写) —— 本体经 -c 重打包后内部偏移会变，
        ///   (组,类型,偏移) 会整体失配；名字是稳定键（DLC 只为「新增文件」重设名字）。</summary>
        public readonly Dictionary<(int, string), ResEntry> ByName = new();
    }
}

/// <summary>package.rdp 的随机读取器（不整份载入内存）</summary>
public sealed class RdpReader : IDisposable
{
    readonly FileStream _fs;
    public long Length => _fs.Length;
    public RdpReader(string path) { _fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); }

    public byte[] Read(long offset, int length)
    {
        if (offset < 0 || offset >= _fs.Length) return null;
        long avail = Math.Min(length, _fs.Length - offset);
        var buf = new byte[avail];
        _fs.Seek(offset, SeekOrigin.Begin);
        int got = 0;
        while (got < avail)
        {
            int n = _fs.Read(buf, got, (int)(avail - got));
            if (n <= 0) break;
            got += n;
        }
        if (got == avail) return buf;
        Array.Resize(ref buf, got);
        return buf;
    }

    public void Dispose() => _fs?.Dispose();
}

/// <summary>解包上下文：rdp 读取 + 全局块表（GE2 正式版是双 rdp：package.rdp + data.rdp）</summary>
public sealed class Ctx : IDisposable
{
    public string Cwd;
    public string RootOut;
    public RdpReader Rdp;            // package.rdp（offset 类型 0x4）/ DLC 模式下则是 patch.rdp
    public RdpReader DataRdp;        // data.rdp（offset 类型 0x5）—— 双 rdp 变革后新增，可不存在
    public RdpReader PatchRdp;       // 更新补丁 patch.rdp（offset 类型 0x6）—— 仅 -xdlc 时有
    public int MaxDepth = 8;
    /// <summary>各 res 的 list 目录名（相对 1.out）：本体 "list"，更新补丁 "list_update"（★不含数据目录 update）</summary>
    public string ListDir = "list";
    /// <summary>非 null 表示更新补丁模式（决定哪些类型落盘）</summary>
    public Unpacker.DlcMode Dlc;
    /// <summary>★ 本体索引器（仅 -xdlc 用）：把补丁里的 Type+Offset 映射回本体 exportPath</summary>
    public BaseIndex BaseIdx;
    /// <summary>★ list-only 模式：只遍历 + 只写 list，不落数据文件、不统计文件数。
    ///   打包（-c）后用它对【刚打出来的 4.pack 产物】再跑一遍，得到"新偏移/新大小"的新 list。</summary>
    public bool ListOnly;
    /// <summary>★ list-only 模式下列表写到哪（绝对目录）。null = 用 RootOut/ListDir。</summary>
    public string ListOutDir;
    /// <summary>list-only 模式下跳过每条 res 的日志（只打汇总）</summary>
    public bool Quiet;

    readonly Dictionary<long, PackageEntry> _blocks = new();
    readonly Dictionary<long, PackageEntry> _dataBlocks = new();
    readonly Dictionary<long, PackageEntry> _patchBlocks = new();
    public IReadOnlyDictionary<long, PackageEntry> Blocks => _blocks;
    public IReadOnlyDictionary<long, PackageEntry> DataBlocks => _dataBlocks;
    public IReadOnlyDictionary<long, PackageEntry> PatchBlocks => _patchBlocks;

    /// <summary>package.rdp 的全部条目（每条都真实占用块）</summary>
    public IEnumerable<PackageEntry> AllEntries => _blocks.Values;

    /// <summary>data.rdp 的全部条目</summary>
    public IEnumerable<PackageEntry> AllDataEntries => _dataBlocks.Values;

    /// <summary>更新补丁 patch.rdp 的全部条目（offset 类型 0x6）</summary>
    public IEnumerable<PackageEntry> AllPatchEntries => _patchBlocks.Values;

    public void AddBlock(long block, long size, bool compressed, long rawSize, bool isRes,
                         string fileName, string exportPath, string resId, string group, int index,
                         string compress = null)
        => Add(_blocks, block, size, compressed, rawSize, isRes, fileName, exportPath, resId, group, index, compress);

    /// <summary>data.rdp 侧条目（offset 类型 0x5）—— 与 package.rdp 分开记，各自输出一份 list</summary>
    public void AddDataBlock(long block, long size, bool compressed, long rawSize, bool isRes,
                             string fileName, string exportPath, string resId, string group, int index,
                             string compress = null)
        => Add(_dataBlocks, block, size, compressed, rawSize, isRes, fileName, exportPath, resId, group, index, compress);

    /// <summary>更新补丁 patch.rdp 侧条目（offset 类型 0x6）—— 单独一块表，输出 1.out/list_update/patch.json</summary>
    public void AddPatchBlock(long block, long size, bool compressed, long rawSize, bool isRes,
                             string fileName, string exportPath, string resId, string group, int index,
                             string compress = null)
        => Add(_patchBlocks, block, size, compressed, rawSize, isRes, fileName, exportPath, resId, group, index, compress);

    static void Add(Dictionary<long, PackageEntry> dst, long block, long size, bool compressed, long rawSize, bool isRes,
                    string fileName, string exportPath, string resId, string group, int index, string compress = null)
    {
        var rf = new EntryRef { Res = resId, Group = group, Index = index };
        if (dst.TryGetValue(block, out var e))
        {
            if (e.Size != size)
                throw new InvalidDataException($"块 {block} 冲突：size 0x{e.Size:X} vs 0x{size:X}（{e.FileName} / {fileName}）");
            if (e.ExportPath != exportPath)
            {
                e.Aliases ??= new List<string>();
                if (!e.Aliases.Contains(exportPath)) e.Aliases.Add(exportPath);
            }
            e.Refs.Add(rf);
        }
        else
        {
            dst[block] = new PackageEntry
            {
                Block = block, Size = size,
                BlockCount = (size + Pres.BlockSize - 1) / Pres.BlockSize,
                Compressed = compressed, Compress = compress, RawSize = rawSize, IsRes = isRes,
                FileName = fileName, ExportPath = exportPath,
                Refs = new List<EntryRef> { rf },
            };
        }
    }

    public void Dispose() { Rdp?.Dispose(); DataRdp?.Dispose(); PatchRdp?.Dispose(); }
}
