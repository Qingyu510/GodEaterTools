using System.IO.Compression;

namespace PSPGodEaterBurstTools;

/// <summary>
/// 解包：读当前目录的 system.res + package.rdp
///   1.out/system/...           数据（递归子 res 落到 1.out/system/&lt;子res名&gt;/）
///   1.out/list/system.json     顶层 list
///   1.out/list/system/xxx.json 递归子 res 的 list
///   1.out/list/package.json    rdp 块表（汇总所有层级）
///
/// 语言层：条目 ElementName 的【第三个值】若非空，则作为一层子目录 ——
///   例 system.res 的 ['ICON0e','PNG','jajp'] → 1.out/system/jajp/ICON0e.PNG；
///   缺失或为空则该条目不加层（默认行为不变）。
/// </summary>
public static class Unpacker
{
    public static int Run(string cwd, string resName, string rdpName, int maxDepth = 8,
                          string dataDirName = null, string listDirName = "list")
    {
        string resPath = Path.Combine(cwd, resName);
        string rdpPath = Path.Combine(cwd, rdpName);
        if (!File.Exists(resPath)) { Console.Error.WriteLine($"错误: 找不到 {resName}（目录 {cwd}）"); return 1; }
        if (!File.Exists(rdpPath)) { Console.Error.WriteLine($"错误: 找不到 {rdpName}（目录 {cwd}）"); return 1; }

        string rootOut = Path.Combine(cwd, Ws.OutDir);
        using var ctx = new Ctx { Cwd = cwd, RootOut = rootOut, Rdp = new RdpReader(rdpPath), MaxDepth = maxDepth };

        Console.WriteLine($"== 解包 ==");
        Console.WriteLine($"  输入 : {resName} + {rdpName}  ({new FileInfo(resPath).Length} + {new FileInfo(rdpPath).Length} 字节)");
        if (dataDirName == null && listDirName == "list")
            Console.WriteLine($"  输出 : {Ws.OutDir}/");
        else
            Console.WriteLine($"  输出 : {Ws.OutDir}/{dataDirName}/  +  {Ws.OutDir}/{listDirName}/");

        byte[] data = File.ReadAllBytes(resPath);
        string stem = Path.GetFileNameWithoutExtension(resName);
        // ★ DLC 数据包（-xdlc）：顶层 res 是哈希名（f3594808….edat），
        //   数据目录名与 list 文件名显式定为 system_update（对应本体的 system.res）。
        string outStem = dataDirName ?? stem;
        string dataDir = Path.Combine(rootOut, outStem);
        string listFile = Path.Combine(rootOut, listDirName, outStem + ".json");

        if (Prof.Enabled) Prof.QuickWriteBench(rootOut);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tRun = Prof.Ticks();
        UnpackRes(data, resName, resName, dataDir, listFile, ctx, 0);
        Prof.TotalMs = Prof.Delta(tRun);

        // 汇总 rdp 块表
        var list = new PackageList
        {
            RdpShift = Pres.RdpShift,
            BlockSize = Pres.BlockSize,
            TotalBlocks = (ctx.Rdp.Length + Pres.BlockSize - 1) / Pres.BlockSize,
            Entries = ctx.AllEntries.OrderBy(x => x.Block).ToList(),
        };
        string pkgPath = Path.Combine(rootOut, listDirName, "package.json");
        JsonIo.Save(pkgPath, list);

        sw.Stop();
        long nref = list.Entries.Sum(e => (long)e.Refs.Count);
        Console.WriteLine();
        Console.WriteLine($"== 完成 ({sw.Elapsed.TotalSeconds:F1}s) ==");
        Console.WriteLine($"  package.json : {list.Entries.Count} 个块条目 / {nref} 条引用");
        if (list.Entries.Count > 0)
            Console.WriteLine($"  块位范围     : {list.Entries[0].Block} ~ {list.Entries[^1].Block}  (rdp 共 {list.TotalBlocks} 块)");
        Console.WriteLine($"  压缩条目     : {list.Entries.Count(e => e.Compressed)}  本身是 res 的条目: {list.Entries.Count(e => e.IsRes)}");
        Console.WriteLine($"  位置         : {Ws.Rel(cwd, pkgPath)}");
        if (Prof.Enabled) Console.WriteLine("  " + Prof.Dump());
        return 0;
    }

