namespace PSPGodEater2TrialTools;

static class Program
{
    const string Version = "1.0";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        string cwd = Directory.GetCurrentDirectory();
        string resName = "system.res";
        string rdpName = "package.rdp";
        int maxDepth = 8;
        string cmd = null;

        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-x": case "--unpack": cmd = "x"; continue;
                case "-c": case "--pack": cmd = "c"; continue;
                case "-h": case "--help": case "-?": case "/?": cmd = "h"; continue;
                case "-d": case "--dir": if (++i < args.Length) cwd = args[i]; continue;
                case "-r": case "--res": if (++i < args.Length) resName = args[i]; continue;
                case "--rdp": if (++i < args.Length) rdpName = args[i]; continue;
                case "--depth": if (++i < args.Length) maxDepth = int.Parse(args[i]); continue;
                case "--iotest": return IoTest(i + 1 < args.Length ? args[i + 1] : null);
                default: rest.Add(a); continue;
            }
        }
        if (rest.Count > 0) resName = rest[0];
        if (rest.Count > 1) rdpName = rest[1];

        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd)) { Console.Error.WriteLine($"错误: 目录不存在 {cwd}"); return 1; }

        try
        {
            return cmd switch
            {
                "x" => Unpacker.Run(cwd, resName, rdpName, maxDepth),
                "c" => Packer.Run(cwd, resName),
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
        Console.WriteLine($@"PSPGodEater2TrialTools v{Version} —— PSP《GOD EATER 2 TRIAL》PRES(.res) / package.rdp 解包打包工具

适用游戏
  NPJH90338：GOD EATER 2 体験版

用法
  PSPGodEater2TrialTools -x        解包当前目录的 system.res + package.rdp
  PSPGodEater2TrialTools -c        按 list 打包（顺移）→ 4.pack/package.rdp + 4.pack/system.res
  PSPGodEater2TrialTools -h        显示本帮助

可选参数
  -d, --dir  <路径>    工作目录（默认当前目录）
  -r, --res  <文件名>  顶层 res 文件名（默认 system.res）
      --rdp  <文件名>  rdp 文件名（默认 package.rdp）
      --depth <n>      递归解包最大层数（默认 8）

目录约定
  1.out/                    解包产物
     list/system.json       顶层 res 的 list
     list/package.json      rdp 块表（汇总所有层级）
     list/system/xxx.json   递归子 res 的 list
     system/                顶层 res 的数据文件
     system/xxx/            子 res 解包出的数据（xxx 为子 res 名）
                            —— rdp 侧子 res 与 local 区【内嵌】的 res 都会递归解开，
                               list 与之同构：list/system/xxx/yyy.json
     system/<rtbl>/         rtbl（组 7）引用的文件：<条目名>.res + <条目名>/（该 res 的解包产物）
                            —— rtbl 每条都索引 package.rdp，两级展开后同样递归；
                               固定槽位清单：list/system/<rtbl>.json（table 即槽位号）
  2.new/                    要打进包的东西（同名优先覆盖 1.out）
     list/                  编辑过的 list
     system/                替换的 / 新增的文件
  3.temp/                   打包中间产物（现场重打包的 res、压缩后的文件）
  4.pack/                   最终产物 package.rdp + system.res

打包规则
  · 按 package.json 顺序（块位升序 = 依赖序：子 res 先于父 res）逐条处理
  · 数据取 2.new/ 优先，其次 1.out/
  · 本身是 res 的条目——rdp 侧子 res 与 local 内嵌 res 都算——会先用它自己的 list
    现场重打包（产物写 3.temp/），并按它在组内的原位置嵌回父 res；
    所以改内嵌 res 里的文件，父 res 的 size / 内容会自动跟着更新
  · 顺移：每条占 max(新块数, 原块数)。变大的条目在本位就地扩展、其后条目整体后移，
    条目间空隙与尾部原样搬运（替换成更大的文件不会被甩到 rdp 末尾；变小不回收空间）
  · 各 res 的 TOC 用新的块位与【真实写入长度】回填（CeckSum 已废弃，0x0C 只留 Sign）
  · rtbl 按固定槽位 JSON 重建：槽位 = rtbl 内偏移 ÷ 0x80，空槽补 0；
    增删文件即改槽位（新增占用空槽 / 删除把该槽从 Files 去掉），并同步 package.json");
        return 0;
    }
}
