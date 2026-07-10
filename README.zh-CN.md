# MacDesk

[English](README.md) | **简体中文**

macOS 风格的 Windows 桌面图标层。MacDesk 在一个**壁纸之上、所有应用窗口之下**的
全屏层（与 Wallpaper Engine 同一 z 序）自绘图标网格，每个图标的位置存成
**分辨率无关的锚定距离**（右上/近边锚定，macOS 模型）。分辨率、DPI 或显示器配置
变化时，图标保持相对布局平滑重排——像 macOS 那样，而不是像原生 Windows 桌面
那样塌缩到左上角。

> 状态：Windows 11 可用（在 24H2 / build 26200 实测），支持混合 DPI 多显示器
> （100% + 225% 已验证，含 4K 电视）。项目源于个人工具，设计笔记见
> [`docs/dev-notes.zh.md`](docs/dev-notes.zh.md)。

## 安装

从 [Releases 页](https://github.com/Nishikinonakai/MacDesk/releases) 下载最新版：

- **`MacDesk-Setup-vX.Y.Z.exe`**（推荐）——单用户安装器，无需管理员权限。
  升级时自动优雅退出旧版（先还原原生图标再换文件）；卸载会还原原生桌面并清理
  开机自启。布局和设置数据永不删除。
- `MacDesk-vX.Y.Z-win-x64.zip`——绿色版，解压运行 `MacDesk.exe`。
  覆盖安装前请先退出正在运行的 MacDesk。

之后更新只要一次点击：**设置 → 关于 → 检查更新**，自动下载新版安装器、
静默替换、自动重启。

## 特性

- **一份跨分辨率的相对布局**（macOS 模型）。每个图标只存一份分辨率无关的锚定
  距离（固定间距、右上/近边锚定）。分辨率或 DPI 变化时显示位置是*现场推导*的，
  存储的布局**永不改写**——1080p ↔ 4K ↔ 720p 来回切换，布局文件逐字节相同。
  屏幕装不下时溢出图标折列集中（不丢失）；更大的屏幕保持同样间距，只是留白更多。
- **动态壁纸（Wallpaper Engine）支持。** 检测到 WE 运行时，MacDesk 把它的每屏
  渲染窗收编进桌面层——原生渲染零额外开销，视差与点击交互照常（可以隔着
  MacDesk 按互动网页壁纸上的按钮）。WE 退出/重启自动检测处理。动态壁纸下图标层
  走脏区渲染管线（只重绘变化区域），1080p 下叠放动画接近 60 fps。低配机可用
  性能开关（禁用图标阴影/动画）。
- **真实的桌面交互**，由 MacDesk 绘制、Windows shell 支撑：双击打开、原生右键
  菜单（图标与桌面空白处——"新建"、"粘贴"、第三方 shell 扩展）、就地重命名、
  删除进回收站、框选 + Ctrl 多选、成组拖拽（吸格与避让）、与其他窗口之间的
  OLE 拖放。取消拖拽会 Finder 式回弹到原位。
- **稳如磐石的原生右键菜单。** shell 菜单在隔离子进程构建（第三方扩展崩溃
  不会带走桌面），序列化后在**主 UI 线程**弹出——免疫让其他桌面覆盖层菜单
  秒消的异步前台大战。菜单跟随系统深色模式、支持完整键盘导航，设置里可屏蔽
  不想要的菜单项。装了 [Locale Emulator](https://github.com/xupefei/Locale-Emulator)
  的话，可执行文件会多一项"用 Locale Emulator 运行"。
- **使用叠放**（背景菜单）——macOS 式自动分组，"分组依据"支持类型/修改日期/
  大小。收起的堆用最多三个真实成员图标扇形叠放；悬停滚轮可刮擦轮换前层图标。
  点击展开成员（弹簧动画飞出），再点收起。不碰规范布局——关闭叠放即恢复
  原来的精确摆放。
- **文件夹堆叠**——叠放模式下右键桌面文件夹选"以堆叠方式展示"：图标多一个
  向下角标，单击原地展开文件夹内容（真实图标，可打开/拖出/右键），再点收起
  （macOS Dock 文件夹堆叠语义）。拖文件到文件夹上仍是移入；把展开的子项拖到
  空白处即移出到桌面。想自定义哪些文件折叠成组，建个文件夹勾上就行——分组
  就是文件夹本身，卸载 MacDesk 也不丢。
- **布局安全。** 布局文件每日滚动备份（保留 7 份）；设置里可**导出/导入布局**
  用于换机迁移。导入后本机不存在的项目显示为 macOS 式问号占位——右键可移除；
  MacDesk 绝不擅自删除布局数据。
- **Finder 式标签**——长文件名中间省略，扩展名始终可见。
- **剪贴板文件操作**——Ctrl+C / Ctrl+X / Ctrl+V 走 shell 剪贴板；剪切的图标
  半透明显示直到粘贴。
- **整理/排序**——"按 mac 式网格整理"与"排序方式"子菜单（名称/日期/大小/类型），
  一次性执行、可撤销。
- **自由摆放模式**——macOS `arrangeBy=none`：图标放哪就在哪，不吸格。
- **macOS 式多显示器。** 每台显示器一个桌面窗口（混合 DPI 按窗口处理）。图标
  *归属*于显示器；跨屏拖拽即迁移。拔掉显示器，它的图标集中到主屏显示——存储
  的位置不被改动——重新接上即原样回归。显示器用 EDID 识别（换线换口不换身份）。
- **Explorer 重启不死。** 无窗口看门狗进程在 Explorer 重启或主进程崩溃时
  ~250ms 内重新拉起桌面层。分辨率切换走无缝进程交接，零裸桌面闪屏。
- **macOS 式设置窗口**——侧栏 + 内容页，跟随系统深浅色主题：开机自启（含
  计划任务加速模式）、**界面语言（跟随系统 / English / 简体中文）**、强调色、
  动态壁纸性能开关、右键菜单黑名单、布局导出导入，以及带一键更新的关于页
  （全应用唯一联网点——无后端、无遥测）。
- **首次启动引导**——全新安装时询问是导入现有桌面摆放，还是从整洁的 mac 式
  右上排列开始。
- **大量文件不虚**——同类型文件共享图标位图，几百个文件的桌面也能秒级启动、
  内存轻盈。
- **高 DPI 感知**——100%–300% 及混合 DPI 切换下渲染正确。

## 键盘快捷键

| 快捷键 | 动作 |
|---|---|
| 双击 | 打开 |
| Enter / F2 | 重命名（mac 式：选中主文件名不含扩展名） |
| Delete / Backspace | 移到回收站 |
| Ctrl+A | 全选 |
| Ctrl+C / Ctrl+X / Ctrl+V | 复制 / 剪切 / 粘贴文件 |
| Ctrl+Shift+N | 新建文件夹（立即进入重命名） |
| 方向键 | 网格间移动选择 |
| 直接输入名称 | 首字母定位选择 |
| F5 | 刷新 |
| Esc | 清除选择 / 取消剪切 |
| Ctrl+Alt+Q | 退出 MacDesk |

## 构建

可从 macOS 或 Linux 交叉编译到 `win-x64`（已设 `EnableWindowsTargeting`）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

需要 .NET 10 SDK。CI 每次推送都构建产物，打 tag 时自动把 zip + Inno Setup
安装器挂到 release（`.github/workflows/build.yml`、`installer/macdesk.iss`）。

## 运行

```
MacDesk.exe                 # 挂载到桌面（SHELLDLL_DefView）
MacDesk.exe --hide-native   # 同时隐藏原生 Explorer 图标（退出时还原）
MacDesk.exe --quit          # 优雅退出运行中的实例（连同看门狗）
```

退出方式：设置 → 通用里的**退出**按钮、**Ctrl+Alt+Q**、或 `--quit`。
MacDesk 设计为常驻——强杀主进程会被看门狗拉起来，请用上述方式完整退出。

数据文件（每用户，`%LOCALAPPDATA%\MacDesk\`）：`layout.json`（规范图标位置，
滚动备份在 `backups\`）、`settings.json`。运行日志 `macdesk.log` 在可执行文件旁。

## 架构

| 文件 | 职责 |
|---|---|
| `App.xaml.cs` | 进程生命周期：单实例、CLI 动词、看门狗、优雅退出协议 |
| `Desktop.cs` | 多显示器协调：共享服务、每屏图标分发 |
| `Interop/DesktopLayer.cs` | 挂载到桌面图标之下（`SHELLDLL_DefView`），扛住 Win+D |
| `Services/WallpaperEngine.cs` | Wallpaper Engine 渲染窗收编（发现、z 序、释放） |
| `Services/UlwPresenter.cs` | 动态壁纸模式的分层呈现层（脏区推帧） |
| `Services/MenuHost.cs` / `MenuSnapshot.cs` / `NativeMenuPresenter.cs` | 隔离进程菜单捕获 → 主线程原生菜单 |
| `Services/LayoutStore.cs` | 规范布局（DIU 锚定距离）、备份、导出导入 |
| `Services/IconLoader.cs` | 高分辨率 shell 图标，同类型共享 |
| `Services/Watchdog.cs` | Explorer 重启 / 崩溃时重新拉起 |
| `Services/UpdateCheck.cs` | 版本检查（防限流）+ 一键更新 |
| `Services/L.cs` | 双语界面文案（跟随系统 / en / zh） |
| `MainWindow.xaml.cs` | 布局引擎、叠放、拖拽/吸格、键盘、剪贴板、重命名 |

## 已知约束

- 不运行 Wallpaper Engine 时桌面层是**不透明**的、由 MacDesk 自己镜像系统壁纸
  （`SetParent` 下的 WPF 透明子窗口不参与合成）；WE 运行时透出真实动态壁纸。
- 右键菜单刻意在隔离子进程构建：`QueryContextMenu` 会加载所有已装 shell 扩展，
  坏扩展的 fail-fast（`c0000409`）连托管异常兜底都接不住。
- 分辨率变化通过无缝进程交接处理——任何 DPI 下"全新挂载"都可靠，活体改尺寸不可靠。

更多用真机换来的实现约束（重构时别退化）见
[`docs/dev-notes.zh.md`](docs/dev-notes.zh.md)。

## 许可

[MIT](LICENSE)。
