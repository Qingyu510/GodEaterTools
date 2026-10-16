using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSVGodEaterUSTools;

// ------------------------------------------------------------------ 模型
// 与 Python 版输出的 JSON 字段名 / 字段顺序保持一致

/// <summary>xxx.res 的 list（用户手写格式）</summary>
public class ResList
{
    [JsonPropertyName("Filename")] public string Filename { get; set; }
    [JsonPropertyName("Magic")] public long Magic { get; set; }
    [JsonPropertyName("GroupsCount")] public int GroupsCount { get; set; }
    [JsonPropertyName("Version")] public int Version { get; set; }
    [JsonPropertyName("package")] public int Package { get; set; }
    [JsonPropertyName("Sign")] public int Sign { get; set; }
    [JsonPropertyName("CeckSum")] public string CeckSum { get; set; }
    /// <summary>★ 本体 res 指针（更新补丁 res 专用）：头部 +0x14 的【来源类型】，
    ///   即 offset 高 4 位的同一套编码（"4"=package.rdp / "5"=data.rdp / "6"=patch.rdp）。
    ///   有值时该 res 的 local(G2) 组 C/D 条目索引到本体 res，而非自身文件。全 0 = 本体即自身，不输出。</summary>
    [JsonPropertyName("BaseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaseType { get; set; }
    /// <summary>★ 本体 res 指针的【偏移部分】：+0x14 的低 28 位（4/5/6 时即块号）。十六进制字符串。</summary>
    [JsonPropertyName("BaseOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaseOffset { get; set; }
    /// <summary>★ 本体 res 的【字节数】：头部 +0x18。★十进制字符串。</summary>
    [JsonPropertyName("BaseSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaseSize { get; set; }
    /// <summary>★ 本体 res 在【本体已解包产物】里的路径（如 "system/god_menu.res"）。
    ///   由头部 +0x14 的 (BaseType, BaseOffset) 到本体 package.json / data.json 里匹配得到；
    ///   匹配不到则不输出。打包 DLC 时据此定位本体 res 的 list，进而更新其 4/5/C/D 条目的偏移与大小。</summary>
    [JsonPropertyName("BaseExportPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaseExportPath { get; set; }
    /// <summary>★ 组在 TOC 里的【物理排布顺序】（只列非空组，如 [2,5,4]）。
    ///   同一 res 内并不总按组号升序：实测 god_menu.res = [2,4,5,6,7,1,0]，而 Field.res = [2,5,4]。
    ///   TOC 区段与 body 里名字区/数据的先后都由它决定 —— 打包时按它重建才能与原文件逐字节一致。
    ///   缺失 / 为空时退回默认顺序 { 2, 4, 5, 6, 7, 1, 0 }。</summary>
    [JsonPropertyName("Groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int> Groups { get; set; }
    /// <summary>★ US 美版：语言分区数（头部 +0x1C）。缺省 / 1 = 单分区（日版无此字段）；
    ///   ≥2 时头部 0x20 起是 N×8 的分区表，Files 里每条带 Lang 指明它属于哪个分区。</summary>
    [JsonPropertyName("LangCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LangCount { get; set; }
    /// <summary>★ US 美版：分区序号 → 目录名（en/fr/it/de/es/ru）。缺省时按此顺序默认。</summary>
    [JsonPropertyName("LangNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> LangNames { get; set; }
    [JsonPropertyName("Files")] public List<ResEntry> Files { get; set; } = new();
}

public class ResEntry
{
    [JsonPropertyName("Group")] public string Group { get; set; }
    /// <summary>★ US 美版多语言分区：本条属于哪个语言分区（0 = en / 1 = fr / …）。
    ///   仅当所在 res 的 LangCount ≥ 2 时输出；缺省即分区 0。</summary>
    [JsonPropertyName("Lang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Lang { get; set; }
    /// <summary>TOC 里 offset 高 4 位的【数据来源类型】：
    ///   "C"=本 res 文件内（local 排包后 / table、string 排 body）/ "4"=package.rdp / "5"=data.rdp / "0"=空条目。
    ///   体验版（ver&lt;3）TOC 无类型位，不输出该字段。</summary>
    [JsonPropertyName("Type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }
    /// <summary>TOC 的 offset 低 28 位（★十进制字符串）：C/D 时=res 文件内绝对偏移，4/5/6 时=rdp 块号。
    ///   高 4 位（来源类型）由上面的 Type 单独记录，这里只存偏移部分。打包时回填 TOC 用。</summary>
    [JsonPropertyName("Offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Offset { get; set; }
    /// <summary>TOC +0x04：数据字节数（原样记录；未压缩时 == decsize，压缩时为压缩态长度）。
    ///   ★ DLC 打包（-cdlc）时，带 BaseExportPath 的 res 内的 4 / 5 / C / D 条目，
    ///   其 offset / size / decsize 要按【基准 list】修正 —— 基准 list（x=1 → 1.out/list，
    ///   x=2 → 4.pack/list）里的这四个值就是本体里的真值，游戏据此定位文件与预分配解压内存。
    ///   打包 DLC 时不重新解包本体，故必须由基准 list 提供；本体改动后需重跑 -x / -c 刷新基准 list。</summary>
    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }
    [JsonPropertyName("ElementName")] public List<string> ElementName { get; set; }
    [JsonPropertyName("FileName")] public string FileName { get; set; }
    [JsonPropertyName("Compression")] public bool Compression { get; set; }
    /// <summary>压缩格式："dflt"=体验版(raw deflate) / "blz4"=GE2RB。缺省但 Compression=true 时按 dflt 处理。</summary>
    [JsonPropertyName("Compress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Compress { get; set; }
    [JsonPropertyName("dummysize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DummySize { get; set; }
    /// <summary>TOC +0x1C：解密（解压）后大小。GE2 正式版新增；未记录 / 体验版为 null（写 0）</summary>
    [JsonPropertyName("decsize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DecSize { get; set; }
    [JsonPropertyName("ExportPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExportPath { get; set; }
}

/// <summary>rtbl 的 list —— 按【固定槽位(table)】记录它引用的 package.rdp 文件。
///   增删文件即按槽位操作：新增占用一个空槽，删除把该槽整段 0x200 清空（从 Files 里去掉该条即可）。</summary>
public class RtblList
{
    [JsonPropertyName("tablecount")] public int TableCount { get; set; }
    [JsonPropertyName("Files")] public List<RtblEntry> Files { get; set; } = new();
}

public class RtblEntry
{
    /// <summary>槽位号（字符串）：rtbl 内偏移 ÷ 0x200</summary>
    [JsonPropertyName("table")] public string Table { get; set; }
    /// <summary>被引用 res 所在 rdp 的来源类型："4"=package.rdp / "5"=data.rdp / "6"=patch.rdp</summary>
    [JsonPropertyName("Type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }
    /// <summary>槽内 +0x00 的 offset 低 28 位（★十进制字符串），即被引用 res 所在的 rdp 块号。
    ///   高 4 位（来源类型）由上面的 Type 记录。打包时按新块位回填。</summary>
    [JsonPropertyName("Offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Offset { get; set; }
    [JsonPropertyName("ElementName")] public List<string> ElementName { get; set; }
    [JsonPropertyName("FileName")] public string FileName { get; set; }
    [JsonPropertyName("Compression")] public bool Compression { get; set; }
    /// <summary>压缩格式："dflt"=体验版 / "blz4"=GE2RB。缺省但 Compression=true 时按 dflt 处理。</summary>
    [JsonPropertyName("Compress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Compress { get; set; }
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
    /// <summary>压缩格式："dflt"=体验版 / "blz4"=GE2RB。缺省但 compressed=true 时按 dflt 处理。</summary>
    [JsonPropertyName("compress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Compress { get; set; }
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
    /// <summary>★ US 美版多语言分区：引用者 res 里该条目属于哪个语言分区（0 = en）。
    ///   仅当 res 的 LangCount ≥ 2 时输出。多分区下同一 (res, group, index) 会在
    ///   不同分区各出现一次（如六分区各调一个 Monitor_01），必须靠它区分。</summary>
    [JsonPropertyName("lang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Lang { get; set; }
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
