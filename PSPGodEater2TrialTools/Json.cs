using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSPGodEater2TrialTools;

// ------------------------------------------------------------------ 模型
// 与 Python 版输出的 JSON 字段名 / 字段顺序保持一致

/// <summary>xxx.res 的 list（用户手写格式）</summary>
public class ResList
{
    [JsonPropertyName("Filename")] public string Filename { get; set; }
    [JsonPropertyName("Magic")] public long Magic { get; set; }
    [JsonPropertyName("Format")] public int Format { get; set; }
    [JsonPropertyName("Version")] public int Version { get; set; }
    [JsonPropertyName("package")] public int Package { get; set; }
    [JsonPropertyName("Sign")] public int Sign { get; set; }
    [JsonPropertyName("CeckSum")] public string CeckSum { get; set; }
    /// <summary>★ 组在 TOC 里的【物理排布顺序】（只列非空组，如 [2,5,4]）。
    ///   同一 res 内并不总按组号升序：实测 god_menu.res = [2,4,5,6,7,1,0]，而 Field.res = [2,5,4]。
    ///   TOC 区段与 body 里名字区/数据的先后都由它决定 —— 打包时按它重建才能与原文件逐字节一致。
    ///   缺失 / 为空时退回默认顺序 { 2, 4, 5, 6, 7, 1, 0 }。</summary>
    [JsonPropertyName("Groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int> Groups { get; set; }
    [JsonPropertyName("Files")] public List<ResEntry> Files { get; set; } = new();
}

public class ResEntry
{
    [JsonPropertyName("Group")] public string Group { get; set; }
    [JsonPropertyName("ElementName")] public List<string> ElementName { get; set; }
    [JsonPropertyName("FileName")] public string FileName { get; set; }
    [JsonPropertyName("Compression")] public bool Compression { get; set; }
    [JsonPropertyName("dummysize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DummySize { get; set; }
    [JsonPropertyName("ExportPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExportPath { get; set; }
}

/// <summary>rtbl 的 list —— 按【固定槽位(table)】记录它引用的 package.rdp 文件。
///   增删文件即按槽位操作：新增占用一个空槽，删除把该槽整段 0x80 清空（从 Files 里去掉该条即可）。</summary>
public class RtblList
{
    [JsonPropertyName("tablecount")] public int TableCount { get; set; }
    [JsonPropertyName("Files")] public List<RtblEntry> Files { get; set; } = new();
}

public class RtblEntry
{
    /// <summary>槽位号（字符串）：rtbl 内偏移 ÷ 0x80</summary>
    [JsonPropertyName("table")] public string Table { get; set; }
    [JsonPropertyName("ElementName")] public List<string> ElementName { get; set; }
    [JsonPropertyName("FileName")] public string FileName { get; set; }
    [JsonPropertyName("Compression")] public bool Compression { get; set; }
    [JsonPropertyName("ExportPath")] public string ExportPath { get; set; }
}

/// <summary>package.rdp 的 list —— 记录 rdp 上每个块的条目 + 引用它的 res 索引</summary>
public class PackageList
{
    [JsonPropertyName("rdpShift")] public int RdpShift { get; set; } = 11;
    [JsonPropertyName("blockSize")] public int BlockSize { get; set; } = 1 << 11;
    [JsonPropertyName("totalBlocks")] public long TotalBlocks { get; set; }
    [JsonPropertyName("entries")] public List<PackageEntry> Entries { get; set; } = new();
}

public class PackageEntry
{
    [JsonPropertyName("block")] public long Block { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("blockCount")] public long BlockCount { get; set; }
    [JsonPropertyName("compressed")] public bool Compressed { get; set; }
    [JsonPropertyName("rawSize")] public long RawSize { get; set; }
    [JsonPropertyName("isRes")] public bool IsRes { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; }
    [JsonPropertyName("exportPath")] public string ExportPath { get; set; }
    [JsonPropertyName("aliases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> Aliases { get; set; }
    [JsonPropertyName("refs")] public List<EntryRef> Refs { get; set; } = new();
}

public class EntryRef
{
    /// <summary>引用者标识：顶层 res 为 "system.res"，子 res 为其相对 1.out 的路径 "system/god_menu.res"</summary>
    [JsonPropertyName("res")] public string Res { get; set; }
    [JsonPropertyName("group")] public string Group { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
}

// ------------------------------------------------------------------ 读写
public static class JsonIo
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static T Load<T>(string path)
    {
        var txt = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(txt, Opts);
    }

    public static void Save<T>(string path, T obj)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(obj, Opts), new System.Text.UTF8Encoding(false));
    }
}
