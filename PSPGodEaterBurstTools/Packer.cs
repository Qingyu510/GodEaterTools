using System.IO.Compression;

namespace PSPGodEaterBurstTools;

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
/// </summary>
public static class Packer
{
    /// <summary>
    /// 顺移打包（唯一模式）：按 block 升序整份重写 package.rdp。
    ///   每条占 max(新块数, 原块数) —— 变小不回收空间（补零），位置保持稳定；
    ///   真正变大的条目在本位就地扩展，其后条目整体后移，条目间空隙与尾部原样搬运。
    /// </summary>
    public static int Run(string cwd, string resName,
                          string rdpName = "package.rdp", string packDirName = Ws.PackDir,
                          string listDirName = "list", string topListStem = null)
    {
        string originRdp = Path.Combine(cwd, rdpName);
        if (!File.Exists(originRdp)) { Console.Error.WriteLine($"错误: 找不到 {rdpName}（目录 {cwd}）"); return 1; }

        string pkgPath = Ws.ResolveList(cwd, "package.json", listDirName);
        if (pkgPath == null)
        {
            Console.Error.WriteLine($"错误: 找不到 package.json（应位于 {Ws.NewDir}/{listDirName}/ 或 {Ws.OutDir}/{listDirName}/）");
            return 1;
        }

        var pkg = JsonIo.Load<PackageList>(pkgPath);
        string packDir = Path.Combine(cwd, packDirName);
        Directory.CreateDirectory(packDir);
        string outRdp = Path.Combine(packDir, rdpName);
        string outRes = Path.Combine(packDir, resName);

        long originSize = new FileInfo(originRdp).Length;
        long totalBlocks = (originSize + Pres.BlockSize - 1) / Pres.BlockSize;

        Console.WriteLine("== 打包 ==");
        Console.WriteLine("  模式  : 顺移（变大的条目原位扩展，其后条目整体后移；未变化区域含空隙原样保留）");
        Console.WriteLine($"  块表  : {Ws.Rel(cwd, pkgPath)}  ({pkg.Entries.Count} 条)");
        Console.WriteLine($"  源 rdp: {rdpName}  ({originSize} 字节 / {totalBlocks} 块)");
        Console.WriteLine($"  输出  : {packDirName}/{rdpName} + {packDirName}/{resName}");
        Console.WriteLine();

        var loc = new Dictionary<(string, int, int), (long block, long size)>();
        var resCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int nComp = 0, nResPack = 0, nReuse = 0;

        // res 路径 → 该 res 引用的全部 rdp 条目。
        //   构建某个 res 之前，必须先让它引用的条目统统「落位」（写进新 rdp 并登记 loc），
        //   否则 res 内记录的 offset 会指向旧块位、或干脆查不到记录。
        //   注意：包里 res 与其被引用条目的 block 顺序并不保证「被引用者在前」
        //   （例：新包 system/god_menu.res 在 block 4，而它引用的 2188 条全部 block > 4），
        //   所以不能只靠「按 block 升序边扫边建」。
        var resRefs = new Dictionary<string, List<PackageEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e0 in pkg.Entries)
            foreach (var r0 in e0.Refs ?? new List<EntryRef>())
            {
                if (string.IsNullOrEmpty(r0.Res)) continue;
                if (!resRefs.TryGetValue(r0.Res, out var l0)) resRefs[r0.Res] = l0 = new List<PackageEntry>();
                l0.Add(e0);
            }

