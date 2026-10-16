using System.Text;

namespace PSVGodEater2TrialTools;

/// <summary>
/// 解包：读当前目录的 system.res + package.rdp
///   1.out/system/...           数据（递归子 res 落到 1.out/system/&lt;子res名&gt;/）
///   1.out/list/system.json     顶层 list
///   1.out/list/system/xxx.json 递归子 res 的 list
///   1.out/list/package.json    rdp 块表（汇总所有层级）
///
/// PSV 差异（相对 PSP，判定见 Pres.IsExcludeRdp / Pres.IsSelfDecl）：
///   · G4 的 ElementName[3] == "EXCLUDE_RDP" ⇒ 文件被排除在 rdp 之外，list 只记 ElementName + Size
///     （区块值恒 -1，打包时按 Size 回填行大小）；
///   · G1 的 ext 为 "self"（PSP 原为 prx）⇒ self 不在 rdp 包内、走本地路径，list 只记 ElementName。
///   两者都不读 rdp、不写解包产物、不登记 package.json。
///
/// rtbl（组 7）：条目本身是「每 0x80 一槽的 rdp 引用表」，里面每一条都索引 package.rdp。
///   解包时额外分两级展开 + 把这些被引用条目登记进 package.json（见 UnpackRtbl）。
///
/// 语言层：条目 ElementName 的【第三个值】若非空，则作为一层子目录 ——
///   例 system.res 的 ['ICON0e','PNG','jajp'] → 1.out/system/jajp/ICON0e.PNG；
///   缺失或为空则该条目不加层（默认行为不变）。
/// </summary>
public static class Unpacker
{
    public static int Run(string cwd, string resName, string rdpName, int maxDepth = 8)
    {
        string resPath = Path.Combine(cwd, resName);
        string rdpPath = Path.Combine(cwd, rdpName);
        if (!File.Exists(resPath)) { Console.Error.WriteLine($"错误: 找不到 {resName}（目录 {cwd}）"); return 1; }
        if (!File.Exists(rdpPath)) { Console.Error.WriteLine($"错误: 找不到 {rdpName}（目录 {cwd}）"); return 1; }

        string rootOut = Path.Combine(cwd, Ws.OutDir);
        using var ctx = new Ctx { Cwd = cwd, RootOut = rootOut, Rdp = new RdpReader(rdpPath), MaxDepth = maxDepth };

        Console.WriteLine($"== 解包 ==");
        Console.WriteLine($"  输入 : {resName} + {rdpName}  ({new FileInfo(resPath).Length} + {new FileInfo(rdpPath).Length} 字节)");
        Console.WriteLine($"  输出 : {Ws.OutDir}/");

        byte[] data = File.ReadAllBytes(resPath);
        string stem = Path.GetFileNameWithoutExtension(resName);
        string dataDir = Path.Combine(rootOut, stem);
        string listFile = Path.Combine(rootOut, "list", stem + ".json");

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
        string pkgPath = Path.Combine(rootOut, "list", "package.json");
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

    static void UnpackRes(byte[] data, string resName, string resId, string dataDir, string listFile, Ctx ctx, int depth)
    {
        Directory.CreateDirectory(dataDir);
        string dataDirRel = Ws.Rel(ctx.RootOut, dataDir);
        long t0 = Prof.Ticks();
        var (groups, entries) = Pres.ParseToc(data);
        Prof.Add(ref Prof.MsToc, t0);
        uint raw0c = Pres.U32(data, 0x0C);

        // ★ 组在 TOC 里的物理顺序（按各组 offset 升序，只列非空组）——
        //   同一 res 内并不总按组号升序（god_menu.res = 2,4,5,6,7,1,0；Field.res = 2,5,4），
        //   而 TOC 区段与 body 的排布都按它来，打包时必须照原样复现。
        var gorder = new List<(long off, int g)>();
        for (int g = 0; g < 8; g++) if (groups[g, 1] > 0) gorder.Add((groups[g, 0], g));
        gorder.Sort((a, b) => a.off.CompareTo(b.off));

        var js = new ResList
        {
            Filename = ".\\" + resName,
            Magic = Pres.U32(data, 0),
            GroupsCount = data[8],
            Version = data[9],
            Package = Pres.U16(data, 0x0A),
            Sign = (int)(raw0c & 3),
            CeckSum = raw0c.ToString("X8"),
            Groups = gorder.Select(x => x.g).ToList(),
            Files = new List<ResEntry>(),
        };

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nFiles = 0, nDummy = 0, nFailed = 0, nExcl = 0, nSelf = 0;
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

            // ★ PSV：EXCLUDE_RDP（ElementName[3]）—— 文件被排除在 rdp 之外，只记录 ElementName + 真实大小。
            //   不读 rdp / 不写产物 / 不登记 package.json（该条目区块值恒为 -1，打包时按 list 的 Size 回填）。
            if (Pres.IsExcludeRdp(e.ElementName))
            {
                item.Size = e.Size.ToString();
                item.Compression = false;
                js.Files.Add(item);
                nExcl++;
                continue;
            }

            // ★ PSV：G1 self（PSP 为 prx）—— self 不存在于 rdp 包内、走本地路径，只记录 ElementName。
            if (Pres.IsSelfDecl(g, e.ElementName))
            {
                item.Compression = false;
                js.Files.Add(item);
                nSelf++;
                continue;
            }

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

            // GE2 体验版数据实测 0 命中 dflt 压缩段，已移除解压缩路径。
            item.Compression = false;
            byte[] outRaw = raw;
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
                ctx.AddBlock(e.Offset, e.Size, false, outRaw.Length, isRes,
                             fname, dataDirRel + "/" + relLang, resId, Pres.SlotName[g], e.Index);
            js.Files.Add(item);

            // ---- rtbl：条目本身是「rdp 引用表」，它引用的文件也要解包并登记进 package.json ----
            if (Rtbl.IsSlot(g) && fromRdp && outRaw.Length >= Rtbl.SlotSize)
            {
                // ★ rtbl 的「槽位 bit 表」写在【父 res 的名字区之后】，不在 .rtbl 文件里。
                //   位数 = rtbl 文件字节数 ÷ 0x80，1 = 该槽有引用（与 rtbl 非零槽 100% 一致）。
                //   记进 ElementName 第 5 项，打包时再写回原位。
                if (e.ElementName != null)
                {
                    int slots = outRaw.Length / Rtbl.SlotSize;
                    long nl = 0;
                    foreach (var s0 in e.ElementName) nl += Encoding.UTF8.GetByteCount(s0) + 1;
                    nl += e.ElementName.Count * 4L;                 // cn 个字符串指针
                    long bitOff = e.NameOffset + nl;
                    var sb = new StringBuilder(slots * 2);
                    for (int s = 0; s < slots; s++)
                    {
                        long q = bitOff + (s >> 3);
                        byte bb = (q >= 0 && q < data.Length) ? data[q] : (byte)0;
                        sb.Append(((bb >> (s & 7)) & 1) != 0 ? '1' : '0');
                        if (s + 1 < slots) sb.Append(',');
                    }
                    e.ElementName.Add(sb.ToString());
                    item.ElementName = e.ElementName;
                }
                try
                {
                    UnpackRtbl(outRaw, fname, dataDirRel + "/" + relLang, dataDirRel, dataDir, langDir, ctx, depth);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  !! rtbl 解包失败 {fname} : {ex.Message}");
                }
            }

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
        Console.WriteLine($"{pad}· {resName}  →  {nFiles} 文件/{nBytes} 字节  dummy {nDummy}  失败 {nFailed}"
            + (nExcl > 0 ? $"  EXCLUDE_RDP {nExcl}" : "") + (nSelf > 0 ? $"  self {nSelf}" : ""));
        Console.WriteLine($"{pad}  list: {Ws.Rel(ctx.Cwd, listFile)}");
        Prof.Add(ref Prof.MsLog, t0);

        if (Prof.Enabled && Prof.ResCount % 25 == 0)
            Console.WriteLine("      " + Prof.Dump());
    }

    /// <summary>
    /// rtbl（组 7）：每 0x80 一槽，槽里记一条 package.rdp 引用（块号 / size / 名字）。
    /// 解包分两级：
    ///   第 1 级 —— 每条引用的 res 原样取出 → &lt;rtbl 目录&gt;/&lt;条目名&gt;.res
    ///   第 2 级 —— 每个 res 按 res 包方式再解包 → &lt;rtbl 目录&gt;/&lt;条目名&gt;/
    /// 这些被引用条目本身不在父 res 的 TOC 里，父 res 只引用 rtbl 文件，
    /// 所以必须在这里 ctx.AddBlock 把它们追加进 package.json（打包时才认得出、才有块位）。
    /// 另输出：固定槽位 JSON（&lt;rtbl&gt;.json）与块区记录（_rtbl_blocks.txt）。
    /// </summary>
    static void UnpackRtbl(byte[] rtblData, string rtblFile, string rtblId, string dataDirRel,
                           string parentDataDir, string langDir, Ctx ctx, int depth)
    {
        string stem = Path.GetFileNameWithoutExtension(rtblFile);
        string outDirRel = (langDir.Length == 0 ? dataDirRel : dataDirRel + "/" + langDir) + "/" + stem;
        string outDir = Path.Combine(parentDataDir, langDir.Length == 0 ? "" : Ws.ToOs(langDir), stem);
        Directory.CreateDirectory(outDir);

        var raw = Rtbl.Parse(rtblData);
        int slots = rtblData.Length / Rtbl.SlotSize;
        var js = new RtblList { TableCount = slots, Files = new List<RtblEntry>(raw.Count) };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rec = new List<string>(raw.Count);
        int ok = 0, fail = 0;

        foreach (var e in raw)
        {
            if (e.Size <= 0) { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} size=0"); continue; }

            byte[] seg;
            try { seg = ctx.Rdp.Read(e.Block << Pres.RdpShift, (int)e.Size); }
            catch (Exception ex) { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} 读取失败: {ex.Message}"); continue; }
            if (seg == null || seg.Length < e.Size)
            { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} rdp 越界 block=0x{e.Block:X}"); continue; }

            // GE2 体验版数据实测 0 命中 dflt 压缩段，rtbl 槽不再解压。
            byte[] resData = seg;
            if (!Pres.IsPres(resData))
            { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} 不是 Pres: {e.FileName}"); continue; }

            string fn = Ws.Dedup(Ws.Sanitize(e.FileName), used);
            string resRel = outDirRel + "/" + fn;
            File.WriteAllBytes(Path.Combine(outDir, fn), resData);

            // ★ rtbl 内引用的条目也要进 package.json（否则打包时不会重建、也查不到块位）
            ctx.AddBlock(e.Block, e.Size, false, resData.Length, true,
                         e.FileName, resRel, rtblId, "rtbl", e.Slot);

            js.Files.Add(new RtblEntry
            {
                Table = e.Slot.ToString(),
                ElementName = new List<string> { e.Name, e.Ext, "" },
                FileName = "\\" + e.FileName,
                Compression = false,
                ExportPath = resRel,
            });
            rec.Add($"{e.Slot,4} 0x{e.Block:X8} 0x{(e.Block << Pres.RdpShift):X10} {e.Size,9}  "
                  + $"{fn,-46} -> {Path.GetFileNameWithoutExtension(fn)}/");

            // 第 2 级：按 res 包方式解包
            string subDir = Path.Combine(outDir, Path.GetFileNameWithoutExtension(fn));
            string subList = Path.Combine(ctx.RootOut, "list", Ws.ToOs(Ws.ListRelOf(resRel)));
            try { UnpackRes(resData, e.FileName, resRel, subDir, subList, ctx, depth + 1); }
            catch (Exception ex) { Console.WriteLine($"  !! rtbl 槽 {e.Slot} 递归解包失败 {fn} : {ex.Message}"); }
            ok++;
        }

        string listPath = Path.Combine(ctx.RootOut, "list", Ws.ToOs(Ws.ListRelOf(rtblId)));
        JsonIo.Save(listPath, js);

        var sb = new StringBuilder();
        sb.AppendLine("# rtbl 块区记录  (条目 offset = rdp 块号 ×0x800)");
        sb.AppendLine($"# 槽位 {slots} 个（有效 {raw.Count}）  rdp 块粒度 0x{Pres.BlockSize:X}  条目数 {ok}  失败 {fail}");
        sb.AppendLine("#  槽位      rdp块号    rdp字节偏移      size  rtbl内文件                                     -> 解包目录");
        foreach (var l in rec) sb.AppendLine(l);
        File.WriteAllText(Path.Combine(outDir, "_rtbl_blocks.txt"), sb.ToString(), new UTF8Encoding(false));

        string pad = new string(' ', depth * 2);
        Console.WriteLine($"{pad}  rtbl {rtblFile}  →  {ok}/{raw.Count} 条 res（槽位 {slots}，失败 {fail}）");
        Console.WriteLine($"{pad}    list: {Ws.Rel(ctx.Cwd, listPath)}");
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
}
