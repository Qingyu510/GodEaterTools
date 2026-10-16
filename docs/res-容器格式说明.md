# 《GOD EATER》系列 PRES(`.res`) / rdp 数据包容器格式说明

> 适用范围：PSP《GOD EATER》/《GOD EATER BURST》/《GOD EATER 2》、
> PSV《GOD EATER 2》/《GOD EATER 2 RAGE BURST》/《GOD EATER RESURRECTION》、
> PS4《GOD EATER 2 RAGE BURST》/《GOD EATER RESURRECTION》及其美版，
> 以及各代的 DLC / 更新补丁数据包。
>
> 本文所有字段偏移、取值与算法均以本工具链源码（10 个 `Pres.cs` / `Unpacker.cs` / `Packer.cs` /
> `Rtbl.cs` / `Blz2.cs` / `Blz4.cs`）及实测样本为准，不含推测。

---

## 目录

- [1. 总览](#1-总览)
- [2. 通用概念](#2-通用概念)
- [3. Version 0：GOD EATER（PSP）](#3-version-0god-eaterpsp)
- [4. Version 0：GOD EATER BURST（PSP）](#4-version-0god-eater-burstpsp)
- [5. Version 0 的 DLC（`.edat` 数据包）](#5-version-0-的-dlcedat-数据包)
- [6. Version 1：GE2 引继数据检查器](#6-version-1ge2-引继数据检查器)
- [7. Version 2：GE2 体験版 —— rtbl 分区引入](#7-version-2ge2-体験版--rtbl-分区引入)
- [8. Version 3：GE2 正式版 —— 奠定后续所有版本](#8-version-3ge2-正式版--奠定后续所有版本)
- [9. Version 3 的 DLC（`SYSTEM_UPDATE.EDAT` + `PATCH.EDAT`）](#9-version-3-的-dlcsystem_updateedat--patchedat)
- [10. Version 3 变体 A：GE2RB / RESURRECTION（PSV）](#10-version-3-变体-age2rb--resurrectionpsv)
- [11. Version 3 变体 B：GE2RB / RESURRECTION（PS4）](#11-version-3-变体-bge2rb--resurrectionps4)
- [12. Version 3 变体 C：美版多语言分区](#12-version-3-变体-c美版多语言分区)
- [13. Version 3 的 DLC（裸 `system_update.res` + `patch.rdp`）](#13-version-3-的-dlc裸-system_updateres--patchrdp)
- [14. 迭代方式总表](#14-迭代方式总表)
- [15. 附录 A：压缩段格式速查](#15-附录-a压缩段格式速查)
- [16. 附录 B：Version 与工具 / 作品对照](#16-附录-bversion-与工具--作品对照)

---

## 1. 总览

### 1.1 文件角色：本体与更新补丁

游戏包分**本体**与**更新补丁**两层。更新补丁一旦安装，**索引主体会被整体顶替** ——
游戏此后**不再回到本体的 `system.res`**。

**① 本体（原始安装）**

| 文件 | 角色 |
|---|---|
| `system.res` | **索引 + 内嵌数据**。魔数 `Pres`，内含头部、8 个组表、TOC（条目表）、以及一部分直接躺在文件里的数据。**游戏本体调用它来索引其他文件。** |
| `package.rdp` | **外部数据块池**。`res` 里「不在自己身上」的条目，实际字节放在这里，TOC 只记一个块号。 |
| `data.rdp` | Version 3（GE2）起新增的**第二个数据块池**。PSP 侧为让过大的包适配 UMD / 加快加载（可单独放进记忆卡），PS4 侧为避免单包编址侵占 `offset` 高 4 位；游戏把一部分数据分到这里。 |

**② 更新补丁（内容更新下发）**

| 文件 | 角色 |
|---|---|
| `system_update.res` | **更新后的索引主体**，规格与本体 `system.res` 同构。游戏加载后**只按它索引文件，不再从 `system.res` 调用文件**。 |
| `patch.rdp` | 本次更新**新增 / 替换**的数据块池，对应本体 `package.rdp` 的位置。更新中**未改动**的数据仍从本体的 `package.rdp` / `data.rdp` 取。 |

> **对应关系**：`system_update.res` 顶替 `system.res` 的「索引」位，`patch.rdp` 顶替 `package.rdp` 的「数据池」位。
> 补丁 `res` 的 TOC 条目据 `offset` 高 4 位分流 —— **6** = 数据在 `patch.rdp`（本次新增 / 替换），
> **4 / 5** = 数据仍在本体 `package.rdp` / `data.rdp`（本次未改动，见 [9.2](#92-跳过规则)）。

更新补丁的封装形态随版本而变：GE2 是 `SYSTEM_UPDATE.EDAT` + `PATCH.EDAT`
（[§9](#9-version-3-的-dlcsystem_updateedat--patchedat)），GE2RB / 美版是裸 `system_update.res` + `patch.rdp`
（[§13](#13-version-3-的-dlc裸-system_updateres--patchrdp)）。
更早的 GE1 / GEB **没有这套「顶替索引」机制**，其 `.edat` 属**追加数据包**形态（见
[§5](#5-version-0-的-dlcedat-数据包)）。

`res` 与 `rdp` 通过 **TOC 条目的 `offset` 字段**绑定：`offset` 左移若干位即得到该数据在 `rdp`
文件内的字节偏移（见 [2.4](#24-rdp-编址单位块)）。哪些条目走 `rdp`、哪些留在 `res` 内，
由**条目所属的组**与（Version 3 起的）**offset 高 4 位的类型标记**共同决定（类型表见
[8.1](#81-数据来源类型offset-高-4-位)）。

### 1.2 版本谱系（★ 核心）

`res` 头部 `+0x09` 有一个 **`Version` 字节**。它是包格式的**版本号**，0 → 1 → 2 → 3 是一条
**逐级迭代**的演进链：

| `Version` | 对应版本（平台） | 容器布局族 | 本版引入 / 变化 |
|---|---|---|---|
| **0** | **GOD EATER**（PSP）<br>**GOD EATER BURST**（PSP） | **v1** | 初版容器。GEB 只是**加了 gzip 压缩段**，`res` 结构一字不变 ⇒ **两作同为 Version 0** |
| **1** | **GE2 引继数据检查器**（PSP，`NPJH00134`） | **v1** | 结构沿用 Version 0；压缩格式换成 `dflt`；**rdp 编址单位由 0x8000 降为 0x800** |
| **2** | **GE2 体験版**（PSP `NPJH90338` / PSV `PCSG90094`） | **v2** | 头部扩展到 0x20（新增 `pkgSize` 字段）、`local` 数据移到文件尾、`CeckSum` 废弃；**★ 首次引入 `rtbl`（组 7）分区** |
| **3** | **GE2 正式版**（PSP `NPJH50832` / PSV `PCSG00240`） | **v2** | **★ 奠定后续所有版本的基础**：`offset` 高 4 位类型编码（C/D/4/5）、第二个 `rdp`（`data.rdp`）、TOC `+0x1C` = 解压后大小、`blz2` 压缩 |
| **3** | **GE2RB / RESURRECTION**（PSV） | **v2** | Version 3 的**布局不变**变体：压缩改 `blz4`、新增 `EXCLUDE_RDP`(类型 3)、组 1 改 `self`（随 PSV 外部依赖后缀 `.self`/`.suprx`）、rtbl 槽位 0x80 → **0x200** |
| **3** | **GE2RB / RESURRECTION**（PS4） | **ps4** | Version 3 的**结构变更**变体：因 PS4 架构原因改用新容器布局（头 0x30 / 组表 8×16 / TOC 64 B），**但语义层与 Version 3 完全一致** |
| **3** | **美版**（PSV，`PCSB/PCSE`） | **v2** | Version 3 的变体：叠加**多语言分区**（`+0x1C` = 分区数、分区表、六分区共享内容池） |

**关键结论：**

1. **`Version` 只到 3，之后不再增加。** GE2 正式版（Version 3）定下的语义层（类型编码、双 `rdp`、
   本体指针、`rtbl`）被**后续所有版本原样继承** —— GE2RB 的 PSV 版、PS4 版、美版**全部是 Version 3 的包**。
2. **`Version` 与「容器布局族」是两个维度。** 布局族（v1 / v2 / ps4）描述**字节怎么排**；
   `Version` 描述**语义迭代到哪一步**。PS4 版之所以看起来「另成一代」，仅仅因为它**换了布局族**，
   其版本号仍是 3。
3. **`Version ≥ 3` 是工具链里唯一被代码显式判断的分界**
   （`Pres.cs`：`bool typed = js.Version >= 3;`）—— 只有 ≥ 3 的包才启用 `offset` 高 4 位的类型编码。
   Version 0 / 1 / 2 的 `offset` 高 4 位恒 0，数据位置**纯由组号决定**。
3. **三次迭代（Ver 1 / Ver 2 / Ver 3）全部发生在 GEB 与 GE2 之间，且迭代顺序 = 发布顺序**：
   **GEB（Ver 0）→ 数据检查器（Ver 1）→ GE2 体験版（Ver 2）→ GE2（Ver 3）**
   （详见 [1.3](#13-迭代的驱动力容量与编址约束)①）。

### 1.3 迭代的驱动力：容量与编址约束

每一轮迭代都不是「为改而改」，而是被**光盘容量**与**编址位数**逼出来的。

**① UMD 单碟 1.8 GB（PSP）**

- PSP 载体的 **UMD 单碟最大容量为 1.8 GB**。
- **GE / GEB 时期**：`rdp` 编址单位是 **0x8000**（32 KB 块）。因资产不多，最终镜像约 **1.36 GB**，尚有余量。
- **GE2 时期**：需**继承 GEB 的大量资产**，同时还要**追加新资产**。若沿用 0x8000 块与老版 `res` 容器，
  **发布资源将无法装进一张 UMD**。这直接促成了三件事：
  1. **`res` 容器的迭代**（Ver 0 → Ver 2 / Ver 3，见 §7 / §8）；
  2. **rdp 编址单位 0x8000 → 0x800**（单位变小、块数变多，见 [2.4](#24-rdp-编址单位块)）；
  3. **`blz2` 压缩的出现**（见 §8.4）。
- **★ Ver 1 / Ver 2 / Ver 3 这三次迭代全部发生在 GEB 与 GE2 之间，且迭代顺序与发布顺序完全一致**：
  **GEB（Ver 0）→ 数据检查器（Ver 1）→ GE2 体験版（Ver 2）→ GE2（Ver 3）**。
- 其中**数据检查器与 GE2 体験版都是在 GE2 开发期间先行发布**的版本，所以**最早发布的检查器就已完成编址单位的演变**
  （0x8000 → 0x800）；但二者是**面向大众的小型版本**，制作组**尚未加入 blz 压缩**。
  `blz2` **首次出现于 GE2**（PSP 版与 PSV 版），此后 **GE2RB 把 `blz2` 改进为 `blz4`**。

**② 容量约束与 `data.rdp` 的分流**

- PSP 侧**并不存在「文件系统单文件上限」这类限制**，`data.rdp` 的拆分同样是**被 UMD 盘的总容量限制逼出来的**：
  为把发布资源装进一张 UMD（并让部分数据能放进记忆卡以加快加载），GE2 起把**即时加载数据**分到
  `data.rdp`，而**不常载入的数据仍留在 `package.rdp`**（详见 [8.2](#82-datardp)）。
- **GE2RB 不再于 PSP 平台发布** ⇒ **再次取消 `data.rdp`**（运行程序**仍保留其接口**）。
- **PS4 回归 `data.rdp` 与文件系统无关**（PS4 用 exFAT，并无容量瓶颈），而是**为解决「单包体过大」**：
  Version ≥ 3 起 `TOC.offset` 的**高 4 位被征用为「来源类型」**（见 [8.1](#81-数据来源类型offset-高-4-位)），
  只剩**低 28 位**能表达块号 / 文件内偏移。**单一包体一旦过大，低位就会溢出、侵占高 4 位，
  从而破坏数据来源类型的判定** ⇒ 必须**把数据拆开、回归 `data.rdp`**。
  唯一例外是 **OFF SHOT**（拍照衍生作）：**资源很少**，包体**远不会大到侵占高 4 位**，因此**没有 `data.rdp`**
  （运行程序**同样保留接口**）。

### 1.4 布局族快速对照

| 布局族 | 头部区 | 组表 | TOC 条目 | `decSize` | 名字指针步长 | 使用它的 Version |
|---|---|---|---|---|---|---|
| **v1** | 0x10 | 8 × 8 B @0x10 | 32 B @0x50 | — | 4 B | Version 0 / 1 |
| **v2** | 0x20 | 8 × 8 B @0x20 | 32 B @0x60 | `+0x1C` | 4 B | Version 2 / 3（及 PSV 侧变体） |
| **ps4** | 0x30 | 8 × **16** B @0x30 | **64 B** @0xB0 | **`+0x34`** | **8 B** | Version 3（PS4 变体） |

> 表中「头部区」= TOC 起点之前的字节数（头部字段 + 组表）。例如 v1 的 TOC 起点 0x50 = 头 0x10 + 组表 0x40。

rdp 编址单位（`RdpShift`）与布局族**无关**，单独见 [2.4](#24-rdp-编址单位块)。

---

## 2. 通用概念

### 2.1 组（Group）

`res` 内部按**用途**把条目分成 8 个组，组号写入组表、TOC 按组的起始偏移分段存放：

| 组 | 名称 | 内容 | 数据存放位置 |
|---|---|---|---|
| 0 | `res` | 子资源（本身又是一个 `res`） | rdp |
| 1 | `prx`（PSP / PS4）/ `self`（PSV）—— 随平台外部依赖模块后缀命名 | 外部依赖 / 自加载声明 | rdp（或仅声明名字） |
| 2 | `local` | 本体数据 | **v1：`res` 包内；v2 / ps4 起：`res` 包之后的文件尾** |
| 3 | （空） | 保留，条目数恒 0 | — |
| 4 | `refer` | 路径引用（`.scr` 等） | rdp |
| 5 | `table` | u32 数组（如槽位表、参数表） | `res` 包内 body |
| 6 | `string` | 以 `\0` 结尾的文本 | `res` 包内 body |
| 7 | `rtbl` | **rdp 引用表**（每槽记录一个 rdp 块引用） | rdp —— **Version 2 起才启用**（见下） |

- **★ 组 7 `rtbl` 是 Version 2（GE2 体験版）才加入的分区。**
  - Version 0 / 1（GE1 / GEB / Checker）**没有 `rtbl` 这个分区**：工具链里既无 `Rtbl.cs`，
    `Pres.cs` 注释亦明确写「`res(G0)/rtbl(G7)` **条目数为 0**，故未纳入」—— 组表里的组 7 槽
    只是一个占位，条目数恒 0。
  - Version 2 起才真正出现 `rtbl` 条目（`Rtbl.cs`：`SlotSize = 0x80`），
    Version 3 变体（GE2RB / 美版）把槽位放大到 **0x200**。
- 组在 TOC / body 里的**物理顺序不是恒定的组号升序**。实测 `god_menu.res` 为 `2,4,5,6,7,1,0`，
  而 `Field.res` 为 `2,5,4`。工具链以 `list` 里记录的 `Groups` 为准，缺失时退回默认顺序
  `{2,4,5,6,7,1,0}`。
- **★ 组 1 的名称随平台变化，取名依据是「该平台外部依赖模块的文件后缀」**：
  - **PSP / PS4** 的可加载模块 / 外部依赖文件后缀是 **`.prx`** ⇒ 组 1 叫 `prx`；
  - **PSV** 上同类文件的后缀是 **`.self` / `.suprx`** ⇒ 组 1 叫 `self`（噬神者系列在 PSV 上取的是 `self`）。
  - 所以同为 `Version 3` 语义，PSV 用 `self`，而 PS4 因平台回到 `.prx` 又叫回 `prx`
    （详见 §10.1 / §11.2）。**名字只反映平台的外部依赖形态，与容器布局族无关**。

### 2.2 TOC 条目

TOC（Table Of Contents）是逐条描述数据的表。v1 / v2 每条 **32 字节**：

| v1 / v2 偏移 | 大小 | 字段 | 说明 |
|---|---|---|---|
| `+0x00` | 4 | `offset` | v1 / Version 2：纯偏移或块号 / **Version ≥ 3：高 4 位 = 来源类型**，低 28 位 = 块号或文件内偏移 |
| `+0x04` | 4 | `size` | 数据字节数（`table` 组记 u32 个数；`string` 含结尾 `\0`） |
| `+0x08` | 4 | `nameOffset` | 名字区起始（相对文件头） |
| `+0x0C` | 4 | `nameCount` | `ElementName` 项数 |
| `+0x1C` | 4 | `decSize` | **解密（解压）后大小**。Version 3（v2 布局）起启用；无压缩 / Version 2 恒 0 |

ps4 布局的 TOC 每条 **64 字节**，且**每个 4 字节值独占 8 字节槽**（高 4 字节恒 0）：

| ps4 偏移 | 字段 |
|---|---|
| `+0x00` | `offset` |
| `+0x08` | `size` |
| `+0x10` | `nameOffset` |
| `+0x18` | `nameCount` |
| `+0x34` | `decSize`（落在 `+0x30` 那个 8 字节槽的后半，**不是** `+0x38`） |

### 2.3 名字区（ElementName）

每个条目在 TOC 之后带一段**名字区**，用「指针表 + 字符串池」的形式存放 `ElementName` 数组：

- `nameCount` 项，每项是一个字符串。
- **v1 / v2**：指针表每项 **4 字节**。
- **ps4**：指针表每项 **8 字节**（值仍在低 4 字节）。
- 文件名的还原规则：`E0 + (E1 非空 ? "." + E1 : "")`。
  例：`["MS_001N_tora", "res"]` → `MS_001N_tora.res`；`["system", "res"]` → `system.res`。
- 官方 `res` 的名字区一律**内联**（指针表紧接在字符串池之前）；读取侧按 `nameOffset` 逐项取串，
  不假设内联，因此外链布局也能正确解析。

### 2.4 rdp 编址单位（块）

`TOC.offset`（低 28 位）**不是字节偏移，而是块号**：

```
rdp 内字节偏移 = offset << RdpShift
```

| 场景 | `RdpShift` | 单位 |
|---|---|---|
| GE1 / GEB 本体（Version 0） | 15 | 0x8000 |
| GE1 / GEB 的 DLC（Version 0） | **4** | **0x10** |
| Checker（Version 1）及 Version 2 / 3 全系 | 11 | 0x800 |

> ★ 该单位与「压缩段内部分块」（0x10000）是**两个无关概念**，不要混淆。
>
> **为什么从 0x8000 降到 0x800**：UMD 单碟只有 1.8 GB（见 [1.3](#13-迭代的驱动力容量与编址约束)①）。
> GE2 要把 GEB 的资产连同新资产一起塞进一张盘，编址单位必须变小以**减少块内浪费**；
> 且**最早发布的检查器就已经完成了这一步演变**（0x8000 → 0x800），早于 GE2 本体发售。

### 2.5 压缩段

`rdp` 与 `res` 内部（`local` 等组）的某些条目整体是一段**压缩数据**。判定方式一律是**魔数嗅探**
—— `res` 头与 TOC 里**没有**任何「压缩与否」的字段：

| 魔数 | 格式 | 使用版本 |
|---|---|---|
| （无） | 明文 | Version 0 的 GOD EATER；Version 2（GE2 体験版） |
| `1F 8B 08` | gzip | Version 0 的 GOD EATER BURST |
| `dflt` | 原始 deflate | Version 1（GE2 引继数据检查器） |
| `blz2` | 分块原始 deflate | Version 3（GE2 正式版） |
| `blz4` | 分块 zlib | Version 3 变体（GE2RB / RESURRECTION / 美版） |

详见 [附录 A](#15-附录-a压缩段格式速查)。

> **压缩施加在哪些数据上（设计动机）**
>
> - **Version 0 的 GEB（`gzip`）与 Version 1 的 Checker（`dflt`）只压缩 `rdp` 内的「主要 `res` 包」。**
>   实测：GEB 的 `system.res` 93 条 TOC 里**仅 1 条**带压缩 —— `god_menu.res`（组 0 `res`）。
>   这么做是为了**避免游戏切换主要 `res` 包时运行内存溢出**（原 PSP 环境内存紧张）。
> - **其他 `res` 包一律不压缩**：否则游戏**即时从 `rdp` 内调用新文件**时还要现场解压，会**拖慢加载速度**。
> - **Version 2（GE2 体験版）起取消了主要 `res` 包的压缩**：`res` 包结构优化后**运行内存冗余空间变多**，
>   不再需要靠压缩来腾内存。自此 `res` 包内的数据（`local` / `table` / `string`）**一律明文**，
>   压缩只出现在 `rdp` 内的数据条目上（`blz2` / `blz4`）。

### 2.6 rtbl 分区（Version 2 起）

`rtbl` 是「**rdp 引用表**」：`res` 的组 7 条目指向一个 `*.rtbl`（躺在 `rdp` 里），
该文件按**固定大小切槽**，每槽描述一条 rdp 引用。

槽位布局（G2 系 `0x80` 槽，GE2RB / 美版 `0x200` 槽）：

| 槽内偏移 | 字段 |
|---|---|
| `+0x00` | u32：rdp 块号（× 0x800 = rdp 字节偏移；Version 3 起高 4 位 = 来源类型 0x4=package / 0x5=data） |
| 之后 | 名字区（`0x80` 槽可用 0x2C… 起；`0x200` 槽从 0x2C 起可用 0x1D4 字节） |

- 空槽**整段全 0**。
- 删除条目 = 该槽清零；新增 = 占用一个空槽（不够则抬高 `tablecount`）。
- 实际槽位数 = `max(tablecount, 最大槽位 + 1)`。
- `res` 内 `rtbl` 条目的写法：名字区（`ElementName` 前 4 项）之后紧跟一段**「槽位 bit 表」**
  —— 每槽 1 bit（1 = 该槽有引用），长度 = `ceil(槽位数 ÷ 8)`、位序 LSB first，
  整块补齐到 `al16(名字区 + bit表 + 1)`。

| 版本 | rtbl 槽位 `SlotSize` |
|---|---|
| Version 0 / 1 | **无此分区**（组 7 条目数恒 0） |
| Version 2（GE2 体験版） | 0x80 |
| Version 3（GE2 正式版） | 0x80 |
| Version 3 变体（GE2RB / 美版，v2 与 ps4 布局） | **0x200** |

---

## 3. Version 0：GOD EATER（PSP）

### 3.1 整体布局

```
+0x00  头部（0x10 字节）
+0x10  组表  8 槽 × 8 字节（偏移 u32 + 条目数 u32）—— 本版本 GroupsCount = 7
+0x50  TOC   Σ(各组条目数) × 32 字节
+0x50+TOC  body：按 TOC 顺序逐条 [16 对齐数据][16 对齐名字区]
           末尾 16 字节对齐补 0
```

### 3.2 头部字段

| 偏移 | 大小 | 字段 | 说明 |
|---|---|---|---|
| `+0x00` | 4 | `magic` | ASCII `Pres` |
| `+0x04` | 4 | `groupOffset` | 组表偏移，实测 **0x10** |
| `+0x08` | 1 | `GroupsCount` | **组数**（组表条目数），实测 **7**（组 0–6，尚无 `rtbl`） |
| `+0x09` | 1 | `Version` | **= 0** |
| `+0x0A` | 2 | `Package` | u16，原样保留（实测 `0x7C6E`） |
| `+0x0C` | 4 | `CeckSum << 2 \| Sign` | 高 30 位校验和，**低 2 位 = Sign** |

> 注：`+0x08` 起的 4 字节整体是一个 u32（实测 `0x7C6E0308`）= `GroupsCount`(1) + `Version`(1) + `Package`(2)。

### 3.3 Sign 与校验和

- **Sign**（`+0x0C` 低 2 位）**只有两个取值：`3` 与 `1`**：
  - `Sign = 3`：本 `res` **正常引用 `package.rdp`**（绝大多数包）。
  - `Sign = 1`：本 `res` **不引用任何 `rdp`**。此时 `prx`(组 1) 与 `refer`(组 4) 的条目
    **退化为「逻辑路径声明」** —— `offset` 变成文件内偏移、`size` 恒为 0、数据是 `\0` 结尾的文本，
    与组 2/5/6 一样排在 body 里。
    实测例子：`MS_001N_tora.res` 的 `prx[0] = "creature_vajra.bin"`；
    `NP_001_AP001.res` 的 `refer[0] = "data/GameData/…/NP_001_AP001.scr"`。
    **★ 该状态只在 Version 0 的 `res` 包上出现过；Version 1 起不再存在。**
  - `Sign = 0`：**不存在**（实测从未出现）。
  - 实测本代：`GE1` / `GEB` 的 `system.res` 低 2 位均为 **3**。
- **CeckSum**（`+0x0C` 高 30 位）为**自定义滚动校验**：

  ```
  t = 0; c = 0
  for b in 整个文件:
      c = b ^ t
      t = b + c
  CeckSum = c
  回填: raw0c = (CeckSum << 2) | (Sign & 3)
  ```

  （计算前先把 `+0x0C` 原地改成 `raw & 3`，即只留 Sign 位。）

### 3.4 数据归属

| 组 | 位置 | 说明 |
|---|---|---|
| 2 `local` / 5 `table` / 6 `string` | `res` 包内 body | — |
| 0 `res` / 1 `prx` / 4 `refer` | `package.rdp` | TOC 记块号 |
| 7 `rtbl` | — | **本版本不存在**（条目数恒 0） |

> **Version 0 的 GE 没有任何压缩** —— 工具链里没有压缩判定代码，`Compression` 恒为 `false`。

---

## 4. Version 0：GOD EATER BURST（PSP）

### 4.1 与 GOD EATER 的差异

**GEB 的 `Version` 仍是 0** —— 容器布局、头部、组表、TOC、名字区**逐字节相同**（同属 v1 布局），
唯一变化是**引入了 gzip 压缩段**：

- 判定：段头 3 字节 `1F 8B 08`。
- 复现参数：**gzip 包装**、`level 9`、`memLevel 9`，且头字段固定为
  `XFL = 0x02`、**`OS = 0x03`**、`MTIME = 0`。
  （注意 `memLevel 9` 与默认值 8 不同；`OS = 0x03` 是 Unix，.NET 默认会写 0x0A，必须手工改写头。）

> 也就是说：**GE → GEB 只是「同一个 Version 0 包多了一种压缩方式」，`res` 结构不变。**

---

## 5. Version 0 的 DLC（`.edat` 数据包）

### 5.1 与本体同构

GE1 与 GEB 的 DLC 各自是一对文件，**格式与本体完全同构**（同样的 v1 布局、同样的 gzip / 无压缩），
工具链里走的是**与 `-x` / `-c` 完全同一条代码路径**。差别只有以下四点：

| # | 项 | 本体 | DLC |
|---|---|---|---|
| ① | 默认文件名 | `system.res` / `package.rdp` | 哈希名 `.edat`（见 §5.3） |
| ② | **rdp 编址单位** | 0x8000 | **0x10**（`RdpShift = 4`） |
| ③ | 目录命名 | `1.out/system/`、`1.out/list/` | `1.out/system_update/`、`1.out/list_update/` |
| ④ | 产物目录（打包） | `4.pack/` | `5.dlcpack/` |

### 5.2 为什么 DLC 的块是 0x10

DLC 大包是**多个任务 `res` 按 16 字节块紧密拼接**而成的：
实测 75 个任务 `res` **75/75 命中 `Pres` 魔数**，首尾相接、**无空隙**。
因此寻址单位必须是 16 字节（0x10）而不是本体的 0x8000，否则相邻任务会被算作同一块。

> GEB 侧还有一个便于理解的点：**DLC 大包开头的 0x10 字节头，本身就是「块 0」**
> ——由 `offset = 0` 的 `version.dat` 条目承载。所以「块号 << 4 = 文件字节偏移」这条规则
> 从文件第 0 字节起就成立，与本体寻址规则完全一致。

### 5.3 文件名（实测）

| 工具 | DLC 的 `res` | DLC 的 `rdp` |
|---|---|---|
| GE1 | `70f92bc5db957c4862ad4a466bd9e117.edat` | `78981103ef31ce78eae569d1b58e00c3.edat` |
| GEB | `f3594808fb6a0f2123811d0d26b0af74.edat` | `4a92f06c65fd9bc1536e36e89a065222.edat` |

> 顶层 `res` 是**哈希名**，其数据目录 / `list` 目录因此**固定命名**为 `system_update` / `list_update`，
> 不能由文件名推导。

### 5.4 命令

```
<工具> -xdlc              解包 DLC 数据包 → 1.out/system_update/ + 1.out/list_update/
<工具> -cdlc              打包 DLC 数据包 → 5.dlcpack/
```

位置参数可覆盖默认文件名（第 1 个 = DLC 的 `res`，第 2 个 = DLC 的 `rdp`）。

---

## 6. Version 1：GE2 引继数据检查器

游戏为《GOD EATER BURST → GE2 引继数据检查器》（`NPJH00134`），**`Version` 由 0 升到 1**。

### 6.1 变化点

容器布局**仍沿用 Version 0 的 v1 布局**（头 0x10、组表 8×8 @0x10、TOC 32 B @0x50），
演进只有两点：

| # | 项 | Version 0 | Version 1 |
|---|---|---|---|
| ① | 压缩格式 | 无 / gzip | **`dflt`**（原始 deflate） |
| ② | **rdp 编址单位** | 0x8000 | **0x800**（`RdpShift = 11`） |

### 6.2 `dflt` 压缩

- 判定：段头 4 字节 `dflt`。
- 结构：`"dflt"` + **原始 deflate 流**（无 zlib / gzip 包装），`level 9 / memLevel 8`。
- 实测：`god_menu.res` 段 134,193 B，从偏移 4 起 raw deflate 解压得 242,912 B 的完整 PRES；
  deflate 流正好用尽整段 ⇒ **头仅 4 字节、无尾部字段**。

> `rtbl`（组 7）在本版本**仍未启用**（条目数恒 0），与 Version 0 一致。

---

## 7. Version 2：GE2 体験版 —— rtbl 分区引入

`Version` 升到 2，容器换到 **v2 布局**（PSP / PSV 同构）。

### 7.1 布局变化

```
+0x00  固定头 0x10
+0x10  新增区 0x10 —— 0x10 处 = res 包大小（pkgSize）
+0x20  组表  8 组 × 8 字节
+0x60  TOC   Σ × 32 字节
+0x60+TOC  body：按 TOC 顺序逐条 [16 对齐数据][16 对齐名字区]（16 对齐收尾）
+ = res 包大小  【Local 区】—— local(组 2) 的数据整体排在 res 包之后
```

两处关键演进：

1. **头部扩展**：固定头之后插入 0x10 字节新增区，`+0x10` 记录 **res 包大小**（不含 Local 数据）。
   组表随之后移到 `+0x20`，TOC 从 `+0x60` 起。
2. **Local 区外置**：`local`（组 2）的数据不再排在 body 里，而是整体移到 **`res` 包之后的文件尾**，
   起点 = `+0x10` 记录的 res 包大小。`table`(5) / `string`(6) 仍在 body 内。

### 7.2 ★ 引入 rtbl 分区

**`rtbl`（组 7）是本版本加入的新分区**，用于把「rdp 引用」从 `res` 里抽出来集中管理：

- 新增 `Rtbl.cs`，槽位大小 **`SlotSize = 0x80`**（详见 [2.6](#26-rtbl-分区version-2-起)）。
- `res` 的组 7 条目指向躺在 `rdp` 里的 `*.rtbl` 文件；条目名字区只写 `ElementName` **前 4 项**，
  其后紧跟「槽位 bit 表」（每槽 1 bit）。
- 重建时 `rtblSlots` 决定槽位数；位表按 `ceil(槽位数 ÷ 8)` 重算，多余位补 0。

> Version 0 / 1 里组 7 只是**空占位**；从本版本起它才是真正的分区。

### 7.3 头部字段（v2）

| 偏移 | 大小 | 字段 | 说明 |
|---|---|---|---|
| `+0x00` | 4 | `magic` | `Pres` |
| `+0x04` | 4 | `groupOffset` | 实测 **0x20** |
| `+0x08` | 1 | `GroupsCount` | **组数**，= **8**（本版本起含 `rtbl` 组 7） |
| `+0x09` | 1 | `Version` | **体験版 = 2** |
| `+0x0A` | 2 | `Package` | — |
| `+0x0C` | 4 | `Sign` | **仅低 2 位，恒为 3**（正常引用 `package.rdp`）；`CeckSum` 已废弃（不计算、不回填） |
| `+0x10` | 4 | `pkgSize` | res 包大小（不含 Local 数据） |
| `+0x14` | 4 | `baseRef.type/offset` | 本体 res 指针（详见 §9.3）。本体即自身时为 0 |
| `+0x18` | 4 | `baseRef.size` | 本体 res 字节数 |
| `+0x1C` | 4 | `langCount` | 语言分区数（**美版专用**，日版为 0） |

> ★ **CeckSum 在 Version 2 起被彻底废弃**：`+0x0C` 只剩低 2 位 `Sign`，高 30 位恒 0，
> 不再参与任何校验。

### 7.4 压缩

**体験版的 `res` 数据未压缩** —— 工具链中既无压缩判定代码，也无原生 zlib / blz 模块，
`Compression` 恒为 `false`，数据按明文直接读写。

> 本版本 `offset` 高 4 位**仍无类型编码**（类型编码是 Version 3 才引入的），
> 数据位置纯由组号决定。

---

## 8. Version 3：GE2 正式版 —— 奠定后续所有版本

`Version` 升到 3。容器布局与 Version 2 **完全相同**（同属 v2 布局），
演进全部发生在**语义层** —— 而这一层被**后续所有版本原样继承**：

### 8.1 数据来源类型（`offset` 高 4 位）

**Version 3 起**，`TOC.offset` 的**高 4 位**开始编码「这份数据从哪儿来」
（工具链判定：`bool typed = js.Version >= 3;`）：

| 高 4 位 | 名称 | 含义 |
|---|---|---|
| `0xC` | `TypeRes` | 数据在本 `res` 文件内，低 28 位 = **文件内绝对偏移**。宿主是 `system.res` 本身或 `data.rdp` 里的 `res`（`tbl` / `string` **必定**是 C） |
| `0xD` | `TypeResInPkg` | 语义与 `0xC` 相同，仅当宿主 `res` 本身躺在 `package.rdp` 里时使用 |
| `0x4` | `TypePackage` | 数据在 `package.rdp`，低 28 位 = 块号 |
| `0x5` | `TypeData` | 数据在 `data.rdp`，低 28 位 = 块号（双 rdp 变革后新增） |
| `0x6` | `TypePatch` | 数据在更新补丁 `patch.rdp`（见 §9） |
| `0x3` | `TypeExcludeRdp` | **Version 3 变体（GE2RB）新增**：不在任何 `rdp` 内（见 §10.1） |
| `0x0` | — | 无类型（`dummy` 条目 / Version ≤ 2 全体条目） |

约束：`table` / `string` 恒为 C；`local`(2) 可为 C / D / 4 / 5；`res` / 组 1（`prx` / `self`） / `refer` / `rtbl` 只出 4 / 5。

> **Version ≤ 2 没有这套编码**：`offset` 高 4 位恒 0，位置纯由组号决定，
> 工具链据此退回「按组判定」的旧逻辑。
>
> **★ 容量含义**：类型编码征用高 4 位后，单个包可用于编址的只剩 **低 28 位** ——
> **单包体一旦过大，低位就会溢出、侵占高 4 位**，破坏来源类型判定。
> 这正是 **PS4 侧必须回归 `data.rdp` 拆包**的原因（见 [11.3](#113-datardp-回归-ps4)）。

### 8.2 `data.rdp`

为让过大的包适配 UMD（并让数据可放进记忆卡加快加载），游戏把一部分数据分包到 `data.rdp`。
打包时 `package.rdp` 与 `data.rdp` **各自独立按块位升序整份重写**。

> **两个平台用它的动机不同**：PSP 侧是**被 UMD 总容量所迫**（见 [1.3](#13-迭代的驱动力容量与编址约束)②）；
> PS4 侧是**避免单包体过大、编址溢出侵占 `offset` 高 4 位的来源类型**（见 [11.3](#113-datardp-回归-ps4)）。

### 8.3 `TOC +0x1C` = 解压后大小

`+0x1C` 记录该条目的**明文（解压后）长度**。对压缩条目，游戏靠它预分配空间；
工具链打包时按**真实写入长度**回填（而不是照抄 `list` 里的旧值），
因为编辑过的压缩文件明文长度会变。

> 注意：`table`(5) / `string`(6) / `rtbl`(7) 的 `+0x1C` **恒为 0** —— 这几类数据本来就在
> `res` 包内、游戏不压缩它们。

### 8.4 `blz2` 压缩

见 [附录 A](#15-附录-a压缩段格式速查)。

---

## 9. Version 3 的 DLC（`SYSTEM_UPDATE.EDAT` + `PATCH.EDAT`）

### 9.1 两份文件

GE2 的更新补丁是**一对 `.edat`**：

| 文件 | 角色 |
|---|---|
| `SYSTEM_UPDATE.EDAT` | 当作 `system_update.res` —— 更新后的 `res` 索引 |
| `PATCH.EDAT` | 当作 `patch.rdp` —— 新增 / 替换的文件数据（`offset` 高 4 位 = **6**） |

游戏启动时先读 `SYSTEM_UPDATE.EDAT` 拿到更新后的 `res` 索引，再按类型 6 到 `PATCH.EDAT` 取数据。

> ★ **前置限制**：打包 DLC 前，本体目录的 `1.out/list` 下必须已有 `system.json` 与 `package.json`
> （即**先跑过一次 `-x`**）—— 补丁里「跳过」的条目要靠本体的 `list` 才能备注出路径。

### 9.2 跳过规则

- `-xdlc` 时，全群组的 **4 / 5 标志文件不解包**（数据在本体 `package.rdp` / `data.rdp` 上，
  不属于本次更新），但会按 `(Type, 块号)` 备注出本体里的 `ExportPath`。
- 带本体指针的 `res`：其 `local`(2) 的 C/D 条目索引**本体 `res`**，不在补丁内 ⇒ 不解包，
  但备注出 `ExportPath`；而 `table`(5) / `string`(6) 的 C/D **仍在自己 body 里**，正常解包。

### 9.3 本体 res 指针（`+0x14` / `+0x18`）

补丁 `res` 头部多出的 8 字节：

| 偏移 | 字段 |
|---|---|
| `+0x14` | u32：高 4 位 = 来源类型（4 / 5 / 6）、低 28 位 = **本体 `res` 的块号** |
| `+0x18` | u32：本体 `res` 的字节数 |

全 0 表示「本体即自身」。有值时：该 `res` 的 **`local`(组 2) 的 C/D 条目不在自身文件内**，
而是索引到本体 `res` 的对应偏移（实测补丁 `god_menu.res` 的 D 条目偏移 `0x1DD00 ~ 0x264E0`
与本体完全一致，且**超出补丁自身的 `pkgSize` = 0x242C0** ⇒ 数据只能在本体里）。

工具链在 `list` 里记录为 `BaseType` / `BaseOffset` / `BaseSize`，并到本体 `package.json` / `data.json`
匹配出 `BaseExportPath`（如 `system/god_menu.res`），打包 DLC 时据此定位本体 `res` 的 `list`。

### 9.4 打包（`-cdlc`）与基准 list

```
<工具> -cdlc [o] [x]
```

- 逆 `-xdlc`：重打包 `PATCH.EDAT` + 重建 `SYSTEM_UPDATE.EDAT` → `5.dlcpack/`。
- **基准 list `x`**：决定 4 / 5 / C / D 与 `+0x14` / `+0x18` 的**真值来源**。
  - `x=1` → `1.out/list`（本体**未**重打包，块位与 `res` 内偏移即原版）
  - `x=2` → `4.pack/list`（本体**已** `-c` 重打包，块位与 `res` 内偏移都会变 —— **本体改过就必须用 2**）

  `4.pack/list` 是 `-c` 打包完成后**自动对刚打出的产物再跑一遍 list-only 解包**生成的
  （只遍历、只写 `list`，不落数据）；其中每个 `res` 的 `exportPath` 与 `1.out/list` 完全一致，
  可按 `exportPath` 逐条对应。
- 带本体指针的 `res`：`+0x14` / `+0x18` 与 4 / 5 / C / D 条目的偏移 / 大小 / 解压大小
  一律按基准 `list` 修正，且**这些字节不写进 DLC `res`**（数据本来就在本体）。

---

## 10. Version 3 变体 A：GE2RB / RESURRECTION（PSV）

**PSV 版仍是 `Version 3` 的包**，容器布局与 GE2 正式版**完全一致**
（头 0x20、组表 8×8 @0x20、TOC 32 B @0x60），只是叠了几项平台侧演进：

| 变化 | 说明 |
|---|---|
| 压缩改 **`blz4`** | 取代 `blz2`（zlib 包装 vs 原始 deflate，不可混用） |
| 新增类型 **`0x3`** `TypeExcludeRdp` | 「**EXCLUDE_RDP**」：文件**不在任何 `rdp` 内**，游戏按 `ElementName[4]` 的 `PATH=…` 从本地路径取。实测 PSV `god_m002_Movie.res` 组 4 `refer` 共 19 条：类型位 = 3、低 28 位 = 0，而 `+0x04` 字节数与 `+0x1C` 解压大小**照常保留真值**（与 `self` 三项全 0 不同） |
| 组 1 改名 **`self`** | `god_m002_Movie.res` 的 `G1[0] = ['god_m002_Movie','self','']`，TOC 的 `offset` / `size` / `+0x1C` **三项恒 0**。**改名依据**：PSV 平台的外部依赖模块后缀是 `.self` / `.suprx`（PSP / PS4 那套 `.prx` 不再使用），噬神者系列在 PSV 上取 `self`（见 [2.1](#21-组group)） |
| **rtbl 槽位放大到 0x200** | 组 7 引用的 `*.rtbl` 从「每 0x80 一槽」变为「每 **0x200** 一槽」（GE2 / 体験版时代是 0x80） |
| **取消 `data.rdp`** | 不再于 PSP 平台发布 ⇒ **取消第二数据池**，但运行程序**保留 `data.rdp` 接口**（见 [8.2](#82-datardp)） |

---

## 11. Version 3 变体 B：GE2RB / RESURRECTION（PS4）

**PS4 版的 `Version` 同样是 3，语义层与 PSV 版一模一样**（4/5/C/D/3 类型编码、`data.rdp`、
本体指针、rtbl）。之所以单列一章，是因为 **PS4 的架构原因让容器换成了新的 `ps4` 布局**
—— 它本质上是**「Version 3 的包，套了一层 64 位对齐的壳」**。

### 11.1 新布局

```
+0x00  头部（0x30 字节）
+0x30  组表  8 组 × 16 字节（偏移 @+0x0，条目数 @+0x8）
+0xB0  TOC   Σ × 64 字节（每个 4 字节值独占 8 字节槽）
+0xB0+TOC  body + Local 区（同 v2 规则）
```

头部字段（ps4）：

| 偏移 | 字段 | 说明 |
|---|---|---|
| `+0x00` | `magic` | `Pres` |
| `+0x08` | `groupOffset` | 实测 **0x30**（注意：**不在** `+0x04`） |
| `+0x10` | `GroupsCount`(1) / `Version`(1) / `Package`(2) | 三者**挤在 `+0x10` 开始的 8 字节槽里**（`GroupsCount` = **8**、`Version` 在 `+0x11`） |
| `+0x14` | `Sign` | 仅低 2 位，**恒为 3** |
| `+0x18` | `pkgSize` | res 包大小 |
| `+0x20` | `baseRef.type/offset` | 本体 res 指针 |
| `+0x28` | `baseRef.size` | 本体 res 字节数 |

### 11.2 与 PSV 的差异（同版本号下的布局对照）

| 项 | PSV（v2 布局） | PS4（ps4 布局） |
|---|---|---|
| 头部 / 组表 / TOC | 0x20 / 8×8 / 32 B | **0x30 / 8×16 / 64 B** |
| `Version` 字段位置 | `+0x09` | **`+0x11`** |
| TOC `decSize` 位置 | `+0x1C` | **`+0x34`** |
| 名字指针表步长 | 4 B | **8 B** |
| 组 1 名称 | `self` | **`prx`**（实测 PS4 `god_menu.res` 的 `G1 = ['system','prx','']`） |
| rtbl 槽位 | 0x200 | 0x200（字段同样是 8 字节槽存 4 字节值） |

> **Version 号相同（= 3），布局族不同** —— 这正是「版本号描述语义、布局族描述排列」的最好例子。
>
> 组 1 名称同样只跟平台走：PSV 的外部依赖模块后缀是 `.self` / `.suprx` ⇒ 叫 `self`；
> PS4 回到 `.prx` ⇒ 又叫回 `prx`。**与布局族、`Version` 号均无因果关系**。

### 11.3 `data.rdp` 回归 PS4

**回归的原因与文件系统无关**（PS4 用 exFAT，并没有容量瓶颈），而是**为解决「单包体过大」**：

- Version ≥ 3 起，`TOC.offset` 的**高 4 位被征用为「来源类型」**（`4` / `5` / `6` / `C` / `D` / `3`，
  见 [8.1](#81-数据来源类型offset-高-4-位)），**只有低 28 位**能表达块号或文件内偏移。
- **单一包体一旦过大，低位就会溢出并侵占高 4 位** ⇒ **破坏数据来源类型的判定**。
  因此 **PS4 重新采用 `data.rdp`**，把数据拆到第二个包池，让每个包的编址都留在低 28 位范围内。
- 背景因素：PS4 的 64 位布局与**高清化贴图**让包体进一步变大，加剧了这一压力。

> **例外：OFF SHOT**（`CUSA03118`–`03123` / `PCSG00720`–`00725`）。它是拍照衍生作，
> **资源很少**，包体**远不会大到侵占高 4 位**，因此**没有 `data.rdp`** ——
> 但运行程序**同样保留了 `data.rdp` 接口**。

---

## 12. Version 3 变体 C：美版多语言分区

PSV 美版（`PSVGodEaterUSTools` 处理的 `PCSB/PCSE` 包）**仍是 `Version 3` 的包**，
在 v2 布局之上叠加**多语言分区**。

### 12.1 头部变化

| 偏移 | 日版 | 美版 |
|---|---|---|
| `+0x04` | 组表偏移（如 0x20） | **废弃，恒 0** |
| `+0x1C` | 0（未使用） | **语言分区数 L** |

`L` 取值：

- `L = 0`：无该字段（日版）。
- `L = 1`：单分区（美版，如 `2.res`）—— **没有分区表**，组表固定在 `0x20`，
  头部仍是 `+0x1C = 1`、`+0x04 = 0`。
- `L ≥ 2`：分区表在 `0x20`。

### 12.2 分区表

`L ≥ 2` 时，`+0x20` 起是 `L × 8` 字节的分区表，每项：

| 大小 | 字段 |
|---|---|
| u32 | 该分区的**组表绝对偏移** |
| u32 | 该分区的**总字节长** |

分区顺序固定为 **英 / 法 / 意 / 德 / 西 / 俄**（工具链用 `common_text.tr2` 解压译文实证）。

```
[0x00] 头部 0x20
[0x20] 分区表 L × 8
[0x50] 分区体 0 —— 依次首尾相接
       …
       分区体 L-1
[pkgSize 起]  六分区共享的 local 内容池
```

实测：US `system.res` 6 分区 @0x50 / 0x58E0 / …；`1.res` 6 分区 @0x50 / 0x1590 / …；
`2.res` 单分区 @0x20。

### 12.3 分区体

**分区体 = 普通 v2 `res`「组表之后」的同构结构**：

```
分区起点  组表 8 × 8
          TOC（16 对齐）
          body（16 对齐）
```

关键点：

- 分区内的 `offset` / `nameOffset` 全是**文件绝对偏移**（**不要**加分区基址）。
- 分区长 = 64（组表）+ TOC（16 对齐）+ body（16 对齐）。
- 每个分区各含 207 条 TOC；每分区各有**自己的名字区**
  （`nameOffset` 相对分区 0 平移 `+0x5890 × i`，实测）。
- TOC 字段布局、`blz4`、`local` 区位置**全部与日版一致**，无需新增压缩 / 字段分支。

### 12.4 内容池

`local`(G2) 的 **`Type = 0xC` 条目不进分区 body**，而是全部集中到 **`pkgSize` 起**的
**全局内容池**（16 对齐、顺序排列），由六个分区**共享**。

复用规则（写入侧）：

1. `p > 0 && 该槽位字节 == 分区 0 同槽位字节` ⇒ 复用分区 0 的偏移；
2. 否则按 `(ExportPath, p)` 查缓存，命中且字节相同 ⇒ 复用；
3. 否则写入。写入顺序按槽位递增；**连续差异槽位按「语言优先、段内槽位序」**。

### 12.5 共享 vs 独立（实测分布）

| 类别 | 每分区一份独立副本？ |
|---|---|
| `res` / `self` / `refer` 的偏移 + G2 的 56 条 `local` | **六分区逐条相同**（一包内多分区调同一文件） |
| `table`(46 条) / `string`(3 条) + G2 的 6 条 `local`（`common_text.tr2`、`network_text.tr2`、`Chat_Parts1.gxt`、`Chat_TrgInfo.gxt`、`Lobby_Window_5.gxt`、`Title_Menu_Window.gxt`） | **每分区独立副本** |
| 名字区 | **每分区一份** |

每分区 207 条 TOC 的类型分布：`0×7 / 3×84 / 4×4 / 6×1 / C×111`。
G2 还含 6 条 `type = 0` 的占位（`offset = 0` / `size = 0`，但 `decSize` 有值）。

### 12.6 已知残差

官方包还存在**「资源实例身份」级别的复用**：同名同内容的资源也可能各写一份
（例：`god_m001_Title` 的 `Trg_Select.gxt` 5 个槽位中 4 个合并、第 5 个单写）。
该身份**不存在于 `res` 结构里**，因此**全重压模式无法对深层子 `res` 做到 byte-identical**；
要 byte-identical 必须走「未修改 `res` 直通 / 复用原始段」。

---

## 13. Version 3 的 DLC（裸 `system_update.res` + `patch.rdp`）

### 13.1 与 GE2 的差异：去掉 EDAT 封装

| 版本 | 补丁 `res` | 补丁 `rdp` |
|---|---|---|
| Version 3（GE2 / PSP） | `SYSTEM_UPDATE.EDAT` | `PATCH.EDAT` |
| **Version 3 变体（GE2RB / 美版）** | **`system_update.res`** | **`patch.rdp`** |

PSV 及之后是**裸 `res` + `rdp`**，不再套 EDAT 容器。`rdp` 的编址单位与本体相同（0x800），
类型 6、本体指针、基准 `list` 等机制完全沿用 §9。

### 13.2 命令

```
<工具> -xdlc              解包更新补丁
<工具> -cdlc [o] [x]      打包更新补丁 → 5.dlcpack/
```

产物：`5.dlcpack/system_update.res` + `5.dlcpack/patch.rdp` + `5.dlcpack/list`。

---

## 14. 迭代方式总表

### 14.1 时间线（一句话版）

```
Version 0  GE1 (PSP)         v1 布局，无压缩，rdp 编址 0x8000，无 rtbl
           ↓                 DLC：裸 .edat 对，编址降到 0x10
Version 0  GEB (PSP)         同一个 Version 0 包 + gzip（res 结构一字不变）
           ↓                 DLC：裸 .edat 对，编址 0x10
Version 1  Checker (PSP)     v1 布局，压缩改 dflt，rdp 编址降到 0x800
           ↓
Version 2  GE2 体験版        换 v2 布局（头扩到 0x20 + local 移文件尾 + CeckSum 废弃）
           ↓                 ★ 首次引入 rtbl（组 7）分区，槽 0x80
Version 3  GE2 正式版        ★ 奠定后续所有版本：offset 高 4 位类型编码（C/D/4/5）、
           ↓                 data.rdp、decSize@+0x1C、blz2；
           ↓                 DLC 改为 SYSTEM_UPDATE.EDAT + PATCH.EDAT + 本体 res 指针
Version 3  GE2RB/RES (PSV)   仍是 v3：压缩改 blz4、新增 EXCLUDE_RDP(3)、组 1 改 self（PSV 后缀 .self/.suprx）、rtbl 槽 0x200
           ↓                 DLC：改为裸 system_update.res + patch.rdp
Version 3  GE2RB/RES (PS4)   仍是 v3，但因架构换 ps4 布局：头 0x30、组表 8×16、TOC 64 B、
           ↓                 decSize@+0x34、指针步长 8 B、组 1 回 prx（PS4 后缀 .prx）
Version 3  美版 (PSV)        仍是 v3：+0x1C = 语言分区数，分区表 + 六分区共享内容池
```

> **迭代的驱动力**：上述每一次 `res` 迭代与 rdp 编址降级，根源都是 **UMD 单碟 1.8 GB** 的容量约束
> （详见 [1.3](#13-迭代的驱动力容量与编址约束)）：
> GE / GEB 因资产不多、镜像约 1.36 GB 尚有富余；GE2 要**继承 GEB 资产并追加新资产**，
> 沿用旧编址与旧容器就**装不进一张 UMD** ⇒ 促成 **Ver 1 / Ver 2 / Ver 3** 的容器迭代、0x8000 → 0x800、
> 以及 `blz2` 的出现。

### 14.2 逐项变化对照（以 Version 为主线）

| 项目 | Ver 0 (GE1) | Ver 0 (GEB) | Ver 1 (Checker) | Ver 2 (GE2 体験版) | Ver 3 (GE2) | Ver 3 (GE2RB PSV) | Ver 3 (GE2RB PS4) | Ver 3 (美版) |
|---|---|---|---|---|---|---|---|---|
| 布局族 | v1 | v1 | v1 | v2 | v2 | v2 | **ps4** | v2 |
| 组表偏移 | 0x10 | 0x10 | 0x10 | 0x20 | 0x20 | 0x20 | **0x30** | 0x20（表内）/ 0 |
| 组表项宽 | 8 B | 8 B | 8 B | 8 B | 8 B | 8 B | **16 B** | 8 B |
| TOC 起点 | 0x50 | 0x50 | 0x50 | 0x60 | 0x60 | 0x60 | **0xB0** | 0x60 / 分区 |
| TOC 条目 | 32 B | 32 B | 32 B | 32 B | 32 B | 32 B | **64 B** | 32 B |
| `Version` 字段位置 | `+0x09` | `+0x09` | `+0x09` | `+0x09` | `+0x09` | `+0x09` | **`+0x11`** | `+0x09` |
| `decSize` | — | — | — | — | `+0x1C` | `+0x1C` | **`+0x34`** | `+0x1C` |
| 名字指针步长 | 4 B | 4 B | 4 B | 4 B | 4 B | 4 B | **8 B** | 4 B |
| 校验 | CeckSum | CeckSum | CeckSum | **无** | 无 | 无 | 无 | 无 |
| `pkgSize` 字段 | — | — | — | `+0x10` | `+0x10` | `+0x10` | `+0x18` | `+0x10` |
| local 位置 | body | body | body | **文件尾** | 文件尾 | 文件尾 | 文件尾 | 全局池 |
| 类型编码 | — | — | — | — | **C/D/4/5** | + **3** | C/D/4/5/3 | + 分区维度 |
| 第二 rdp | — | — | — | — | **data.rdp** | data.rdp | data.rdp | data.rdp |
| **rtbl 分区** | **无** | **无** | **无** | **引入** | 有 | 有 | 有 | 有 |
| rtbl 槽位 | — | — | — | 0x80 | 0x80 | **0x200** | 0x200 | 0x200 |
| rdp 编址 | 0x8000 | 0x8000 | 0x800 | 0x800 | 0x800 | 0x800 | 0x800 | 0x800 |
| 压缩 | 无 | **gzip** | **dflt** | 无 | **blz2** | **blz4** | blz4 | blz4 |
| 组 1 名称 | prx | prx | prx | prx | prx | **self** | **prx** | self |
| DLC 形态 | .edat（0x10 块） | .edat（0x10 块） | 无 | 无 | SYSTEM_UPDATE.EDAT + PATCH.EDAT | system_update.res + patch.rdp | 同 PSV | 同 PSV |

> **关于「组 1 名称」一行**：`prx` ↔ `self` 的切换**不是版本演进，而是平台外部依赖模块后缀的直接映射**
> —— PSP / PS4 用 `.prx`，PSV 用 `.self` / `.suprx`。所以 PSP 的三代（GE1 / GEB / GE2）恒为 `prx`、
> PSV 的 GE2RB / RES / 美版取 `self`，而**同代**的 PS4 版又回到 `prx`（见 [2.1](#21-组group)）。

### 14.3 DLC 的迭代（按 Version 归档）

| Version | DLC 形态 | 块 / 编址 | 关键新增机制 |
|---|---|---|---|
| **Version 0**（GE1 / GEB） | 一对**裸 `.edat`**，与本体同构 | **0x10**（16 字节块，多任务紧密拼接） | 无 —— 只是同一格式换文件名与编址单位 |
| **Version 3**（GE2 / PSP） | `SYSTEM_UPDATE.EDAT` + `PATCH.EDAT` | 0x800 | 类型 6（patch.rdp）、**本体 res 指针**（`+0x14`/`+0x18`）、跳过规则、**基准 list** |
| **Version 3 变体**（GE2RB / 美版） | **裸** `system_update.res` + `patch.rdp` | 0x800 | 去掉 EDAT 封装；机制沿用 GE2 |

---

## 15. 附录 A：压缩段格式速查

| 格式 | 魔数 | 段头 | 算法 | 分块 | 写入顺序 | 使用版本 |
|---|---|---|---|---|---|---|
| 明文 | — | — | — | — | — | Ver 0 的 GE1；Ver 2 |
| **gzip** | `1F 8B 08` | 10 B | gzip 包装，L9 / **memLevel 9** | 单流 | — | Ver 0 的 GOD EATER BURST |
| **dflt** | `dflt` | 4 B | **raw deflate**，L9 / M8 | 单流 | — | Ver 1 |
| **blz2** | `blz2` | 4 B | **raw deflate**，L9 / M8 | 0x10000，**尾 → 头切**（头部为余数） | 轮转 `[末, 首, 中…]` | Ver 3（GE2 正式版） |
| **blz4** | `blz4` | 32 B | **zlib 包装**（wbits=15），L9 / M8 | 0x10000，**头 → 尾切**（首块为余数） | 轮转 `[末, 首, 中…]`，解压拼接序 `[1..N-1, 0]` | Ver 3 变体（GE2RB / 美版） |

**blz4 段头（32 B）**

| 偏移 | 大小 | 字段 |
|---|---|---|
| `+0x00` | 4 | 魔数 `blz4` |
| `+0x04` | 4 | 解压后总大小 `decompSize`（i32 LE） |
| `+0x08` | 8 | 保留（全 0） |
| `+0x10` | 16 | 原始数据 **MD5** |
| `+0x20` | … | 重复 `{ u16 压缩块长度, zlib 块 }` 直到段尾 |

**复现要求**

- 只有**官方 C 版 zlib** 能保证字节一致。.NET `DeflateStream` / `ZLibStream` 等价于 `memLevel = 8`
  且**不可配置**，gzip 头 `OS` 会写成 `0x0A`（`0x0A` ≠ 官方 `0x03`）。
- 工具链因此统一走 P/Invoke 调用 `zlib1.dll`（zlib 1.3.2 MSVC x64）。
- zlib 版本无关：1.3 / 1.3.1 / 1.3.2 输出逐字节一致。
- `gzip` 的 `XFL = 0x02`、`OS = 0x03`、`MTIME = 0` 需手工改写头。

---

## 16. 附录 B：Version 与工具 / 作品对照

| Version | 布局族 | 工具目录 | 平台 | 代表作品（title id） | 压缩 | rdp 编址 | rtbl 槽 | `GroupsCount` |
|---|---|---|---|---|---|---|---|---|
| 0 | v1 | `PSPGodEaterTools` | PSP | GOD EATER（`NPJH90069` / `ULJM91019` / `ULJS00237`） | 无 | 0x8000 | — | 7 |
| 0 | v1 | `PSPGodEaterBurstTools` | PSP | GOD EATER BURST（`NPJH90167` / `NPJH50352` / `ULJS00350` / `ULUS10563` / `ULES01519`） | gzip | 0x8000 | — | 7 |
| 1 | v1 | `PSPGodEaterCheckerTools` | PSP | GE2 引继数据检查器（`NPJH00134`） | dflt | 0x800 | — | 7 |
| 2 | v2 | `PSPGodEater2TrialTools` | PSP | GE2 体験版（`NPJH90338`） | 无 | 0x800 | 0x80 | 8 |
| 2 | v2 | `PSVGodEater2TrialTools` | PSV | GE2 体験版（`PCSG90094`） | 无 | 0x800 | 0x80 | 8 |
| 3 | v2 | `PSPGodEater2Tools` | PSP | GE2 序盤まるごと体験版 / GE2（`NPJH90342` / `NPJH50832`） | blz2 | 0x800 | 0x80 | 8 |
| 3 | v2 | `PSVGodEater2Tools` | PSV | GE2 序盤まるごと体験版 / GE2（`PCSG90106` / `PCSG00240`） | blz2 | 0x800 | 0x80 | 8 |
| 3 | v2 | `PSVGodEaterSeriesTools` | PSV | GE2RB / RESURRECTION / OFF SHOT（`PCSG90161` / `PCSG00532` / `PCSG90193` / `PCSG00719`–`00725` / `PCSH00144` / `PCSH00133` / `PCSH00212` / `PCSH00199`） | blz4 | 0x800 | 0x200 | 8 |
| 3 | **ps4** | `PS4GodEaterSeriesTools` | PS4 | GE2RB / RESURRECTION / OFF SHOT（`CUSA01787` / `CUSA01516` / `CUSA03592` / `CUSA03105` / `CUSA03118`–`03123` / `CUSA01838` / `CUSA01714` / `CUSA03703` / `CUSA03409`） | blz4 | 0x800 | 0x200 | 8 |
| 3 | v2 | `PSVGodEaterUSTools` | PSV | 美版 GE2RB / RESURRECTION（`PCSB00875` / `PCSE00789` / `PCSB00874` / `PCSE00801`） | blz4 | 0x800 | 0x200 | 8 |

> 补充：**Version 0 的 GE1 与 Version 2 的两个 `GE2 体験版` 不依赖 `zlib1.dll`**，
> 因为它们的 `res` 数据未压缩；其余 7 个工具各带一份 `zlib1.dll` 以保证压缩字节与游戏一致。