        // 顺移模式的【布局预案】：每条占 max(新块数, 原块数) —— 变小不回收，
        //   于是「没变大的条目」位置完全不动，只有真正变大的条目会把它之后的条目整体后移；
        //   条目之间的空隙也原样保留。非 res 的新长度取文件大小；res 先按原块数占位
        //   （若它实际变长跨块，写入阶段直接报错，以免写出坏包）。
        var planBlock = new Dictionary<PackageEntry, (long dst, long occupy, long len)>();
        long reflowBlocks = 0;
        {
            long pc = 0, psEnd = 0;
            foreach (var e in pkg.Entries.OrderBy(x => x.Block))
            {
                if (e.Block > psEnd) pc += e.Block - psEnd;           // 空隙照搬
                long len = e.IsRes ? e.Size : FileLen(e.ExportPath);
                long occupy = Math.Max(NBlk(len), NBlk(e.Size));
                planBlock[e] = (pc, occupy, len);
                pc += occupy;
                psEnd = e.Block + NBlk(e.Size);
            }
            reflowBlocks = pc + (totalBlocks > psEnd ? totalBlocks - psEnd : 0);
            Console.WriteLine($"  预案  : {planBlock.Count} 条 → {reflowBlocks} 块（原 {totalBlocks} 块）");
            // 注意：预案只定【块位】。loc 表（父 res 记录子里填的 offset/size）必须用
            //   构建阶段的【真实写入长度】登记 —— 压缩条目重 gzip 后长度会变（实测 43 条里
            //   39 条变小、2 条变大），用预案的旧长度会写出 size 错位的坏包（游戏解压截断）。
        }

