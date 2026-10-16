using System.IO.Compression;
using System.Text;

namespace PS4GodEaterSeriesTools;

/// <summary>
/// 解包：读当前目录的 system.res + package.rdp + data.rdp
///   1.out/system/...           数据（递归子 res 落到 1.out/system/&lt;子res名&gt;/）
///   1.out/list/system.json     顶层 list
///   1.out/list/system/xxx.json 递归子 res 的 list
///   1.out/list/package.json    package.rdp 块表（汇总所有层级）
///   1.out/list/data.json       data.rdp 块表（汇总所有层级）—— 双 rdp 变革后新增
///
/// ★ GE2 正式版 offset 的高 4 位是【数据来源类型】（体验版没有，恒 0）：
///   0xC —— 数据在本 res 文件内（低 28 位 = 文件内绝对偏移）；local(G2) 排在 res 包之后的文件尾，
///          table(G5) / string(G6) 排在 body 里。宿主 res 在 system.res 本身 / data.rdp 里时用它
///   0xD —— 语义与 0xC 相同（数据同样在本 res 文件内），仅宿主 res 本身位于 package.rdp 时用它
///   0x4 —— 数据在 package.rdp（低 28 位 = 块号）
///   0x5 —— 数据在 data.rdp（低 28 位 = 块号；为了让过大的包适配 UMD，游戏可把 data.rdp 放进记忆卡）
///   0x6 —— 数据在【更新补丁 patch.rdp】上（低 28 位 = 块号）。仅更新补丁的 res 会出现
///   0x3 —— ★ PSV：EXCLUDE_RDP —— 文件【不在任何 rdp 内】，游戏按 ElementName 里的 "PATH=..." 从本地路径取；
///          低 28 位恒 0，但 +0x08 字节数与 +0x34 解压大小【照常保留真值】（PSV 侧实测 god_m002_Movie.res 组 4）
///   0   —— 空条目（无数据）。★ G1 的 prx 条目（ElementName = [base,"prx",""]）也是 0，
///          但它的 +0x08 / +0x34 同样为 0（三项全空，与 EXCLUDE_RDP 的"保留真值"相反）
/// 类型与组的关系：tbl(G5) / string(G6) 必为 C；local(G2) 可为 C / D / 4 / 5；res(G0) / prx(G1) / refer(G4) / rtbl(G7) 只出 4 / 5 / 6。
/// list 里以 Type 字段原样记录（"C" / "D" / "4" / "5" / "6" / "0"），打包时【以 Type 为唯一判据】，不看组、也不靠 rdp 表命中来猜。
/// 另：TOC +0x34 是【解密（解压）后大小】，原样记进 list 的 decsize，打包时回填。
///
/// ★ 更新补丁（-xdlc）：system_update.res 解包、patch.rdp 当数据源。
///   补丁 res 头部 +0x20 / +0x28 是【本体 res 指针】（BaseType / BaseOffset / BaseSize）：
///   有值时该 res 的【local(G2) 组 C/D 条目】索引到本体 res 的对应偏移（offset 与本体 res 的完全一致、
///   且可能超出补丁 res 自身长度），而 G5(table) / G6(string) 仍在自己 body 里。
///   按需求：res 头部 14 有值时 local 的 C / D 一律【不解包】；-xdlc 时全群组的 4 / 5 标志文件也【不解包】
///   （它们在本体 package.rdp / data.rdp 上，不属于本次更新的产物），只有 6（patch.rdp）与 res 内的 C/D 才落盘。
///
/// rtbl（组 7）：条目本身是「每 0x200 一槽的 rdp 引用表」，里面每一条都索引 package.rdp。
///   解包时额外分两级展开 + 把这些被引用条目登记进 package.json（见 UnpackRtbl）。
///   ★ rtbl 条目在 res 里的【名字区之后】还紧跟一段「槽位 bit 表」：每槽 1 bit（1 = 该槽有引用），
///     长度 = ceil(槽位数 ÷ 8)，位序 LSB first。解包时展开成逗号分隔的位串，存进 ElementName 第 5 项
///     （第 4 项仍是 "_EXISTS="）；打包时按它回写（见 Pres.Build）。
///
/// 语言层：条目 ElementName 的【第三个值】若非空，则作为一层子目录 ——
///   例 system.res 的 ['ICON0e','PNG','jajp'] → 1.out/system/jajp/ICON0e.PNG；
///   缺失或为空则该条目不加层（默认行为不变）。
/// </summary>
public static class Unpacker
{
    /// <summary>-xdlc 模式的开关集合：影响「哪些类型落盘」与「list / 产物目录名」</summary>
    public sealed class DlcMode
    {
        /// <summary>全群组的 4 / 5 标志文件都不解包（它们在本体 package.rdp / data.rdp 上）</summary>
        public bool SkipRdp45 = true;
        /// <summary>res 头部 14 有值时，local 组的 C / D 不解包（数据在本体 res 里）</summary>
        public bool SkipBaseLocal = true;
    }

    public static int Run(string cwd, string resName, string rdpName, string dataRdpName = "data.rdp", int maxDepth = 8)
        => RunCore(cwd, resName, rdpName, dataRdpName, maxDepth, null);

