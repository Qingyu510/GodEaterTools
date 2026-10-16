namespace PSPGodEaterTools;

/// <summary>
/// 打包：按 package.json 顺序重建 package.rdp，最后生成顶层 res。
///   数据来源：2.new/&lt;路径&gt; 优先，其次 1.out/&lt;路径&gt;
///   list 来源：2.new/list/&lt;路径&gt;.json 优先，其次 1.out/list/...
///   中间产物：3.temp/（重打包的 res、压缩后的文件）
///   最终产物：4.pack/package.rdp + 4.pack/&lt;顶层res&gt;
///
/// 块位策略（append）：未超出原块数的条目原位覆盖，超出的从 rdp 末尾顺位追加。
/// 未参与本次编辑的块原样保留 → 不引用本 res 树的其它 res 不受影响。
///
/// 递归重打包：list 里任何条目，只要其数据本身是 Pres 且能找到它自己的 list，
///   就先用那份 list 现场重建它（见 PackRes）——覆盖 rdp 侧子 res 与 local 内嵌 res，
///   并按组内原位置嵌回父 res。
/// </summary>
public static class Packer
{
    /// <param name="compact">
    /// true = 紧密重排模式：按 block 升序把每条数据依次贴着写（子 res 在前、父 res 在后），
    ///   块号全部重算 —— 于是在两条目之间插入新条目时，其后所有数据自然整体后移。
    ///   前提：package.json 的 entries 必须完整覆盖原 rdp（首尾相接、无空隙），
    ///   否则空隙里的数据会丢。解包产物天然满足（rdp 本就是紧密排列）。
    /// false = 默认 append 模式：原位覆盖 + 超长追加，未编辑的块原样保留。
    /// </param>
    public static int Run(string cwd, string resName, bool compact = false,
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
        Console.WriteLine($"  模式  : {(compact ? "紧凑重排（按条目顺序重算块位，插入即生效）" : "原位追加（un-edited 块保留）")}");
        Console.WriteLine($"  块表  : {Ws.Rel(cwd, pkgPath)}  ({pkg.Entries.Count} 条)");
        Console.WriteLine($"  源 rdp: {rdpName}  ({originSize} 字节 / {totalBlocks} 块)");
        Console.WriteLine($"  输出  : {packDirName}/{rdpName} + {packDirName}/{resName}");
        Console.WriteLine();

        var loc = new Dictionary<(string, int, int), (long block, long size)>();
        var resCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int nResPack = 0;

        // 重建一个 res。depth==0 = 顶层 res（不写 3.temp）；depth>0 = 子 res / 内嵌 res（写 3.temp）。
        // 递归规则：list 里任何条目，只要数据本身是 Pres 且能找到它自己的 list，就现场重打包
        //   —— 同时涵盖 ① rdp 侧子 res（G0/G1/G4/G7）  ② res 内嵌子 res（local G2）
        //   组内顺序严格按 list 的 Files 出现顺序，所以 local 里的 res 会按它原本的位置嵌回。
        byte[] PackRes(string resExportPath, int depth, string listStem = null)
        {
            if (resCache.TryGetValue(resExportPath, out var cached)) return cached;

            // ★ 顶层 res：list 名不由 res 文件名推（DLC 的 res 是哈希名），
            //   而由调用方显式给定的 listStem（DLC = system_update）决定。
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

        if (compact) { if (File.Exists(outRdp)) File.Delete(outRdp); }
        else File.Copy(originRdp, outRdp, true);
        long tail = totalBlocks;
        long cursor = 0;                                     // 紧密重排模式下的下一个可写块号
        int nInplace = 0, nAppend = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using (var fs = new FileStream(outRdp, compact ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None))
        {
            foreach (var e in pkg.Entries.OrderBy(x => x.Block))
            {
                byte[] d;
                if (e.IsRes)
                {
                    d = PackRes(e.ExportPath, 1);
                }
                else
                {
                    string p = Ws.ResolveData(cwd, e.ExportPath);
                    if (p == null) throw new FileNotFoundException($"找不到数据文件: {e.ExportPath}");
                    d = File.ReadAllBytes(p);
                }

                // GE1（PSPGodEaterTools）数据实测 0 命中 gzip 压缩段，已移除压缩路径：
                //   不再复用原始压缩字节、不重新压缩，条目内容原样进 rdp。

                bool inplace = e.Block >= 0 && e.Block + NBlk(e.Size) <= totalBlocks
                               && NBlk(d.Length) <= NBlk(e.Size);
                long nb;
                bool padded;
                if (compact)
                {
                    nb = cursor;                             // 依次紧贴落盘：插入点之后的条目自动顺移
                    cursor += NBlk(d.Length);
                    padded = true;
                }
                else
                {
                    nb = inplace ? e.Block : tail;
                    padded = !inplace;
                    if (!inplace)
                    {
                        tail += NBlk(d.Length);
                        nAppend++;
                        Console.WriteLine($"  + 追加 block {e.Block} → {nb}  {(e.IsRes ? "[res] " : "")}{e.FileName}");
                    }
                    else nInplace++;
                }
                fs.Seek(nb << Pres.RdpShift, SeekOrigin.Begin);
                fs.Write(d, 0, d.Length);
                if (padded)
                {
                    int pad = (int)((Pres.BlockSize - (d.Length & (Pres.BlockSize - 1))) & (Pres.BlockSize - 1));
                    if (pad > 0) fs.Write(new byte[pad], 0, pad);
                }

                foreach (var r in e.Refs)
                {
                    int gi = SlotInt(r.Group);
                    if (gi >= 0) loc[(r.Res, gi, r.Index)] = (nb, d.Length);
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
        Console.WriteLine(compact
            ? $"  rdp  : 紧凑重排 {pkg.Entries.Count} 条 → {cursor} 块  →  {newSize} 字节 ({(newSize - originSize >= 0 ? "+" : "")}{newSize - originSize})"
            : $"  rdp  : 原位 {nInplace} / 追加 {nAppend}  →  {newSize} 字节 ({(newSize - originSize >= 0 ? "+" : "")}{newSize - originSize})");
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
    /// 打包 DLC 数据包（-cdlc）：与 -c 同一条代码路径 ——
    ///   ① 默认文件名换成 DLC 的「system.res / package.rdp」（那两个 .edat）；
    ///   ② rdp 块大小 0x8000 → 0x10；③ 产物目录 4.pack → 5.dlcpack；
    ///   ④ list 目录 list → list_update，顶层 res 的 list 基名 → system_update
    ///      （顶层 res 是哈希名，list 名不能由它推）。
    /// </summary>
    public static int RunDlc(string cwd, string resName, string rdpName, bool compact = false)
    {
        int save = Pres.RdpShift;
        Pres.RdpShift = 4;                          // ★ DLC 的 rdp 块大小 = 0x10
        try
        {
            return Run(cwd, resName, compact, rdpName, Ws.DlcPackDir, "list_update",
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

    static int SlotInt(string n) => n switch
    {
        "res" => 0, "prx" => 1, "local" => 2, "refer" => 4,
        "table" => 5, "string" => 6, "rtbl" => 7, _ => -1,
    };

        }
