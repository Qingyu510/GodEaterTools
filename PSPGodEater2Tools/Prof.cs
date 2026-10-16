using System.Diagnostics;

namespace PSPGodEater2Tools;

/// <summary>诊断计时（设 GETOOLS_PROF=1 启用）</summary>
public static class Prof
{
    public static bool Enabled =
        Environment.GetEnvironmentVariable("GETOOLS_PROF") == "1";

    public static long MsToc, MsRead, MsWrite, MsJson, MsDedup, MsLog;
    public static int ResCount, FileCount;
    public static long ReadCount, ReadBytes, WriteCount, WriteBytes, MaxWriteMs, MaxReadMs;
    public static long TotalMs;

    static readonly Stopwatch Sw = Stopwatch.StartNew();

    public static long Ticks() => Enabled ? Sw.ElapsedTicks : 0;

    public static long Delta(long t0)
        => (!Enabled || t0 == 0) ? 0 : (Sw.ElapsedTicks - t0) * 1000 / Stopwatch.Frequency;

    public static void Add(ref long slot, long t0) => slot += Delta(t0);

    public static string Dump() =>
        $"[prof] res={ResCount} | toc={MsToc}ms | rdp读={MsRead}ms({ReadCount}次/{ReadBytes / 1024}KB,max{MaxReadMs}) "
        + $"| 写文件={MsWrite}ms({WriteCount}次/{WriteBytes / 1024}KB,均{(WriteCount == 0 ? 0 : MsWrite / WriteCount)}ms,max{MaxWriteMs}) "
        + $"| 写json={MsJson}ms | dedup={MsDedup}ms | 日志={MsLog}ms | 墙钟={TotalMs}ms "
        + $"| GC={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} 分配={GC.GetTotalAllocatedBytes(false) / 1048576}MB";

    /// <summary>进程内纯写基准：同样大小的文件、同样用 File.WriteAllBytes，排除解包逻辑</summary>
    public static void QuickWriteBench(string root)
    {
        string d = Path.Combine(root, "_bench");
        Directory.CreateDirectory(d);
        var buf = new byte[58000];
        new Random(1).NextBytes(buf);
        File.WriteAllBytes(Path.Combine(d, "warm.bin"), buf);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(d, $"b{i}.bin"), buf);
        sw.Stop();
        double mb = 300 * 58000 / 1048576.0;
        Console.WriteLine($"  [bench] 进程内纯写 300×58KB ({mb:F1}MB): {sw.Elapsed.TotalSeconds:F3}s "
                          + $"({mb / sw.Elapsed.TotalSeconds:F1} MB/s, {sw.Elapsed.TotalMilliseconds / 300:F2} ms/file)");
    }
}
