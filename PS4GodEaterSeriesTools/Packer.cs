using System.IO.Compression;

namespace PS4GodEaterSeriesTools;

/// <summary>
/// 打包：按 package.json 顺序重建 package.rdp，最后生成顶层 res。
///   数据来源：2.new/&lt;路径&gt; 优先，其次 1.out/&lt;路径&gt;
///   list 来源：2.new/list/&lt;路径&gt;.json 优先，其次 1.out/list/...
///   中间产物：3.temp/（重打包的 res、压缩后的文件）
///   最终产物：4.pack/package.rdp + 4.pack/&lt;顶层res&gt;
///
/// 块位策略（顺移）：按 block 升序整份重写。每条占 max(新块数, 原块数) —— 变小不回收空间，
///   位置保持稳定；只有真正变大的条目会把它之后的条目整体后移，条目间空隙与尾部原样保留。
///   （替换成更大的文件时，它在本位就地扩展，不再被甩到 rdp 末尾。）
///
/// 递归重打包：list 里任何条目，只要其数据本身是 Pres 且能找到它自己的 list，
///   就先用那份 list 现场重建它（见 PackRes）——覆盖 rdp 侧子 res 与 local 内嵌 res，
///   并按组内原位置嵌回父 res。
///
/// rtbl 重打包：rtbl 条目（*.rtbl，由 res 的组 7 引用）不是 Pres，而是「每 0x200 一槽的 rdp 引用表」，
///   用它的固定槽位 JSON 现场重建（见 PackRtbl）：每个槽位回填写【该 res 在新 rdp 上的块号/size】。
///   因此 rtbl 必须等它引用的条目构建完 —— 那些条目的 EntryRef.Res 正是该 rtbl 的 exportPath，
///   ReadyS 会自然把它们排到 rtbl 之前。
/// </summary>
public static class Packer
{
    /// <summary>
    /// 顺移打包（唯一模式）：按 block 升序整份重写 package.rdp。
    ///   每条占 max(新块数, 原块数) —— 变小不回收空间（补零），位置保持稳定；
    ///   真正变大的条目在本位就地扩展，其后条目整体后移，条目间空隙与尾部原样搬运。
    /// </summary>
    /// <param name="reuseOrig">压缩条目来源：
    ///   false（默认，-c）= 全部从 2.new/1.out 明文直接压缩 —— 原生 zlib 后端与游戏压缩字节级一致，
    ///                     原 package.rdp 无需在位；
    ///   true（-c o）= 未编辑条目复用原 rdp 原始压缩段（旧行为，双保险）。</param>
    public static int Run(string cwd, string resName, string dataRdpName = "data.rdp", bool reuseOrig = false)
    {
        string packDir = Path.Combine(cwd, Ws.PackDir);
        Directory.CreateDirectory(packDir);
        string outRes = Path.Combine(packDir, resName);

        // ---- rdp 集合：package.rdp（必需）+ data.rdp（GE2 正式版双 rdp 变革后可选）----
        var sets = new List<RdpSet>();
        string pkgPath = Ws.ResolveList(cwd, "package.json");
        if (pkgPath == null)
        {
            Console.Error.WriteLine($"错误: 找不到 package.json（应位于 {Ws.NewDir}/list/ 或 {Ws.OutDir}/list/）");
            return 1;
        }
        string rdp1 = Path.Combine(cwd, "package.rdp");
        if (!File.Exists(rdp1)) { Console.Error.WriteLine($"错误: 找不到 package.rdp（目录 {cwd}）"); return 1; }
        sets.Add(new RdpSet(Pres.TypePackage, "package.rdp", rdp1,
                            Path.Combine(packDir, "package.rdp"), Ws.Rel(cwd, pkgPath), JsonIo.Load<PackageList>(pkgPath)));

        if (!string.IsNullOrEmpty(dataRdpName))
        {
            string dj = Ws.ResolveList(cwd, "data.json");
            string rdp2 = Path.Combine(cwd, dataRdpName);
            bool hasJson = dj != null, hasRdp = File.Exists(rdp2);
            if (hasJson != hasRdp)
                Console.WriteLine($"  注意 : data.json 与 {dataRdpName} 只存在其一 —— 跳过 data.rdp（不重建）");
            else if (hasJson)
                sets.Add(new RdpSet(Pres.TypeData, dataRdpName, rdp2,
                                    Path.Combine(packDir, dataRdpName), Ws.Rel(cwd, dj), JsonIo.Load<PackageList>(dj)));
        }
        foreach (var s in sets) s.Measure();

        Console.WriteLine("== 打包 ==");
        Console.WriteLine("  模式  : 顺移（变大的条目原位扩展，其后条目整体后移；未变化区域含空隙原样保留）");
        Console.WriteLine($"  压缩  : {(reuseOrig ? "复用原始压缩段优先（-c o），未编辑条目沿用原 rdp 字节"
                                            : "全部从 2.new/1.out 明文直接压缩（原生 zlib L9/M8，与游戏字节级一致）")}");
        foreach (var s in sets)
            Console.WriteLine($"  块表  : {s.JsonRel}  ({s.List.Entries.Count} 条)   源 {s.Name} {s.OriginSize} 字节 / {s.TotalBlocks} 块");
        Console.WriteLine($"  输出  : {Ws.PackDir}/" + string.Join(" + " + Ws.PackDir + "/", sets.Select(s => s.Name))
                          + $" + {Ws.PackDir}/{resName}");
        Console.WriteLine();

        var loc = new Dictionary<(string, int, int), (int type, long block, long size)>();
        // 条目【还原后大小】—— 编辑过压缩文件后长度会变，父 res 的 TOC +0x34 必须跟着改
        var decLen = new Dictionary<(string, int, int), long>();
        var resCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rtblCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rtblSlotsCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int nComp = 0, nResPack = 0, nReuse = 0, nRtbl = 0, nInnerComp = 0, nInnerReuse = 0;

        // ★ res 路径 → 原始 res 文件字节（懒加载）。
        //   顶层 res = 工作目录里的原件；子 res 在 rdp 上 ⇒ 由 BuildOne 传进来的 origRes。
        var origResOf = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // res 路径 → 它原始 TOC 的条目表（懒解析一次）。ReadOrigInnerSegment 会被每个条目调用一次，
        //   不缓存的话每条都要重新 ParseToc 整个 res（god_menu.res 有上千条目 ⇒ 平方级开销）。
        var origTocOf = new Dictionary<string, List<TocEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>res 内部条目的压缩态写入：
        ///   · Compression=false → 原样（tbl/string/rtbl 本来就不压；未压缩 local 也不压）
        ///   · Compression=true  → reuseOrig 时先试原始压缩段：解压后与明文一致就【原样复用】
        ///                        （零改动往返的旧保险）；不一致或未启用 reuseOrig 才按 Compress 重压，
        ///                        此时 +0x34 由 Pres.Build 写明文长度。</summary>
        byte[] EncodeResInner(byte[] origResBytes, int g, int gi, ResEntry it, byte[] plain, string resPath)
        {
            if (!it.Compression || string.IsNullOrEmpty(it.Compress))
                return plain;

            byte[] orig = reuseOrig ? ReadOrigInnerSegment(origResBytes, resPath, g, gi, it) : null;
            bool same = orig != null && SameAsCompressed(orig, plain);
            if (same) { nInnerReuse++; return orig; }

            nInnerComp++;
            byte[] enc = it.Compress == "blz4" ? Blz4.Compress(plain) : Dflt(plain);
            WriteTemp(cwd, it.ExportPath ?? "", enc);
            return enc;
        }

        /// <summary>从【原始 res】里按 TOC 取出某个条目的原始字节（不解压）；取不到返回 null。</summary>
        byte[] ReadOrigInnerSegment(byte[] origResBytes, string resPath, int g, int gi, ResEntry it)
        {
            byte[] orig = origResBytes;
            if (orig == null && !origResOf.TryGetValue(resPath, out orig)) orig = null;
            if (orig == null || !Pres.IsPres(orig)) return null;

            // 原始 TOC 只解析一次
            if (!origTocOf.TryGetValue(resPath, out var entries))
            {
                try { entries = Pres.ParseToc(orig).entries; }
                catch { entries = new List<TocEntry>(); }
                origTocOf[resPath] = entries;
            }

            // 按组序在 TOC 里找「组 g 的第 gi 条」
            TocEntry te = null;
            int seen = -1;
            foreach (var e in entries)
            {
                if (e.Group != g) continue;
                if (++seen == gi) { te = e; break; }
            }
            if (te == null || te.Size == 0) return null;
            // table(G5) 的 size 记的是 u32 个数（仅「在 res 内」的 table 成立）
            long len = (g == 5 && !Pres.IsRdp(te.OffsetType)) ? te.Size * 4 : te.Size;
            long off = te.Offset;
            if (off < 0 || off + len > orig.Length) return null;
            var buf = new byte[len];
            Array.Copy(orig, off, buf, 0, len);
            return buf;
        }

        // 条目 → 它所属的 rdp 集合（同一条目只属于一个 rdp：0x4 → package / 0x5 → data）
        var entrySet = new Dictionary<PackageEntry, RdpSet>();

