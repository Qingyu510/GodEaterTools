using System.Text;

namespace PSVGodEater2Tools;

/// <summary>rtbl 里解析出的一条：槽位 + rdp 引用 + 名字</summary>
public class RtblRawEntry
{
    public int Slot;
    /// <summary>rdp 块号（×0x800 = rdp 字节偏移）</summary>
    public long Block;
    /// <summary>offset 高 4 位的来源类型：0x4=package.rdp / 0x5=data.rdp（GE2 正式版）；0=无类型（体验版）</summary>
    public int Type;
    /// <summary>被引用 res 的字节数</summary>
    public long Size;
    public string Name = "";
    public string Ext = "";

    public string FileName => string.IsNullOrEmpty(Ext) ? Name : Name + "." + Ext;
}

/// <summary>
/// rtbl —— 每 0x80 一槽的「rdp 引用表」，由 res 的 rtbl 组（G7）条目引用。
/// 槽位布局（0x80）：
///   +0x00 u32  rdp 块号（×0x800 = rdp 字节偏移；高 4 位 = 来源类型 0x4=package / 0x5=data）
///   +0x04 u32  被引用 res 的字节数
///   +0x08 u32  0x20（常量）
///   +0x0C u32  3（Sign；CeckSum 已废弃）
///   +0x10..1B  0
///   +0x1C u32  同 +0x04（size 的副本；实测 100%：106/106、212/212、918/918）
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
            long rawBlk = Pres.U32(d, b);
            list.Add(new RtblRawEntry
            {
                Slot = b / SlotSize,
                Block = Pres.Untag(rawBlk),          // ★ 低 28 位才是块号（高 4 位是 rdp 来源类型）
                Type = Pres.TagOf(rawBlk),
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

    /// <summary>槽位 → 它在【新 rdp】上的 (来源类型, 块号, 字节数)；未知返回 (-1,-1,-1)</summary>
    public delegate (int type, long block, long size) SlotLocator(int slot);

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
            var (lt, blk, size) = locate(slot);
            if (blk < 0 || size < 0)
                throw new InvalidDataException(
                    $"rtbl 槽 {slot}（{f.FileName}）在 package.json / data.json 里没有对应的 rdp 记录 —— 它引用了 rdp 却没被登记");
            // ★ 来源类型以 list 的 Type 为准（"4"=package.rdp / "5"=data.rdp）；缺省时退回块表登记的归属
            int typ = string.IsNullOrEmpty(f.Type) ? lt : Pres.ParseType(f.Type, lt);
            if (lt >= 0 && typ != lt)
                throw new InvalidDataException(
                    $"rtbl 槽 {slot}（{f.FileName}）Type 不符：list 写 \"{f.Type}\"，"
                  + $"但它登记在 \"{Pres.TypeStr(lt)}\"（{(lt == Pres.TypeData ? "data.rdp" : "package.rdp")}）上");

            string name = f.ElementName != null && f.ElementName.Count > 0 ? (f.ElementName[0] ?? "") : "";
            string ext = f.ElementName != null && f.ElementName.Count > 1 ? (f.ElementName[1] ?? "") : "";
            int nOff = NameBase;
            int eOff = nOff + Encoding.UTF8.GetByteCount(name) + 1;
            int xOff = eOff + Encoding.UTF8.GetByteCount(ext) + 1;
            if (xOff > SlotSize)
                throw new InvalidDataException($"rtbl 槽 {slot} 名字过长（{xOff} > {SlotSize}）：{name}.{ext}");

            Pres.WriteU32At(buf, p, (uint)Pres.Tag(typ, blk));   // ★ 高 4 位 = 来源类型（0x4 package / 0x5 data）
            Pres.WriteU32At(buf, p + 4, (uint)size);
            Pres.WriteU32At(buf, p + 8, 0x20);
            Pres.WriteU32At(buf, p + 0xC, 3);
            Pres.WriteU32At(buf, p + 0x1C, (uint)size);          // ★ +0x1C 再记一次 size（实测 100%：106/106、212/212、918/918）
            Pres.WriteU32At(buf, p + 0x20, (uint)nOff);
            Pres.WriteU32At(buf, p + 0x24, (uint)eOff);
            Pres.WriteU32At(buf, p + 0x28, (uint)xOff);
            Encoding.UTF8.GetBytes(name).CopyTo(buf, p + nOff);
            Encoding.UTF8.GetBytes(ext).CopyTo(buf, p + eOff);
        }
        return buf;
    }
}
