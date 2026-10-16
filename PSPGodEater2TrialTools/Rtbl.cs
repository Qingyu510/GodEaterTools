using System.Text;

namespace PSPGodEater2TrialTools;

/// <summary>rtbl 里解析出的一条：槽位 + rdp 引用 + 名字</summary>
public class RtblRawEntry
{
    public int Slot;
    public long Block;      // rdp 块号（×0x800 = rdp 字节偏移）
    public long Size;       // 被引用 res 的字节数
    public string Name = "";
    public string Ext = "";

    public string FileName => string.IsNullOrEmpty(Ext) ? Name : Name + "." + Ext;
}

/// <summary>
/// rtbl —— 每 0x80 一槽的「rdp 引用表」，由 res 的 rtbl 组（G7）条目引用。
/// 槽位布局（0x80）：
///   +0x00 u32  rdp 块号（×0x800 = rdp 字节偏移）
///   +0x04 u32  被引用 res 的字节数
///   +0x08 u32  0x20（常量）
///   +0x0C u32  3（Sign；CeckSum 已废弃）
///   +0x10..1F  0
///   +0x20 u32  名字相对偏移（恒 0x2C）
///   +0x24 u32  扩展名相对偏移（0x2C + len(name)+1）
///   +0x28 u32  内容结束偏移（扩展名偏移 + len(ext)+1）
///   +0x2C      名字 \0 扩展名 \0（槽内其余补 0）
/// 空槽整段 0x80 全 0。删除条目 = 该槽清零；新增 = 占用一个空槽（不够则抬高 tablecount）。
/// </summary>
public static class Rtbl
{
    public const int SlotSize = 0x80;
    public const int NameBase = 0x2C;

    /// <summary>按文件名后缀判定是否 rtbl</summary>
    public static bool IsPath(string p) =>
        !string.IsNullOrEmpty(p) && p.EndsWith(".rtbl", StringComparison.OrdinalIgnoreCase);

    /// <summary>rtbl 在 res 里的组号（SlotName[7] = "rtbl"）</summary>
    public static bool IsSlot(int g) => g == 7;

    public static List<RtblRawEntry> Parse(byte[] d)
    {
        var list = new List<RtblRawEntry>();
        for (int b = 0; b + SlotSize <= d.Length; b += SlotSize)
        {
            bool nz = false;
            for (int i = 0; i < SlotSize; i++) if (d[b + i] != 0) { nz = true; break; }
            if (!nz) continue;

            int nOff = (int)Pres.U32(d, b + 0x20);
            int eOff = (int)Pres.U32(d, b + 0x24);
            list.Add(new RtblRawEntry
            {
                Slot = b / SlotSize,
                Block = Pres.U32(d, b),
                Size = Pres.U32(d, b + 4),
                Name = nOff > 0 ? Pres.CStr(d, b + nOff) : "",
                Ext = eOff > 0 ? Pres.CStr(d, b + eOff) : "",
            });
        }
        return list;
    }

    /// <summary>实际槽位数 = max(tablecount, 最大槽位 + 1)（槽位超出 tablecount 时自动抬高）</summary>
    public static int SlotCountOf(RtblList js)
    {
        int maxSlot = -1;
        if (js?.Files != null)
            foreach (var f in js.Files)
                if (int.TryParse(f.Table, out int s) && s > maxSlot) maxSlot = s;
        return Math.Max(js?.TableCount ?? 0, maxSlot + 1);
    }

    public static long LengthOf(RtblList js) => (long)SlotCountOf(js) * SlotSize;

    /// <summary>槽位 → 它在【新 rdp】上的 (块号, 字节数)；未知返回 (-1,-1)</summary>
    public delegate (long block, long size) SlotLocator(int slot);

    /// <summary>由 rtbl list 重建 rtbl 字节（全部槽位按 tablecount 铺开，空槽补 0）</summary>
    public static byte[] Build(RtblList js, SlotLocator locate)
    {
        int slots = SlotCountOf(js);
        var buf = new byte[(long)slots * SlotSize];

        foreach (var f in js.Files ?? new List<RtblEntry>())
        {
            if (!int.TryParse(f.Table, out int slot) || slot < 0)
                throw new InvalidDataException($"rtbl 槽位无效: \"{f.Table}\"");
            if (slot >= slots)
                throw new InvalidDataException($"rtbl 槽位越界: {slot}（tablecount {slots}）");

            long p = (long)slot * SlotSize;
            var (blk, size) = locate(slot);
            if (blk < 0 || size < 0)
                throw new InvalidDataException(
                    $"rtbl 槽 {slot}（{f.FileName}）在 package.json 里没有对应的 rdp 记录 —— 它引用了 package.rdp 却没被登记");

            string name = f.ElementName != null && f.ElementName.Count > 0 ? (f.ElementName[0] ?? "") : "";
            string ext = f.ElementName != null && f.ElementName.Count > 1 ? (f.ElementName[1] ?? "") : "";
            int nOff = NameBase;
            int eOff = nOff + Encoding.UTF8.GetByteCount(name) + 1;
            int xOff = eOff + Encoding.UTF8.GetByteCount(ext) + 1;
            if (xOff > SlotSize)
                throw new InvalidDataException($"rtbl 槽 {slot} 名字过长（{xOff} > {SlotSize}）：{name}.{ext}");

            Pres.WriteU32At(buf, p, (uint)blk);
            Pres.WriteU32At(buf, p + 4, (uint)size);
            Pres.WriteU32At(buf, p + 8, 0x20);
            Pres.WriteU32At(buf, p + 0xC, 3);
            Pres.WriteU32At(buf, p + 0x20, (uint)nOff);
            Pres.WriteU32At(buf, p + 0x24, (uint)eOff);
            Pres.WriteU32At(buf, p + 0x28, (uint)xOff);
            Encoding.UTF8.GetBytes(name).CopyTo(buf, p + nOff);
            Encoding.UTF8.GetBytes(ext).CopyTo(buf, p + eOff);
        }
        return buf;
    }
}