        // res 路径 → 该 res 引用的全部 rdp 条目（两个 rdp 合并统计）。
        //   构建某个 res 之前，必须先让它引用的条目统统「落位」（写进新 rdp 并登记 loc），
        //   否则 res 内记录的 offset 会指向旧块位、或干脆查不到记录。
        //   注意：包里 res 与其被引用条目的 block 顺序并不保证「被引用者在前」
        //   （例：新包 system/god_menu.res 在 block 4，而它引用的 2188 条全部 block > 4），
        //   所以不能只靠「按 block 升序边扫边建」。
        var resRefs = new Dictionary<string, List<PackageEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets)
            foreach (var e0 in s.List.Entries)
            {
                entrySet[e0] = s;
                foreach (var r0 in e0.Refs ?? new List<EntryRef>())
                {
                    if (string.IsNullOrEmpty(r0.Res)) continue;
                    if (!resRefs.TryGetValue(r0.Res, out var l0)) resRefs[r0.Res] = l0 = new List<PackageEntry>();
                    l0.Add(e0);
                }
            }

        // ★ 日志开关：第 1 轮先缓冲 —— 预案够用（没出现变长重排）就一次性输出；
        //   若检测到条目变长需要重划块位，则丢弃第 1 轮日志、实时输出第 2 轮，避免刷屏两遍。
        bool buffering = true;
        var logBuf = new List<string>();
        void Log(string s) { if (buffering) logBuf.Add(s); else Console.WriteLine(s); }

        /// <summary>顺移模式的【布局预案】：每条占 max(新块数, 原块数) —— 变小不回收，
        ///   于是「没变大的条目」位置完全不动，只有真正变大的条目会把它之后的条目整体后移；
        ///   条目之间的空隙也原样保留。非 res 的新长度取文件大小 / 上一轮实测长度。
        ///   real = 上一轮【实测写入长度】：res 重建后长度事先不可知（用户替换了内部文件就会变长），
        ///   靠它迭代收敛（见下方「迭代重排」）。块位是各自 rdp 文件内的，两个 rdp 各算一套。
        ///   注意：预案只定【块位】。loc 表（父 res 记录子里填的 offset/size）必须用
        ///   构建阶段的【真实写入长度】登记 —— 压缩条目重压后长度会变（.NET 压缩器与游戏原生不同），
        ///   用预案的旧长度会写出 size 错位的坏包（游戏解压截断）。</summary>
        void BuildPlan(IReadOnlyDictionary<PackageEntry, long> real)
        {
            foreach (var s in sets)
            {
                long pc = 0, psEnd = 0;
                foreach (var e in s.List.Entries.OrderBy(x => x.Block))
                {
                    if (e.Block > psEnd) pc += e.Block - psEnd;           // 空隙照搬
                    long len = PlanLen(e, real);
                    long occupy = Math.Max(NBlk(len), NBlk(e.Size));
                    s.Plan[e] = (pc, occupy, len);
                    pc += occupy;
                    psEnd = e.Block + NBlk(e.Size);
                }
                s.ReflowBlocks = pc + (s.TotalBlocks > psEnd ? s.TotalBlocks - psEnd : 0);
                Console.WriteLine($"  预案  : [{s.Name}] {s.Plan.Count} 条 → {s.ReflowBlocks} 块（原 {s.TotalBlocks} 块）");
            }
        }

        // rtbl 的槽位数（= 重建后 rtbl 文件字节数 ÷ 0x200）。
        //   父 res 的 G7 条目在名字区之后要按它生成「槽位 bit 表」，所以构建 res 时必须知道。
        int RtblSlotsOf(string exportPath)
        {
            if (rtblSlotsCache.TryGetValue(exportPath, out var v)) return v;
            string jp = Ws.ResolveList(cwd, Ws.ListRelOf(exportPath));
            if (jp == null)
                throw new FileNotFoundException(
                    $"找不到 rtbl list: {Ws.NewDir}/list/{Ws.ListRelOf(exportPath)} 或 {Ws.OutDir}/list/{Ws.ListRelOf(exportPath)}");
            v = Rtbl.SlotCountOf(JsonIo.Load<RtblList>(jp));
            rtblSlotsCache[exportPath] = v;
            return v;
        }

        // 重建一个 res。depth==0 = 顶层 res（不写 3.temp）；depth>0 = 子 res / 内嵌 res（写 3.temp）。
        // 递归规则：list 里任何条目，只要数据本身是 Pres 且能找到它自己的 list，就现场重打包
        //   —— 同时涵盖 ① rdp 侧子 res（G0/G1/G4/G7）  ② res 内嵌子 res（local G2）
        //   组内顺序严格按 list 的 Files 出现顺序，所以 local 里的 res 会按它原本的位置嵌回。
        byte[] PackRes(string resExportPath, int depth, byte[] origRes = null)
        {
            if (resCache.TryGetValue(resExportPath, out var cached)) return cached;

            string listRel = Ws.ListRelOf(resExportPath);
            string listPath = Ws.ResolveList(cwd, listRel);
            if (listPath == null)
                throw new FileNotFoundException(
                    $"找不到 list: {Ws.NewDir}/list/{listRel} 或 {Ws.OutDir}/list/{listRel}");

            var js = JsonIo.Load<ResList>(listPath);
            string resId = resExportPath;

            var bytes = Pres.Build(js,
                (it, g, gi) =>
                {
                    string rel = !string.IsNullOrEmpty(it.ExportPath) ? it.ExportPath
                               : (it.FileName ?? "").TrimStart('\\');
                    string p = Ws.ResolveData(cwd, rel);
                    if (p == null) throw new FileNotFoundException($"找不到数据文件: {rel}   (被 {resExportPath} 引用)");
                    if (depth < MaxResDepth && Pres.IsPresFile(p)
                        && Ws.ResolveList(cwd, Ws.ListRelOf(rel)) != null)
                        // 内嵌 / 子 res：递归重打包。原始字节从【原始 res】里按 TOC 位置取
                        //   （它自己也可能有压缩的内部条目，reuseOrig 时靠原始字节复用压缩段）
                        return PackRes(rel, depth + 1,
                                       reuseOrig ? ReadOrigInnerSegment(origRes, resExportPath, g, gi, it) : null);
                    return File.ReadAllBytes(p);
                },
                (g, gi) => loc.TryGetValue((resId, g, gi), out var v) ? v : (-1, -1L, -1L),
                RtblSlotsOf,                                     // ★ rtbl 的槽位数（写「槽位 bit 表」用）
                // ★ 条目还原后大小：编辑过压缩文件则用真值，否则沿用 list 的 decsize
                (g, gi, it) => decLen.TryGetValue((resId, g, gi), out var dv) ? dv : (long?)null,
                // ★ res 内部条目（Type C/D）的压缩态写入。Type 只回答「数据在哪张 rdp」，
                //   压缩与否只看 list 的 Compression/Compress —— GE2RB 的 local(G2) 就是 Type=C + blz4。
                (g, gi, it, plain) => EncodeResInner(origRes, g, gi, it, plain, resExportPath));

            resCache[resExportPath] = bytes;
            if (depth > 0)
            {
                WriteTemp(cwd, resExportPath, bytes);
                nResPack++;
                Log($"  {new string(' ', depth * 2)}· 重打包 res  {resExportPath}  →  {bytes.Length} 字节");
            }
            return bytes;
        }

        // 重建一个 rtbl（rtbl 已接入工具，见 Rtbl.cs）：
        //   数据来源 = 它自己的 list（固定槽位 JSON），每个槽位记的是「被引用条目在新 rdp 上的块号/size」，
        //   所以要等它引用的条目统统构建完才能构建（调度见 ReadyS —— 那些条目的 EntryRef.Res 正是本 rtbl）。
        byte[] PackRtbl(string rtblExportPath)
        {
            if (rtblCache.TryGetValue(rtblExportPath, out var cached)) return cached;

            string listPath = Ws.ResolveList(cwd, Ws.ListRelOf(rtblExportPath));
            if (listPath == null)
                throw new FileNotFoundException(
                    $"找不到 rtbl list: {Ws.NewDir}/list/{Ws.ListRelOf(rtblExportPath)} 或 {Ws.OutDir}/list/{Ws.ListRelOf(rtblExportPath)}");

            var rj = JsonIo.Load<RtblList>(listPath);
            byte[] bytes = Rtbl.Build(rj, slot =>
                loc.TryGetValue((rtblExportPath, 7, slot), out var v) ? v : (-1, -1L, -1L));
            rtblCache[rtblExportPath] = bytes;
            WriteTemp(cwd, rtblExportPath, bytes);
            nRtbl++;
            Log($"  · 重打包 rtbl  {rtblExportPath}  →  {bytes.Length} 字节（{rj.Files.Count} 条 / {Rtbl.SlotCountOf(rj)} 槽）");
            return bytes;
        }

        // 顺移预案用：数据文件的实际大小（只取 FileInfo.Length，不读内容）
        long FileLen(string rel)
        {
            string p = Ws.ResolveData(cwd, rel);
            if (p == null) throw new FileNotFoundException($"找不到数据文件: {rel}");
            return new FileInfo(p).Length;
        }

        // 预案长度：res 取原 size（只占位，真实长度构建时登记）；rtbl 取「重建后长度」
        //   （= max(tablecount, 最大槽位+1) × 0x200，增删槽位后长度会变，必须按新长度预留，
        //     否则增表会被预案的旧长度卡住报错）；其余取文件大小。
        // ★ 压缩条目（Compression=true）必须取【原压缩段长度 e.Size】而不是明文文件长度：
        //   1.out 里躺的是解压后的明文（可能几十 KB），而零改动往返时 BuildOne 会复用原始压缩字节
        //   （几百字节）。若按明文长度预留 occupy，该条会多占 max(明文块, 原块) 个块，
        //   把其后所有条目块号整体推后 —— 表现为 rdp 变大 N 块、res 的 TOC 块号全部偏移。
        long PlanLen(PackageEntry e, IReadOnlyDictionary<PackageEntry, long> real)
        {
            // ★ 上一轮【实测写入长度】优先：res 重建后长度事先不可知（用户替换了内部文件就会变长），
            //   用原 size 占位会让它跨块、预案失效 —— 第 2 轮起一律按实测值占位。
            if (real != null && real.TryGetValue(e, out var rl) && rl > 0) return rl;
            if (e.IsRes) return e.Size;
            if (Rtbl.IsPath(e.ExportPath)) return (long)RtblSlotsOf(e.ExportPath) * Rtbl.SlotSize;
            if (e.Compressed) return e.Size;
            return FileLen(e.ExportPath);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        {
            // 顺移分两阶段：
            //   阶段 1 —— 按【依赖序】（子先父后）构建每条的最终字节；每条落位后立刻用
            //             【真实写入长度】登记 loc，这样父 res 记录子条目的 offset/size 才是对的。
            //             阶段 1 不写 rdp，只把字节留在内存。块位取自预案（预案保证位置稳定）。
            //   阶段 2 —— 按预案块位顺序把字节写进新 rdp，条目间空隙与尾部原样搬运。
            // 之所以要分两阶段：GEB 里父 res 常常排在子条目【之前】（block 更小），
            //   若边扫边建，父 res 构建时子条目的真实长度还不知道，只能拿旧长度凑 —— 必错。
            var built = new Dictionary<PackageEntry, byte[]>(Math.Max(16, sets.Sum(s => s.List.Entries.Count)));

            // 两个 rdp 的全部条目（合并后统一调度）
            var allEntries = sets.SelectMany(s => s.List.Entries).ToList();
            // 本轮构建中「超出预案占位」的条目数（>0 则需按实测长度重划块位后重建）
            int nOverflow = 0;

            void BuildOne(PackageEntry e)
            {
                var set = entrySet[e];
                byte[] d;
                if (Rtbl.IsPath(e.ExportPath)) d = PackRtbl(e.ExportPath);
                else if (e.IsRes)
                {
                    // ★ 把【原始 res 字节】传给 PackRes —— 复用模式（-c o）下它内部的压缩条目
                    //   要靠它复用原始压缩段；未启用复用时不必读原 rdp（全部走明文重压）。
                    byte[] origRes = reuseOrig ? ReadOrigSegment(set.Origin, e.Block, e.Size) : null;
                    if (origRes != null && Pres.IsBlz4(origRes)) origRes = Blz4.Decompress(origRes);
                    else if (origRes != null && Pres.IsDflt(origRes)) origRes = UndfltAny(origRes);
                    origResOf[e.ExportPath] = origRes;
                    d = PackRes(e.ExportPath, 1, origRes);
                }
                else
                {
                    string p = Ws.ResolveData(cwd, e.ExportPath);
                    if (p == null) throw new FileNotFoundException($"找不到数据文件: {e.ExportPath}");
                    d = File.ReadAllBytes(p);
                }

                // 压缩条目：默认全部从明文重压（原生 zlib L9/M8 与游戏压缩字节级一致）；
                //   复用模式（-c o）下没被编辑就复用原始压缩字节（长度不变 ⇒ 布局也不受影响）
                long plainLen = d.Length;                 // ★ 还原后（解压后）长度 → 写进父 res 的 TOC +0x34
                if (e.Compressed)
                {
                    string cfmt = e.Compress ?? "dflt";
                    byte[] orig = reuseOrig ? ReadOrigSegment(set.Origin, e.Block, e.Size) : null;
                    if (orig != null && SameAsCompressed(orig, d)) { d = orig; nReuse++; }
                    else
                    {
                        d = cfmt == "blz4" ? Blz4.Compress(d) : Dflt(d);
                        nComp++; WriteTemp(cwd, e.ExportPath, d);
                    }
                }

                var pl = set.Plan[e];
                // ★ 超出预案占位（res 重建后变长跨块 / 压缩文件重压后变大）不再直接报错，
                //   改为记数：本轮构建完成后按【实测长度】重划块位并重建，直到每条都落在占位内
                //   （见下方「迭代重排」）。零改动往返时不会触发，行为与原版完全一致。
                if (NBlk(d.Length) > pl.occupy) nOverflow++;

                // ★ 用真实长度登记，而不是预案里的旧长度；type 决定父 res 里 offset 的高 4 位
                foreach (var r in e.Refs ?? new List<EntryRef>())
                {
                    int gi = SlotInt(r.Group);
                    if (gi < 0) continue;
                    loc[(r.Res, gi, r.Index)] = (set.Kind, pl.dst, d.Length);
                    // ★ tbl(5)/string(6)/rtbl(7) 的 decsize 恒 0（本来就在 res 包内 / 游戏不压缩它们）
                    if (gi is not (5 or 6 or 7)) decLen[(r.Res, gi, r.Index)] = plainLen;
                }
                built[e] = d;
            }

            // 一轮完整构建：按【依赖序】（子先父后）构建每条 —— 普通条目 → res / rtbl。
            //   父 res 常常排在子条目【之前】（block 更小），若边扫边建，父 res 构建时子条目的
            //   真实长度还不知道，只能拿旧长度凑 ⇒ 必错，所以要先把被引用的条目全部建完。
            void BuildAll()
            {
                built.Clear();
                var doneS = new HashSet<PackageEntry>();

                bool ReadyS(PackageEntry e)
                {
                    if (!resRefs.TryGetValue(e.ExportPath, out var kids)) return true;
                    foreach (var k in kids) if (!doneS.Contains(k)) return false;
                    return true;
                }

                // ① 普通条目（无依赖）
                foreach (var e in allEntries.Where(x => !x.IsRes && !Rtbl.IsPath(x.ExportPath)).OrderBy(x => x.Block))
                {
                    BuildOne(e);
                    doneS.Add(e);
                }

                // ② res 与 rtbl：引用的条目全部构建完才构建自己
                //    （res 引用 rdp 条目；rtbl 也引用 rdp 条目 —— 它的每个槽位都要填被引用条目的新块号/size）
                var pendS = allEntries.Where(x => x.IsRes || Rtbl.IsPath(x.ExportPath)).OrderBy(x => x.Block).ToList();
                while (pendS.Count > 0)
                {
                    var next = new List<PackageEntry>();
                    foreach (var e in pendS)
                    {
                        if (!ReadyS(e)) { next.Add(e); continue; }
                        BuildOne(e);
                        doneS.Add(e);
                    }
                    if (next.Count == pendS.Count)
                        throw new InvalidDataException(
                            $"打包无法收敛：{next.Count} 个 res 的被引用条目始终未能构建（疑似 res 引用成环），例：{next[0].ExportPath}");
                    pendS = next;
                }
            }

            // ★ 迭代重排：第 1 轮按【原块数】占位构建 —— 若某条构建后超出占位（典型：用户替换了
            //   res 内部文件，重建后变长跨块），就按本轮【实测长度】重划块位（其后条目整体后移）
            //   再重建，直到所有条目都落在自己的占位内。
            //   实测长度不依赖块位（res 长度只由内部文件长度决定）⇒ 必然在 2 轮内收敛。
            //   零改动往返时第 1 轮即收敛，行为与改造前完全一致。
            var realLen = new Dictionary<PackageEntry, long>();
            int rounds = 0, nOver = 0;
            for (int round = 0; round < 6; round++)
            {
                rounds = round + 1;
                BuildPlan(round == 0 ? null : realLen);

                // 每轮都重建：res 内部记录的子条目 offset/size 依赖 loc，而 loc 依赖块位
                //   （块位要到第 2 轮才正确），所以第 2 轮必须重新构建全部 res 与 rtbl。
                resCache.Clear(); rtblCache.Clear(); loc.Clear(); decLen.Clear();
                nResPack = nRtbl = nComp = nReuse = nInnerComp = nInnerReuse = 0;
                nOverflow = 0;
                BuildAll();

                // 收敛判断：每条是否都落在自己的占位内
                realLen.Clear();
                int over = 0;
                foreach (var e in allEntries)
                {
                    long L = built[e].Length;
                    realLen[e] = L;
                    if (NBlk(L) > entrySet[e].Plan[e].occupy) over++;
                }
                nOver = over;
                if (over == 0) break;
                if (round == 0) { buffering = false; logBuf.Clear(); }
                Console.WriteLine($"  重排  : {over} 条超出预案占位（条目变长跨块），按实测长度重划块位并重建 → 第 {rounds + 1} 轮");
            }
            if (nOver > 0)
                throw new InvalidDataException($"顺移重排未收敛（仍有 {nOver} 条超占位）—— 疑似条目长度随块位震荡，请反馈此例");
            if (rounds == 1) foreach (var l in logBuf) Console.WriteLine(l);

            // 阶段 2：每个 rdp 各按自己的预案块位写出
            foreach (var s in sets)
            {
                if (File.Exists(s.OutPath)) File.Delete(s.OutPath);
                using var fs = new FileStream(s.OutPath, FileMode.Create, FileAccess.Write, FileShare.None);
                long cur = 0, sEnd = 0;
                foreach (var e in s.List.Entries.OrderBy(x => x.Block))
                {
                    var pl = s.Plan[e];
                    if (e.Block > sEnd)                                  // 空隙原样搬运
                    {
                        CopyOrig(fs, s.Origin, sEnd, e.Block - sEnd, cur);
                        cur += e.Block - sEnd;
                    }

                    byte[] d = built[e];
                    long grew = NBlk(d.Length) - NBlk(e.Size);
                    if (grew > 0)
                        Console.WriteLine($"  → 原位扩展 [{s.Name}] block {e.Block}  +{grew} 块  {(e.IsRes ? "[res] " : "")}{e.FileName}");

                    fs.Seek(cur << Pres.RdpShift, SeekOrigin.Begin);
                    fs.Write(d, 0, d.Length);
                    long padLen = pl.occupy * Pres.BlockSize - d.Length;     // 比原块数小的部分补零（不回收空间）
                    if (padLen > 0) { var z = new byte[padLen]; fs.Write(z, 0, z.Length); }
                    if (pl.occupy == NBlk(d.Length)) s.NInplace++; else s.NAppend++;
                    cur += pl.occupy;
                    sEnd = e.Block + NBlk(e.Size);
                }
                if (sEnd < s.TotalBlocks)
                {
                    CopyOrig(fs, s.Origin, sEnd, s.TotalBlocks - sEnd, cur);
                    cur += s.TotalBlocks - sEnd;
                }
            }
        }
        sw.Stop();

        // ---- 顶层 res ----
        // 顶层 res 也登记原始字节 —— 它内部的压缩条目（GE2RB 的 system.res 有若干 blz4）
        //   要靠它复用原始压缩段才能保持零改动往返逐字节一致。
        string topOrigPath = Path.Combine(cwd, resName);
        byte[] topOrig = File.Exists(topOrigPath) ? File.ReadAllBytes(topOrigPath) : null;
        origResOf[resName] = topOrig;
        byte[] top = PackRes(resName, 0);
        File.WriteAllBytes(outRes, top);

        Console.WriteLine();
        Console.WriteLine($"== 完成 ({sw.Elapsed.TotalSeconds:F1}s) ==");
        foreach (var s in sets)
        {
            long sz = new FileInfo(s.OutPath).Length;
            Console.WriteLine($"  {s.Name,-11}: 顺移 {s.List.Entries.Count} 条 → {s.ReflowBlocks} 块（原位 {s.NInplace} / 顺移 {s.NAppend}）  →  {sz} 字节 ({(sz - s.OriginSize >= 0 ? "+" : "")}{sz - s.OriginSize})");
            if (sz == s.OriginSize)
            {
                bool same = SameFile(s.Origin, s.OutPath);
                Console.WriteLine($"  零改动校验 [{s.Name}]: {(same ? "byte-identical ✓" : "与原始不同 ✗")}");
            }
        }
        if (nComp > 0 || nReuse > 0)
            Console.WriteLine($"  压缩 : {nComp} 条重新压（blz4/dflt，原生 zlib L9/M8）"
                              + (reuseOrig ? $" / {nReuse} 条复用原始字节" : "（全部重压模式，未复用原始段）")
                              + (nComp > 0 ? $"（新产物已写入 {Ws.TempDir}/）" : ""));
        if (nInnerComp > 0 || nInnerReuse > 0)
            Console.WriteLine($"  内部压缩 : {nInnerComp} 条重新压回 res"
                              + (reuseOrig ? $" / {nInnerReuse} 条复用原始字节" : "（全部重压模式）"));
        if (nResPack > 0) Console.WriteLine($"  res  : 现场重打包 {nResPack} 个（产物已写入 {Ws.TempDir}/）");
        if (nRtbl > 0) Console.WriteLine($"  rtbl : 现场重打包 {nRtbl} 个（按固定槽位 JSON 回填块号/size；产物已写入 {Ws.TempDir}/）");
        foreach (var s in sets)
            Console.WriteLine($"  输出 : {Ws.Rel(cwd, s.OutPath)}  ({new FileInfo(s.OutPath).Length} 字节)");
        Console.WriteLine($"         {Ws.Rel(cwd, outRes)}  ({top.Length} 字节)");

        // ---- ★ 生成【新 list】到 4.pack/list ----
        //   本体一旦被修改，原本 package.rdp / data.rdp 里的块位、以及 res 内 C/D 条目的文件内偏移都会变动。
        //   打包 DLC 时，带 BaseType 的 res 头部 +0x20 / +0x28 必须改成【本体 res 的新块号 / 新字节数】，
        //   其 4 / 5 / C / D 条目的 offset / size / decsize 也必须跟着改 —— 依据就是这份新 list。
        //   做法：对刚写出的 4.pack 产物再跑一遍 list-only 解包（只遍历、只写 list，不落数据）。
        //   ★ exportPath 前缀与 1.out/list 完全一致，所以新旧 list 可按 exportPath 逐条对应。
        Console.WriteLine();
        Console.WriteLine("== 生成新 list（4.pack/list） ==");
        string newListDir = Path.Combine(packDir, "list");
        try
        {
            int rcList = Unpacker.RunListOnly(cwd, resName, "package.rdp",
                                             sets.Count > 1 ? dataRdpName : null,
                                             MaxResDepth, newListDir);
            if (rcList == 0)
                Console.WriteLine($"  新 list 目录 : {Ws.Rel(cwd, newListDir)}");
            else
                Console.WriteLine("  新 list 生成失败（不影响已产出的包）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  新 list 生成失败 : {ex.Message}（不影响已产出的包）");
        }
        return 0;
    }

    /// <summary>
    /// ★ 打包更新补丁（-cdlc x）：system_update.res + patch.rdp。
    ///   与 -c 的区别：DLC 的 patch.rdp 只放【本次更新新增/替换】的条目（offset 类型 6），
    ///   而顶层 res 里大量条目（4 / 5 引本体 rdp、local C/D 引本体 res）数据都在本体里、这块不重打包。
    ///
    ///   基准 list（x 决定，4/5/C/D 的真值来源，也是本体 res 指针 +0x20/+0x28 的真值来源）：
    ///     x=1 → 1.out/list（本体未改动 / 仅改内容未重打包，块位与原版一致）
    ///     x=2 → 4.pack/list（本体经 -c 重打包后，块位 / res 内偏移都已变动 —— 打包 DLC 必须用它）
    ///
    ///   阶段 1：patch.rdp 顺移重打包（数据优先 2.new/SYSTEM_UPDATE，其次 1.out/SYSTEM_UPDATE）。
    ///   阶段 2：重建顶层 system_update.res。对每个 res：
    ///     · 头部有本体指针（BaseExportPath 非空）⇒ 写 +0x20（类型+本体块号）/ +0x28（本体字节数），值取自基准 list；
    ///     · 其 4 / 5 / C / D 条目的 offset / size / decsize ⇒ 按基准 list 修正（数据在本体，【不读不写】）。
    ///   阶段 3：对刚产出的 5.dlcpack 再跑一遍 list-only，生成 5.dlcpack/list（记录 DLC 的新偏移）。
    /// </summary>
    /// <param name="reuseOrig">压缩条目来源（同 -c）：
    ///   false（默认）= 全部从 2.new/1.out 明文直接压缩（zlib 后端 L9/M8 字节级复现游戏压缩）；
    ///   true = 未编辑条目复用原 patch.rdp 原始压缩段。</param>
    public static int RunDlc(string cwd, string patchResName, string patchName, int x, bool reuseOrig = false)
    {
        // ---- 基准 list 根目录：x=1 → 1.out/list；x=2 → 4.pack/list ----
        string listRoot = x == 2 ? Path.Combine(cwd, Ws.PackDir, "list")
                                 : Path.Combine(cwd, Ws.OutDir, "list");
        string baseLabel = x == 2 ? $"{Ws.PackDir}/list（本体已 -c 重打包）" : $"{Ws.OutDir}/list（本体原版）";
        if (!BaseIndex.HasBaseList(cwd, out string why, listRoot))
        {
            Console.Error.WriteLine($"错误: {why}");
            Console.Error.WriteLine($"      （-cdlc {x} 的基准 list 取自 {baseLabel}）");
            return 1;
        }

        string packDir = Path.Combine(cwd, Ws.DlcPackDir);
        Directory.CreateDirectory(packDir);
        // 顶层 res 与 patch.rdp 的输入：DLC 工作目录里（2.new 优先，其次工作目录原件）
        string resIn = Ws.ResolveData(cwd, patchResName) ?? Path.Combine(cwd, patchResName);
        string rdpIn = Ws.ResolveData(cwd, patchName) ?? Path.Combine(cwd, patchName);
        if (!File.Exists(resIn)) { Console.Error.WriteLine($"错误: 找不到 {patchResName}"); return 1; }
        if (!File.Exists(rdpIn)) { Console.Error.WriteLine($"错误: 找不到 {patchName}"); return 1; }

        // patch.rdp 的块表（DLC 侧自己的：1.out/list_update/patch.json，2.new/list_update 优先）
        string pj = Ws.ResolveList(cwd, "patch.json", "list_update");
        if (pj == null)
        {
            Console.Error.WriteLine($"错误: 找不到 patch.json（应位于 {Ws.NewDir}/list_update/ 或 {Ws.OutDir}/list_update/）");
            return 1;
        }

        // 载入基准索引（本体块表 + 本体各 res 的 list），x=2 走 4.pack/list
        var baseIdx = new BaseIndex(cwd, listRoot);
        // ★ 原版（重打包前）list 根：DLC list_update 里 C/D 条目的 offset 属【原版 res 空间】，
        //   靠它把 offset 反查成组内序号，再到 baseIdx（重打包后）取同序号条目（见 BuildDlcRes）。
        baseIdx.OriginListRoot = Path.Combine(cwd, Ws.OutDir, "list");
        baseIdx.Load();

        // ---- 阶段 1：patch.rdp 顺移重打包 ----
        var set = new RdpSet(Pres.TypePatch, patchName, rdpIn, Path.Combine(packDir, patchName),
                             Ws.Rel(cwd, pj), JsonIo.Load<PackageList>(pj));
        set.Measure();
        Console.WriteLine("== 打包更新补丁（-cdlc " + x + (reuseOrig ? " o" : "") + "） ==");
        Console.WriteLine($"  基准 list : {baseLabel}");
        Console.WriteLine($"  压缩模式 : {(reuseOrig
            ? "复用优先（未编辑条目沿用原 patch.rdp 原始压缩段）"
            : "全部重压（2.new/1.out 明文 → blz4/dflt，原生 zlib L9/M8 字节级复现）")}");
        Console.WriteLine($"  补丁输入  : {patchResName} + {patchName}"
                          + $"  ({new FileInfo(resIn).Length} + {new FileInfo(rdpIn).Length} 字节)");
        Console.WriteLine($"  patch 块表: {set.JsonRel}  ({set.List.Entries.Count} 条)  源 {set.Name} {set.OriginSize} 字节 / {set.TotalBlocks} 块");
        Console.WriteLine();

        // 顺移预案：压缩条目取压缩态大小，res 取原 size（第 1 轮）/ 实测长度（第 2 轮起），其余取文件大小。
        //   real = 上一轮【实测写入长度】—— 重建后变长的 res 也据此正确占位（见下方「迭代重排」）。
        long FileLenDlc(string rel)
        {
            string p = Ws.ResolveData(cwd, rel);
            if (p == null) throw new FileNotFoundException($"找不到数据文件: {rel}");
            return new FileInfo(p).Length;
        }
        void BuildPlanDlc(IReadOnlyDictionary<PackageEntry, long> real)
        {
            long pc = 0, psEnd = 0;
            foreach (var e in set.List.Entries.OrderBy(x => x.Block))
            {
                if (e.Block > psEnd) pc += e.Block - psEnd;           // 空隙照搬
                long len = real != null && real.TryGetValue(e, out var rl) && rl > 0 ? rl
                         : e.Compressed ? e.Size
                         : e.IsRes ? e.Size
                         : FileLenDlc(e.ExportPath);
                long occupy = Math.Max(NBlk(len), NBlk(e.Size));
                set.Plan[e] = (pc, occupy, len);
                pc += occupy;
                psEnd = e.Block + NBlk(e.Size);
            }
            set.ReflowBlocks = pc + (set.TotalBlocks > psEnd ? set.TotalBlocks - psEnd : 0);
            Console.WriteLine($"  预案      : [{set.Name}] {set.Plan.Count} 条 → {set.ReflowBlocks} 块（原 {set.TotalBlocks} 块）");
        }

        int nComp = 0, nReuse = 0, nOverflow = 0;
        var built = new Dictionary<PackageEntry, byte[]>();
        // ★ 每个 patch.rdp 条目的【明文长度】—— res TOC 的 +0x34 要用它
        //   （被重新压缩的文件，list 里记的还是本体旧 decsize）。
        var plainLen = new Dictionary<PackageEntry, long>();
        // Rebuild 闭包定义在后面，这里先占位（BuildDlc 执行时它已赋值）
        Func<string, int, byte[]> rebuildFn = null;
        // ★ loc（顶层 res 内 6 类型条目的新块位）与 rtblCtr（rtbl 现场重建计数）在这里提前声明：
        //   BuildDlc / BuildRtblDlc 要先引用它们；loc 的初值按【本轮预案块位】在下面循环内填。
        var loc = new Dictionary<(string, int, int), (int type, long block, long size)>();
        var rtblCtr = new int[1];

        void BuildDlc(PackageEntry e)
        {
            byte[] d;
            // ★ rtbl 条目（*.rtbl）：顶层条目表里的「rdp 引用表」—— 槽内嵌「被引用 res 的 rdp 块号」，
            //   重排（-c 动本体 / 用户编辑动 DLC）后块位会变，必须按新块位现场重建（含指向本体的 4/5 槽）；
            //   否则游戏按旧块号读到错数据。重建不可用（list 缺失 / 定位失败）→ 退化为读原文件。
            byte[] rtRebuilt = Rtbl.IsPath(e.ExportPath)
                             ? BuildRtblDlc(cwd, e.ExportPath, loc, baseIdx, rtblCtr) : null;
            if (rtRebuilt != null) d = rtRebuilt;
            else
            {
                string p = Ws.ResolveData(cwd, e.ExportPath);
                if (p == null) throw new FileNotFoundException($"找不到数据文件: {e.ExportPath}");
                // ★ 该条目本身是一个 res 且有自己的 DLC list ⇒ 用递归重建后的字节
                //   （其内部 4/5/C/D 索引与 +0x20/+0x28 本体指针已按基准 list 修正）；
                //   否则是普通数据文件，原样读入。
                if (Pres.IsPresFile(p) && File.Exists(ResolveDlcList(cwd, Ws.ListRelOf(e.ExportPath))))
                    d = rebuildFn.Invoke(e.ExportPath, 1);
                else
                    d = File.ReadAllBytes(p);
            }
            byte[] d0 = d;                       // ★ 压缩前的明文（res 重建字节按未压缩存储，明文即它本身）
            if (e.Compressed)
            {
                // 默认全部重压；复用模式（-cdlc o x）下没被编辑就复用原始压缩字节
                byte[] orig = reuseOrig ? ReadOrigSegment(set.Origin, e.Block, e.Size) : null;
                if (orig != null && SameAsCompressed(orig, d)) { d = orig; nReuse++; }
                else { d = e.Compress == "blz4" ? Blz4.Compress(d) : Dflt(d); nComp++; }
            }
            var pl = set.Plan[e];
            // ★ 超出预案占位（res 重建后变长跨块 / 压缩文件重压后变大）不再直接报错，
            //   改为记数：本轮结束后按【实测长度】重划块位并重建（见下方「迭代重排」）。
            if (NBlk(d.Length) > pl.occupy) nOverflow++;
            built[e] = d;
            plainLen[e] = d0.Length;             // ★ 明文长度（+0x34 的真值）
        }

        // 阶段 2 写出 patch.rdp（先填 built —— 子 res 在此递归重建 —— 再落盘）
        void WritePatchRdp()
        {
            if (File.Exists(set.OutPath)) File.Delete(set.OutPath);
            using (var fs = new FileStream(set.OutPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long cur = 0, sEnd = 0;
                foreach (var e in set.List.Entries.OrderBy(x => x.Block))
                {
                    var pl = set.Plan[e];
                    if (e.Block > sEnd) { CopyOrig(fs, set.Origin, sEnd, e.Block - sEnd, cur); cur += e.Block - sEnd; }
                    byte[] d = built[e];
                    fs.Seek(cur << Pres.RdpShift, SeekOrigin.Begin);
                    fs.Write(d, 0, d.Length);
                    long padLen = pl.occupy * Pres.BlockSize - d.Length;
                    if (padLen > 0) { var z = new byte[padLen]; fs.Write(z, 0, z.Length); }
                    if (pl.occupy == NBlk(d.Length)) set.NInplace++; else set.NAppend++;
                    cur += pl.occupy;
                    sEnd = e.Block + NBlk(e.Size);
                }
                if (sEnd < set.TotalBlocks) { CopyOrig(fs, set.Origin, sEnd, set.TotalBlocks - sEnd, cur); cur += set.TotalBlocks - sEnd; }
            }
        }

        // ---- 阶段 3：重建顶层 res（system_update.res） ----
        string outRes = Path.Combine(packDir, patchResName);
        string topListPath = Ws.ResolveList(cwd, Path.GetFileNameWithoutExtension(patchResName) + ".json", "list_update")
                         ?? Path.Combine(cwd, Ws.OutDir, "list_update", Path.GetFileNameWithoutExtension(patchResName) + ".json");
        byte[] topOrig = File.Exists(resIn) ? File.ReadAllBytes(resIn) : null;
        var topJs = JsonIo.Load<ResList>(topListPath);

        // ★ 6 类型【被重新压缩】条目的明文长度：键 = (引用者 res, 组, 索引) → 明文字节数。
        //   由主流程每轮重建前填，供 Pres.Build 写 res TOC 的 +0x34（详见 BuildDlcRes 的 decLen 委托）。
        var decLenOf = new Dictionary<(string, int, int), long>();
        // （loc 的初值按【本轮预案块位】在下面循环内填 —— 预案要先于它算出来。）

        // ★ ext 覆盖表在【子 res】层面生效（见下 BuildDlcRes）—— 顶层 res 自身的 4/5/C/D 同样要修正，
        //   故顶层也走同一条路径：这里统计修正数量，实际修正逻辑在 BuildDlcRes 内按基准 list 完成。
        int nBaseRes = 0, nFix45 = 0, nFixCD = 0;

        // res 路径 → 重建后的字节（顶层 + 各子 res 共享，避免重复重建）
        var resCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int nResRebuilt = 0;
        var innerCounters = new int[2];   // [0]=复用原始压缩段  [1]=重新压

        // ★ 重建【一个】res（含递归子 res）。resRel = 它相对 1.out 的 exportPath（loc / list 的键）。
        byte[] Rebuild(string resRel, int depth)
        {
            if (resCache.TryGetValue(resRel, out var cached)) return cached;
            // list 优先 2.new/list_update（DLC 的 list 目录），其次 1.out/list_update
            string lp = ResolveDlcList(cwd, Ws.ListRelOf(resRel));
            if (!File.Exists(lp))
                throw new FileNotFoundException(
                    $"找不到 DLC res 的 list: {Ws.NewDir}/list_update/{Ws.ListRelOf(resRel)} 或 {Ws.OutDir}/list_update/{Ws.ListRelOf(resRel)}");
            var js = JsonIo.Load<ResList>(lp);
            // ★ 子 res 各自可能带自己的 BaseExportPath（+0x20/+0x28），由 BuildDlcRes 内部按基准 list 处理。
            //   顶层 res 的原始字节 = 输入的 system_update.res（复用压缩段用）；子 res 由 BuildDlcRes 按需读。
            byte[] origFor = depth == 0 ? topOrig : null;
            byte[] b = BuildDlcRes(cwd, resRel, resRel, js, origFor, loc, baseIdx, null,
                                   Rebuild, depth,
                                   out int a, out int b45, out int bcd,
                                   innerCounters, decLenOf, reuseOrig: reuseOrig);
            nBaseRes += a; nFix45 += b45; nFixCD += bcd;
            resCache[resRel] = b;
            nResRebuilt++;
            if (depth > 0)
            {
                WriteTemp(cwd, resRel, b);
                Console.WriteLine($"  {new string(' ', depth * 2)}· 重建 res  {resRel}  →  {b.Length} 字节"
                                + $"（本体指针 {a} / 修正 4·5 {b45} / C·D {bcd}）");
            }
            return b;
        }

        rebuildFn = Rebuild;

        // ★ 迭代「重建 → 回填 loc」直到稳定 —— 修一个真实的时序 bug：
        //   BuildDlc 阶段为写 patch.rdp 会先重建各子 res 并存入 resCache，而那时 loc 还是【预案值】；
        //   若只回填一次就收尾，顶层 Rebuild 会直接从 resCache 取到这份「旧 loc 版本」，
        //   导致 res TOC 里的 size 停在旧值（实测 article.kst：数据 111209 字节，TOC 却写 111737，
        //   游戏按 111737 读到 528 字节 0 填充 → blz4 解析出非法块 → 整包拒载）。
        //   回填只改 TOC 的数值、不改 res 长度 ⇒ 第 2 轮起 loc 不再变，必然收敛。
        //   同理 decLenOf（6 类型被重压文件的明文长度）也要用过一轮之后再重建才生效，
        //   否则 TOC 的 +0x34 还是本体旧 decsize（article.kst：343600 写成 363520）。
        //   （decLenOf 已在 loc 旁声明，Rebuild 闭包要用。）
        int nLocFix = 0, rounds = 0, nOver = 0;
        var realLenDlc = new Dictionary<PackageEntry, long>();
        for (int round = 0; round < 6; round++)
        {
            rounds = round + 1;
            nBaseRes = nFix45 = nFixCD = 0;      // 统计只保留最后一轮（迭代会把 res 重建多遍）
            nResRebuilt = 0;
            // ⓪ 重划块位：第 2 轮起用上一轮的【实测写入长度】—— res 重建后变长跨块时，
            //   其后条目整体后移（否则会写出块位重叠的坏包）。
            BuildPlanDlc(round == 0 ? null : realLenDlc);
            // ⓪' loc 初值按本轮【预案块位】填（built 尚未填）；BuildDlc 完成后 ③ 再用实测长度回填。
            loc.Clear();
            foreach (var e in set.List.Entries)
                foreach (var r in e.Refs ?? new List<EntryRef>())
                {
                    int gi = SlotInt(r.Group);
                    if (gi < 0) continue;
                    loc[(r.Res, gi, r.Index)] = (Pres.TypePatch, set.Plan[e].dst, set.Plan[e].len);
                }
            // ① 用【上一轮】的明文长度填 decLenOf（第 1 轮为空 → 先按旧值建，第 2 轮纠正）
            decLenOf.Clear();
            foreach (var e in set.List.Entries)
                foreach (var r in e.Refs ?? new List<EntryRef>())
                {
                    int gi = SlotInt(r.Group);
                    if (gi >= 0 && plainLen.TryGetValue(e, out var pl0)) decLenOf[(r.Res, gi, r.Index)] = pl0;
                }
            // ② 重建全部 res（含 6 类型子 res）+ 压缩全部 patch 条目
            resCache.Clear();
            nComp = nReuse = nOverflow = 0;
            foreach (var e in set.List.Entries.OrderBy(x => x.Block)) BuildDlc(e);
            // ③ 回填 loc：用精确的压缩态长度替换预案值
            int fixedThis = 0;
            foreach (var e in set.List.Entries)
                foreach (var r in e.Refs ?? new List<EntryRef>())
                {
                    int gi = SlotInt(r.Group);
                    if (gi < 0) continue;
                    var k = (r.Res, gi, r.Index);
                    if (loc.TryGetValue(k, out var cur) && cur.size != built[e].Length)
                    {
                        loc[k] = (Pres.TypePatch, set.Plan[e].dst, built[e].Length);
                        fixedThis++;
                    }
                }
            nLocFix += fixedThis;
            // ④ 记录本轮【实测长度】并统计超占位条目
            realLenDlc.Clear();
            int over = 0;
            foreach (var e in set.List.Entries)
            {
                realLenDlc[e] = built[e].Length;
                if (NBlk(built[e].Length) > set.Plan[e].occupy) over++;
            }
            nOver = over;
            if (over > 0 && round == 0)
                Console.WriteLine($"  重排      : {over} 条超出预案占位（条目变长跨块），按实测长度重划块位并重建");
            // ⑤ 收敛：loc 不再变 且 无条目超占位；至少跑满 2 轮（第 2 轮才会用正确的 loc / decLenOf 重建）
            if (fixedThis == 0 && over == 0 && round >= 1) break;
        }
        if (nOver > 0)
            throw new InvalidDataException($"顺移重排未收敛（仍有 {nOver} 条超占位）—— 疑似条目长度随块位震荡，请反馈此例");
        if (rounds > 1) Console.WriteLine($"  loc 回填  : {nLocFix} 项压缩态大小按重建结果校正（{rounds} 轮收敛）");

        set.NInplace = set.NAppend = 0;
        WritePatchRdp();

        byte[] top = Rebuild(patchResName, 0);
        Console.WriteLine($"  重建 res   {patchResName}  →  {top.Length} 字节"
                        + $"（本体指针 {nBaseRes} 个 / 修正 4·5 {nFix45} 项 / C·D {nFixCD} 项，共重建 {nResRebuilt} 个 res）");
        File.WriteAllBytes(outRes, top);

        Console.WriteLine();
        Console.WriteLine($"== 完成 ==");
        Console.WriteLine($"  {set.Name,-11}: 顺移 {set.List.Entries.Count} 条 → {set.ReflowBlocks} 块（原位 {set.NInplace} / 顺移 {set.NAppend}）  →  {new FileInfo(set.OutPath).Length} 字节");
        if (nComp > 0 || nReuse > 0) Console.WriteLine($"  压缩      : {nComp} 条重新压（blz4/dflt，原生 zlib L9/M8）"
                                                   + (reuseOrig ? $" / {nReuse} 条复用原始字节" : "（全部重压模式，未复用原始段）"));
        Console.WriteLine($"  res 内部  : local C/D {(reuseOrig ? "复用原始压缩段" : "重新压")} {innerCounters[0]} 条 / 重新压 {innerCounters[1]} 条");
        if (rtblCtr[0] > 0) Console.WriteLine($"  rtbl      : 现场重建 {rtblCtr[0]} 个（槽位块号按新 rdp 布局回填；含指向本体的 Type 4/5 槽）");
        Console.WriteLine($"  基准修正  : 带本体指针 res {nBaseRes} 个，修正 4/5 条目 {nFix45} 项 / C/D 条目 {nFixCD} 项");
        Console.WriteLine($"  输出      : {Ws.Rel(cwd, outRes)}  ({top.Length} 字节)");
        Console.WriteLine($"         {Ws.Rel(cwd, set.OutPath)}  ({new FileInfo(set.OutPath).Length} 字节)");

        // ---- 阶段 4：生成 DLC 的新 list 到 5.dlcpack/list ----
        Console.WriteLine();
        Console.WriteLine("== 生成新 list（5.dlcpack/list） ==");
        string newListDir = Path.Combine(packDir, "list");
        try
        {
            int rcList = Unpacker.RunDlcListOnly(cwd, patchResName, patchName, MaxResDepth, newListDir);
            if (rcList == 0) Console.WriteLine($"  新 list 目录 : {Ws.Rel(cwd, newListDir)}");
            else Console.WriteLine("  新 list 生成失败（不影响已产出的包）");
        }
        catch (Exception ex) { Console.WriteLine($"  新 list 生成失败 : {ex.Message}（不影响已产出的包）"); }
        return 0;
    }

    /// <summary>
    /// ★ 重建【一个】更新补丁里的 res（需求 4 的核心）。
    ///
    ///   ① 头部本体指针：res 自带 BaseExportPath（+0x20/+0x28 原本有值）⇒ 按【基准 list】重算
    ///      —— 本体块号（x=1 原版 / x=2 重打包后）+ 本体 res 字节数。
    ///   ② 4 / 5 条目（数据在【本体 rdp】）：块号 / size / 解压大小按基准块表修正，不读不写。
    ///   ③ local(G2) 的 C / D（数据在【本体 res】内部）：偏移 / size / 解压大小按基准 res list 修正，不读不写。
    ///      ★ 判定不依赖该 res 是否带本体指针 —— 顶层 SYSTEM_UPDATE.res 自身没有指针，
    ///        但它的 4/5 条目同样指向本体 rdp，必须一并修正，否则游戏找不到文件。
    ///   ④ 6 类型（数据在 patch.rdp）：用本轮顺移重打包得到的新块位（loc）。
    ///   ⑤ 该 res 内嵌 / 引用的子 res（本身是 Pres 且有自己的 DLC list）⇒ 递归重建后按原位置嵌回。
    ///   以上修正项一律走 ext 覆盖表交给 Pres.Build —— 它只写 TOC、不碰 body / local 区。
    /// </summary>
    static byte[] BuildDlcRes(string cwd, string resRel, string resId, ResList js, byte[] origRes,
                              Dictionary<(string, int, int), (int type, long block, long size)> loc,
                              BaseIndex baseIdx, string baseResListForCD,
                              Func<string, int, byte[]> rebuildChild, int depth,
                              out int nBaseRes, out int nFix45, out int nFixCD,
                              int[] innerCounters = null,
                              Dictionary<(string, int, int), long> decLenOf = null,
                              int[] rtblCtr = null,
                              bool reuseOrig = false)
    {
        nBaseRes = nFix45 = nFixCD = 0;
        var ext = new Dictionary<(int, int), (long Offset, long Size, long DecSize)>();
        var origCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        BaseRef? baseRef = null;

        // ---- ① 头部本体指针：按基准块表重算 +0x20 / +0x28 ----
        if (!string.IsNullOrEmpty(js.BaseExportPath))
        {
            nBaseRes++;
            var blk = baseIdx.BlockOfExport(js.BaseExportPath);
            baseRef = blk.HasValue
                ? new BaseRef(Pres.ParseType(js.BaseType, 0), blk.Value.Block, blk.Value.Size)
                // 基准块表里没有这个 exportPath ⇒ 沿用 list 里记录的 BaseOffset（十六进制）/ BaseSize（十进制）
                : new BaseRef(Pres.ParseType(js.BaseType, 0), ParseNum(js.BaseOffset), ParseNum(js.BaseSize));
            // local C/D 要匹配的「本体 res」就是它
            baseResListForCD = js.BaseExportPath;
        }

        // ---- ②③ 建 ext 覆盖表：4 / 5 按基准块表（只取块号）、local C/D 按基准 res list ----
        var seen = new Dictionary<int, int>();
        foreach (var it in js.Files)
        {
            int g = int.TryParse(it.Group, out var gg) ? gg : -1;
            if (!seen.TryGetValue(g, out var index)) seen[g] = 0;
            int idx = seen[g]++;
            if (it.ElementName == null) continue;              // dummy 无需修正

            int ot = Pres.ParseType(it.Type, 0);

            // ★ rtbl(G7) 条目的 ElementName[4]（槽位 bit 表）不再在这里补 ——
            //   解包侧（Unpacker 的 skip 分支）已按「以本 res（update 包）为准」从 DLC res 自己的
            //   body 里读出位串：更新补丁可能新增文件、位串会重设，与本体同名 rtbl 的位表可以
            //   完全不同（索引的 rtbl 文件却仍是本体原来那个），所以绝不能从本体 list 抄。
            if (ot == Pres.TypePackage || ot == Pres.TypeData)
            {
                // 4 / 5：数据在本体 rdp —— 只把【块号（索引）】按基准块表修正；
                //   ★ 同一块可能被多个 res 引用，块表只记【首个引用者】的 exportPath，
                //     所以 EP 反查 miss 时按【块号】直查（DLC list 的 Offset 就是本体块号）。
                //   ★ 压缩大小 / 解压大小一律取本 DLC res TOC 里记的值（解包时已原样记录）——
                //     这两个数属于 update res 包自己；本体块表的值只在 list 缺字段时兜底。
                long? blk = null;
                long? tblSize = null, tblDec = null;
                var be = baseIdx.BlockOfExport(it.ExportPath);
                if (be.HasValue)
                {
                    blk = be.Value.Block; tblSize = be.Value.Size;
                    var pe = baseIdx.ExportEntryOf(be.Value.Type, be.Value.Block);
                    tblDec = pe?.RawSize;
                }
                else if (BaseIndex.TryParseOff(it.Offset, out long blkNo))
                {
                    var pe = baseIdx.ExportEntryOf(ot, blkNo);
                    if (pe != null) { blk = blkNo; tblSize = pe.Size; tblDec = pe.RawSize; }
                }
                if (blk.HasValue)
                {
                    ext[(g, idx)] = (blk.Value, it.Size ?? tblSize ?? 0, it.DecSize ?? tblDec ?? 0);
                    nFix45++;
                }
                else
                    Console.WriteLine($"  !! 基准块表无此 exportPath：{resRel} 的 {it.FileName}"
                                    + $"（组 {g} / 索引 {idx} / Type \"{it.Type}\" / EP \"{it.ExportPath}\" / 块号 \"{it.Offset}\"）");
            }
            else if (Pres.IsRes(ot) && g == 2 && !string.IsNullOrEmpty(baseResListForCD))
            {
                // local(G2) 的 C / D：数据在本体 res 内部 —— 到基准 res list 取真值（offset / size / decsize）。
                // ★ 匹配键【首选 (组, 组内序号)】：DLC 条目记的 offset 是【原版 res 空间】的值，
                //   在 OriginListRoot（原版 list）的同一组里反查得组内序号，再到 baseIdx（重打包后）
                //   取同序号条目 —— -c 重打包只改偏移/大小、不改组内条目顺序（实测 100% 同名同文件），
                //   故 (组, 序号) 是唯一稳定的映射键。
                //   ★ 不能用 (组, 名字)：本体里同基名条目极多（Trg_Circle.gim ×5 / Face.gim ×2），
                //   甚至有同基名不同扩展名的兄弟（Save_Data_Import_Info_Restrict 同时有 .gim 与 .ssq），
                //   只按名字会「取第一条」—— 实测 god_m001_Title 的 369 条 C/D 里 83 条取错，其中
                //   .gim 命中 .ssq 兄弟的那 1 条让游戏按 GIM 解析 SSQ 数据 → sub_128500 死循环 → 进标题卡死。
                //   （(组,类型,偏移) 也不行：本体若经 -c 重打包，其内部偏移会整体改变，100% 失配。）
                string nm0 = (it.ElementName != null && it.ElementName.Count > 0) ? (it.ElementName[0] ?? "") : "";
                bool hasOff = BaseIndex.TryParseOff(it.Offset, out long off);
                ResEntry ent = null;
                if (hasOff)
                {
                    int seq = baseIdx.SeqOfOffsetOrigin(baseResListForCD, g, off);
                    if (seq >= 0)
                    {
                        var cand = baseIdx.EntryBySeq(baseResListForCD, g, seq);
                        // ★ 名字必须对得上再采用 —— 防止原版 list 与重打包 list 不同源（顺序不一致）时乱取
                        if (cand != null && SameEntryName(cand, it)) ent = cand;
                    }
                }
                // 回退：原版 list 不可用 / 偏移不唯一 / 名字对不上（确属本次新增文件）→ 旧的名字匹配
                if (ent == null) ent = baseIdx.EntryByName(baseResListForCD, g, nm0);
                if (ent == null && hasOff)
                    ent = baseIdx.EntryOf(baseResListForCD, g, Pres.TypeStr(ot), off);
                if (ent != null)
                {
                    long size = ent.Size ?? it.Size ?? 0;
                    long dec = ent.DecSize ?? it.DecSize ?? 0;
                    // ★ offset 也要用【本体 list 的新值】—— 游戏会拿它去本体 res 里定位数据
                    long eoff = (ent.Offset != null && BaseIndex.TryParseOff(ent.Offset, out long eo2)) ? eo2
                              : (hasOff ? off : 0);
                    ext[(g, idx)] = (eoff, size, dec);
                    nFixCD++;
                }
            }
        }
        if (!string.IsNullOrEmpty(baseResListForCD) && baseIdx.ResListOf(baseResListForCD) == null)
            Console.WriteLine($"  注意: 基准 list 里找不到本体 res 的 list {Ws.ListRelOf(baseResListForCD)}"
                            + " —— 其 C/D 条目将沿用 DLC list 里的旧值（请确认基准 list 是否完整）");

        // ★ 子 res 递归重建：判定「这个条目本身是不是一个要重建的 res」——
        //   条件：解包产物是 Pres 格式、且 DLC list_update 里有它的 list（说明它参与本轮 DLC 打包）。
        byte[] TryRebuildChild(string rel, int childDepth)
        {
            if (rebuildChild == null || childDepth >= MaxResDepth) return null;
            string p0 = Ws.ResolveData(cwd, rel);
            if (p0 != null && Pres.IsPresFile(p0)
                && File.Exists(ResolveDlcList(cwd, Ws.ListRelOf(rel))))
                return rebuildChild(rel, childDepth);
            return null;
        }

        // rtbl 的槽位数（= 重建后 rtbl 文件字节数 ÷ 0x200）。
        //   ★ 必须提供：组 7 条目的名字区只写前 4 项，第 5 项是「槽位 bit 表」的位串，
        //     不截断的话它会被当成普通字符串写进 body（实测 god_menu.res 多出 326KB）。
        var rtblSlotsCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int RtblSlotsOfDlc(string exportPath)
        {
            if (rtblSlotsCache.TryGetValue(exportPath, out var v)) return v;
            string jp = ResolveDlcList(cwd, Ws.ListRelOf(exportPath));
            // ★ rtbl 指向本体时它的 list 在【基准 list 目录】（1.out/list 或 4.pack/list）
            if (jp == null || !File.Exists(jp))
            {
                string bp = Path.Combine(baseIdx.ListRoot, Ws.ToOs(Ws.ListRelOf(exportPath)));
                if (File.Exists(bp)) jp = bp;
            }
            if (jp == null || !File.Exists(jp))
                throw new FileNotFoundException(
                    $"找不到 rtbl list: {Ws.NewDir}/list_update/{Ws.ListRelOf(exportPath)} 或 {Ws.OutDir}/list_update/{Ws.ListRelOf(exportPath)}");
            v = Rtbl.SlotCountOf(JsonIo.Load<RtblList>(jp));
            rtblSlotsCache[exportPath] = v;
            return v;
        }

        byte[] bytes = Pres.Build(js,
            (it, g, gi) =>
            {
                // ★ 组 7（rtbl）：现场按【新块位】重建 body —— rtbl 的槽内嵌「被引用 res 的 rdp 块号」，
                //   重排（-c 动本体 / 用户编辑动 DLC）后块号会变，必须重写，否则游戏按旧块号读到错数据。
                //   （ext 只管 TOC，管不到 rtbl 的 body；-c 侧已有 PackRtbl，DLC 侧原先直接读旧 rtbl 文件，
                //     块位不变时碰巧正确，一变就整片「不是 Pres」。）
                if (Rtbl.IsSlot(g))
                {
                    byte[] rt = BuildRtblDlc(cwd, it.ExportPath, loc, baseIdx, rtblCtr);
                    if (rt != null) return rt;
                }
                // ext 未命中的条目才需要取数据（命中的 4/5/C/D 根本不进这里）
                string rel = !string.IsNullOrEmpty(it.ExportPath) ? it.ExportPath
                           : (it.FileName ?? "").TrimStart('\\');
                // ★ 该条目本身是 res 且有自己的 DLC list ⇒ 递归重建（local 内嵌的 C/D 子 res）
                byte[] rb = TryRebuildChild(rel, depth + 1);
                if (rb != null) return rb;
                string p = Ws.ResolveData(cwd, rel);
                if (p == null) throw new FileNotFoundException($"找不到数据文件: {rel}   (被 {resRel} 引用)");
                return File.ReadAllBytes(p);
            },
            (g, gi) =>
            {
                if (loc.TryGetValue((resId, g, gi), out var v)) return v;
                // 诊断：把命中的 list 条目也报出来，便于定位是哪个 res 的哪条
                var hit = js.Files.Where(x => (int.TryParse(x.Group, out var gg) ? gg : -1) == g)
                                  .ElementAtOrDefault(gi);
                throw new InvalidDataException(
                    $"rdp 定位失败：{resRel} 的 {hit?.FileName ?? "?"}（组 {g} / 组内索引 {gi} / Type \"{hit?.Type}\" / "
                  + $"ExportPath \"{hit?.ExportPath}\"）—— patch.json 无新块位、基准块表也无此 exportPath");
            },
            RtblSlotsOfDlc,                                    // ★ rtbl 的槽位数（组 7 名字区之后的 bit 表用）
            // ★ 条目还原后大小（+0x34）：
            //   · ext 命中项（4/5 / local C/D）→ 基准 list 给的显式解压大小；
            //   · 6 类型且【被重新压缩】的文件 → 用本轮实际的明文长度（decLenOf），
            //     否则 res TOC 里还是本体旧的 decsize（实测 article.kst：数据 343600，TOC 却写 363520）。
            //   · 未压缩条目 / 原始 +0x34 本就是 0 的（如顶层 res 的 6 类型条目）→ 不覆盖，
            //     沿用 list 里的原值，避免破坏零改动往返。
            (g, gi, it) => ext.TryGetValue((g, gi), out var ev) ? ev.DecSize
                         : (it.Compression && decLenOf != null
                            && decLenOf.TryGetValue((resId, g, gi), out var nd) ? nd : (long?)null),
            // ★ res 内部条目（Type C/D）的压缩态写入：优先从【原始 res】复用原始压缩段，
            //   保证零改动往返逐字节一致；改过的才重新压（否则 local 明文写入会体积暴涨）。
            (g, gi, it, plain) => EncodeDlcInner(g, gi, it, plain),
            ext.Count > 0 ? ext : null, baseRef,
            // ★ 6 类型子 res（躺在 patch.rdp 上）：用重建后的字节，块号仍取本轮顺移的新块位
            (g, gi, it) =>
            {
                if (ext.ContainsKey((g, gi))) return null;   // ext 命中项不碰
                string rel = !string.IsNullOrEmpty(it.ExportPath) ? it.ExportPath
                           : (it.FileName ?? "").TrimStart('\\');
                return TryRebuildChild(rel, depth + 1);
            },
            // ★ ext 命中的 C/D：数据在原始 res 文件内的（off+size 不超文件长）原样搬回；
            //   取不到（数据真在本体 res 里）返回 null，Pres.Build 就只写索引不写数据。
            (g, gi, it) => ReadOrigInnerSegment(OrigBytesOf(resRel), resRel, g, gi, it));
        return bytes;

        // ---- res 内部条目压缩态写入：复用模式读原始 res 的压缩段，否则直接重压 ----
        byte[] EncodeDlcInner(int g, int gi, ResEntry it, byte[] plain)
        {
            if (!it.Compression || string.IsNullOrEmpty(it.Compress)) return plain;
            byte[] seg = reuseOrig ? ReadOrigInnerSegment(OrigBytesOf(resRel), resRel, g, gi, it) : null;
            if (seg != null && SameAsCompressed(seg, plain)) { if (innerCounters != null) innerCounters[0]++; return seg; }
            if (innerCounters != null) innerCounters[1]++;
            byte[] enc = it.Compress == "blz4" ? Blz4.Compress(plain) : Dflt(plain);
            WriteTemp(cwd, it.ExportPath ?? "", enc);
            return enc;
        }

        // 原始字节：顶层用调用方传入的；子 res 从 1.out 解包产物按需读（带缓存）
        byte[] OrigBytesOf(string rel)
        {
            if (origRes != null) return origRes;
            if (!origCache.TryGetValue(rel, out var o))
            {
                string p = Ws.ResolveData(cwd, rel);
                o = (p != null && Pres.IsPresFile(p)) ? File.ReadAllBytes(p) : null;
                origCache[rel] = o;
            }
            return o;
        }
    }

    /// <summary>★ DLC 的 rtbl 现场重建（顶层 *.rtbl 条目 = patch.rdp 上的「rdp 引用表」）：
    ///   rtbl 文件的槽内嵌「被引用 res 的 rdp 块号 / size / 来源类型」—— 一 -c 重排本体（或用户编辑
    ///   使 DLC 自身重排）块位就变了，必须按【新块位】重写整个 rtbl，否则游戏按旧块号读到错数据
    ///   （表现为解包侧整片「rtbl 槽 N 不是 Pres」、patch.json 缩水）。
    ///     · Type "6" 槽 → 数据在 patch.rdp → loc（本轮顺移的新块位）
    ///     · Type "4"/"5" 槽 → 数据在本体 rdp → baseIdx（x=2 时基于 4.pack/list = 本体重排后的新块位）
    ///   找不到 rtbl 的 list / 任一定位失败 → 返回 null（调用方退化为直接读原 rtbl 文件，保持旧行为）。
    /// </summary>
    static byte[] BuildRtblDlc(string cwd, string rtblExportPath,
                               Dictionary<(string, int, int), (int type, long block, long size)> loc,
                               BaseIndex baseIdx, int[] rtblCtr)
    {
        if (string.IsNullOrEmpty(rtblExportPath)) return null;
        string jp = ResolveDlcList(cwd, Ws.ListRelOf(rtblExportPath));
        if (jp == null || !File.Exists(jp))
        {
            string bp = Path.Combine(baseIdx.ListRoot, Ws.ToOs(Ws.ListRelOf(rtblExportPath)));
            if (File.Exists(bp)) jp = bp;
        }
        if (jp == null || !File.Exists(jp)) return null;
        RtblList rj;
        try { rj = JsonIo.Load<RtblList>(jp); } catch { return null; }
        if (rj?.Files == null || rj.Files.Count == 0) return null;

        var bySlot = new Dictionary<int, RtblEntry>();
        foreach (var f in rj.Files)
            if (f != null && int.TryParse(f.Table, out int s)) bySlot[s] = f;

        try
        {
            byte[] rt = Rtbl.Build(rj, slot =>
            {
                // ① patch.rdp 槽：本轮顺移得到的新块位（loc 的 key 前缀 = 该 rtbl 的 exportPath，与 -c 的 PackRtbl 同规则）
                if (loc.TryGetValue((rtblExportPath, 7, slot), out var v)) return v;
                // ② 本体 rdp 槽：基准 list 的新块位（x=2 → 4.pack/list）
                if (bySlot.TryGetValue(slot, out var f))
                {
                    var be = baseIdx.BlockOfExport(f.ExportPath);
                    if (be.HasValue) return (be.Value.Type, be.Value.Block, be.Value.Size);
                }
                return (-1, -1L, -1L);
            });
            WriteTemp(cwd, rtblExportPath, rt);
            if (rtblCtr != null) rtblCtr[0]++;
            return rt;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! rtbl 重建失败（退化为原文件）{rtblExportPath} : {ex.Message}");
            return null;
        }
    }

    /// <summary>从【原始 res】里按 TOC 取出某个条目的原始字节（不解压）；取不到返回 null。
    /// ★ 只取【local 区】（offset ≥ pkgSize）的数据 —— DLC res 的组 2 C/D 条目的 offset 虽照搬本体，
    ///   但 DLC 自己的 local 区常常是空的（pkgSize == 文件长），那些 offset 落在 body 内或超出文件，
    ///   都不能取（会把 body / 名字区的字节误当条目数据）。</summary>
    static byte[] ReadOrigInnerSegment(byte[] origResBytes, string resPath, int g, int gi, ResEntry it)
    {
        byte[] orig = origResBytes;
        if (orig == null || !Pres.IsPres(orig)) return null;
        long pkgSize = BitConverter.ToUInt32(orig, 0x18);   // ★ PS4：res 包大小（不含 Local 数据）在 +0x18
        // 按组序在 TOC 里找「组 g 的第 gi 条」
        var entries = Pres.ParseToc(orig).entries;
        TocEntry te = null;
        int seen = -1;
        foreach (var e in entries)
        {
            if (e.Group != g) continue;
            if (++seen == gi) { te = e; break; }
        }
        if (te == null || te.Size == 0) return null;
        // table(G5) 的 size 记的是 u32 个数（仅「在 res 内」的 table 成立）
        long len = (g == 5 && !Pres.IsRdp(te.OffsetType)) ? te.Size * 4 : te.Size;
        long off = te.Offset;
        // ★ 必须在 local 区（off ≥ pkgSize）且不超文件长 —— 否则该条目的数据不在本 res 内
        if (off < pkgSize || off + len > orig.Length) return null;
        var buf = new byte[len];
        Array.Copy(orig, off, buf, 0, len);
        return buf;
    }

    /// <summary>DLC res 的 list 路径：2.new/list_update 优先，其次 1.out/list_update</summary>
    static string ResolveDlcList(string cwd, string listRel)
        => Ws.ResolveList(cwd, listRel, "list_update")
        ?? Path.Combine(cwd, Ws.OutDir, "list_update", Ws.ToOs(listRel));

    /// <summary>两个 list 条目的名字表前 2 项（基名 + 扩展名）是否一致（忽略大小写）——
    ///   用于「按 (组, 序号) 取出的条目」与「DLC 条目」的身份校验。</summary>
    static bool SameEntryName(ResEntry a, ResEntry b)
    {
        for (int i = 0; i < 2; i++)
        {
            string x = (a?.ElementName != null && a.ElementName.Count > i) ? a.ElementName[i] : null;
            string y = (b?.ElementName != null && b.ElementName.Count > i) ? b.ElementName[i] : null;
            if (!string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>list 里的数值字符串（Offset/BaseOffset 为十六进制、BaseSize/decsize 为十进制）→ long</summary>
    static long ParseNum(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                 System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
        return long.TryParse(s, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out long v2) ? v2 : 0;
    }

    /// <summary>内嵌 res 的最大递归深度（防环）</summary>
    const int MaxResDepth = 8;

    static void WriteTemp(string cwd, string rel, byte[] data)
    {
        string p = Path.Combine(cwd, Ws.TempDir, Ws.ToOs(rel));
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(p, data);
    }

    static long NBlk(long size) => (size + Pres.BlockSize - 1) / Pres.BlockSize;

    /// <summary>把原 rdp 的 [srcBlock, srcBlock+nBlocks) 逐块原样搬到 dst 的 dstBlock 处（顺移模式保留空隙用）</summary>
    static void CopyOrig(FileStream dst, string origPath, long srcBlock, long nBlocks, long dstBlock)
    {
        if (nBlocks <= 0) return;
        long left = nBlocks << Pres.RdpShift;
        using var src = File.OpenRead(origPath);
        src.Seek(srcBlock << Pres.RdpShift, SeekOrigin.Begin);
        dst.Seek(dstBlock << Pres.RdpShift, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        while (left > 0)
        {
            int want = (int)Math.Min(buf.Length, left);
            int got = src.Read(buf, 0, want);
            if (got <= 0) throw new EndOfStreamException($"原始 rdp 读取不足（block {srcBlock} 起，还差 {left} 字节）");
            dst.Write(buf, 0, got);
            left -= got;
        }
    }

    /// <summary>流式逐字节比对两个文件（rdp 可达数 GB，整读会撞 .NET 单数组 2GB 上限）</summary>
    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        const int N = 1 << 20;
        var ba = new byte[N]; var bb = new byte[N];
        using var x = File.OpenRead(a); using var y = File.OpenRead(b);
        long rem = fa.Length;
        while (rem > 0)
        {
            int want = (int)Math.Min(N, rem);
            if (x.Read(ba, 0, want) != want || y.Read(bb, 0, want) != want) return false;
            if (!ba.AsSpan(0, want).SequenceEqual(bb.AsSpan(0, want))) return false;
            rem -= want;
        }
        return true;
    }

    static int SlotInt(string n) => n switch
    {
        // ★ PS4：G1 的槽位名是 prx（PSV 那一代曾叫 self）——"self" 分支保留以兼容旧 list。
        "res" => 0, "prx" => 1, "self" => 1, "local" => 2, "refer" => 4,
        "table" => 5, "string" => 6, "rtbl" => 7, _ => -1,
    };

    /// <summary>压缩成 dflt 段："dflt"（4 字节魔数）+ raw deflate（DeflateStream 本身就是 raw deflate）</summary>
    static byte[] Dflt(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write(Pres.DfltMagic, 0, Pres.DfltMagic.Length);
        using (var df = new DeflateStream(ms, CompressionLevel.SmallestSize, true))
            df.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>读原 package.rdp 上的一段原始字节（不解压）；越界或失败返回 null</summary>
    static byte[] ReadOrigSegment(string path, long block, long size)
    {
        if (block < 0 || size <= 0) return null;
        try
        {
            using var fs = File.OpenRead(path);
            long off = block << Pres.RdpShift;
            if (off + size > fs.Length) return null;
            fs.Seek(off, SeekOrigin.Begin);
            var buf = new byte[size];
            int got = 0;
            while (got < size)
            {
                int n = fs.Read(buf, got, (int)(size - got));
                if (n <= 0) break;
                got += n;
            }
            return got == size ? buf : null;
        }
        catch { return null; }
    }

    /// <summary>原压缩段（dflt 或 blz4）解压后是否与 d 逐字节相同 —— 用来判断压缩条目是否被编辑过。
    ///   没被编辑就复用原始压缩字节 ⇒ 长度不变、零改动往返可保持逐字节一致。</summary>
    static bool SameAsCompressed(byte[] orig, byte[] d)
    {
        byte[] un;
        try
        {
            un = Blz4.IsBlz4(orig) ? Blz4.Decompress(orig)
               : Pres.IsDflt(orig) ? UndfltAny(orig)
               : null;
        }
        catch { return false; }
        if (un == null) return false;
        return un.Length == d.Length && un.AsSpan().SequenceEqual(d);
    }

    /// <summary>dflt 段（"dflt" + raw deflate）解压</summary>
    static byte[] UndfltAny(byte[] orig)
    {
        using var ms = new MemoryStream(orig, 4, orig.Length - 4);
        using var df = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        df.CopyTo(outMs);
        return outMs.ToArray();
    }
}

/// <summary>一个 rdp 文件 + 它的块表 list（GE2 正式版是双 rdp：package.rdp / data.rdp 各一个）</summary>
sealed class RdpSet
{
    /// <summary>offset 类型：Pres.TypePackage(0x4) / Pres.TypeData(0x5)</summary>
    public readonly int Kind;
    public readonly string Name;
    public readonly string Origin;      // 源 rdp（工作目录）
    public readonly string OutPath;     // 产物（4.pack/）
    public readonly string JsonRel;     // 块表 list 的相对路径（显示用）
    public readonly PackageList List;

    public long OriginSize, TotalBlocks;
    public long ReflowBlocks, NInplace, NAppend;
    public readonly Dictionary<PackageEntry, (long dst, long occupy, long len)> Plan = new();

    public RdpSet(int kind, string name, string origin, string outPath, string jsonRel, PackageList list)
    {
        Kind = kind; Name = name; Origin = origin; OutPath = outPath; JsonRel = jsonRel; List = list;
    }

    public void Measure()
    {
        OriginSize = new FileInfo(Origin).Length;
        TotalBlocks = (OriginSize + Pres.BlockSize - 1) / Pres.BlockSize;
    }
}
