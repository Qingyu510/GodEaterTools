namespace PSPGodEaterTools;

static class Program
{
    const string Version = "1.0";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        string cwd = Directory.GetCurrentDirectory();
        string resName = "system.res";
        string rdpName = "package.rdp";
        // DLC 数据包（-xdlc / -cdlc）的默认文件名 —— 就是 DLC 的「system.res」与「package.rdp」
        string dlcResName = "70f92bc5db957c4862ad4a466bd9e117.edat";
        string dlcRdpName = "78981103ef31ce78eae569d1b58e00c3.edat";
        int maxDepth = 8;
        bool compact = false;
        string cmd = null;

        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-x": case "--unpack": cmd = "x"; continue;
                case "-xdlc": case "--unpack-dlc": cmd = "xdlc"; continue;
                case "-c": case "--pack": cmd = "c"; continue;
                case "-cdlc": case "--pack-dlc": cmd = "cdlc"; continue;
                case "-h": case "--help": case "-?": case "/?": cmd = "h"; continue;
                case "-d": case "--dir": if (++i < args.Length) cwd = args[i]; continue;
                case "-r": case "--res": if (++i < args.Length) resName = args[i]; continue;
                case "--rdp": if (++i < args.Length) rdpName = args[i]; continue;
                case "--depth": if (++i < args.Length) maxDepth = int.Parse(args[i]); continue;
                case "-C": case "--compact": compact = true; continue;
                case "--iotest": return IoTest(i + 1 < args.Length ? args[i + 1] : null);
                default: rest.Add(a); continue;
            }
        }
        if (rest.Count > 0) resName = rest[0];
        if (rest.Count > 1) rdpName = rest[1];
        if (cmd == "xdlc" || cmd == "cdlc")
        {
            // 位置参数优先：第 1 个是 DLC 的 res，第 2 个是 DLC 的 rdp
            if (rest.Count > 0) dlcResName = rest[0];
            if (rest.Count > 1) dlcRdpName = rest[1];
        }

        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd)) { Console.Error.WriteLine($"错误: 目录不存在 {cwd}"); return 1; }

        try
        {
            return cmd switch
            {
                "x" => Unpacker.Run(cwd, resName, rdpName, maxDepth),
                "c" => Packer.Run(cwd, resName, compact),
                "xdlc" => Unpacker.RunDlc(cwd, dlcResName, dlcRdpName, maxDepth),
                "cdlc" => Packer.RunDlc(cwd, dlcResName, dlcRdpName, compact),
                _ => ShowHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    /// <summary>最小 I/O 复现：与解包侧同规格的文件写入，用于隔离「是环境慢还是代码慢」</summary>
    static int IoTest(string dirArg)
    {
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
        Console.WriteLine($@"PSPGodEaterTools v{Version} —— PSP《GOD EATER》PRES(.res) / package.rdp 解包打包工具

适用游戏
  NPJH90069：GOD EATER 体験版（数字版）
  ULJM91019：GOD EATER 体験版（UMD版）
  ULJS00237：噬神者

用法
  PSPGodEaterTools -x                 解包当前目录的 system.res + package.rdp
  PSPGodEaterTools -c                 按 list 打包 → 4.pack/package.rdp + 4.pack/system.res
  PSPGodEaterTools -xdlc              解包 DLC 数据包 → 1.out/（同 -x，见下）
  PSPGodEaterTools -cdlc              打包 DLC 数据包 → 5.dlcpack/（同 -c，见下）
  PSPGodEaterTools -h                 显示本帮助

可选参数
  -d, --dir  <路径>    工作目录（默认当前目录）
  -r, --res  <文件名>  顶层 res 文件名（默认 system.res）
      --rdp  <文件名>  rdp 文件名（默认 package.rdp）
      --depth <n>      递归解包最大层数（默认 8）
      --compact        打包时紧凑重排 rdp（用于在两条目之间插入新文件；默认原位追加）

DLC 数据包（-xdlc / -cdlc）
  · 与 -x / -c 走【完全同一条路径】，差别只有四点：
      ① 默认文件名    system.res  → 70f92bc5db957c4862ad4a466bd9e117.edat
                      package.rdp → 78981103ef31ce78eae569d1b58e00c3.edat
      ② rdp 块大小    0x8000 → 0x10（rdp 编址单位 16 字节）
      ③ 顶层 res 是哈希名，数据目录 / list 固定命名为：
                      1.out/system_update/    （对应本体 -x 的 1.out/system/）
                      1.out/list_update/      （对应本体 -x 的 1.out/list/）
      ④ -cdlc 产物目录 4.pack → 5.dlcpack
  · 输入仍取当前目录，产物仍写 1.out / 2.new / 3.temp；文件名可用位置参数覆盖。

目录约定
  1.out/                    解包产物
     list/system.json       顶层 res 的 list
     list/package.json      rdp 块表（汇总所有层级）
     list/system/xxx.json   递归子 res 的 list
     system/                顶层 res 的数据文件
     system/xxx/            子 res 解包出的数据（xxx 为子 res 名）
                            —— rdp 侧子 res 与 local 区【内嵌】的 res 都会递归解开，
                               list 与之同构：list/system/xxx/yyy.json
  2.new/                    要打进包的东西（同名优先覆盖 1.out）
     list/                  编辑过的 list
     system/                替换的 / 新增的文件
  3.temp/                   打包中间产物（现场重打包的 res、压缩后的文件）
  4.pack/                   最终产物 package.rdp + system.res

DLC 目录约定（-xdlc / -cdlc）
  1.out/system_update/      DLC 顶层 res 的数据文件（对应 -x 的 system/）
  1.out/list_update/        DLC 的 list + 块表 package.json（对应 -x 的 list/）
                            list_update/system_update.json    DLC 顶层 res 的 list
                            list_update/package.json          rdp 块表
                            list_update/system_update/xxx.json 递归子 res 的 list
  2.new/system_update/      DLC 要打进包的东西（同名优先覆盖 1.out）
  2.new/list_update/        DLC 编辑过的 list
  5.dlcpack/                DLC 最终产物（-cdlc）

打包规则
  · 按 package.json 顺序（块位升序 = 依赖序：子 res 先于父 res）逐条处理
  · 数据取 2.new/ 优先，其次 1.out/
  · 本身是 res 的条目——rdp 侧子 res 与 local 内嵌 res 都算——会先用它自己的 list
    现场重打包（产物写 3.temp/），并按它在组内的原位置嵌回父 res；
    所以改内嵌 res 里的文件，父 res 的 size / 内容会自动跟着更新
  · 未超出原块数的条目原位覆盖，超出的从 rdp 末尾顺位追加（未编辑的块原样保留）
  · 各 res 的 TOC 用新的块位/大小回填，ceckSum 自动重算");
        return 0;
    }
}
