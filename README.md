# GodEaterTools

《GOD EATER》系列 PRES(`.res`) / `package.rdp` / `data.rdp` 容器解包打包工具集（.NET 8 / C#）。

每个子目录是一个独立工具，互不依赖，可单独构建。

---

## 中文

### 工具一览

| 目录 | 平台 | 对应作品 | 压缩格式 |
|---|---|---|---|
| `PSPGodEaterTools` | PSP | GOD EATER | 无 |
| `PSPGodEaterBurstTools` | PSP | GOD EATER BURST | gzip |
| `PSPGodEaterCheckerTools` | PSP | GOD EATER BURST GE2 引继数据检查器 | deflate |
| `PSPGodEater2TrialTools` | PSP | GOD EATER 2 体验版 | blz2 |
| `PSVGodEater2TrialTools` | PSV | GOD EATER 2 体验版 | blz2 |
| `PSPGodEater2Tools` | PSP | GOD EATER 2 | blz2 |
| `PSVGodEater2Tools` | PSV | GOD EATER 2 | blz2 |
| `PSVGodEaterSeriesTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION / OFF SHOT 系列 | blz4 |
| `PS4GodEaterSeriesTools` | PS4 | 同上（PS4 版） | blz4 |
| `PSVGodEaterUSTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION 美版（多语言分区） | blz4 |

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

在工具的工作目录下会生成 `1.out/`（解包）、`4.pack/`（打包）、`5.dlcpack/`（DLC 打包）等目录；工具不覆盖原始文件。

---

## English

A collection of unpack/pack tools for the `PRES(.res)` / `package.rdp` / `data.rdp` containers used by the *GOD EATER* game series (.NET 8 / C#).

Each subdirectory is a standalone tool with no cross-project dependencies.

### Tools

| Directory | Platform | Titles | Compression |
|---|---|---|---|
| `PSPGodEaterTools` | PSP | GOD EATER | none |
| `PSPGodEaterBurstTools` | PSP | GOD EATER BURST | gzip |
| `PSPGodEaterCheckerTools` | PSP | GOD EATER BURST GE2 transfer-data checker | deflate |
| `PSPGodEater2TrialTools` | PSP | GOD EATER 2 trial | blz2 |
| `PSVGodEater2TrialTools` | PSV | GOD EATER 2 trial | blz2 |
| `PSPGodEater2Tools` | PSP | GOD EATER 2 | blz2 |
| `PSVGodEater2Tools` | PSV | GOD EATER 2 | blz2 |
| `PSVGodEaterSeriesTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION / OFF SHOT | blz4 |
| `PS4GodEaterSeriesTools` | PS4 | same as above (PS4 builds) | blz4 |
| `PSVGodEaterUSTools` | PSV | GOD EATER 2 RAGE BURST / RESURRECTION US (multi-language partitions) | blz4 |

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

`1.out/` (unpack), `4.pack/` (pack) and `5.dlcpack/` (DLC pack) are created inside the working directory. Original files are never overwritten.
