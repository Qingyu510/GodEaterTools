namespace PSPGodEaterTools;

/// <summary>工作目录约定 + 路径解析（2.new 覆盖 1.out）</summary>
public static class Ws
{
    public const string OutDir = "1.out";
    public const string NewDir = "2.new";
    public const string TempDir = "3.temp";
    public const string PackDir = "4.pack";
    /// <summary>DLC 数据包（-cdlc）的产物目录</summary>
    public const string DlcPackDir = "5.dlcpack";

    public static string ToOs(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
    public static string ToRel(string p) => p.Replace('\\', '/');

    /// <summary>数据文件：2.new/&lt;rel&gt; 优先，其次 1.out/&lt;rel&gt;</summary>
    public static string ResolveData(string cwd, string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        string a = Path.Combine(cwd, NewDir, ToOs(rel));
        if (File.Exists(a)) return a;
        string b = Path.Combine(cwd, OutDir, ToOs(rel));
        return File.Exists(b) ? b : null;
    }

    /// <summary>list 文件：2.new/&lt;listDir&gt;/&lt;rel&gt; 优先，其次 1.out/&lt;listDir&gt;/&lt;rel&gt;
    ///   listDir 默认 "list"；DLC 数据包用 "list_update"。</summary>
    public static string ResolveList(string cwd, string rel, string listDirName = "list")
    {
        string a = Path.Combine(cwd, NewDir, listDirName, ToOs(rel));
        if (File.Exists(a)) return a;
        string b = Path.Combine(cwd, OutDir, listDirName, ToOs(rel));
        return File.Exists(b) ? b : null;
    }

    /// <summary>从 res 的 exportPath（相对 1.out）推出它的 list 相对路径
    /// system/god_menu.res → system/god_menu.json ; system.res → system.json</summary>
    public static string ListRelOf(string resExportPath)
    {
        string p = ToRel(resExportPath);
        int i = p.LastIndexOf('/');
        string dir = i < 0 ? "" : p[..i];
        string stem = Path.GetFileNameWithoutExtension(p);
        return dir.Length == 0 ? stem + ".json" : dir + "/" + stem + ".json";
    }

    /// <summary>相对路径（用 / 分隔），失败返回文件名</summary>
    public static string Rel(string baseDir, string full)
    {
        try
        {
            string r = Path.GetRelativePath(baseDir, full);
            return ToRel(r);
        }
        catch { return ToRel(full); }
    }

    public static string Sanitize(string name)
    {
        foreach (char c in "<>:\"|?*") name = name.Replace(c, '_');
        return name.Length == 0 ? "_" : name;
    }

    /// <summary>重名去重：name → name_0001</summary>
    public static string Dedup(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        string b = Path.GetFileNameWithoutExtension(name);
        string x = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            string c = $"{b}_{i:D4}{x}";
            if (used.Add(c)) return c;
        }
    }
}

/// <summary>package.rdp 的随机读取器（不整份载入内存）</summary>
public sealed class RdpReader : IDisposable
{
    readonly FileStream _fs;
    public long Length => _fs.Length;
    public RdpReader(string path) { _fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); }

    public byte[] Read(long offset, int length)
    {
        if (offset < 0 || offset >= _fs.Length) return null;
        long avail = Math.Min(length, _fs.Length - offset);
        var buf = new byte[avail];
        _fs.Seek(offset, SeekOrigin.Begin);
        int got = 0;
        while (got < avail)
        {
            int n = _fs.Read(buf, got, (int)(avail - got));
            if (n <= 0) break;
            got += n;
        }
        if (got == avail) return buf;
        Array.Resize(ref buf, got);
        return buf;
    }

    public void Dispose() => _fs?.Dispose();
}

/// <summary>解包上下文：rdp 读取 + 全局块表</summary>
public sealed class Ctx : IDisposable
{
    public string Cwd;
    public string RootOut;
    public RdpReader Rdp;
    public int MaxDepth = 8;

    readonly Dictionary<long, PackageEntry> _blocks = new();
    public IReadOnlyDictionary<long, PackageEntry> Blocks => _blocks;

    /// <summary>全部 rdp 条目（每条都真实占用块）</summary>
    public IEnumerable<PackageEntry> AllEntries => _blocks.Values;

    public void AddBlock(long block, long size, bool compressed, long rawSize, bool isRes,
                         string fileName, string exportPath, string resId, string group, int index)
    {
        var rf = new EntryRef { Res = resId, Group = group, Index = index };
        if (_blocks.TryGetValue(block, out var e))
        {
            if (e.Size != size)
                throw new InvalidDataException($"块 {block} 冲突：size 0x{e.Size:X} vs 0x{size:X}（{e.FileName} / {fileName}）");
            if (e.ExportPath != exportPath)
            {
                e.Aliases ??= new List<string>();
                if (!e.Aliases.Contains(exportPath)) e.Aliases.Add(exportPath);
            }
            e.Refs.Add(rf);
        }
        else
        {
            _blocks[block] = new PackageEntry
            {
                Block = block, Size = size,
                BlockCount = (size + Pres.BlockSize - 1) / Pres.BlockSize,
                Compressed = compressed, RawSize = rawSize, IsRes = isRes,
                FileName = fileName, ExportPath = exportPath,
                Refs = new List<EntryRef> { rf },
            };
        }
    }

    public void Dispose() => Rdp?.Dispose();
}