    /// <summary>
    /// ★ list-only 解包：只遍历 + 只写 list，【不落任何数据文件】。
    ///   供打包（-c）后对【刚打出来的 4.pack 产物】再跑一遍，生成一份记录着
    ///   【新偏移值 / 新大小 / 新解压大小】的新 list（4.pack/list/）。
    ///   用途：本体被修改后，原本 package.rdp / data.rdp 里的块位与 res 内的 C/D 偏移都会变动；
    ///   打包 DLC 时若带 BaseType 的 res，就必须按这份新 list 回填其 +0x20 / +0x28 与 4/5/C/D 条目的位置。
    ///   ★ 各 res 的 list 里 exportPath 前缀与 1.out/list 保持一致（dataDir 不变、只是不写文件），
    ///     所以新 list 与旧 list 可按 exportPath 逐条对应。
    /// </summary>
    public static int RunListOnly(string cwd, string resName, string rdpName, string dataRdpName,
                                  int maxDepth, string listOutDir)
    {
        // rdp 源在 4.pack/ 里（刚打出来的产物），res 也在 4.pack/；但 Cwd 仍指向工作目录
        //   —— exportPath 前缀由 RootOut/stem 决定，与 -x 时一致。
        string packDir = Path.Combine(cwd, Ws.PackDir);
        return RunCore(cwd, resName, rdpName, dataRdpName, maxDepth,
                       null, listDirName: "list", outDirName: null,
                       listOutDir: listOutDir, listOnly: true,
                       resPathOverride: Path.Combine(packDir, resName),
                       rdpPathOverride: Path.Combine(packDir, rdpName),
                       dataPathOverride: string.IsNullOrEmpty(dataRdpName) ? null
                                                 : Path.Combine(packDir, dataRdpName));
    }

    /// <summary>解包更新补丁：system_update.res + patch.rdp。
    ///   ★ 前置限制：本体目录的 1.out/list 下必须已有 system.json 与 package.json（先跑过一次 -x），
    ///     因为补丁里跳过的条目要靠本体 list 才能备注出 BaseExportPath 与条目级 ExportPath。</summary>
    public static int RunDlc(string cwd, string resName, string patchName, int maxDepth)
    {
        if (!BaseIndex.HasBaseList(cwd, out string why))
        {
            Console.Error.WriteLine($"错误: {why}");
            Console.Error.WriteLine($"      （-xdlc 需要本体已解包的 list 来备注 exportPath；请先在同一目录执行：PS4GodEaterSeriesTools -x）");
            return 1;
        }
        return RunCore(cwd, resName, patchName, null, maxDepth,
                       new DlcMode(), listDirName: "list_update", outDirName: "update");
    }

    /// <summary>
    /// ★ DLC 的 list-only 解包：打包（-cdlc）后对【刚产出的 5.dlcpack 产物】再跑一遍，
    ///   只遍历 + 只写 list、不落数据，输出到 5.dlcpack/list —— 记录 DLC 重打包后的新偏移 / 大小。
    ///   与 RunDlc 的区别：
    ///     · res / rdp 输入改指 5.dlcpack（刚打出来的产物）；
    ///     · 仍带 DlcMode（4/5 与 local C/D 不落盘），并用【本体 1.out/list】作基准备注 exportPath
    ///       （DLC 的 4/5/C/D 依旧指向本体，基准必须是本体的 list）。
    /// </summary>
    public static int RunDlcListOnly(string cwd, string patchResName, string patchName, int maxDepth, string listOutDir)
    {
        string packDir = Path.Combine(cwd, Ws.DlcPackDir);
        return RunCore(cwd, patchResName, patchName, null, maxDepth,
                       new DlcMode(), listDirName: "list_update", outDirName: "update",
                       listOutDir: listOutDir, listOnly: true,
                       resPathOverride: Path.Combine(packDir, patchResName),
                       rdpPathOverride: Path.Combine(packDir, patchName));
    }

