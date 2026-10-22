# GodEaterTools

《GOD EATER》系列 PRES(`.res`) / `package.rdp` / `data.rdp` 容器解包打包工具集（.NET 8 / C#）。

每个子目录是一个独立工具，互不依赖，可单独构建。

---

## 中文

### 工具一览

| 目录 | 平台 | 对应作品 | 游戏ID | 压缩格式 |
|---|---|---|---|---|
| `PSPGodEaterTools` | PSP | GOD EATER | NPJH90069、ULJM91019、ULJS00237 | 无 |
| `PSPGodEaterBurstTools` | PSP | GOD EATER BURST | NPJH90167、NPJH50352、ULJS00350、ULUS10563、ULES01519 | gzip |
| `PSPGodEaterCheckerTools` | PSP | GOD EATER BURST GE2 引继数据检查器 | NPJH00134 | deflate |
| `PSPGodEater2TrialTools` | PSP | GOD EATER 2 体験版 | NPJH90338 | 无 |
| `PSVGodEater2TrialTools` | PSV | GOD EATER 2 体験版 | PCSG90094 | 无 |
| `PSPGodEater2Tools` | PSP | GOD EATER 2 序盤まるごと体験版 / GOD EATER 2 | NPJH90342、NPJH50832 | blz2 |
| `PSVGodEater2Tools` | PSV | GOD EATER 2 序盤まるごと体験版 / GOD EATER 2 | PCSG90106、PCSG00240 | blz2 |
| `PSVGodEaterSeriesTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION / OFF SHOT 系列 | PCSG90161、PCSG00532、PCSG90193、PCSG00719、PCSG00720、PCSG00721、PCSG00722、PCSG00723、PCSG00724、PCSG00725、PCSH00144、PCSH00133、PCSH00212、PCSH00199 | blz4 |
| `PS4GodEaterSeriesTools` | PS4 | 同上（PS4 版） | CUSA01787、CUSA01516、CUSA03592、CUSA03105、CUSA03118、CUSA03119、CUSA03120、CUSA03121、CUSA03122、CUSA03123、CUSA01838、CUSA01714、CUSA03703、CUSA03409 | blz4 |
| `PSVGodEaterUSTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION 美版（多语言分区） | PCSB00875、PCSE00789、PCSB00874、PCSE00801 | blz4 |

各工具支持的 title id 详见其 `-h` 输出的「适用游戏」段。

### 构建

需要 .NET 8 SDK：

```bash
dotnet build PSPGodEaterTools -c Release
# 产物：PSPGodEaterTools/bin/Release/net8.0/
```

### 运行依赖 `zlib1.dll`

除 GE1 与两个体验版工具（无压缩）外，其余 7 个工具通过 P/Invoke 调用原生 zlib 以保证压缩字节与游戏一致，因此各自目录下带一份 `zlib1.dll`（zlib 1.3.2, MSVC x64）。该文件已随仓库提供，无需额外安装。

### 用法

```text
<工具> -x                  解包当前目录的 system.res + package.rdp
<工具> -c                  按 list 打包 → 4.pack/
<工具> -xdlc               解包 DLC 数据包
<工具> -cdlc               打包 DLC 数据包 → 5.dlcpack/
<工具> -h                  显示帮助
-d, --dir <路径>           工作目录（默认当前目录）
```

所有中间与最终产物都生成在工具的工作目录下；**工具永不覆盖原始文件**。

### 工作目录结构

```text
1.out/                    解包产物（只读基准）
  list/system.json        顶层 res 的 list
  list/package.json       package.rdp 块表（汇总所有层级）
  list/data.json          data.rdp 块表（汇总所有层级）
  list/system/xxx.json    递归子 res 的 list
  list_update/            -xdlc：各 res 的 list + patch.json 块表
  SYSTEM_UPDATE/          -xdlc：补丁数据
  system/                 顶层 res 的数据文件
  system/xxx/             子 res 解包出的数据（rdp 侧子 res 与 local 区内嵌 res 均递归解开）
2.new/                    要打进包的东西（同名优先覆盖 1.out）
  list/                   编辑过的 list
  list_update/            编辑过的更新补丁 list
  system/                 替换的 / 新增的文件
  SYSTEM_UPDATE/          替换的 / 新增的更新补丁文件
3.temp/                   打包中间产物（现场重打包的 res、压缩后的文件）
4.pack/                   最终产物 package.rdp + data.rdp + system.res
  list/                   打包后自动生成的新 list（记录新偏移 / 大小 / 解压大小，供打包 DLC 用）
5.dlcpack/                -cdlc 产物：patch.rdp + system_update.res
  list/                   DLC 打包后的新 list
```

