# Third-Party Notices / 第三方组件声明

本仓库（GodEaterTools）在开发与分发过程中涉及以下第三方成果，按其许可要求在此保留原始声明。

This repository distributes or derives from the following third-party works. Their notices are reproduced below as required by their licenses.

---

## 1. zlib 1.3.2

**分发内容 / Distributed as**：各工具目录下的 `zlib1.dll`（zlib 1.3.2，MSVC x64 构建），工具通过 P/Invoke 调用以保证压缩字节与游戏一致。
`zlib1.dll` in each tool directory (zlib 1.3.2, MSVC x64 build), called via P/Invoke so that compressed output matches the games byte-for-byte.

```text
(C) 1995-2024 Jean-loup Gailly and Mark Adler

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

---

## 2. GEBCS

**参考内容 / Referenced for**：PSP《GOD EATER BURST》`package.rdp` 解包/打包工具，作为 `res` 容器结构研究与字段命名的参考实现。
A `package.rdp` unpack/repack tool for the PSP title *GOD EATER BURST*, used as a reference implementation for the `res` container layout and field naming.

- 作者 / Author：gil-unx
- 许可 / License：MIT — Copyright (c) 2021 gil-unx

```text
MIT License

Copyright (c) 2021 gil-unx

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 3. 游戏资源与商标 / Game assets and trademarks

本仓库只包含**工具源码与格式说明文档**，不包含任何游戏资源文件。
《GOD EATER》系列名称、商标及其中一切资源，版权归 BANDAI NAMCO Entertainment Inc. 所有。
使用本工具处理游戏文件时，请自行确保对相应游戏副本拥有合法使用权。

This repository contains **tool source code and format documentation only**, no game assets.
*GOD EATER* and all related names, trademarks and assets are property of BANDAI NAMCO Entertainment Inc.
When using these tools on game files, you are responsible for holding a legitimate license to the game copy involved.

---

## 4. 本项目许可与兼容性 / This project's license and compatibility

本仓库（GodEaterTools）本身以 **GNU General Public License v3.0（GPL-3.0）** 授权，版权归 © 2026 Qingyu510，全文见 [`LICENSE`](LICENSE)。

This repository (GodEaterTools) is itself licensed under the **GNU General Public License v3.0 (GPL-3.0)**, © 2026 Qingyu510 — see [`LICENSE`](LICENSE).

上面列出的第三方成果**均与本项目许可兼容**：

- **zlib 许可** —— 自由软件基金会（FSF）认定为与 GPL 兼容的宽松许可；
- **MIT（GEBCS）** —— 允许并入并以 GPL-3.0 分发（单向兼容），其版权与许可声明已在本文件第 2 节完整保留。

> **GPL-3.0 的义务提醒**：分发本项目的衍生作品时，必须（1）以 GPL-3.0 授权，（2）提供完整的对应源码，（3）保留原有版权与许可声明。仅分发编译后的可执行文件而不提供源码，不符合本许可要求。

The third-party works listed above are **all compatible with this project's license**:

- **zlib license** — a permissive license recognized by the FSF as GPL-compatible;
- **MIT (GEBCS)** — permits incorporation and redistribution under GPL-3.0 (one-way compatibility); its copyright and permission notices are reproduced in full in section 2 above.

> **GPL-3.0 obligations**: when you distribute a derivative work of this project you must (1) license it under GPL-3.0, (2) make the complete corresponding source code available, and (3) retain the original copyright and license notices. Shipping binaries alone without source does not satisfy this license.
