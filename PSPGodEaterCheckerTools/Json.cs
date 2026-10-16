using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSPGodEaterCheckerTools;

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