        // 重建一个 res。depth==0 = 顶层 res（不写 3.temp）；depth>0 = 子 res / 内嵌 res（写 3.temp）。
        // 递归规则：list 里任何条目，只要数据本身是 Pres 且能找到它自己的 list，就现场重打包
        //   —— 同时涵盖 ① rdp 侧子 res（G0/G1/G4/G7）  ② res 内嵌子 res（local G2）
        //   组内顺序严格按 list 的 Files 出现顺序，所以 local 里的 res 会按它原本的位置嵌回。
        // topListStem：顶层 res 的 list 基名。DLC 的顶层 res 是哈希名（f3594808….edat），
        //   但它的 list 叫 system_update.json；传 null 则按 res 文件名推（本体 system.res → system.json）。
        byte[] PackRes(string resExportPath, int depth, string listStem = null)
        {
            if (resCache.TryGetValue(resExportPath, out var cached)) return cached;

            string listRel = listStem != null ? listStem + ".json" : Ws.ListRelOf(resExportPath);
            string listPath = Ws.ResolveList(cwd, listRel, listDirName);
            if (listPath == null)
                throw new FileNotFoundException(
                    $"找不到 list: {Ws.NewDir}/{listDirName}/{listRel} 或 {Ws.OutDir}/{listDirName}/{listRel}");

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
                        && Ws.ResolveList(cwd, Ws.ListRelOf(rel), listDirName) != null)
                        return PackRes(rel, depth + 1);              // 内嵌 / 子 res：递归重打包
                    return File.ReadAllBytes(p);
                },
                (g, gi) => loc.TryGetValue((resId, g, gi), out var v) ? v : (-1L, -1L));

            resCache[resExportPath] = bytes;
            if (depth > 0)
            {
                WriteTemp(cwd, resExportPath, bytes);
                nResPack++;
                Console.WriteLine($"  {new string(' ', depth * 2)}· 重打包 res  {resExportPath}  →  {bytes.Length} 字节");
            }
            return bytes;
        }

        // 顺移预案用：数据文件的实际大小（只取 FileInfo.Length，不读内容）
        long FileLen(string rel)
        {
            string p = Ws.ResolveData(cwd, rel);
            if (p == null) throw new FileNotFoundException($"找不到数据文件: {rel}");
            return new FileInfo(p).Length;
        }

        int nInplace = 0, nAppend = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        {
            // 顺移分两阶段：
            //   阶段 1 —— 按【依赖序】（子先父后）构建每条的最终字节；每条落位后立刻用
            //             【真实写入长度】登记 loc，这样父 res 记录子条目的 offset/size 才是对的。
            //             阶段 1 不写 rdp，只把字节留在内存。块位取自预案（预案保证位置稳定）。
            //   阶段 2 —— 按预案块位顺序把字节写进新 rdp，条目间空隙与尾部原样搬运。
            // 之所以要分两阶段：GEB 里父 res 常常排在子条目【之前】（block 更小），
            //   若边扫边建，父 res 构建时子条目的真实长度还不知道，只能拿旧长度凑 —— 必错。
            var built = new Dictionary<PackageEntry, byte[]>(Math.Max(16, pkg.Entries.Count));

            void BuildOne(PackageEntry e)
            {
                byte[] d;
                if (e.IsRes) d = PackRes(e.ExportPath, 1);
                else
                {
                    string p = Ws.ResolveData(cwd, e.ExportPath);
                    if (p == null) throw new FileNotFoundException($"找不到数据文件: {e.ExportPath}");
                    d = File.ReadAllBytes(p);
                }

                // 压缩条目：没被编辑就复用原始压缩字节（长度不变 ⇒ 布局也不受影响）
                if (e.Compressed)
                {
                    byte[] orig = ReadOrigSegment(originRdp, e.Block, e.Size);
                    if (orig != null && Pres.IsGzip(orig) && SameAsGunzip(orig, d)) { d = orig; nReuse++; }
                    else { d = Gzip(d); nComp++; WriteTemp(cwd, e.ExportPath, d); }
                }

                var pl = planBlock[e];
                if (NBlk(d.Length) > pl.occupy)
                    throw new InvalidDataException(
                        $"顺移失败：{e.ExportPath} 实际 {NBlk(d.Length)} 块 > 预案 {pl.occupy} 块 —— "
                      + "顺移布局按「原块数」为该条占位，要求它不变长（res 重打包跨块会让预案失效）。");

                // ★ 用真实长度登记，而不是预案里的旧长度
                foreach (var r in e.Refs ?? new List<EntryRef>())
                {
                    int gi = SlotInt(r.Group);
                    if (gi >= 0) loc[(r.Res, gi, r.Index)] = (pl.dst, d.Length);
                }
                built[e] = d;
            }

            var doneS = new HashSet<PackageEntry>();

            bool ReadyS(PackageEntry e)
            {
                if (!resRefs.TryGetValue(e.ExportPath, out var kids)) return true;
                foreach (var k in kids) if (!doneS.Contains(k)) return false;
                return true;
            }

            // ① 普通条目（无依赖）
            foreach (var e in pkg.Entries.Where(x => !x.IsRes).OrderBy(x => x.Block))
            {
                BuildOne(e);
                doneS.Add(e);
            }

            // ② res：引用的条目全部构建完才构建本 res
            var pendS = pkg.Entries.Where(x => x.IsRes).OrderBy(x => x.Block).ToList();
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

            if (File.Exists(outRdp)) File.Delete(outRdp);
            using (var fs = new FileStream(outRdp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long cur = 0, sEnd = 0;
                foreach (var e in pkg.Entries.OrderBy(x => x.Block))
                {
                    var pl = planBlock[e];
                    if (e.Block > sEnd)                                  // 空隙原样搬运
                    {
                        CopyOrig(fs, originRdp, sEnd, e.Block - sEnd, cur);
                        cur += e.Block - sEnd;
                    }

                    byte[] d = built[e];
                    long grew = NBlk(d.Length) - NBlk(e.Size);
                    if (grew > 0)
                        Console.WriteLine($"  → 原位扩展 block {e.Block}  +{grew} 块  {(e.IsRes ? "[res] " : "")}{e.FileName}");

                    fs.Seek(cur << Pres.RdpShift, SeekOrigin.Begin);
                    fs.Write(d, 0, d.Length);
                    long padLen = pl.occupy * Pres.BlockSize - d.Length;     // 比原块数小的部分补零（不回收空间）
                    if (padLen > 0) { var z = new byte[padLen]; fs.Write(z, 0, z.Length); }
                    if (pl.occupy == NBlk(d.Length)) nInplace++; else nAppend++;
                    cur += pl.occupy;
                    sEnd = e.Block + NBlk(e.Size);
                }
                if (sEnd < totalBlocks)
                {
                    CopyOrig(fs, originRdp, sEnd, totalBlocks - sEnd, cur);
                    cur += totalBlocks - sEnd;
                }
            }
        }
        sw.Stop();

        // ---- 顶层 res ----
        byte[] top = PackRes(resName, 0, topListStem);
        File.WriteAllBytes(outRes, top);

        long newSize = new FileInfo(outRdp).Length;
        Console.WriteLine();
        Console.WriteLine($"== 完成 ({sw.Elapsed.TotalSeconds:F1}s) ==");
        Console.WriteLine($"  rdp  : 顺移 {pkg.Entries.Count} 条 → {reflowBlocks} 块（原位 {nInplace} / 顺移 {nAppend}）  →  {newSize} 字节 ({(newSize - originSize >= 0 ? "+" : "")}{newSize - originSize})");
        if (nComp > 0 || nReuse > 0)
            Console.WriteLine($"  压缩 : {nComp} 条重新 gzip / {nReuse} 条复用原始字节"
                              + (nComp > 0 ? $"（新产物已写入 {Ws.TempDir}/）" : ""));
        if (nResPack > 0) Console.WriteLine($"  res  : 现场重打包 {nResPack} 个（产物已写入 {Ws.TempDir}/）");
        if (newSize == originSize)
        {
            bool same = File.ReadAllBytes(originRdp).SequenceEqual(File.ReadAllBytes(outRdp));
            Console.WriteLine($"  零改动校验: {(same ? "byte-identical ✓" : "与原始不同 ✗")}");
        }
        Console.WriteLine($"  输出 : {Ws.Rel(cwd, outRdp)}  ({newSize} 字节)");
        Console.WriteLine($"         {Ws.Rel(cwd, outRes)}  ({top.Length} 字节)");
        return 0;
    }

    /// <summary>
    /// 打包 DLC 数据包（-cdlc）：与 -c 走【完全同一条路径】，差别只有三点 ——
    ///   ① 默认文件名：system.res → f3594808….edat、package.rdp → 4a92f06c….edat
    ///   ② rdp 块大小 0x8000 → 0x10
    ///   ③ 顶层 res 是哈希名：list 目录 list_update、顶层 list 基名 system_update、产物目录 5.dlcpack
    /// 注：大包开头那 0x10 字节头【就是块 0】（由 offset=0 的 version.dat 条目承载），
    ///     故块寻址与本体完全一致，无需额外基址偏移。
    /// </summary>
    public static int RunDlc(string cwd, string resName, string rdpName)
    {
        int save = Pres.RdpShift;
        Pres.RdpShift = 4;                          // ★ DLC 的 rdp 块大小 = 0x10
        try
        {
            return Run(cwd, resName, rdpName, Ws.DlcPackDir, "list_update",
                       topListStem: "system_update");
        }
        finally { Pres.RdpShift = save; }
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

    static int SlotInt(string n) => n switch
    {
        "res" => 0, "prx" => 1, "local" => 2, "refer" => 4,
        "table" => 5, "string" => 6, "rtbl" => 7, _ => -1,
    };

    /// <summary>
    /// gzip 压缩 —— 走原生 zlib（P/Invoke），与游戏原生压缩器字节级一致
    /// （实测 2026-10-15：level 9 / memLevel 9 / gzip 包装 / 头 OS=0x03，1/1 复现）。
    /// .NET GZipStream 输出与 C zlib 不同（差 3,680 B），不能用于字节一致场景。
    /// </summary>
    static byte[] Gzip(byte[] data) => NativeZlib.GzipDeflate(data);

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

    /// <summary>orig（gzip 段）解压后是否与 d 逐字节相同 —— 用来判断压缩条目是否被编辑过</summary>
    static bool SameAsGunzip(byte[] orig, byte[] d)
    {
        try
        {
            using var ms = new MemoryStream(orig);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            byte[] un = outMs.ToArray();
            return un.Length == d.Length && un.AsSpan().SequenceEqual(d);
        }
        catch { return false; }
    }
}