典型流程：先 `-x` 解包到 `1.out/`，只把要改动的文件（或整个 list）按**相同相对路径**放进 `2.new/`，再 `-c` 打包。
数据与 list 的解析规则一律是 **`2.new/` 优先，其次 `1.out/`**，因此 `1.out/` 可作为不可变基准保留。

### 打包规则

- **默认（不加 `o`）= 全部明文重压**：每个条目从 `2.new/`（无则 `1.out/`）取明文数据，用原生 zlib 现场重新压缩。
  原生 zlib 与游戏压缩字节级一致，未编辑条目重压后长度与原生长度相同。
- **`-c o` / `-cdlc o [1|2]` = 复用原始压缩段**：未编辑条目直接沿用原 rdp 中的压缩字节（旧行为）。
  要让官方包零改动往返做到 byte-identical（res 与 rdp 均逐字节一致），**必须**使用此模式。
- **`-cdlc x` 基准 list（默认 `2`；`o` 与 `1|2` 顺序不限，`-cdlc o 1` 等价 `-cdlc 1 o`）**：决定 DLC 中
  4/5 类（本体 rdp）与 local 的 C/D 类（本体 res 内部）条目偏移 / 大小的真值来源：
  - `x=1` → `1.out/list`（本体未重打包，块位与偏移即原版）
  - `x=2` → `4.pack/list`（本体已 `-c` 重打包，块位与 res 内偏移都会变 —— **本体改过就必须用 2**）
- **顺移策略**：`package.rdp` 与 `data.rdp` 各自独立按块位升序整份重写。每条占 `max(新块数, 原块数)` ——
  变小不回收空间（补零）、位置保持稳定；真正变大的条目在本位就地扩展，其后条目整体后移。
- **递归重打包**：list 中本身是 res 的条目（rdp 侧子 res 与 local 区内嵌 res）会先用它自己的 list
  现场重建（产物写 `3.temp/`），再按原位置嵌回父 res —— 改内嵌 res 里的文件，父 res 会自动跟着更新。
- **rtbl 重打包**：`*.rtbl`（res 组 7 引用的 rdp 引用表，每 `0x200` 一槽）用固定槽位清单现场重建，
  逐槽回填该 res 在新 rdp 上的块号 / 大小。

### 文档

`docs/` 目录收录三份格式说明（Markdown）：

| 文件 | 内容 |
|---|---|
| `docs/res-容器格式说明.md` | `res` 容器结构：头部字段、组与 TOC、名字区、rdp 编址、版本谱系、更新补丁、DLC 三平台形态、美版多语言分区 |
| `docs/res-压缩段说明.md` | gzip / dflt / blz2 / blz4 四种压缩段的段结构、分块与写盘算法、解压流程与实测统计 |
| `docs/executable-text-illustrated.md` | 可执行文件侧的文本翻译：`EBOOT.BIN` / `eboot.bin` 与 `prx` / `sprx` / `suprx` / `self` 里 UTF-8 文本的排布、指针槽与指令内硬编码地址的定位和改写，含 PSP 专有的 `PATCH.EDAT` 大小校验 |

三份文档按 Wiki 页面惯例排版。待本仓库公开后，将转为仓库 Wiki 页面展示。

### 许可证

本项目以 **GNU General Public License v3.0（GPL-3.0）** 开源，版权归 © 2026 Qingyu510。你可以自由使用、修改与再分发（含商业用途），但**衍生作品必须同样以 GPL-3.0 授权并公开完整对应源码**，同时须保留版权声明与许可声明。全文见 [`LICENSE`](LICENSE)。