    static int RunCore(string cwd, string resName, string rdpName, string dataRdpName, int maxDepth,
                       DlcMode dlc, string listDirName = "list", string outDirName = null,
                       string listOutDir = null, bool listOnly = false,
                       string resPathOverride = null, string rdpPathOverride = null, string dataPathOverride = null)
    {
        string resPath = resPathOverride ?? Path.Combine(cwd, resName);
        string rdpPath = rdpPathOverride ?? Path.Combine(cwd, rdpName);
        string dataPath = string.IsNullOrEmpty(dataRdpName) ? null
                        : (dataPathOverride ?? Path.Combine(cwd, dataRdpName));
        if (!File.Exists(resPath)) { Console.Error.WriteLine($"错误: 找不到 {resName}（目录 {cwd}）"); return 1; }
        if (!File.Exists(rdpPath)) { Console.Error.WriteLine($"错误: 找不到 {rdpName}（目录 {cwd}）"); return 1; }
        if (dataPath != null && !File.Exists(dataPath))
        {
            Console.WriteLine($"  注意 : 未找到 {dataRdpName} —— 若 TOC 中出现 0x5 类型条目会报错");
            dataPath = null;
        }

        string rootOut = Path.Combine(cwd, Ws.OutDir);
        using var ctx = new Ctx
        {
            Cwd = cwd, RootOut = rootOut, Rdp = new RdpReader(rdpPath),
            DataRdp = dataPath != null ? new RdpReader(dataPath) : null, MaxDepth = maxDepth,
            Dlc = dlc,
            // ★ 各 res 的 list 目录：本体 = list/，更新补丁 = list_update/（patch.json 同目录）
            ListDir = listDirName ?? "list",
            ListOnly = listOnly,
            ListOutDir = listOutDir,
            Quiet = listOnly,
        };
        // ★ DLC 模式：rdpName 就是 patch.rdp，条目类型 0x6，数据源与本体 rdp 分开记块表。
        if (dlc != null) ctx.PatchRdp = ctx.Rdp;

        // ★ DLC 模式：载入本体索引（package.json / data.json /各 res 的 list），
        //   用来给跳过的条目备注 BaseExportPath 与条目级 ExportPath。
        if (dlc != null)
        {
            ctx.BaseIdx = new BaseIndex(cwd);
            ctx.BaseIdx.Load();
        }

        Console.WriteLine($"== 解包{(dlc != null ? "（更新补丁）" : listOnly ? "（list-only）" : "")} ==");
        Console.WriteLine($"  输入 : {resName} + {rdpName}" + (dataPath != null ? $" + {dataRdpName}" : "")
                          + $"  ({new FileInfo(resPath).Length} + {new FileInfo(rdpPath).Length}"
                          + (dataPath != null ? $" + {new FileInfo(dataPath).Length}" : "") + " 字节)");
        if (listOnly)
            Console.WriteLine($"  输出 : 只写 list → {Ws.Rel(cwd, listOutDir)}（不落数据文件）");
        else
            Console.WriteLine($"  输出 : {Ws.OutDir}/" + (outDirName != null ? outDirName + "/" : "")
                              + (listDirName != "list" ? listDirName + "/" : ""));

        byte[] data = File.ReadAllBytes(resPath);
        string stem = Path.GetFileNameWithoutExtension(resName);
        // ★ list-only 模式（打包后生成 4.pack/list 用）：只遍历 + 只写 list，不落数据文件。
        //   dataDir 路径保持不变 —— dataDirRel 是 exportPath 的前缀，必须与 1.out/list 里逐字一致，
        //   否则打包 DLC 时无法按 exportPath 把新偏移对上旧条目。
        string dataDir = Path.Combine(rootOut, stem);
        // ★ res 的 list 目录：本体在 1.out/list/，更新补丁统一在 1.out/list_update/（块表 patch.json 同目录）
        string listDir = ctx.ListOutDir ?? Path.Combine(rootOut, ctx.ListDir);
        string listFile = Path.Combine(listDir, stem + ".json");

        if (Prof.Enabled) Prof.QuickWriteBench(rootOut);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tRun = Prof.Ticks();
        UnpackRes(data, resName, resName, dataDir, listFile, ctx, 0);
        Prof.TotalMs = Prof.Delta(tRun);

        // ★ DLC 模式：只有 patch.rdp 的块表（patch.json），本体 package.rdp / data.rdp 不参与
        if (dlc != null)
        {
            var plist = new PackageList
            {
                RdpShift = Pres.RdpShift,
                BlockSize = Pres.BlockSize,
                TotalBlocks = (ctx.PatchRdp.Length + Pres.BlockSize - 1) / Pres.BlockSize,
                Entries = ctx.AllPatchEntries.OrderBy(x => x.Block).ToList(),
            };
            // ★ list-only（-cdlc 打包后生成 5.dlcpack/list）：块表写到 listOutDir，
            //   否则会覆盖掉 1.out/list_update/patch.json（那轮解包的原产物）。
            string pPath = Path.Combine(ctx.ListOutDir ?? Path.Combine(rootOut, listDirName), "patch.json");
            JsonIo.Save(pPath, plist);
            sw.Stop();
            long pRef = plist.Entries.Sum(e => (long)e.Refs.Count);
            Console.WriteLine();
            Console.WriteLine($"== 完成 ({sw.Elapsed.TotalSeconds:F1}s) ==");
            Console.WriteLine($"  patch.json   : {plist.Entries.Count} 个块条目 / {pRef} 条引用");
            if (plist.Entries.Count > 0)
                Console.WriteLine($"  块位范围     : {plist.Entries[0].Block} ~ {plist.Entries[^1].Block}  (patch.rdp 共 {plist.TotalBlocks} 块)");
            Console.WriteLine($"  压缩条目     : {plist.Entries.Count(e => e.Compressed)}  本身是 res 的条目: {plist.Entries.Count(e => e.IsRes)}");
            Console.WriteLine($"  位置         : {Ws.Rel(cwd, pPath)}");
            return 0;
        }

        // ★ list-only 模式：块表写到 listOutDir（4.pack/list），不落数据
        string blkDir = ctx.ListOutDir ?? Path.Combine(rootOut, "list");

        // 汇总 rdp 块表（package.rdp / data.rdp 各自一份）
        var list = new PackageList
        {
            RdpShift = Pres.RdpShift,
            BlockSize = Pres.BlockSize,
            TotalBlocks = (ctx.Rdp.Length + Pres.BlockSize - 1) / Pres.BlockSize,
            Entries = ctx.AllEntries.OrderBy(x => x.Block).ToList(),
        };
        string pkgPath = Path.Combine(blkDir, "package.json");
        JsonIo.Save(pkgPath, list);

        PackageList dlist = null;
        string dataListPath = Path.Combine(blkDir, "data.json");
        if (ctx.DataRdp != null)
        {
            dlist = new PackageList
            {
                RdpShift = Pres.RdpShift,
                BlockSize = Pres.BlockSize,
                TotalBlocks = (ctx.DataRdp.Length + Pres.BlockSize - 1) / Pres.BlockSize,
                Entries = ctx.AllDataEntries.OrderBy(x => x.Block).ToList(),
            };
            JsonIo.Save(dataListPath, dlist);
        }

        sw.Stop();
        long nref = list.Entries.Sum(e => (long)e.Refs.Count);
        Console.WriteLine();
        Console.WriteLine($"== 完成 ({sw.Elapsed.TotalSeconds:F1}s) ==");
        Console.WriteLine($"  package.json : {list.Entries.Count} 个块条目 / {nref} 条引用");
        if (list.Entries.Count > 0)
            Console.WriteLine($"  块位范围     : {list.Entries[0].Block} ~ {list.Entries[^1].Block}  (rdp 共 {list.TotalBlocks} 块)");
        Console.WriteLine($"  压缩条目     : {list.Entries.Count(e => e.Compressed)}  本身是 res 的条目: {list.Entries.Count(e => e.IsRes)}");
        Console.WriteLine($"  位置         : {Ws.Rel(cwd, pkgPath)}");
        if (dlist != null)
        {
            long dref = dlist.Entries.Sum(e => (long)e.Refs.Count);
            Console.WriteLine($"  data.json    : {dlist.Entries.Count} 个块条目 / {dref} 条引用");
            if (dlist.Entries.Count > 0)
                Console.WriteLine($"  块位范围     : {dlist.Entries[0].Block} ~ {dlist.Entries[^1].Block}  (rdp 共 {dlist.TotalBlocks} 块)");
            Console.WriteLine($"  压缩条目     : {dlist.Entries.Count(e => e.Compressed)}  本身是 res 的条目: {dlist.Entries.Count(e => e.IsRes)}");
            Console.WriteLine($"  位置         : {Ws.Rel(cwd, dataListPath)}");
        }
        if (Prof.Enabled) Console.WriteLine("  " + Prof.Dump());
        if (listOnly)
        {
            Console.WriteLine($"  说明     : 该 list 记录的是【本轮打包后】的新偏移 / 新大小 / 新解压大小；");
            Console.WriteLine($"             打包 DLC 时带 BaseType 的 res 即按它回填 +0x20 / +0x28 与 4/5/C/D 条目的位置。");
        }
        return 0;
    }

