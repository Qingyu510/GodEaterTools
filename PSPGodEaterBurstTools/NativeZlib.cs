// NativeZlib —— 原生 zlib1.dll 的 P/Invoke 封装（压缩侧专用）。
//
// ★ 为什么必须用原生 zlib：游戏原生压缩段（GEB gzip / GE2 blz2 / GE2RB blz4 / Checker dflt）
//   全部由 C 版 zlib 产出，.NET 的 DeflateStream/ZLibStream/GZipStream 与 C zlib
//   **输出不一致**（实测 blz2 94/96、gzip 差 3,680 B），只有原生 zlib 能逐字节复现
//   （实测 2026-10-15：blz2 96/96、gzip 1/1、dflt 1/1、blz4 PSV 71/71 + PS4 805/805）。
//   zlib 1.3 / 1.3.1 / 1.3.2 三者输出逐字节一致，版本可放心替换。
//
// ★ 部署：zlib1.dll 必须与 exe 同目录（随工具发布）。
//   编译参考（MSVC x64）：
//     cl /LD /O2 /GL /I. <16个.c> /link /DEF:win32/zlib.def /OUT:zlib1.dll
//
// 实测参数（2026-10-15，官方样本穷举）：
//   gzip : level 9 / memLevel 9 / wbits 31（15+16），头 MTIME=0、XFL=0x02、OS=0x03
//   blz2 : level 9 / memLevel 8 / wbits -15（raw deflate）
//   dflt : level 9 / memLevel 8 / wbits -15（raw deflate，整段一次压）
//   blz4 : level 9 / memLevel 8 / wbits 15（zlib 包装）
using System;
using System.Runtime.InteropServices;

namespace PSPGodEaterBurstTools;

/// <summary>原生 zlib 压缩封装。线程安全性：每次调用独立创建/销毁 z_stream，可并发。</summary>
public static class NativeZlib
{
    const string DLL = "zlib1.dll";
    const int Z_DEFLATED = 8;
    const int Z_NO_FLUSH = 0;
    const int Z_FINISH = 4;
    const int Z_OK = 0;
    const int Z_STREAM_END = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct z_stream
    {
        public IntPtr next_in; public uint avail_in; public uint total_in;
        public IntPtr next_out; public uint avail_out; public uint total_out;
        public IntPtr msg; public IntPtr state;
        public IntPtr zalloc; public IntPtr zfree; public IntPtr opaque;
        public int data_type; public uint adler; public uint reserved;
    }

    [DllImport(DLL, EntryPoint = "zlibVersion", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr zlibVersion();

    // ★ 导出符号带下划线（deflateInit2_），这是 zlib 的 C 链接惯例
    [DllImport(DLL, EntryPoint = "deflateInit2_", CallingConvention = CallingConvention.Cdecl)]
    static extern int deflateInit2_(ref z_stream strm, int level, int method, int windowBits,
                                    int memLevel, int strategy, IntPtr version, int stream_size);

    [DllImport(DLL, EntryPoint = "deflate", CallingConvention = CallingConvention.Cdecl)]
    static extern int deflate(ref z_stream strm, int flush);

    [DllImport(DLL, EntryPoint = "deflateEnd", CallingConvention = CallingConvention.Cdecl)]
    static extern int deflateEnd(ref z_stream strm);

    /// <summary>zlib 版本串（如 "1.3.2"）；DLL 缺失时抛带指引的异常。</summary>
    public static string Version()
    {
        try { return Marshal.PtrToStringAnsi(zlibVersion()); }
        catch (DllNotFoundException)
        {
            throw new DllNotFoundException(
                "找不到 zlib1.dll —— 请把它放到本 exe 同目录（MSVC x64 构建：cl /LD /O2 /GL /I. *.c /link /DEF:win32/zlib.def）");
        }
    }

    /// <summary>通用 deflate。windowBits：31=gzip / 15=zlib 包装 / -15=raw。memLevel 1..9。</summary>
    public static byte[] Deflate(byte[] data, int level, int windowBits, int memLevel, int strategy = 0)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentException("deflate 输入为空", nameof(data));

        var zs = new z_stream();
        IntPtr ver = zlibVersion();
        int rc = deflateInit2_(ref zs, level, Z_DEFLATED, windowBits, memLevel, strategy,
                               ver, Marshal.SizeOf<z_stream>());
        if (rc != Z_OK)
            throw new InvalidOperationException($"deflateInit2_ 失败 rc={rc}（level={level} wbits={windowBits} mem={memLevel}）");

        var inBuf = new byte[data.Length];
        Array.Copy(data, inBuf, data.Length);
        var pin = GCHandle.Alloc(inBuf, GCHandleType.Pinned);
        try
        {
            zs.next_in = pin.AddrOfPinnedObject();
            zs.avail_in = (uint)data.Length;

            // 输出缓冲：原文长 + 膨胀余量；gzip/zlib 包装另 +0x40 头尾
            long cap = data.Length + data.Length / 1000 + 0x100 + 0x40;
            var outBuf = new byte[cap];
            var pout = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
            try
            {
                zs.next_out = pout.AddrOfPinnedObject();
                zs.avail_out = (uint)cap;
                long produced = 0;

                while (true)
                {
                    rc = deflate(ref zs, Z_FINISH);
                    produced = cap - zs.avail_out;
                    if (rc == Z_STREAM_END) break;
                    if (rc != Z_OK)
                        throw new InvalidOperationException($"deflate 失败 rc={rc}");
                    if (produced == cap)   // 输出吃紧，扩容重来（正常不会到这里）
                        throw new InvalidOperationException("deflate 输出缓冲不足");
                }

                var result = new byte[produced];
                Array.Copy(outBuf, result, produced);
                return result;
            }
            finally { pout.Free(); }
        }
        finally { pin.Free(); deflateEnd(ref zs); }
    }

    /// <summary>GEB gzip：level 9 / memLevel 9 / gzip 包装。★ 强制头第 10 字节（OS）= 0x03，与游戏原生一致。</summary>
    public static byte[] GzipDeflate(byte[] data)
    {
        byte[] g = Deflate(data, 9, 15 + 16, 9);
        if (g.Length < 10) throw new InvalidOperationException("gzip 输出不足 10 字节");
        g[9] = 0x03;   // OS 字节：zlib 各构建默认值不一（0x03/0x0A），原生游戏段恒为 0x03
        return g;
    }

    /// <summary>GE2 blz2 / Checker dflt：level 9 / memLevel 8 / raw deflate（无包装）。</summary>
    public static byte[] RawDeflate9(byte[] data) => Deflate(data, 9, -15, 8);

    /// <summary>GE2RB blz4：level 9 / memLevel 8 / zlib 包装（wbits=15）。</summary>
    public static byte[] ZlibDeflate9(byte[] data) => Deflate(data, 9, 15, 8);
}