仓库内分发 `zlib1.dll`（zlib 许可），并在格式研究上参考了 MIT 许可的 GEBCS —— 两者均与 GPL-3.0 兼容，原始声明见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

> 本仓库只包含工具源码与格式说明文档，不含任何游戏资源。《GOD EATER》系列名称、商标及其资源版权归 BANDAI NAMCO Entertainment Inc. 所有。

---

## English

A collection of unpack/pack tools for the `PRES(.res)` / `package.rdp` / `data.rdp` containers used by the *GOD EATER* game series (.NET 8 / C#).

Each subdirectory is a standalone tool with no cross-project dependencies.

### Tools

| Directory | Platform | Titles | Game ID | Compression |
|---|---|---|---|---|
| `PSPGodEaterTools` | PSP | GOD EATER | NPJH90069, ULJM91019, ULJS00237 | none |
| `PSPGodEaterBurstTools` | PSP | GOD EATER BURST | NPJH90167, NPJH50352, ULJS00350, ULUS10563, ULES01519 | gzip |
| `PSPGodEaterCheckerTools` | PSP | GOD EATER BURST GE2 transfer-data checker | NPJH00134 | deflate |
| `PSPGodEater2TrialTools` | PSP | GOD EATER 2 体験版 (trial) | NPJH90338 | none |
| `PSVGodEater2TrialTools` | PSV | GOD EATER 2 体験版 (trial) | PCSG90094 | none |
| `PSPGodEater2Tools` | PSP | GOD EATER 2 序盤まるごと体験版 (prologue trial) / GOD EATER 2 | NPJH90342, NPJH50832 | blz2 |
| `PSVGodEater2Tools` | PSV | GOD EATER 2 序盤まるごと体験版 (prologue trial) / GOD EATER 2 | PCSG90106, PCSG00240 | blz2 |
| `PSVGodEaterSeriesTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION / OFF SHOT | PCSG90161, PCSG00532, PCSG90193, PCSG00719, PCSG00720, PCSG00721, PCSG00722, PCSG00723, PCSG00724, PCSG00725, PCSH00144, PCSH00133, PCSH00212, PCSH00199 | blz4 |
| `PS4GodEaterSeriesTools` | PS4 | same as above (PS4 builds) | CUSA01787, CUSA01516, CUSA03592, CUSA03105, CUSA03118, CUSA03119, CUSA03120, CUSA03121, CUSA03122, CUSA03123, CUSA01838, CUSA01714, CUSA03703, CUSA03409 | blz4 |
| `PSVGodEaterUSTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION US (multi-language partitions) | PCSB00875, PCSE00789, PCSB00874, PCSE00801 | blz4 |

Supported title ids are listed in each tool's `-h` output.

### Build

Requires the .NET 8 SDK:

```bash
dotnet build PSPGodEaterTools -c Release
# output: PSPGodEaterTools/bin/Release/net8.0/
```

### Runtime dependency `zlib1.dll`

All tools except the GE1 and the two trial tools (which store data uncompressed) call into native zlib via P/Invoke so that compressed output stays byte-identical to the games'. A copy of `zlib1.dll` (zlib 1.3.2, MSVC x64) is bundled in each of those 7 project directories — no extra setup needed.

### Usage

```text
<tool> -x                  unpack system.res + package.rdp in the working directory
<tool> -c                  repack according to the list → 4.pack/
<tool> -xdlc               unpack DLC data pack
<tool> -cdlc               repack DLC data pack → 5.dlcpack/
<tool> -h                  show help
-d, --dir <path>           working directory (default: current directory)
```

All intermediate and final output is written inside the tool's working directory. **Original files are never overwritten.**

### Working directory layout

```text
1.out/                    unpacked output (read-only baseline)
  list/system.json        top-level res list
  list/package.json       package.rdp block table (all levels)
  list/data.json          data.rdp block table (all levels)
  list/system/xxx.json    recursive child-res lists
  list_update/            -xdlc: per-res lists + patch.json block table
  SYSTEM_UPDATE/          -xdlc: patch data
  system/                 top-level res data files
  system/xxx/             child-res data (both rdp-side and local-embedded res are unpacked recursively)
2.new/                    files to be packed (same relative path overrides 1.out)
  list/                   edited lists
  list_update/            edited update-patch lists
  system/                 replacement / new files
  SYSTEM_UPDATE/          replacement / new update-patch files
3.temp/                   packing intermediates (re-packed res files, compressed files)
4.pack/                   final output: package.rdp + data.rdp + system.res
  list/                   auto-generated post-pack lists (new offsets/sizes, used for DLC packing)
5.dlcpack/                -cdlc output: patch.rdp + system_update.res
  list/                   post-pack lists for the DLC
```

Typical workflow: run `-x` to unpack into `1.out/`, copy only what you changed into `2.new/` (same relative paths), then run `-c`. Data and list resolution is always **`2.new/` first, `1.out/` second**, so `1.out/` can be kept as an immutable baseline.

### Packing rules

- **Default (no `o`) = full plaintext re-compression**: every entry is read from `2.new/` (falling back to `1.out/`) and re-compressed on the fly with native zlib. Because native zlib reproduces the games' compression byte-for-byte, re-compressed unedited entries keep their original length.
- **`-c o` / `-cdlc o [1|2]` = reuse original compressed segments**: unedited entries keep their original compressed bytes from the source rdp (legacy behaviour). This mode is **required** to get a byte-identical round-trip (both res and rdp) on untouched official packages.
- **`-cdlc x` baseline list (default `2`; `o` and `1|2` may appear in any order — `-cdlc o 1` equals `-cdlc 1 o`)**: selects the source of truth for the offsets/sizes of type 4/5 entries (main-body rdp) and local C/D entries (main-body res) referenced by the DLC:
  - `x=1` → `1.out/list` (main body not re-packed; block positions and offsets are the original ones)
  - `x=2` → `4.pack/list` (main body already re-packed, so block positions and in-res offsets have moved — **required whenever the main body was modified**)
- **Shift-on-write**: `package.rdp` and `data.rdp` are each rewritten in full, in ascending block order. Each entry occupies `max(new block count, original block count)` — shrinking does not reclaim space (zero-filled) so positions stay stable; only genuinely larger entries expand in place and push the following entries back.
- **Recursive re-packing**: any list entry whose data is itself a res (rdp-side child res as well as local-embedded res) is first rebuilt in place from its own list (output to `3.temp/`) and re-embedded at its original position inside the parent res — editing a file inside an embedded res updates the parent res automatically.
- **rtbl re-packing**: `*.rtbl` files (rdp reference tables referenced by res group 7, one slot per `0x200` bytes) are rebuilt from their fixed slot list, back-filling each slot with the entry's block number/size in the new rdp.

### Documentation

The `docs/` directory contains three format references (Markdown):

| File | Contents |
|---|---|
| `docs/res-容器格式说明.md` | `res` container layout: header fields, groups & TOC, name area, rdp addressing, version lineage, update patches, DLC forms on three platforms, US multi-language partitions |
| `docs/res-压缩段说明.md` | the four compressed segment formats (gzip / dflt / blz2 / blz4): segment structure, chunking & write-order algorithms, decompression flow, and measured statistics |
| `docs/executable-text-illustrated.md` | text translation on the executable side: how UTF-8 strings are laid out in `EBOOT.BIN` / `eboot.bin` and the `prx` / `sprx` / `suprx` / `self` modules, and how to locate and rewrite pointer slots and hard-coded addresses inside instructions — including the PSP-only `PATCH.EDAT` size check |

All three documents are laid out wiki-style. Once this repository is made public, they will be published as repository Wiki pages.

### License

Released under the **GNU General Public License v3.0 (GPL-3.0)**, © 2026 Qingyu510. You are free to use, modify and redistribute it, including commercially, but **derivative works must also be licensed under GPL-3.0 with complete corresponding source code made available**, and the copyright and license notices must be retained — see [`LICENSE`](LICENSE).

`zlib1.dll` (zlib license) is distributed with the tools, and this project references the MIT-licensed GEBCS — both are compatible with GPL-3.0; see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for the original notices.

> This repository contains tool source code and format documentation only, no game assets. *GOD EATER* and all related names, trademarks and assets are property of BANDAI NAMCO Entertainment Inc.
