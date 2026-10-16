namespace PSVGodEaterUSTools;

static class Program
{
    const string Version = "1.0";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        string cwd = Directory.GetCurrentDirectory();
        string resName = "system.res";
        string rdpName = "package.rdp";
        string dataRdpName = "data.rdp";
        // 更新补丁默认文件名（★ PSV 版是裸 res + rdp，不是 PSP 的 EDAT）
        string patchResName = "system_update.res";
        string patchName = "patch.rdp";
        int maxDepth = 8;
        string cmd = null;
        int cdlcX = 2;              // ★ -cdlc x：基准 list 来源（1=1.out/list 原版，2=4.pack/list 重打包后）
        // ★ 压缩条目来源：默认 false = 全部从 2.new/1.out 明文直接压缩（原生 zlib 可字节级复现游戏压缩）；
        //   -c o / -cdlc o [1|2] 显式开启 = 未编辑条目复用原 rdp 原始压缩段（旧行为，双保险）。
        bool reuseOrig = false;

        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-x": case "--unpack": cmd = "x"; continue;
                case "-xdlc": case "--unpack-dlc": cmd = "xdlc"; continue;
                case "-c": case "--pack":
                    cmd = "c";
                    // ★ o 紧跟其后（-c o）= 启用「复用原始压缩段」；其余参数留给位置参数
                    if (i + 1 < args.Length && (args[i + 1] == "o" || args[i + 1] == "--orig")) { reuseOrig = true; i++; }
                    continue;
                case "-cdlc": case "--pack-dlc":
                    cmd = "cdlc";
                    // ★ 后续可跟 o（复用原始压缩段）与 1|2（基准 list），顺序不限：
                    //   -cdlc o 1 / -cdlc o 2 / -cdlc 1 / -cdlc 2 / -cdlc 1 o
                    while (i + 1 < args.Length)
                    {
                        string n = args[i + 1];
                        if (n == "o" || n == "--orig") { reuseOrig = true; i++; }
                        else if (n == "1" || n == "2") { cdlcX = n == "2" ? 2 : 1; i++; }
                        else break;
                    }
                    continue;
                case "-h": case "--help": case "-?": case "/?": cmd = "h"; continue;
                case "-d": case "--dir": if (++i < args.Length) cwd = args[i]; continue;
                case "-r": case "--res": if (++i < args.Length) resName = args[i]; continue;
                case "--rdp": if (++i < args.Length) rdpName = args[i]; continue;
                case "--datardp": if (++i < args.Length) dataRdpName = args[i]; continue;
                case "--patchrdp": if (++i < args.Length) patchName = args[i]; continue;
                case "--depth": if (++i < args.Length) maxDepth = int.Parse(args[i]); continue;
                case "--iotest": return IoTest(i + 1 < args.Length ? args[i + 1] : null);
                case "-b": case "--blz4": return Blz4Test(i + 1 < args.Length ? args[i + 1] : null);
                default: rest.Add(a); continue;
            }
        }
        if (rest.Count > 0) resName = rest[0];
        if (rest.Count > 1) rdpName = rest[1];
        if (rest.Count > 2) dataRdpName = rest[2];
        if (cmd == "xdlc" || cmd == "cdlc")
        {
            // 位置参数优先：第 1 个是补丁 res（system_update.res），第 2 个是 patch.rdp
            if (rest.Count > 0) patchResName = rest[0];
            if (rest.Count > 1) patchName = rest[1];
        }

        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd)) { Console.Error.WriteLine($"错误: 目录不存在 {cwd}"); return 1; }

        try
        {
            return cmd switch
            {
                "x" => Unpacker.Run(cwd, resName, rdpName, dataRdpName, maxDepth),
                "c" => Packer.Run(cwd, resName, dataRdpName, reuseOrig),
                "xdlc" => Unpacker.RunDlc(cwd, patchResName, patchName, maxDepth),
                "cdlc" => Packer.RunDlc(cwd, patchResName, patchName, cdlcX, reuseOrig),
                _ => ShowHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    /// <summary>blz4 自检：解压 → 重新压缩 → 与原始段逐字节比对。
    /// 验证 .NET 的 ZLibStream(Optimal) 能否 bit-identical 复现游戏原生压缩流。</summary>
    static int Blz4Test(string file)
    {
        if (file == null || !File.Exists(file))
        {
            Console.Error.WriteLine("用法: PSVGodEaterUSTools -b <blz4 文件>");
            return 1;
        }
        byte[] seg = File.ReadAllBytes(file);
        Console.WriteLine($"文件 : {file}");
        Console.WriteLine($"大小 : {seg.Length:N0} 字节");
        Console.WriteLine($"魔数 : {Convert.ToHexString(seg.AsSpan(0, 4))} " +
                          (Blz4.IsBlz4(seg) ? "= blz4 OK" : "≠ blz4 ✗"));
        if (!Blz4.IsBlz4(seg)) return 1;

        // 读块结构（★ blz4 头 32 字节，之后才是 {u16 块长, zlib 块} 序列）
        var chunks = new List<int>();
        for (int off = Blz4.HeaderSize; off < seg.Length;)
        {
            int n = seg[off] | (seg[off + 1] << 8);
            off += 2;
            if (off + n > seg.Length) { Console.Error.WriteLine($"块越界 off={off} n={n}"); return 1; }
            chunks.Add(n);
            off += n;
        }
        Console.WriteLine($"块数 : {chunks.Count}   块长 = [{string.Join(", ", chunks)}]");

        byte[] plain;
        try { plain = Blz4.Decompress(seg); }
        catch (Exception ex) { Console.Error.WriteLine($"解压失败: {ex.Message}"); return 1; }
        Console.WriteLine($"解压 : {plain.Length:N0} 字节  OK");

        byte[] re;
        try { re = Blz4.Compress(plain); }
        catch (Exception ex) { Console.Error.WriteLine($"压缩失败: {ex.Message}"); return 1; }
        Console.WriteLine($"重压 : {re.Length:N0} 字节");

        bool same = re.AsSpan().SequenceEqual(seg);
        Console.WriteLine(same
            ? "★ bit-identical ✓  .NET ZLibStream(SmallestSize=level9) 可复现游戏原生 blz4 段"
            : $"✗ 不一致（{re.Length:N0} vs {seg.Length:N0} 字节）");
        return same ? 0 : 2;
    }

    /// <summary>最小 I/O 复现：与解包侧同规格的文件写入，用于隔离「是环境慢还是代码慢」</summary>
    static int IoTest(string dirArg)    {
        string d = dirArg != null
            ? Path.GetFullPath(dirArg)
            : Path.Combine(Path.GetTempPath(), "getools_iotest");
        Directory.CreateDirectory(d);
        var rnd = new Random(1);
        var bufs = new List<byte[]>(2500);
        long total = 0;
        for (int i = 0; i < 2500; i++)
        {
            int n = rnd.Next(1000, 200000);
            var b = new byte[n];
            rnd.NextBytes(b);
            bufs.Add(b);
            total += n;
        }
        double mb = total / 1048576.0;
        Console.WriteLine($"目标目录: {d}\n预热 {bufs[0].Length} 字节 …");

        File.WriteAllBytes(Path.Combine(d, "warm.bin"), bufs[0]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < bufs.Count; i++) File.WriteAllBytes(Path.Combine(d, $"f{i}.bin"), bufs[i]);
        sw.Stop();
        Report("File.WriteAllBytes", bufs.Count, mb, sw.Elapsed.TotalSeconds);

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < bufs.Count; i++)
        {
            using var fs = new FileStream(Path.Combine(d, $"g{i}.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            fs.Write(bufs[i], 0, bufs[i].Length);
        }
        sw2.Stop();
        Report("FileStream(64K buf)", bufs.Count, mb, sw2.Elapsed.TotalSeconds);

        // 模式 B：2500 个子目录各 1 文件（模拟解包真实写入模式）
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < bufs.Count; i++)
        {
            string sd = Path.Combine(d, "sub", $"d{i}");
            Directory.CreateDirectory(sd);
            File.WriteAllBytes(Path.Combine(sd, "a.bin"), bufs[i]);
        }
        sw3.Stop();
        Report("子目录结构(2500目录)", bufs.Count, mb, sw3.Elapsed.TotalSeconds);

        // 模式 C：走一遍解包同款路径拼接
        var sw4 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < bufs.Count; i++)
        {
            string full = Path.Combine(d, "sub", $"d{i}", "a.bin");
            File.WriteAllBytes(full, bufs[i]);
        }
        sw4.Stop();
        Report("覆盖写(已存在2615文件)", bufs.Count, mb, sw4.Elapsed.TotalSeconds);

        return 0;
    }

    static void Report(string tag, int n, double mb, double sec)
        => Console.WriteLine($"{tag,-22} {n} 文件 / {mb:F1} MB -> {sec:F2}s  ({mb / sec:F2} MB/s, {sec * 1000 / n:F2} ms/file)");

    static int ShowHelp()
    {
        Console.WriteLine($@"PSVGodEaterUSTools v{Version} —— PSV《GOD EATER US》PRES(.res) / package.rdp + data.rdp 解包打包工具

适用游戏
  PCSB00875：GOD EATER 2 RAGR BURST
  PCSE00789：GOD EATER 2 RAGR BURST
  PCSB00874：GOD EATER RESURRECTION
  PCSE00801：GOD EATER RESURRECTION

用法
  PSVGodEaterUSTools -x             解包当前目录的 system.res + package.rdp + data.rdp
  PSVGodEaterUSTools -c [o]         按 list 打包（顺移）→ 4.pack/package.rdp + data.rdp + system.res
                                   o = 启用「复用原始压缩段」：未编辑条目沿用原 rdp 压缩字节（旧行为）；
                                       不加 o（默认）= 全部从 2.new/1.out 明文直接压缩
                                       （原生 zlib L9/M8，与游戏压缩字节级一致）
  PSVGodEaterUSTools -xdlc          解包更新补丁：system_update.res + patch.rdp
  PSVGodEaterUSTools -cdlc [o] [x]  打包更新补丁 → 5.dlcpack/patch.rdp + system_update.res
                                   o = 复用原始压缩段（同 -c o）；x=1/2 选基准 list（默认 2），顺序不限（-cdlc o 1 / -cdlc 1 o）
  PSVGodEaterUSTools -b <blz4文件>  blz4 自检：解压 → 重新压缩 → 与原始段逐字节比对
  PSVGodEaterUSTools -h             显示本帮助

可选参数
  -d, --dir     <路径>    工作目录（默认当前目录）
  -r, --res     <文件名>  顶层 res 文件名（默认system.res）
      --rdp     <文件名>  rdp 文件名（默认 package.rdp）
      --datardp <文件名>  第二个 rdp 文件名（默认 data.rdp）
      --patchrdp <文件名> 更新补丁 rdp 文件名（默认 patch.rdp）
      --depth   <n>       递归解包最大层数（默认 8）

压缩格式（按作品代际不同，解包自动识别、list 的 Compress 字段记录）
  gzip  GE / GEB
  dflt  GE2 体验版   —— ""dflt"" + raw deflate
  blz4  GE2RB   —— ""blz4"" + 32B 头(魔数+解压大小+保留+MD5) + 重复(u16 块长, zlib 块)；首块为余数、写入序 [末块,首块,中...]
  压缩条目默认全部从 2.new/1.out 明文直接压缩（原生 zlib，与游戏字节级一致）；
  加 o（-c o / -cdlc o）可改回「未编辑条目复用原始压缩字节」的旧行为

数据来源（TOC offset 的高 4 位）
  0xC  数据在本 res 文件内（低 28 位 = 文件内绝对偏移）
       —— 宿主是 system.res 本身 / data.rdp 里的 res；local(组 2) 排在 res 包之后的文件尾，
          table(组 5)/string(组 6) 在 body 里（tbl / string 必定是 C）
  0xD  与 0xC 语义相同（数据同样在本 res 文件内），仅宿主 res 本身躺在 package.rdp 里时用它
  0x4  数据在 package.rdp（低 28 位 = 块号）
  0x5  数据在 data.rdp（低 28 位 = 块号）
       —— 为让过大的包适配 UMD，游戏把 data.rdp 单独分包、可放进记忆卡以加快加载
  0x6  数据在【更新补丁 patch.rdp】上（低 28 位 = 块号）—— 仅更新补丁的 res 会出现
  TOC +0x1C = 解密（解压）后大小，原样记进 list 的 decsize，打包时回填
  TOC offset 的高 4 位记进 list 的 Type、低 28 位记进 list 的 Offset（★十进制，如 ""119842""），打包时一并回填

更新补丁（-xdlc）
  游戏启动时先读【system_update.res】拿到更新后的 res 索引，
  新增/替换的文件数据放在【patch.rdp】（offset 高 4 位 = 6）里。
  ★ 前置限制：本体目录的 1.out/list 下必须已有 system.json 与 package.json（先跑过一次 -x）——
    补丁里跳过的条目要靠本体 list 才能备注出路径。
  · 补丁 res 头部 +0x14 / +0x18 是【本体 res 指针】，记进 list 的
    BaseType（= offset 高 4 位）/ BaseOffset（低 28 位）/ BaseSize（+0x18 字节数，★十进制）
  · 另按 (BaseType, BaseOffset) 到本体 package.json / data.json 匹配出 BaseExportPath
    （如 ""system/god_menu.res""）—— 打包 DLC 时据此定位本体 res 的 list
  · 有本体指针时，该 res 的 local(组 2) 的 C/D 条目索引【本体 res】（偏移与本体res 完全一致），
    不在补丁 res 内 ⇒ 按需求【不解包】，但会按 Type+Offset 备注出本体里的 ExportPath；
    而 table(组 5)/string(组 6) 的 C/D 仍在自己 body 里，正常解包
  · -xdlc 时全群组的 4 / 5 标志文件【不解包】（数据在本体 package.rdp / data.rdp 上，不属于本次更新），
    同样按 (Type, 块号) 备注出本体的 ExportPath
  · 目录：1.out/SYSTEM_UPDATE/ 数据、1.out/list_update/ 各 res 的 list 与 patch.json 块表

打包更新补丁（-cdlc x）
  · 逆 -xdlc：重打包 patch.rdp + 重建 system_update.res → 5.dlcpack/
  · ★ 基准 list（x 决定 4/5/C/D 与 +0x14/+0x18 的真值来源，默认 2）：
      x=1 → 1.out/list（本体未重打包，块位与偏移即原版）
      x=2 → 4.pack/list（本体已 -c 重打包，块位 / res 内偏移都会变 —— 本体改过就必须用 2）
  · 带本体指针（BaseExportPath 非空）的 res：头部 +0x14（类型+本体块号）/ +0x18（本体字节数）
    按基准 list 重算；其 4 / 5（本体 rdp）与 local 的 C / D（本体 res 内部）条目的
    偏移 / 大小 / 解压大小也按基准 list 修正，且这些字节【不写进 DLC res】（数据本来就在本体）
  · 数据优先 2.new/SYSTEM_UPDATE/，其次 1.out/SYSTEM_UPDATE/；list 优先 2.new/list_update/
  · 打完后对 5.dlcpack 再跑一遍 list-only → 5.dlcpack/list（记录 DLC 的新偏移）

新 list（打包 DLC 用）
  · -c 打包完成后会自动对【刚打出的 4.pack 产物】再跑一遍 list-only 解包（只遍历、只写 list，
    不落数据），输出到 4.pack/list：各 res 的 list + package.json + data.json。
    里面记录的是【打包后】的新偏移值、大小、解压大小 —— 本体被改过之后，原本的块位与 res 内
    C/D 偏移都会变动，打包 DLC 时带 BaseType 的 res 就靠它回填 +0x14 / +0x18 与 4/5/C/D 条目的位置。
  · 4.pack/list 里各 res 的 exportPath 与 1.out/list 完全一致，所以新旧 list 可按 exportPath 逐条对应。

目录约定
  1.out/                    解包产物
     list/system.json       顶层 res 的 list
     list/package.json      package.rdp 块表（汇总所有层级）
     list/data.json         data.rdp 块表（汇总所有层级）
     list/system/xxx.json   递归子 res 的 list
     list_update/           ★ -xdlc 的各 res list + patch.json 块表
     SYSTEM_UPDATE/         ★ -xdlc 的补丁数据
     system/                顶层 res 的数据文件
     system/xxx/            子 res 解包出的数据（xxx 为子 res 名）
                            —— rdp 侧子 res 与 local 区【内嵌】的 res 都会递归解开，
                               list 与之同构：list/system/xxx/yyy.json
     system/<rtbl>/         rtbl（组 7）引用的文件：<条目名>.res + <条目名>/（该 res 的解包产物）
                            —— rtbl 每条都索引 package.rdp，两级展开后同样递归；
                               固定槽位清单：list/system/<rtbl>.json（table 即槽位号）
  2.new/                    要打进包的东西（同名优先覆盖 1.out）
     list/                  编辑过的 list
     list_update/           编辑过的更新补丁 list
     system/                替换的 / 新增的文件
     SYSTEM_UPDATE/         替换的 / 新增的更新补丁文件
  3.temp/                   打包中间产物（现场重打包的 res、压缩后的文件）
  4.pack/                   最终产物 package.rdp + data.rdp + system.res
     list/                   ★ 打包后自动生成的新 list（记录新的偏移 / 大小 / 解压大小，供打包 DLC 用）
  5.dlcpack/                ★ -cdlc 产物：patch.rdp + system_update.res
     list/                   ★ DLC 打包后的新 list

打包规则
  · 按 list 顺序（块位升序 = 依赖序：子 res 先于父 res）逐条处理；package.rdp 与 data.rdp 各自独立顺移
  · 数据取 2.new/ 优先，其次 1.out/
  · 本身是 res 的条目——rdp 侧子 res 与 local 内嵌 res 都算——会先用它自己的 list
    现场重打包（产物写 3.temp/），并按它在组内的原位置嵌回父 res；
    所以改内嵌 res 里的文件，父 res 的 size / 内容会自动跟着更新
  · 顺移：每条占 max(新块数, 原块数)。变大的条目在本位就地扩展、其后条目整体后移，
    条目间空隙与尾部原样搬运（替换成更大的文件不会被甩到 rdp 末尾；变小不回收空间）
  · 各 res 的 TOC 用新的块位与【真实写入长度】回填，offset 高 4 位原样写回 list 里的 Type
    （C / D / 4 / 5 / 0 一律以 list 为准，不看组、也不靠 rdp 表命中来猜），+0x1C 回填 decsize
  · rtbl 按固定槽位 JSON 重建：槽位 = rtbl 内偏移 ÷ 0x200，空槽补 0；
    增删文件即改槽位（新增占用空槽 / 删除把该槽从 Files 去掉），并同步 package.json
  · rtbl 条目在 res 里：名字区（ElementName 前 4 项）之后紧跟一段「槽位 bit 表」——
    每槽 1 bit（1 = 该槽有引用），长度 = ceil(槽位数 ÷ 8)、位序 LSB first，整块补齐到 al16(名字区+bit表+1)；
    解包时展开成逗号分隔的位串存进 ElementName 第 5 项（如 ""1,0,1,…""），打包时按它回写；
    rtbl 变大（槽位增多）时，位串没写到的槽一律按 0 补");
        return 0;
    }
}