    /// <summary>
    /// 解包 DLC 数据包（-xdlc）：与 -x 走【完全同一条路径】，差别只有三点 ——
    ///   ① 默认文件名：system.res → f3594808….edat、package.rdp → 4a92f06c….edat
    ///   ② rdp 块大小 0x8000 → 0x10（块号 &lt;&lt; 4 即文件字节偏移）
    ///   ③ 顶层 res 是哈希名，数据目录 / list 固定命名为 system_update / list_update
    /// 注：大包开头那 0x10 字节头【就是块 0】—— 由 offset=0 的 version.dat 条目承载，
    ///     故块寻址与本体完全一致（无额外基址偏移），重建时也自动复原。
    /// </summary>
    public static int RunDlc(string cwd, string resName, string rdpName, int maxDepth = 8)
    {
        int save = Pres.RdpShift;
        Pres.RdpShift = 4;                          // ★ DLC 的 rdp 块大小 = 0x10
        try
        {
            return Run(cwd, resName, rdpName, maxDepth,
                       dataDirName: "system_update", listDirName: "list_update");
        }
        finally { Pres.RdpShift = save; }
    }

    static void UnpackRes(byte[] data, string resName, string resId, string dataDir, string listFile, Ctx ctx, int depth)
    {
        Directory.CreateDirectory(dataDir);
        string dataDirRel = Ws.Rel(ctx.RootOut, dataDir);
        long t0 = Prof.Ticks();
        var (groups, entries) = Pres.ParseToc(data);
        Prof.Add(ref Prof.MsToc, t0);
        uint raw0c = Pres.U32(data, 0x0C);

        var js = new ResList
        {
            Filename = ".\\" + resName,
            Magic = Pres.U32(data, 0),
            GroupsCount = data[8],
            Version = data[9],
            Package = Pres.U16(data, 0x0A),
            Sign = (int)(raw0c & 3),
            CeckSum = raw0c.ToString("X8"),
            Files = new List<ResEntry>(),
        };

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nFiles = 0, nDummy = 0, nPacked = 0, nFailed = 0;
        long nBytes = 0;

        // 重名去重：键带上语言层，使不同语言层的同名文件互不干扰（返回值仍是纯名字）
        string DedupIn(string lang, string name)
        {
            string Key(string n) => lang.Length == 0 ? n : lang + "/" + n;
            if (used.Add(Key(name))) return name;
            string b = Path.GetFileNameWithoutExtension(name);
            string x = Path.GetExtension(name);
            for (int i = 1; ; i++)
            {
                string c = $"{b}_{i:D4}{x}";
                if (used.Add(Key(c))) return c;
            }
        }

        foreach (var e in entries.OrderBy(x => x.Group).ThenBy(x => x.Index))
        {
            int g = e.Group;
            var item = new ResEntry { Group = g.ToString() };

            // ---- dummy ----
            if (e.ChunkName == 0)
            {
                item.ElementName = null;
                item.FileName = "dummy";
                item.Compression = false;
                item.DummySize = e.Size;
                js.Files.Add(item);
                nDummy++;
                continue;
            }

            item.ElementName = e.ElementName;
            string fname = Pres.NameOf(e.ElementName);
            item.FileName = "\\" + fname;

            // ElementName 的第三个值（如 jajp）= 该条目的解包子目录；缺失或为空则不加层
            string langRaw = e.ElementName.Count >= 3 ? (e.ElementName[2] ?? "").Trim() : "";
            string langDir = langRaw.Length == 0 ? "" : Ws.Sanitize(langRaw);

            long length = g == 5 ? e.Size * 4 : e.Size;
            // Sign==1 的 res 不引用 package.rdp：prx(G1)/refer(G4) 的 offset 是文件内偏移、size 恒为 0，
            // 数据是 \0 终止的文本（同 G2/G5/G6 一样排在 body 里），按 res 内条目读取。
            bool asText = (raw0c & 3) == 1 && Pres.Sign1TextSlots.Contains(g) && e.Size == 0;
            bool fromRdp = !Pres.InRes.Contains(g) && !asText;
            byte[] raw;
            try
            {
                t0 = Prof.Ticks();
                if (!fromRdp) raw = asText ? SliceCStr(data, e.Offset) : Slice(data, e.Offset, length);
                else if (e.Size == 0) raw = Array.Empty<byte>();      // 无数据可读，也不占块
                else
                {
                    raw = ctx.Rdp.Read(e.Offset << Pres.RdpShift, (int)length);
                    if (raw == null || raw.Length < length) throw new IOException($"rdp 越界 block=0x{e.Offset:X}");
                }
                long dR = Prof.Delta(t0);
                Prof.MsRead += dR;
                Prof.ReadCount++;
                Prof.ReadBytes += raw.Length;
                if (dR > Prof.MaxReadMs) Prof.MaxReadMs = dR;
            }
            catch (Exception ex)
            {
                item.Compression = false;
                js.Files.Add(item);
                nFailed++;
                Console.WriteLine($"  !! 读取失败 G{g}[{e.Index}] {fname} : {ex.Message}");
                continue;
            }

            bool packed = Pres.IsGzip(raw);
            item.Compression = packed;
            byte[] outRaw = raw;
            if (packed)
            {
                nPacked++;
                try { outRaw = Gunzip(raw); }
                catch { outRaw = raw; item.Compression = false; }
            }
            if (g == 6 && outRaw.Length > 0) outRaw = outRaw[..^1];   // string 去掉结尾 0x00

            t0 = Prof.Ticks();
            string rel = DedupIn(langDir, Ws.Sanitize(fname));
            Prof.Add(ref Prof.MsDedup, t0);

            string relLang = langDir.Length == 0 ? rel : langDir + "/" + rel;
            string outPath = Path.Combine(dataDir, Ws.ToOs(relLang));
            if (langDir.Length > 0) Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            t0 = Prof.Ticks();
            File.WriteAllBytes(outPath, outRaw);
            long dW = Prof.Delta(t0);
            Prof.MsWrite += dW;
            Prof.WriteCount++;
            Prof.WriteBytes += outRaw.Length;
            if (dW > Prof.MaxWriteMs) Prof.MaxWriteMs = dW;
            nFiles++;
            nBytes += outRaw.Length;
            item.ExportPath = dataDirRel + "/" + relLang;

            // res 只可能出现在 rdp 侧条目、或 local(G2) 内嵌；table/string/prx文本/refer文本 是纯数值/文本，防字节巧合误判
            bool isRes = Pres.IsPres(outRaw) && (fromRdp || g == 2);
            if (fromRdp && e.Size > 0)
                ctx.AddBlock(e.Offset, e.Size, packed, outRaw.Length, isRes,
                             fname, dataDirRel + "/" + relLang, resId, Pres.SlotName[g], e.Index);
            js.Files.Add(item);

            // ---- 递归：该条目本身就是一个 res ----
            // 两种来源都要递归：① rdp 侧条目（G0/G1/G4/G7）② res 内嵌（local/table/string 里的 Pres）
            if (isRes && depth < ctx.MaxDepth)
            {
                string subStem = Path.GetFileNameWithoutExtension(rel);
                string subName = DedupIn(langDir, subStem);
                string subDataDir = langDir.Length == 0 ? Path.Combine(dataDir, subName)
                                                       : Path.Combine(dataDir, Ws.ToOs(langDir), subName);
                string subResRel = dataDirRel + "/" + relLang;
                string subListFile = listFile[..^".json".Length] + "/"
                                   + (langDir.Length == 0 ? "" : langDir + "/") + subName + ".json";
                try
                {
                    UnpackRes(outRaw, fname, subResRel, subDataDir, subListFile, ctx, depth + 1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  !! 递归解包失败 {fname} : {ex.Message}");
                }
            }
        }

        t0 = Prof.Ticks();
        JsonIo.Save(listFile, js);
        Prof.Add(ref Prof.MsJson, t0);

        Prof.ResCount++;
        if (depth == 0) Prof.FileCount = nFiles;

        string pad = new string(' ', depth * 2);
        t0 = Prof.Ticks();
        Console.WriteLine($"{pad}· {resName}  →  {nFiles} 文件/{nBytes} 字节  dummy {nDummy}  压缩 {nPacked}  失败 {nFailed}");
        Console.WriteLine($"{pad}  list: {Ws.Rel(ctx.Cwd, listFile)}");
        Prof.Add(ref Prof.MsLog, t0);

        if (Prof.Enabled && Prof.ResCount % 25 == 0)
            Console.WriteLine("      " + Prof.Dump());
    }

    static byte[] Slice(byte[] d, long off, long len)
    {
        if (off < 0) off = 0;
        var buf = new byte[len];
        if (off >= d.Length) return buf;
        long avail = Math.Min(len, d.Length - off);
        Array.Copy(d, off, buf, 0, avail);
        return buf;
    }

    /// <summary>从 off 读到第一个 0x00 之前（prx/refer 文本用，长度不由 size 给出）</summary>
    static byte[] SliceCStr(byte[] d, long off)
    {
        if (off < 0 || off >= d.Length) return Array.Empty<byte>();
        long e = off;
        while (e < d.Length && d[e] != 0) e++;
        if (e == off) return Array.Empty<byte>();
        var buf = new byte[e - off];
        Array.Copy(d, off, buf, 0, buf.Length);
        return buf;
    }

    static byte[] Gunzip(byte[] raw)
    {
        using var ms = new MemoryStream(raw);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }
}