    static void UnpackRes(byte[] data, string resName, string resId, string dataDir, string listFile, Ctx ctx, int depth)
    {
        // ★ list-only 模式：只建目录不落数据（list 目录仍要建）
        if (!ctx.ListOnly) Directory.CreateDirectory(dataDir);
        string dataDirRel = Ws.Rel(ctx.RootOut, dataDir);
        long t0 = Prof.Ticks();
        var (groups, entries) = Pres.ParseToc(data);
        Prof.Add(ref Prof.MsToc, t0);
        uint raw0c = Pres.U32(data, 0x14);            // ★ PS4：Sign 在 +0x14

        // ★ 本体 res 指针（头部 +0x20 / +0x28）—— 更新补丁专用，全 0 = 本体即自身。
        //   有值 ⇒ 该 res 的 local(G2) 组 C/D 条目索引到本体 res（偏移与本体一致），而非自身文件。
        var baseRef = Pres.ReadBaseRef(data);
        bool hasBase = baseRef.Present;

        // ★ 组在 TOC 里的物理顺序（按各组 offset 升序，只列非空组）——
        //   同一 res 内并不总按组号升序（god_menu.res = 2,4,5,6,7,1,0；Field.res = 2,5,4），
        //   而 TOC 区段与 body 的排布都按它来，打包时必须照原样复现。
        var gorder = new List<(long off, int g)>();
        for (int g = 0; g < 8; g++) if (groups[g, 1] > 0) gorder.Add((groups[g, 0], g));
        gorder.Sort((a, b) => a.off.CompareTo(b.off));

        var js = new ResList
        {
            Filename = ".\\" + resName,
            Magic = Pres.U32(data, 0x00),
            GroupsCount = data[0x10],                          // ★ PS4：GroupsCount / Version / PackageId 在 +0x10
            Version = data[0x11],
            Package = Pres.U16(data, 0x12),
            Sign = (int)(raw0c & 3),
            CeckSum = raw0c.ToString("X8"),
            Groups = gorder.Select(x => x.g).ToList(),
            Files = new List<ResEntry>(),
        };
        // ★ 本体指针原样记进 list（BaseType = 高 4 位类型，BaseOffset = 低 28 位，BaseSize = +0x28）
        //   BaseSize 为★十进制；BaseExportPath 到本体 package.json / data.json 按 (BaseType, BaseOffset) 匹配得到。
        string baseExportPath = null;
        if (hasBase)
        {
            js.BaseType = Pres.TypeStr(baseRef.Type);
            js.BaseOffset = baseRef.Offset.ToString("X");
            js.BaseSize = baseRef.Size.ToString();          // ★十进制
            // ★ 本体 res 的解包路径：+0x20 的 (类型, 块号) 就是本体块表里的键
            baseExportPath = ctx.BaseIdx?.ExportOfBlock(baseRef.Type, baseRef.Offset);
            js.BaseExportPath = baseExportPath;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nFiles = 0, nDummy = 0, nPacked = 0, nFailed = 0, nSkipped = 0, nSkipPath = 0, nRtblSkip = 0;
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
            // ★ 正式版（ver≥3）：把 offset 高 4 位的来源类型原样记进 list（C/4/5/0），打包时以它为准；
            //   体验版 TOC 没有类型位，不输出该字段（回退按组判定）。
            if (js.Version >= 3) item.Type = Pres.TypeStr(e.OffsetType);
            // ★ offset 低 28 位原样记进 list：C/D 是 res 文件内绝对偏移，4/5/6 是 rdp 块号（类型见上面的 Type）
            item.Offset = DecOf(e.Offset);
            // ★ TOC +0x08 的数据字节数原样记进 list —— 打包 DLC（-cdlc）修正 4/5/C/D 索引时要用它。
            //   跳过解包的条目（指向本体的 4/5 与 local C/D）同样要记：它们的真值只存在于本体 res 里。
            item.Size = e.Size;

            // ---- dummy ----
            if (e.ChunkName == 0)
            {
                item.ElementName = null;
                item.FileName = "dummy";
                item.Compression = false;
                item.DummySize = e.Size;
                if (e.DecSize != 0) item.DecSize = e.DecSize;    // ★ +0x34 解密后大小（正式版 dummy 也会记）
                js.Files.Add(item);
                nDummy++;
                continue;
            }

            item.ElementName = e.ElementName;
            string fname = Pres.NameOf(e.ElementName);
            item.FileName = "\\" + fname;
            if (e.DecSize != 0) item.DecSize = e.DecSize;        // ★ +0x34 解密后大小（打包时回填）

            // ElementName 的第三个值（如 jajp）= 该条目的解包子目录；缺失或为空则不加层
            string langRaw = e.ElementName.Count >= 3 ? (e.ElementName[2] ?? "").Trim() : "";
            string langDir = langRaw.Length == 0 ? "" : Ws.Sanitize(langRaw);

            // ★ offset 高 4 位 = 数据来源（GE2 正式版）；体验版无类型位（0），退回按组判定。
            // Sign==1 的 res 不引用 package.rdp：prx(G1)/refer(G4) 的 offset 是文件内偏移、size 恒为 0，
            // 数据是 \0 终止的文本（同 G2/G5/G6 一样排在 body 里），按 res 内条目读取。
            int ot = e.OffsetType;
            bool sign1Text = (raw0c & 3) == 1 && Pres.Sign1TextSlots.Contains(g) && e.Size == 0;
            bool inRes, fromData, fromPatch;
            switch (ot)
            {
                case Pres.TypeRes:                              // 0xC：本 res 文件内（宿主 = system.res / data.rdp 里的 res）
                case Pres.TypeResInPkg: inRes = true; fromData = false; fromPatch = false; break;  // 0xD：同上，宿主 = package.rdp 里的 res
                case Pres.TypeData: inRes = false; fromData = true; fromPatch = false; break;    // 0x5：data.rdp
                case Pres.TypePackage: inRes = false; fromData = false; fromPatch = false; break; // 0x4：package.rdp
                case Pres.TypePatch: inRes = false; fromData = false; fromPatch = true; break;    // 0x6：更新补丁 patch.rdp
                case Pres.TypeExcludeRdp: inRes = false; fromData = false; fromPatch = false; break; // 0x3：EXCLUDE_RDP（不在任何 rdp 内）
                default:                                                       // 0：无类型（体验版）
                    inRes = Pres.InRes.Contains(g) || sign1Text; fromData = false; fromPatch = false; break;
            }
            bool asText = inRes && sign1Text;
            bool fromRdp = !inRes;

            // ★ 更新补丁：本体指针存在时，local(G2) 的 C/D 条目索引【本体 res】，不在本 res 内 —— 不解包。
            //   （G5(table)/G6(string) 的 C/D 偏移与本体不同、仍在本 res body 里，故只挡 local 组。）
            //   按需求：res 头部 14 有值时 local 的 C / D 一律不解包。
            bool skipBaseLocal = inRes && g == 2 && hasBase && ctx.Dlc?.SkipBaseLocal == true;
            // ★ 更新补丁：全群组的 4 / 5 标志文件都不解包（它们在本体 package.rdp / data.rdp 上，不属于本次更新）
            bool skipRdp45 = ctx.Dlc?.SkipRdp45 == true && (ot == Pres.TypePackage || ot == Pres.TypeData);
            // ★ EXCLUDE_RDP（Type 3）与 G1 prx 的文件【不在任何 rdp 内】（游戏按本地路径取）——
            //   与 DLC 的 skip 同样只登记元信息：否则前者会去 rdp 找块而失败、后者会落下一个 0 字节空文件。
            bool excludeRdp = ot == Pres.TypeExcludeRdp;
            bool selfDecl = ot == 0 && g == 1 && e.ElementName != null
                            && e.ElementName.Count >= 2 && e.ElementName[1] == "prx";

            // ---- 不解包的条目：只登记元信息，不落盘、不进块表 ----
            //   local(G2) 的 C/D 且本体指针存在 → 数据在本体 res 里；
            //   4 / 5 类型且 DLC 模式 → 数据在本体 package.rdp / data.rdp 里。
            //   两者都必须原样保留 TOC 记录（offset / size / decsize / Type），打包时照原样写回。
            if (excludeRdp || selfDecl || skipBaseLocal || skipRdp45)
            {
                item.Compression = false;
                // ★ DLC：条目级 ExportPath + 【真实的 Compression / Compress】——
                //   这两类条目的数据在本体里（本体 res 内部 或 本体 package.rdp / data.rdp），
                //   本 DLC res 里并没有它们的字节，所以不能靠「读原始数据」判断压缩 ——
                //   必须回到本体 list / 本体块表把状态抄过来，否则会一律误判成「未压缩」。
                //   （实测 god_menu.json：本体侧这些条目多为 blz4 压缩，DLC 侧旧版全写成 false。）
                //   ★ EXCLUDE_RDP / G1 prx 没有任何 rdp / 本体记录可抄，故跳过整段查询。
                if (ctx.BaseIdx != null && !excludeRdp && !selfDecl)
                {
                    // ① Type 4/5：块表权威 —— EP / 压缩态一律以本体块表为准。
                    //   ★ 必须让 EP 与块表逐字一致：同一块可能被多个 res 引用，块表只记
                    //     【首个引用者】的 exportPath；若这里按引用者视角记 EP（如
                    //     Event_0001.res 记 .../Event_0001/Event.gmo，而块表记 .../Event/Event.gmo），
                    //     -cdlc 打包侧按 EP 反查块表就会 miss。
                    if (ot == Pres.TypePackage || ot == Pres.TypeData)
                    {
                        var bi = ctx.BaseIdx.BlockInfo(ot, e.Offset);
                        if (bi.HasValue)
                        {
                            item.ExportPath = bi.Value.ExportPath;
                            item.Compression = bi.Value.Compressed;
                            item.Compress = bi.Value.Compress;
                            nSkipPath++;
                        }
                    }
                    // ② local(G2) 的 C/D：在【本体对应 res 的 list】里按 (组, 类型, 偏移) 查真实压缩态
                    if (item.ExportPath == null)
                    {
                        var ent = ctx.BaseIdx.EntryOf(baseExportPath, g, Pres.TypeStr(ot), e.Offset);
                        if (ent != null)
                        {
                            item.ExportPath = ent.ExportPath;
                            item.Compression = ent.Compression;
                            item.Compress = ent.Compress;
                            nSkipPath++;
                        }
                    }
                    // ③ 兜底：仍按 (类型, 块号) 查一次块表，至少把 exportPath 备注上。
                    if (item.ExportPath == null)
                    {
                        string ep = ctx.BaseIdx.ExportOfBlock(ot, e.Offset);
                        if (ep != null) { item.ExportPath = ep; nSkipPath++; }
                    }
                }
                if (item.ExportPath == null) item.Compression = false;

                // ★ rtbl(组 7)：ElementName[4]（槽位 bit 表）在本 res 的 body 里、名字区之后 ——
                //   与 rdp 数据无关，只依赖 NameOffset + 前几项名字长度，所以 4/5 跳过的条目同样要读。
                //   ElementName（含位串）一律以【本 res（update 包）】为准：更新补丁可能新增文件、
                //   位串会重设，与本体同名 rtbl 的位表可以完全不同（实测 god_menu.res：
                //   julius 系列 DLC 换成 patch.rdp 上 1761 槽的新版，本体同名单条只有 358 槽）。
                //   槽位数按该 rtbl 文件的实际字节数定（本体块表的 rawSize ÷ 0x200）——
                //   rtbl 文件本体仍是本体 rdp 里那份，长度即本体长度；但【哪些槽有效】是本 res 自己的。
                if (g == 7 && Rtbl.IsPath(fname) && e.ElementName != null && e.ElementName.Count < 5)
                {
                    var pe7 = ctx.BaseIdx?.ExportEntryOf(ot, Pres.Untag(e.Offset));
                    int slots7 = pe7 != null && pe7.RawSize > 0 ? (int)(pe7.RawSize / Rtbl.SlotSize) : 0;
                    if (slots7 > 0)
                    {
                        long nl7 = e.ElementName.Count * 8L;   // ★ PS4：指针表每项 8 字节
                        foreach (var s0 in e.ElementName) nl7 += Encoding.UTF8.GetByteCount(s0) + 1;
                        long bitOff = e.NameOffset + nl7;
                        var sb7 = new StringBuilder(slots7 * 2);
                        for (int s = 0; s < slots7; s++)
                        {
                            long q = bitOff + (s >> 3);
                            byte bb = (q >= 0 && q < data.Length) ? data[q] : (byte)0;
                            sb7.Append(((bb >> (s & 7)) & 1) != 0 ? '1' : '0');
                            if (s + 1 < slots7) sb7.Append(',');
                        }
                        e.ElementName.Add(sb7.ToString());         // ★ 第 5 项 = 本 res 的槽位 bit 表
                        item.ElementName = e.ElementName;
                        nRtblSkip++;
                    }
                }

                js.Files.Add(item);
                nSkipped++;
                continue;
            }

            // table(G5) 的 size 记的是 u32 个数 —— 只对「在 res 内」的 table 成立；rdp 侧一律按字节数
            long length = inRes && g == 5 ? e.Size * 4 : e.Size;
            byte[] raw;
            try
            {
                t0 = Prof.Ticks();
                if (inRes) raw = asText ? SliceCStr(data, e.Offset) : Slice(data, e.Offset, length);
                else if (e.Size == 0) raw = Array.Empty<byte>();      // 无数据可读，也不占块
                else
                {
                    var rd = fromPatch ? ctx.PatchRdp : fromData ? ctx.DataRdp : ctx.Rdp;
                    string rdName = fromPatch ? "patch.rdp" : fromData ? "data.rdp" : "package.rdp";
                    if (rd == null)
                        throw new IOException($"需要 {rdName}，但未提供（组 {g} block=0x{e.Offset:X}）");
                    raw = rd.Read(e.Offset << Pres.RdpShift, (int)length);
                    if (raw == null || raw.Length < length)
                        throw new IOException($"{rdName} 越界 block=0x{e.Offset:X}");
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

            // ★ 压缩格式：GE2RB 用 blz4（32B 头 + 多块轮转 + zlib），体验版用 dflt（raw deflate）
            string cfmt = Pres.IsBlz4(raw) ? "blz4" : (Pres.IsDflt(raw) ? "dflt" : null);
            bool packed = cfmt != null;
            item.Compression = packed;
            if (cfmt != null) item.Compress = cfmt;
            byte[] outRaw = raw;
            if (packed)
            {
                nPacked++;
                try { outRaw = cfmt == "blz4" ? Blz4.Decompress(raw) : Undflt(raw); }
                catch { outRaw = raw; item.Compression = false; item.Compress = null; }
            }
            if (g == 6 && outRaw.Length > 0) outRaw = outRaw[..^1];   // string 去掉结尾 0x00

            t0 = Prof.Ticks();
            string rel = DedupIn(langDir, Ws.Sanitize(fname));
            Prof.Add(ref Prof.MsDedup, t0);

            string relLang = langDir.Length == 0 ? rel : langDir + "/" + rel;
            string outPath = Path.Combine(dataDir, Ws.ToOs(relLang));
            if (!ctx.ListOnly)
            {
                if (langDir.Length > 0) Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                t0 = Prof.Ticks();
                File.WriteAllBytes(outPath, outRaw);
                long dW = Prof.Delta(t0);
                Prof.MsWrite += dW;
                Prof.WriteCount++;
                Prof.WriteBytes += outRaw.Length;
                if (dW > Prof.MaxWriteMs) Prof.MaxWriteMs = dW;
            }
            nFiles++;
            nBytes += outRaw.Length;
            item.ExportPath = dataDirRel + "/" + relLang;

            // res 只可能出现在 rdp 侧条目、或 local(G2) 内嵌；table/string/prx文本/refer文本 是纯数值/文本，防字节巧合误判
            bool isRes = Pres.IsPres(outRaw) && (fromRdp || g == 2);
            if (fromRdp && e.Size > 0)
            {
                if (fromPatch)                                    // ★ 0x6 类型 → patch.rdp 的块表
                    ctx.AddPatchBlock(e.Offset, e.Size, packed, outRaw.Length, isRes,
                                      fname, dataDirRel + "/" + relLang, resId, Pres.SlotName[g], e.Index, cfmt);
                else if (fromData)                                // ★ 0x5 类型 → data.rdp 的块表
                    ctx.AddDataBlock(e.Offset, e.Size, packed, outRaw.Length, isRes,
                                     fname, dataDirRel + "/" + relLang, resId, Pres.SlotName[g], e.Index, cfmt);
                else
                    ctx.AddBlock(e.Offset, e.Size, packed, outRaw.Length, isRes,
                                 fname, dataDirRel + "/" + relLang, resId, Pres.SlotName[g], e.Index, cfmt);
            }

            // ★ rtbl(组 7)：条目名字区之后紧跟一段「槽位 bit 表」—— 每槽 1 bit（1 = 该槽有引用），
            //   长度 = ceil(N ÷ 8)，N = rtbl 文件字节数 ÷ 0x200（即槽位数），位序 LSB first
            //   （第 s 槽 = byte[s/8] 的 bit(s%8)）。
            //   解包时把它展开成逗号分隔的位串，存进 ElementName 第 5 项；打包时按它回写（见 Pres.Build）。
            if (fromRdp && Rtbl.IsSlot(g) && Rtbl.IsPath(fname) && outRaw.Length >= Rtbl.SlotSize)
            {
                int slots = outRaw.Length / Rtbl.SlotSize;
                long nl = 0;
                if (e.ElementName != null)
                {
                    nl = e.ElementName.Count * 8L;                 // ★ PS4：指针表每项 8 字节
                    foreach (var s0 in e.ElementName) nl += Encoding.UTF8.GetByteCount(s0) + 1;
                }
                long bitOff = e.NameOffset + nl;
                var sb = new StringBuilder(slots * 2);
                for (int s = 0; s < slots; s++)
                {
                    long q = bitOff + (s >> 3);
                    byte bb = (q >= 0 && q < data.Length) ? data[q] : (byte)0;
                    sb.Append(((bb >> (s & 7)) & 1) != 0 ? '1' : '0');
                    if (s + 1 < slots) sb.Append(',');
                }
                e.ElementName.Add(sb.ToString());                 // ★ 第 5 项 = 槽位 bit 表
                item.ElementName = e.ElementName;
            }
            js.Files.Add(item);

            // ---- rtbl：条目本身是「rdp 引用表」，它引用的文件也要解包并登记进 package.json ----
            if (Rtbl.IsSlot(g) && fromRdp && outRaw.Length >= Rtbl.SlotSize)
            {
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
        if (!ctx.Quiet)
        {
            t0 = Prof.Ticks();
            Console.WriteLine($"{pad}· {resName}  →  {nFiles} 文件/{nBytes} 字节  dummy {nDummy}  压缩 {nPacked}  跳过 {nSkipped}  失败 {nFailed}"
                              + (ctx.BaseIdx != null && nSkipPath > 0 ? $"（已备注本体路径 {nSkipPath}）" : "")
                              + (nRtblSkip > 0 ? $"（rtbl 位串 {nRtblSkip}）" : ""));
            Console.WriteLine($"{pad}  list: {Ws.Rel(ctx.Cwd, listFile)}");
            Prof.Add(ref Prof.MsLog, t0);
        }

        if (Prof.Enabled && Prof.ResCount % 25 == 0)
            Console.WriteLine("      " + Prof.Dump());
    }

    /// <summary>
    /// rtbl（组 7）：每 0x200 一槽，槽里记一条 package.rdp 引用（块号 / size / 名字）。
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
        if (!ctx.ListOnly) Directory.CreateDirectory(outDir);
        // ★ list-only 模式：list 输出到指定目录（4.pack/list），其余同解包
        string listRoot = ctx.ListOutDir ?? Path.Combine(ctx.RootOut, ctx.ListDir);

        var raw = Rtbl.Parse(rtblData);
        int slots = rtblData.Length / Rtbl.SlotSize;
        var js = new RtblList { TableCount = slots, Files = new List<RtblEntry>(raw.Count) };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rec = new List<string>(raw.Count);
        int ok = 0, fail = 0;

        foreach (var e in raw)
        {
            if (e.Size <= 0) { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} size=0"); continue; }

            // ★ 槽位 block 的高 4 位同样是来源类型（正式版）：0x5 → data.rdp，0x6 → 更新补丁 patch.rdp，0x4/0 → package.rdp
            bool fromData = e.Type == Pres.TypeData;
            bool fromPatch = e.Type == Pres.TypePatch;
            // ★ DLC 模式：全群组的 4 / 5 标志文件都不解包 —— rtbl 槽位同理（它们指向本体 rdp）
            if (ctx.Dlc?.SkipRdp45 == true && !fromPatch)
            {
                // ★ DLC：槽位指向的都是本体 rdp 上的 res —— 压缩状态同样要从本体块表抄，
                //   不能因为「本次没读数据」就一律写成未压缩。
                var bi = ctx.BaseIdx?.BlockInfo(e.Type, e.Block);
                js.Files.Add(new RtblEntry
                {
                    Table = e.Slot.ToString(),
                    Type = Pres.TypeStr(e.Type),
                    Offset = DecOf(e.Block),
                    ElementName = new List<string> { e.Name, e.Ext, "" },
                    FileName = "\\" + e.FileName,
                    Compression = bi?.Compressed ?? false,
                    Compress = bi?.Compress,
                    ExportPath = bi?.ExportPath ?? ctx.BaseIdx?.ExportOfBlock(e.Type, e.Block),
                });
                rec.Add($"{e.Slot,4} t{e.Type:X} 0x{e.Block:X8} 0x{(e.Block << Pres.RdpShift):X10} {e.Size,9}  "
                      + $"{e.FileName,-46} -> (DLC 模式跳过：在本体 rdp 上)");
                ok++;
                continue;
            }

            var rd = fromPatch ? ctx.PatchRdp : fromData ? ctx.DataRdp : ctx.Rdp;
            if (rd == null)
            { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} 需要 data.rdp，但未提供"); continue; }

            byte[] seg;
            try { seg = rd.Read(e.Block << Pres.RdpShift, (int)e.Size); }
            catch (Exception ex) { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} 读取失败: {ex.Message}"); continue; }
            string rdName = fromPatch ? "patch.rdp" : fromData ? "data.rdp" : "package.rdp";
            if (seg == null || seg.Length < e.Size)
            { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} {rdName} 越界 block=0x{e.Block:X}"); continue; }

            // ★ 内层 res 同样可能是 blz4（GE2RB）—— rtbl 本身不压缩，但它引用的 res 会
            string ifmt = Pres.IsBlz4(seg) ? "blz4" : (Pres.IsDflt(seg) ? "dflt" : null);
            bool packed = ifmt != null;
            byte[] resData = seg;
            if (packed)
            {
                try { resData = ifmt == "blz4" ? Blz4.Decompress(seg) : Undflt(seg); }
                catch { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} {ifmt} 解压失败"); continue; }
            }
            if (!Pres.IsPres(resData))
            { fail++; Console.WriteLine($"  !! rtbl 槽 {e.Slot} 不是 Pres: {e.FileName}"); continue; }

            string fn = Ws.Dedup(Ws.Sanitize(e.FileName), used);
            string resRel = outDirRel + "/" + fn;
            if (!ctx.ListOnly) File.WriteAllBytes(Path.Combine(outDir, fn), resData);

            // ★ rtbl 内引用的条目也要进块表（否则打包时不会重建、也查不到块位）
            if (fromPatch)
                ctx.AddPatchBlock(e.Block, e.Size, packed, resData.Length, true,
                                  e.FileName, resRel, rtblId, "rtbl", e.Slot, ifmt);
            else if (fromData)
                ctx.AddDataBlock(e.Block, e.Size, packed, resData.Length, true,
                                 e.FileName, resRel, rtblId, "rtbl", e.Slot, ifmt);
            else
                ctx.AddBlock(e.Block, e.Size, packed, resData.Length, true,
                             e.FileName, resRel, rtblId, "rtbl", e.Slot, ifmt);

            js.Files.Add(new RtblEntry
            {
                Table = e.Slot.ToString(),
                Type = Pres.TypeStr(e.Type),                 // ★ 被引用 res 所在 rdp（"4"=package / "5"=data / "6"=patch）
                Offset = DecOf(e.Block),                      // ★ 槽内 +0x00 的低 28 位（块号），打包时按新块位回填
                ElementName = new List<string> { e.Name, e.Ext, "" },
                FileName = "\\" + e.FileName,
                Compression = packed,
                Compress = ifmt,
                ExportPath = resRel,
            });
            rec.Add($"{e.Slot,4} t{e.Type:X} 0x{e.Block:X8} 0x{(e.Block << Pres.RdpShift):X10} {e.Size,9}  "
                  + $"{fn,-46} -> {Path.GetFileNameWithoutExtension(fn)}/");

            // 第 2 级：按 res 包方式解包
            string subDir = Path.Combine(outDir, Path.GetFileNameWithoutExtension(fn));
            string subList = Path.Combine(listRoot, Ws.ToOs(Ws.ListRelOf(resRel)));
            try { UnpackRes(resData, e.FileName, resRel, subDir, subList, ctx, depth + 1); }
            catch (Exception ex) { Console.WriteLine($"  !! rtbl 槽 {e.Slot} 递归解包失败 {fn} : {ex.Message}"); }
            ok++;
        }

        string listPath = Path.Combine(listRoot, Ws.ToOs(Ws.ListRelOf(rtblId)));
        JsonIo.Save(listPath, js);

        if (!ctx.ListOnly)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# rtbl 块区记录  (条目 offset = rdp 块号 ×0x800；t4=package.rdp / t5=data.rdp / t6=patch.rdp)");
            sb.AppendLine($"# 槽位 {slots} 个（有效 {raw.Count}）  rdp 块粒度 0x{Pres.BlockSize:X}  条目数 {ok}  失败 {fail}");
            sb.AppendLine("#  槽位 类型    rdp块号    rdp字节偏移      size  rtbl内文件                                     -> 解包目录");
            foreach (var l in rec) sb.AppendLine(l);
            File.WriteAllText(Path.Combine(outDir, "_rtbl_blocks.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        if (!ctx.Quiet)
        {
            string pad = new string(' ', depth * 2);
            Console.WriteLine($"{pad}  rtbl {rtblFile}  →  {ok}/{raw.Count} 条 res（槽位 {slots}，失败 {fail}）");
            Console.WriteLine($"{pad}    list: {Ws.Rel(ctx.Cwd, listPath)}");
        }
    }

    /// <summary>offset 低 28 位 → list 里的★十进制字符串（如 "119842"）</summary>
    static string DecOf(long off) => off.ToString();

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

    /// <summary>dflt 段（"dflt" + raw deflate）解压 —— 跳过 4 字节魔数后用 DeflateStream 解</summary>
    static byte[] Undflt(byte[] raw)
    {
        using var ms = new MemoryStream(raw, 4, raw.Length - 4);
        using var df = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        df.CopyTo(outMs);
        return outMs.ToArray();
    }
}
