# MacDesk — mac 式 Windows 桌面覆盖层（方案 D MVP）

全屏覆盖式假桌面：挂在壁纸层之上、所有应用窗口之下（Wallpaper Engine 同款层级），自绘图标网格，位置存**归一化坐标（0~1）**，分辨率/DPI 变化时按新屏幕尺寸平滑重排——macOS 行为。

## 架构

| 文件 | 职责 |
|---|---|
| `Interop/DesktopLayer.cs` | 桌面层级接管：SHELLDLL_DefView 在 Progman 下则挂 Progman（Win11 24H2+/经典结构），否则找 WorkerW（Win8~11 23H2）。WS_CHILD + SetParent + HWND_TOP，Win+D 不消失 |
| `MessageWindow.cs` | 隐藏顶层消息窗口：收 WM_DISPLAYCHANGE（子窗口收不到广播）、全局热键 Ctrl+Alt+Q、原生右键菜单 owner |
| `Services/DesktopItemProvider.cs` | 合并用户+公共桌面，FileSystemWatcher 监听增删改 |
| `Services/IconLoader.cs` | IShellItemImageFactory 高清图标，GetDIBits 手工转 BGRA 保 alpha |
| `Services/ShellContextMenu.cs` | IShellFolder→IContextMenu 原生右键菜单转发 |
| `Services/LayoutStore.cs` | 归一化坐标持久化（%LOCALAPPDATA%\MacDesk\layout.json，键=文件名） |
| `MainWindow.xaml.cs` | 布局引擎（右上锚定 mac 网格）、拖拽吸附、双击打开、350ms 重排动画 |

## 构建（在 macOS 上交叉编译）

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish
# EnableWindowsTargeting=true 已配置，Mac/Linux 可直接出 win-x64 包
```

## 运行

```
MacDesk.exe                        # 默认：不透明模式（自绘壁纸/纯色背景），挂 SHELLDLL_DefView
MacDesk.exe --hide-native          # 同时隐藏 Explorer 原生图标列表（退出时自动还原）
MacDesk.exe --quit                 # 优雅退出正在运行的实例（还原原生图标、保存布局）
MacDesk.exe --parent=progman       # 实验：改挂 Progman（Win11 26200 上不渲染，仅调试用）
MacDesk.exe --no-child --soft --transparent   # 其他实验开关（默认都别用）
MacDesk.exe --contextmenu <path> <x> <y>      # 内部用：右键菜单隔离子进程
```

退出：`--quit` / 桌面右键菜单 / Ctrl+Alt+Q。日志写在 exe 同目录 `macdesk.log`。
布局文件：`%LOCALAPPDATA%\MacDesk\layout.json`（键=文件名，值=归一化中心坐标 0~1）。
导入 Explorer 原生布局：用 desktop-icon-backup-manager 的备份 JSON + `convert_layout.py` 生成 layout.json。

## 已验证的验收结果（home-win，Win11 26200，2026-07-06）

- 挂载 DefView + Win+D 不消失 ✓；进程扛住右键/双击/三连分辨率切换 ✓
- 1080p ↔ 4K（自动 225% 缩放）↔ 720p：归一化重排 + 350ms 动画，往返布局一致 ✓
- 原生图标位置导入（26/26 含回收站）✓；双击打开、原生右键菜单 ✓

## 已知问题 / 踩坑记录（重构勿踩）

- **挂载父窗口必须是 SHELLDLL_DefView**：Win11 26200 上 Progman 直接子窗口不参与合成（4 变体矩阵实测）。WS_CHILD 必须**首帧渲染后**再设——Show 中途改样式会掐死 WPF 渲染管线。
- **WM_WINDOWPOSCHANGING 钩子强制窗口=主屏物理尺寸**：混合 DPI 下 WPF 会拿自己的尺寸账本反向改窗口（4K@225% 缩成 1024px 的教训），别用 Width/Height 同步。
- **右键菜单必须走子进程**：QueryContextMenu 加载的第三方 shell 扩展会 fail-fast 硬崩宿主（BEX64/c0000409，.NET 兜底接不到）。
- **WPF AllowsTransparency + SetParent 子窗口 = 不渲染**；纯色壁纸时藏 ListView 连壁纸色都会消失（壁纸画在 ListView 背景）→ 背景自绘（图片壁纸或 Control Panel\Colors 纯色）。
- **InvariantGlobalization=true 会让 WPF 菜单模板炸进程**——已移除，勿再加。
- Explorer 重启会连带销毁本窗口，需重启 MacDesk（后续加自动重挂）。
- 强杀（taskkill /F）不会还原原生图标；救急：ShowWindow(SysListView32, SW_SHOW)。

## 已发布（2026-07-07 下午 IV）

**v0.9.0 = 首个公开版**：仓库已切 public，tag v0.9.0 → CI（tag 触发的 release job，
softprops/action-gh-release）出 `MacDesk-v0.9.0-win-x64.zip`（76MB self-contained）挂上
Release，正文换手写 notes（Highlights/Install/Known limitations）。下载 302 正常；
检查更新端点两侧代理出口 IP 当时都在匿名限额中（每小时重置，App 已有降级提示，非缺陷）。
发布地址：github.com/Nishikinonakai/MacDesk/releases/tag/v0.9.0。
机主同批确认：展开态占位简化为"单个白色半透明圆角矩形+居中向下 V 箭头"（已部署验证）；
动画手感"相当不错，先保持"。

## 2026-07-07 下午 V：壁纸透传 spike——三命题全过（真机验证，架构拿到通行证）

`MacDesk.exe --ulwtest`（`Services/UlwSpike.cs`，纯 Win32 绕开 WPF 渲染管线）在 home-win
真机验证了真透明路线的三个关键命题：

- **① 渲染**：WS_EX_LAYERED | WS_CHILD 子窗口挂 SHELLDLL_DefView 下，UpdateLayeredWindow
  推 premultiplied BGRA 帧**成功上屏**——半透明红块下壁纸清晰透出（逐像素 alpha 合成正确），
  不透明绿块实色。"WPF 分层子窗口不渲染"是 WPF 渲染管线的问题，不是桌面合成的限制。
- **② 输入**：点击不透明像素，WM_LBUTTONDOWN 正常到达（client 坐标精确），点击变色回推成功。
- **③ 穿透**：点击 alpha=0 像素，事件穿透到下层兄弟窗口（先选中桌面图标，再隔着 spike
  透明区点空白——选中被下层 MacDesk 清除，spike 自己没收到消息）。
- **结论**：主窗口渲染管线可改造为"WPF 视觉树离屏渲染（RenderTargetBitmap）→ 按帧推
  ULW"；空白区 alpha=1（近隐形）保住点击归属，框选/背景菜单不破；动态壁纸（Wallpaper
  Engine）在下层原生播放。改造量在渲染管线一处，交互代码不动。
- **踩坑**：①WNDCLASSEX 的 cbClsExtra/cbWndExtra 必须在 hInstance **之前**——字段顺序错
  → RegisterClassExW 静默失败 → CreateWindowEx err=1407（找不到类）；②spike 自带 120s
  自毁定时器，实测按时退场（测试进程不留尸体的保险丝值得沿用）。

## 2026-07-07 下午 VI：透传真机实施——"三明治"撞墙，WGC 镜像定为正解

上午定稿的"三明治"架构（WPF 输入层近隐形 + 纯 Win32 呈现层透出）**真机实施后证伪**。
呈现层（`Services/UlwPresenter.cs`，UlwSpike 生产化）本身没问题，卡在**输入层隐身**：

**真机诊断链（双屏 Dell 1080p + SKG 4K@225%，Wallpaper Engine neon_sunset 实测）**：
1. 部署透传态 → 桌面全黑（图标在，壁纸没了），不透出 WE。
2. 逐层隐藏窗口二分：隐藏两个 presenter 后图标**仍在** → 图标其实是 WPF 输入层画的
   （z 序 WPF 层在 presenter 之上），说明 `LWA_ALPHA(1)` 没让 WPF 层隐身。
3. 再隐藏两个 WPF 输入层 → **两屏 WE 动态壁纸完整铺满**（霓虹日落 + 红色跑车）。
   **关键正面结论：SHELLDLL_DefView 完全透传其下的 WorkerW（WE 壁纸）**——透出链本身通。
4. 读 WPF 输入层 exstyle = `0x0`：我们外部 `SetWindowLong(GWL_EXSTYLE, |WS_EX_LAYERED)`
   **同进程读回仍非 layered**，`WM_STYLECHANGING` 拦截 styleNew 也保不住。

**硬约束（新，勿再走这条路）**：**WPF child 窗口无法成为 layered 窗口**。WPF 用 D3D
重定向表面渲染（DirectComposition），与 GDI layered 互斥，系统静默拒绝 `WS_EX_LAYERED`。
- 非 layered → 不能 per-pixel 透明 → 全屏覆盖挡住 WE（Background=Transparent 在非合成
  child 上=黑）。
- `AllowsTransparency=true` 的 per-pixel layered → 对 child"不渲染"（旧约束）=透明，
  但输入穿透（alpha=0 不命中）→ 收不到输入。
- constant layered（SLWA）→ 被 WPF 拒（上面第 4 点实测）。
三条路全断。三明治依赖的"WPF 输入层既 layered 隐身又整矩形收输入"在 WPF child 上不成立。
顶层 layered 窗口能透传但会重新引入 Win+D/z 序战争（当初选 DefView child 的原因），不划算。

**正解改道：Windows.Graphics.Capture（WGC）动态壁纸镜像（backlog 头号大件）**。不透传，
把 WE 的渲染窗（`WPEDesktopDX11Window`，挂在顶层 `WorkerW` 下）用 WGC 实时捕获成帧，
画进现有 WPF 背景（替换/叠加静态壁纸镜像的 Image）。WPF 架构**完全不变**（非 layered、
正常渲染、交互逻辑零改动），把"动态壁纸"降维成"静态壁纸镜像"的实时版。要点/风险：
WGC 捕获 `WorkerW`（顶层，CreateForWindow 应可）得纯 WE 画面（避开捕显示器的自我循环）；
D3D11 设备 + Direct3D11CaptureFramePool → WPF `D3DImage`/`WriteableBitmap`；4K 捕获降频到
~30fps 省 GPU；Win11 可禁捕获黄框；捕获 child 窗口可能不支持，退而捕 WorkerW 或整显示器
减去自身。约 300–500 行 D3D interop，单独会话专攻。

**本次落地（保留，非死代码）**：`UlwPresenter.cs`（Win32 layered 呈现层，WGC 若需自绘图标
层可复用）；`MainWindow` 透传集成加了**自动回退保险**——`EnablePassthrough` 施加 layered 后
读回验证，非 layered 即 `reverting to mirror` 退回镜像态（真机实测：`TrueTransparency=true`
下桌面照常显示雪城壁纸镜像，绝不黑屏）。设置 UI 的"真透明"开关已撤下（Settings 字段留着给
保险路径），避免用户开了没反应。踩坑：`SaveFileDialog` 在 WPF+WinForms 混引用下要 `using
SaveFileDialog = Microsoft.Win32.SaveFileDialog` 消歧义。

**同批独立改进（与透传无关，已构建通过、非透传路径真机回归 OK）**：
- **自启动加速**（`Services/Autostart.cs` 重写）：机主反馈 HKCU\Run 开机自启慢（Windows 对
  启动应用有串行延迟，实测 ~40s+）。加计划任务模式——`onlogon` 触发即启、`Priority 5`
  （默认 7=below-normal 拖慢首屏挂载）、`InteractiveToken`/`LeastPrivilege` 非管理员注册，
  schtasks `/Create /XML`（XML 必须 UTF-16）。失败自动回退 Run 键。设置窗通用页"加速自启动"
  开关；`Settings.FastAutostart` 记偏好，`--enable-autostart` 读它决定机制。**真机开机速度
  验证待机主方便时**（远程重启不便）。
- **日志轮转**（`Services/Log.cs`）：`macdesk.log` 原先无限增长，加 2MB 上限滚到 `.1`
  （共约 4MB 封顶），每 128 条写入抽检一次大小。
- **诊断导出**（关于页"导出诊断包…"）：zip 打包日志+settings+layout+环境摘要，落用户选的
  路径。**只在手动点击时生成、绝不自动上传**（守"无遥测"承诺），弹窗明示内容含桌面文件名等
  个人痕迹、发送前可自查删改。回应机主"要不要加带 log 反馈"的想法——做成手动导出而非常驻
  记录，隐私风险最低（默认不额外收集，日志本就在本机为调试写着）。

## Backlog（2026-07-07 下午 VI 刷新）

- Stacks v2 剩余：拖文件进堆（手动归类）、多屏叠放策略复核（待机主双屏实用反馈，现状=每窗口对自己图标聚堆；机主拖 Jellyfin 去 TV 就是在试这个）。
- **动态壁纸 = WGC 镜像（头号大件，见上"下午 VI"节）**：三明治透传证伪，改用 Windows.Graphics.Capture 实时抓 WE 画面画进 WPF 背景。DefView 透传 WE 已证（备用知识），presenter/ULW 代码保留。
- 远期：拖拽图像掠过高 DPI 屏偏小（shell 限制）。

## 测试（远程，home-win）

首选 `C:\work\agent\agent.py` 调试代理（HTTP :18800，token 鉴权，LAN-only；`POST /ps` 交互会话执行 PowerShell、`GET /screen` 物理分辨率截屏；curl 记得 `--noproxy '*'`）。代理挂了用 schtasks 后备：`/sc once /st 23:59 /ru nakai /IT /f` + `/run`。

---

## 2026-07-06 补充：健壮性与交互批次的硬约束（勿回退）

- **Explorer 重启恢复必须靠独立看门狗进程**，不能靠主进程自救。实测：Explorer/shell
  重启销毁我们挂靠的 `SHELLDLL_DefView` 父窗口时，主进程（WPF 子窗口）**突然死亡**——
  `Window.OnClosed`、`DispatcherTimer`、`AppDomain.UnhandledException`/`DispatcherUnhandledException`
  全都来不及跑，也不产生 WER 崩溃报告。所以恢复逻辑放在无窗口的兄弟进程
  `MacDesk.exe --watchdog <pid>`（`Services/Watchdog.cs`）：盯着主进程，非正常消失就
  重新拉起（带 `--recovered`，走"启动即挂载 + 等 DefView 出现"的可靠路径）。约 250ms 接管。
- **清洁退出协议**：用户主动退出（Ctrl+Alt+Q / 菜单 / `--quit`）走 `App.BeginUserQuit`——
  置命名事件 `MacDesk.CleanQuit` 让看门狗停手、再 Shutdown。其他退出（分辨率变化/崩溃/被
  shell 带走）不置该事件，看门狗一律重新拉起。分辨率变化不再自我 spawn，统一交看门狗。
- **挂载要带重试**：`AttemptAttach` 按 500ms 节奏重试最多 ~20s。看门狗在旧 shell 死、新
  shell 未起的空窗期拉起替身时，替身要耐心等新 DefView，而不是立刻报错退出。
- **桌面本体必须禁用 IME**（`InputMethod.SetIsInputMethodEnabled(this,false)`）：否则中文
  输入法把首字母定位的字母键拦成拼音候选。重命名框单独放开 IME（`_renameBox` 上设 true），
  以便输入中文文件名。
- **剪贴板粘贴用 `FOF_RENAMEONCOLLISION`**：同文件夹复制/粘贴（Ctrl+C/V 到桌面自身）会撞
  "destination folder is the same as the source folder"，加此标志才像 Explorer 那样自动
  出"- 副本/- Copy"，避免弹出无人应答的 shell 冲突对话框。剪切（FO_MOVE）不加此标志。
- **网格改 112×112 方形**（`GapX=16` 使横向 pitch=112，纵向本就 112）；`MaxCol()` 减去
  `MarginLeft` 保证最左格左上角 ≥ 边距，杜绝左缘半截图标。
- **自由摆放模式**（`Settings.FreePlacement`，默认关，`settings.json` 持久化）：`LayoutAll`
  与拖放落点分支——有位置的按精确归一坐标还原（只屏内钳制、不吸格/不避让），新图标仍从
  右上格种。切回网格时立刻重排吸格。网格模式是像素级验证过的核心路径，自由模式为可选叠加。

## 2026-07-06 傍晚：布局模型 v2→v3（推翻分档，改回 macOS 单一规范）

机主反馈：按分辨率分档存储不对——1080p 调好的布局切 4K 变成显然不同的布局。根因：v2 每个
显示尺寸一份独立布局档，首次切分辨率就做种子变换+量化落格+各档独立演化，必然分叉。

**v3 = macOS 单一规范布局**：
- 每图标存一份**分辨率无关的 DIU 锚距** `CanonPos(RightDist, EdgeDist, FromBottom)`：中心到
  右缘的逻辑距离（右锚）、到近边（上/下）的逻辑距离。固定间距、右上锚定。
- 切分辨率**只现场推导**显示位置（`CanonToPos`/`CanonToCell`），**绝不回写 Canon**。放不下
  才折行/钳制（仅显示）。只有新图标（LayoutAll 分配）或用户拖动才写 Canon。
- `CellToCanon(col,row)` 是纯 col/row 函数（与分辨率无关）；网格模式 Canon 恒上锚，自由模式
  用近边锚（cy>0.6h 锚底）。
- 验证：1080p↔4K@225°（逻辑 1707×960，比 1080 矮 ~2 行）顶部行完全一致、底部溢出图标折进
  相邻列；往返后 layout.json 逐字节相同；拖动 Word 后 Canon 从 (286,66) 变 (1070,402) 并持久。
- **坑**：自由↔网格切换别读 Canvas 实时坐标重吸格回写 Canon（打乱到左侧的元凶）——直接
  LayoutAll，让 CanonToCell 就近吸格显示、不回写即可。
- 文件 v3：`{"version":3,"icons":{name:{RightDist,EdgeDist,FromBottom}}}`；迁移 v2 时取 last 档
  按其分辨率把归一化中心换算成 DIU 锚距（上锚）。v1 平铺格式无分辨率信息 → 忽略。
- 机主在"极端尺寸差"选项里选了**固定间距+右上锚定**（最贴 macOS），非等比铺满/整体缩放。

## 2026-07-06 傍晚 II：多显示器（v4，双屏真机全矩阵验证）

架构 = **每显示器一个桌面窗口** + **布局 v4 每显示器一个规范布局区**：
- DefView 在 26200 上横跨整个虚拟桌面（实测 (0,0)-(6016,2160) 物理），每屏一个 WS_CHILD
  子窗口挂同一个 DefView，各自 CoverRect 自己的显示器物理矩形（MapWindowPoints 转父客户区）。
- ForceCoverHook 从"强制主屏尺寸"改为"强制本窗口的显示器矩形"（每窗口缓存 _forceRect）。
- 混合 DPI（Dell 100% + TV 225%）：副屏窗口 WPF believed=1.0、actual=2.25 →
  既有 LayoutTransform 补偿机制原样生效（correction=2.250，逻辑工作区 1820×960）。
  输入坐标 WPF 自动换算（GetPosition/PointFromScreen 都过 LayoutTransform）。
- **图标归属显示器**（macOS 语义）：layout v4 `{"monitors":{"<EDID key>":{name:CanonPos}}}`，
  Set 单一归属（写 A 屏自动从 B 屏区移除）。归属显示器不在场 → 主屏窗口**现场推导**显示
  （不改写归属/坐标），重新接上即原位回归——实测 detach→27 图标聚拢主屏，reattach→Word
  原位回 TV，全程 layout.json 零写入；TV 1080p↔4K 往返布局文件字节相同。
- **跨屏拖拽**：拖拽中 WindowFromPoint 命中兄弟桌面窗口时不切 OLE（保持手动拖）；松手
  GetCursorPos（物理）→ 按显示器矩形找目标窗口 → AcceptCrossDrop（IconCanvas.PointFromScreen
  转本地逻辑坐标，吸格避让，Set 归属）→ RefreshAll 双方窗口各自增删图标。
- **显示器稳定 key = EDID 厂商+型号码**（EnumDisplayDevices 的 monitor child DeviceID 第二段，
  如 DEL41A5/SKG5500）。**大坑（26200 实测）**：适配器下可能挂多个 monitor 子设备（连线但
  未点亮的也在列，child0 不一定是活动的）——必须选 StateFlags 含 DISPLAY_DEVICE_ACTIVE 的；
  另外拓扑切换后短时间内映射会给旧值 → Desktop.Init 里等两次枚举一致（400ms/轮，最多 4s）。
- 协调器 `Desktop`（静态）：共享 Provider/LayoutStore/Settings、RefreshAll 按归属分发子集、
  菜单命令只挂主屏窗口但作用于所有窗口（Arrange/Sort 各屏各排、Undo 全局快照）。
- 新建文件夹/外部拖放/粘贴预登记都写"事发窗口"的归属区；重命名过户保持原归属。
- 测试工具：调试代理 /screen 升级为 PMv2 全虚拟桌面物理像素截屏（6016×2160）；
  inputlib 加 SetThreadDpiAwarenessContext(-4)（否则 225% 屏上点击坐标被虚拟化）；
  ChangeDisplaySettingsExW(dev, 0×0, CDS_UPDATEREGISTRY|CDS_NORESET) + apply = 程序化断开
  显示器（DisplaySwitch.exe /internal 在远程会话里不生效）。

## 2026-07-06 深夜：右键菜单大修（双菜单/图标右键失效/性能）+ 拖拽统一 OLE（真机验证）

**双菜单根因**：WPF 桌面窗口不拦 `WM_CONTEXTMENU`，DefWindowProc 会把它一路转发给父窗口
SHELLDLL_DefView → 原生桌面菜单先弹，我们的子进程菜单后弹。修 = ForceCoverHook 里
`msg == WM_CONTEXTMENU → handled = true` 直接吞掉（我们的菜单走 MouseRightButtonUp，不受影响）。

**菜单进程模型：一次性子进程 → 常驻 MenuHost**（`MacDesk.exe --menuhost <mainPid>`）：
- 命名管道 `MacDesk.MenuHost` 收请求（verb\x1F x\x1F y\x1F paths...），启动时预热背景菜单扩展，
  右键零冷启动。隔离性不变：host 崩了下次请求自动重拉，双兜底退化为旧 --contextmenu 一次性进程。
- **文件项菜单在这台机必崩**（QueryContextMenu(CMF_NORMAL) 加载某第三方扩展 → 0xc0000409
  fail-fast，托管 catch 接不到）——这就是"图标右键无效"的真相：旧一次性子进程也是静默崩。
  修 = **牺牲进程探针**（`--menuprobe <path>`：只 QueryContextMenu 不显示，看退出码）按扩展名
  缓存判定；不安全类型走**降级菜单**（打开/打开方式/剪切/复制/重命名/删除/属性，动词经
  CommandChannel 回主进程对当前选中执行，属性走 SEE_MASK_INVOKEIDLIST 在主进程 UI 线程弹）。
  注意探针/预热的 hwndOwner 必须传真实窗口，传 IntPtr.Zero 部分扩展直接 fail-fast。
- **shell 菜单 owner-draw 转发必须做**：owner 窗口把 WM_INITMENUPOPUP/WM_DRAWITEM/
  WM_MEASUREITEM/WM_MENUCHAR 转给 IContextMenu2.HandleMenuMsg（MessageWindow → 
  ShellContextMenu.ForwardMenuMessage），否则图标不画、"新建"子菜单是空的。
- **白块菜单 = TrackPopupMenu 淡入动画 bug**（后台进程弹菜单动画不渲染，划过才显示，26200
  实测；机主证实是 Windows 老毛病）。修 = `TPM_NOANIMATION`。与 uxtheme #135/#136 无关
  （对照实验排除；深色菜单主题暂未启用，需要时可再试）。
- **WM_CANCELMODE 竞态两连坑**：①host 线程在两次菜单之间不泵消息，滞留的 WM_CANCELMODE
  会把下一个菜单秒杀（第一版"菜单永不显示"的根因）→ Dismiss 信号只在 _menuOpen 时才 post；
  ②"请求前先 Dismiss"本身有竞态（信号线程可能晚于管道请求调度，杀掉自己刚开的菜单）→
  请求前不 Dismiss（菜单有前台权限后 OS 自动处理点击外部关闭），TrackPopupMenu 前再
  PeekMessage 排空 WM_CANCELMODE 双保险。Dismiss 只保留在主窗口左键点击兜底。

**拖拽统一 shell OLE**（删掉"手动拖 + WindowFromPoint 切 OLE"双轨制）：
- 旧版三症状同根：光标一压到任何别的窗口（长距离拖拽必然路过）就切 OLE，图标先弹回原位
  （"从鼠标上消失"），而 WPF DoDragDrop 没有拖拽图像（光标上没图标）；跨屏手动拖的视觉画在
  源窗口 Canvas 里被窗口边界裁掉 + 混合 DPI 坐标换算漂移。
- 新版 = 过 4px 阈值直接 `ShellDrag.Start`：SHCreateDataObject（**WPF DataObject 不支持
  SetData 任意格式，塞不进拖拽图像，必须用 shell 数据对象**）+ CF_HDROP 手工 DROPFILES +
  自家格式 `MacDesk.DragPaths`（含回收站等虚拟项，\n 分隔 UTF-8）+ IDragSourceHelper.
  InitializeFromBitmap（组图标按相对位置渲染成预乘 alpha 顶朝下 DIB，稀疏大组退化为锚点
  图标+红色计数角标）+ ole32 DoDragDrop（自实现 IDropSource）。原位图标拖拽中 Opacity 0.35。
- 接收端四个事件全程调 IDropTargetHelper（不调的话拖拽图像在我们窗口上会消失）；
  Drop 分流：自家格式 → RepositionAt（本窗口图标动画落座；不在本窗口 = 跨屏换归属 +
  RefreshAll，替代旧 AcceptCrossDrop）；落点命中文件夹图标 = SHFileOperation 移入、命中
  回收站图标 = FO_DELETE|FOF_ALLOWUNDO（Finder/Explorer 语义）；外来文件不变。
- 真机验证：拖拽中半透明图标+标签全程跟手（截图 drag_mid.png），松手落点即位置（自由摆放），
  拖回原位无异常。跨屏 OLE 本次单屏未测（机制上每窗口都是 drop target，接 TV 后应直接成立）。

**部署/调试环境补充**：Mac 侧 dotnet SDK 装在 `~/.dotnet`（上次装在会话 scratchpad 被清了）；
agent token 持久副本 `~/.config/macdesk/homewin-token.txt`（scratchpad 副本会随会话清理，
ssh 一度不通时曾因此无法用代理）。增量部署只需 scp MacDesk.exe/.dll/.pdb（deploy 前必须先
--quit，文件被运行中进程锁定）。

## 2026-07-06 深夜 II：右键手感修复（机主实测反馈驱动）+ 双屏 OLE 拖拽验证

机主反馈四症状：右键时灵时不灵、右键有时变框选且框死在桌面（截图确认两个残留框）、
FFXIV 图标虚化卡死、Zuma 图标右键出"原生菜单"。诊断结论与修复：

- **框选死框 = 两个 bug 叠加**：①OnCanvasMouseDown 创建新框前不清理旧框 → 旧矩形永久
  泄漏在 Canvas（"死在桌面上"）；②菜单 host 抢前台会打断 WPF 鼠标捕获，MouseLeftButtonUp
  从此丢失 → _bandActive 卡 true，框跟着鼠标走，且 RootGrid 持续持有捕获把图标上的右键
  也路由成背景右键（解释机主"先选中再右键更灵敏"——选中动作恰好抢回了捕获）。
  修 = EndBand() 统一收尾（可重入），挂在 MouseUp/RightClick/新框创建前/**LostMouseCapture**；
  MouseMove 里左键已抬起即收框。
- **虚化卡死/幽灵拖拽**：捕获被打断后 iv.MouseDown 卡 true，之后一次悬停划过就够阈值
  触发"没按键的拖拽"。修 = 图标 LostMouseCapture 清按下态 + OnIconMouseMove 检查
  e.LeftButton 必须 Pressed + LayoutAll 自愈（拖拽/剪切之外不允许 Opacity<1 残留）。
- **右键时灵时不灵的主根因在客户端**：TrySend 800ms 连不上就杀 host 重拉——host 只是在忙
  （探针进程 ~1.5s/种、或菜单开着占串行循环）就被误杀，请求跟着丢。修 = 阶梯式耐心
  （1.5s → 活着再等 4s → 确认死了才杀+重拉 5s → 一次性子进程兜底）；host 侧管道循环立即
  可用（预热+探针清扫挪到后台 STA 线程），启动时对桌面上**所有**文件类型预探针（按扩展名
  去重 + _probeGate 串行），首次右键不再付探针延迟。
- **Zuma"原生菜单"之谜 = 探针分流的正常表现**：Zuma Deluxe 是 **.url**，探针判定安全 →
  host 弹完整原生 shell 菜单；.lnk/.txt/.docx/.bin/.ini 都被崩溃扩展带崩 → 降级菜单。
  回收站（<none>）也安全 → 完整菜单。肇事扩展只挂普通文件类不挂 .url。
- **双屏跨屏 OLE 拖拽真机全程验证**（Dell 1080p@100% + SKG TV 4096×2160@225% Extend）：
  拖拽图像跨屏边界跟手（截图 xdrag_mid）、落 TV 归属切换（log: ownership transferred）、
  225% 屏上按正确缩放渲染落位、拖回 Dell 原位回归。**已知小瑕疵**：拖拽位图按源屏 DPI
  渲染，掠过 225% 屏时图像偏小（shell 不替我们缩放 DragImageBits）——backlog。
- 注入测试要点：非 DPI-aware 的 PowerShell 线程要先 SetThreadDpiAwarenessContext(-4) 再
  SetCursorPos，否则 225% 屏坐标被虚拟化。

**GitHub**：github.com/Nishikinonakai/MacDesk（私有，main）。新机无 gh CLI；建仓用的是
macOS 钥匙串里迁移过来的 GitHub OAuth token（`security find-internet-password -s github.com -w`）。

## 2026-07-06 深夜 III：右键激活风暴、Locale Emulator 真凶、零漂移落位（全部真机验证）

- **"右键时灵时不灵"终极根因 = 异步激活风暴**：点击桌面（WS_CHILD 挂 DefView、无
  NOACTIVATE）会让 Progman 父链**异步**激活，激活落地时把菜单 host 刚开出的菜单扫掉——
  实测菜单 ~8-10ms 自灭（cmd=0、无人点击、凶手 hwnd 恒定），时序抖动决定谁存活。
  修 = TrackWithRetry：开出 <300ms 即灭且 GetAsyncKeyState 显示无按键/点击 → 判误杀重开
  （≤2 次）。6 连击回归：每击必出菜单、存活到下一用户动作。
- **文件菜单崩溃真凶 = Locale Emulator 的 LEContextMenuHandler**（自动化二分定位：Blocked
  逐一封禁 + --menuprobe 看退出码；12 候选中唯有 {C52B9871-E5E9-41FD-B84D-C5ACADBEC7AE}
  被封后探针翻绿）。本质：**它是 .NET Framework 托管 shell 扩展（InprocServer32=mscoree.dll），
  在 .NET 10 进程里加载 = 双 CLR 冲突 fail-fast 0xC0000409**；Explorer 不崩因为没载新 CLR；
  .url/虚拟项不聚合 `*` 类 handler 所以幸免。迅雷/火绒/网盘全是无辜的。
  修 = ManagedHandlerShield：扫描各 handler 根（注意两种注册姿势：默认值=CLSID，或
  键名=CLSID+默认值=显示名——LE 是后者，第一版扫描漏掉过），凡 mscoree 的 CLSID 在
  QueryContextMenu 的毫秒级窗口内临时写入 HKCU Blocked、完事即删——Explorer 平时的 LE
  菜单不受影响，通用规则兜住一切托管扩展。结果：全部文件类型探针翻绿 full native，
  .lnk 右键完整原生菜单回归（迅雷/火绒/7-Zip/Bandizip/网盘/ToDesk/PowerToys 项齐全）。
  注意：LE 自己的右键项在 MacDesk 菜单里永远不会出现（技术上无法加载）——backlog 有
  "检测到 LE 时自绘一个'用 Locale Emulator 运行'项调 LEProc.exe"的兼容思路。
  测试坑：PowerShell Start-Process -ArgumentList 不给含空格参数加引号（探针路径被截断，
  第一轮二分全体假阴性）；bisect 测试文件用无空格路径。
- **自由摆放"迷之 align"= 落位丢失抓取偏移**：旧码把图标中心对齐到落点光标 → 每次放下
  平移一个抓取偏移 + 350ms 飞行动画，看着像在吸一个看不见的网格。修 = DragContext
  （进程内静态传递：抓取偏移 + 组内相对位置；OLE 数据对象带不了这个）：落位 = 拖拽图像
  视觉位置、组保持相对队形、去掉飞行动画；抓取基准用**按下点**（不是过阈值时的光标，
  大步长下两者差 20px+）且拖拽图像热点同源。验证：大步长注入拿起-拖出-放回 ×2，
  layout.json 锚距逐字节不变（零漂移）。

## 2026-07-06 深夜 IV：右键手感精修 + 字体（机主实测反馈第三轮）

- **激活风暴的治本尝试**：FocusDesktop 先同步 `SetForegroundWindow(GetAncestor(_hwnd, GA_ROOT))`
  把 Progman 链请到前台，让点击引发的激活在菜单开出**之前**落地（此前 TrackWithRetry 只是
  事后补救；机主"从别的应用切过来第一下右键最容易死"与该模型吻合）。retry 保留为兜底，
  且重试间加 50ms 让迟到激活落地，insta-cancel 日志加凶手窗口类名（下次直接定罪）。
- **File Locksmith 误触**（机主推断正确）：菜单在按住的按键正下方"物化"，抬起被判成选中
  第 ~5 项。修 = TrackWithRetry 进门先等两个鼠标键全部松开（10ms 轮询 ≤400ms）。
- **按住右键轻微移动**（机主提问命中盲区）：PreviewMouseRightButtonDown 快照按下点+按下时
  压着的图标（800ms 内有效）；抬起时以按下快照为准决定图标菜单 vs 背景菜单、菜单钉在
  按下点——微移滑出图标不再误出背景菜单。
- **字体**：标签/重命名框 `Segoe UI, Microsoft YaHei UI` + SemiBold（中文落雅黑 Bold，
  近苹方 Medium 质感；Windows 自带无版权问题）。"有的字清晰有的糊"元凶 = 图标落在
  亚像素坐标 → MoveIcon 整数 DIU 吸附 + TextFormattingMode.Display。3x 放大截图确认锐利。
- 测试插曲：两轮注入回归全落在机主打开的记事本窗口上（机主正实时看 macdesk.log）——
  **注入测试前必须查 idle + 截屏确认桌面无遮挡**（老规矩，这次违反了）。新构建的手感
  以机主实测为准；若仍有 insta-cancel，日志会带凶手类名。

## 2026-07-07 凌晨：菜单序列化进主进程（前台战争终极解，真机全回归通过）

机主实测反馈：settle-wait 版（8f7cac3）拖拽后立即右键仍偶发"大方框闪现即灭"——
菜单窗口已创建、绘制中途被迟到的激活风暴掐死；拖拽后的风暴比 300ms 等待上限更长，
重试也会被连击耗尽。时序补丁类方案永远有洞，按既定计划上终极方案。

**架构（v2，settings.MenuInMainProcess 默认开）**：
- host 只构建不显示：QueryContextMenu（第三方扩展隔离不变）→ `MenuSnapshot.ForceInit`
  对每个子菜单强制喂 WM_INITMENUPOPUP（"新建/发送到/7-Zip"等懒填充离线展开，真机验证
  可行）→ `MenuSnapshot.Capture` 把 HMENU 摘成纯数据树（文本/状态/hbmpItem 位图 BGRA
  往返）→ 帧协议（4 字节长度前缀）经管道 `MacDesk.MenuHost.v2` 回主进程。
- 主进程 UI 线程 `MenuSnapshot.Build` 重建原生 HMENU + 追加自定义项（勾选态直读本进程
  Settings，不再跨进程）→ `NativeMenuPresenter.Track` 同线程 TrackPopupMenuEx。
  选中的 shell 命令 id 回传 host 由同一 IContextMenu 实例 InvokeCommand（崩溃隔离不变）；
  0x7xxx 本地命令直接 CommandChannel.Signal 进程内闭环。降级菜单也移到主进程本地构建。
- **为什么杀不掉**：主窗口 SetParent 挂 DefView（跨进程 SetParent = 与 Explorer 桌面线程
  共享输入队列），菜单模态循环在自己线程、owner 是自己——激活风暴两站（主窗口→Progman）
  都落在共享队列内，不构成 owner 失活；杂散 WM_CANCELMODE 由 MainWindow 钩子在
  MenuOpen 期间吞掉（点击外部的正常关闭走菜单自身捕获判定，不经这条路，实测不受影响）。
- 旧路径完整保留：settings.json `MenuInMainProcess:false` + 重启 = 回退 host 内 track
  （settle-wait+重试），免重建逃生口。

**真机侦察推翻旧认知（--menudump 模式，留作诊断工具）**：26200 的 shell 菜单项
**全部是普通 MFT_STRING + 静态 hbmpItem 位图，零 owner-draw**（MessageWindow 旧注释
"Win11 全部项 owner-draw"是误判）——序列化走文本+位图即像素级还原。owner-draw 渲染
捕获路径（合成 WM_MEASUREITEM/WM_DRAWITEM 进内存 DC，normal/selected 两态）已实现
留作其他机器兜底。

**两个新坑（都已修）**：
- **子菜单白块**：TPM_NOANIMATION 只管顶层弹出，子菜单自带淡入动画，非前台线程动画
  不渲染 → 白块、划过哪行显影哪行（与深夜批次顶层白块同病）。修 = owner 收
  WM_INITMENUPOPUP 后 SetTimer 50ms×6 拍，对所有可见 #32768 菜单窗口 RedrawWindow
  (INVALIDATE|ERASE|FRAME|UPDATENOW)。修后子菜单落地即完整绘制。
- **键盘进不了菜单**：焦点在 Explorer 侧时方向键/Esc 不进同线程菜单循环。修 = Track 前
  SetFocus(owner)。旧 host 路径禁 SetFocus 是因为 WPF 拿焦点异步夺前台杀别进程的菜单；
  同线程菜单里该激活落在自己身上无杀伤力。修后 Esc/方向键/Enter 全通。

**真机回归（injection，双屏在接，机主 idle>300s 才动手）**：①空白右键背景菜单完整
（shell 项+自定义项+勾选态+图标）；②点击外部关闭正常；③**拖拽后立即右键 + 80ms 风暴
窗口内连续 4 击全部一次开成、每个菜单活满脚本设定寿命、零瞬灭**（v2 无重试机制也不需要）；
④"新建"子菜单懒填充完整（Word/PPT/Excel/ZIP 模板项+图标）；⑤键盘 ↓↓Enter 选"New Folder"
真建出文件夹（InvokeCommand 回传链路）；⑥.txt 文件菜单 35 项完整原生（迅雷/火绒/网盘/
7-Zip/ToDesk 齐全）。捕获耗时：bg 首次 174ms、预热后 84ms，文件菜单 142ms（35 项）。
测试产物已清理，桌面与部署前逐像素一致（仅任务栏时钟区有 diff）。

**顺手发现的新 bug（未修，backlog）**：新建文件的种子落位不避让已占格——MacDeskTest.txt
直接叠在既有 New Folder 同一格上（自由摆放模式）。种子应过 NearestFreeCell。

**backlog 补充**：v2 使深色菜单跟随（uxtheme #135/#136）变trivial——菜单在主进程，
SetPreferredAppMode 一行即生效（机主现用浅色主题，改了也不可见，未启用）；
菜单序列化也为"设置 GUI 菜单项屏蔽"铺平了路（数据树在手，过滤即所得）。

## 2026-07-07 早：种子避让修复 + Finder 式标签中间省略（真机验证）

- **新建文件叠图标 bug（昨夜发现）修复**：LayoutAll 自由摆放分支从不填充 `placed`，
  新图标右上列流对空集合避让 → 必然叠在右上角已有图标上。修 = `MarkFootprint`：自由
  摆放的图标不吸格、可跨格，把显示脚印（CellW×CellH）盖到的所有格子标记为已占（列向/
  行向区间重叠判定，边界外跳过）；Ctrl+Shift+N 新建文件夹的占位集合同步换 `OccupiedCellsForSeeding`
  （自由=脚印、网格=目标格）。真机：连建两个文件依次落 New Folder 下方空格，零叠加。
- **标签中间省略保扩展名**（Finder 手感调研项）：两行装不下时 `TruncateLabel` 用
  FormattedText（同字体/Display 模式/同宽）测量 + 二分最长前缀，渲染成"前缀…尾3字符+扩展名"
  （无扩展名/伪扩展名>8字符按纯文本截）。TextBlock 保留 CharacterEllipsis 作测量偏差兜底；
  tooltip 条件从"长度>14"改为"实际被截断"。真机："这是一个非常非…格验证.txt"、
  "Adobe Photosh…022" 两行中间省略，全列布局不变。
- **机主晨间自测（08:25，被动观测）**：菜单序列化版上线后机主 2 秒内连开 3 个背景菜单 +
  2 个 42 项文件菜单，全部正常开出关闭（cmd=0 快速浏览），日志零异常——正是原"时灵时不灵"
  场景，首次经受机主本人连击。

## 2026-07-07 上午：菜单闪烁修复 + 设置 GUI + LE 兼容项 + 深色菜单（真机全验证）

机主反馈：菜单打开后项目会"闪烁几次"，观感差。

- **闪烁根因 = 白块补绘过度**：昨夜的子菜单白块修复对**所有**可见菜单窗口无差别连刷 6 拍
  且带 RDW_ERASE——已经画好的主菜单被反复"擦背景+重绘"，肉眼可见闪烁。修 = 精准补绘：
  ①WM_INITMENUPOPUP 时快照"弹出前已可见的菜单窗口"（_preExisting，已画好，全程不碰）；
  ②只对本次新弹出的子菜单窗口补绘，且**只有首拍带 ERASE**（空白窗口擦除无闪烁代价），
  后续拍只 INVALIDATE|UPDATENOW（对已画好内容重绘同像素，肉眼不可见）；③顶层菜单
  （== Current.Handle，TPM_NOANIMATION 已即时绘制）直接跳过。**真机验证：子菜单开着时
  连拍 5 帧，帧 2-5 逐像素 diff=0（完全静止），主菜单零闪烁；子菜单本身完整绘制。**
  多拍仍需要（分层窗口淡入期太早的补绘不上屏），改 60ms×5。
- **设置 GUI v1 交付**（机主点名，settings.json 手编的替代）：`SettingsWindow.cs`，代码构
  WPF UI、改动即存、共享 Desktop.Config 实例（勾选态与右键菜单同源）。含：自由摆放/开机
  自启/菜单在主进程弹出三开关 + 菜单项屏蔽列表（增删，回车即添加）。背景菜单加"MacDesk
  设置…"项（0x700B → CommandChannel "OpenSettings" → 主进程 UI 线程 ShowSingleton）。
  单例，居中主屏。真机验证：菜单点击开出、UI 完整渲染、列表显示现有黑名单 AMD Software。
  **测试坑：窗口 CenterScreen 落主屏（Dell 物理 0-1920），别拿副屏坐标裁截图找不到。**
- **深色菜单跟随启用**：v2 菜单在主进程弹出，`EnableModernMenuTheme`（uxtheme #135
  SetPreferredAppMode(AllowDark) + #136 FlushMenuThemes）在 OnStartup 调一次即生效。
  真机确认菜单变深色（机主当前浅色系统主题下菜单仍跟随出深色——SAB/EP 同款观感）。
- **Locale Emulator 兼容项**（原 backlog）：LE 的托管扩展在我们进程必炸（长期屏蔽），
  LE 自己的菜单项无法出现。补偿 = 检测 LE 已装（CLSID InprocServer32 的 CodeBase 找到
  LEProc.exe）且右键目标是 .exe 或解析到 .exe 的 .lnk（IShellLinkW.GetPath，STA 线程内
  解析）时，文件菜单尾部自绘"用 Locale Emulator 运行" → `LEProc.exe -run <真实exe路径>`。
  **真机确认 CLI：`LEProc -run <系统真实exe全路径>` 有效且 LEProc 驻留等子进程（fire-and-forget，
  别 Wait）；传复制出来的 exe 副本会被 LE 拒绝（早先假阴性的原因）。** Feishu.lnk 右键真机
  见到该项 + 完整原生菜单 + 深色主题。
- 文件菜单项数：42 shell → +分隔线+LE 项 = 44；背景菜单 16 → +设置项 = 17（日志实录）。

## 2026-07-07 午：属性窗口秒开修复（host STA 泵）+ 拖拽取消回弹（真机验证）

- **"属性要等好多秒"（机主反馈）真凶 = host STA 停摆**：shell 的"属性"等异步动词把
  数据对象留在 host 的 STA 上、另开线程建属性页，初始化时的编组回调要回本 STA 派发；
  而 host 闲时死等管道（WaitForConnection/Read 均不泵消息）→ 回调无限卡住。**真机实锤：
  InvokeCommand 日志瞬间返回，但属性窗口 15 秒不出现**（机主看到的"几秒后弹出"其实是
  下一次操作唤醒了 host）。修 = host 所有空闲等待改 MsgWaitForMultipleObjects+消息泵
  （PumpUntilSignaled：等连接用 BeginWaitForConnection+泵、读帧用 ReadAsync+泵）。
  **修后实测：点击属性 259ms 可见**（旧：∞）。同理惠及一切把回调留在 host STA 的
  异步动词（打开方式、压缩等）。注意管道要开 PipeOptions.Asynchronous。
- **拖拽取消回弹动画**（Finder 手感调研项）：ShellDrag.Start 改返回 `DragDropEffects?`
  （null = DRAGDROP_S_CANCEL/失败），取消分支用拖拽位图做幽灵 Image，从取消点光标位置
  220ms CubicEase-out 飞回组包围盒原位，落地才恢复原图标透明度（期间 _dragGhosts 豁免
  自愈）。真机：拖出 550px 后 Esc，图标精确回原位、全不透明、无残影、无漂移。
- README 同步：v3 锚距模型描述（旧文还写着 0–1 归一化坐标）、菜单序列化架构、设置
  GUI、深色菜单、LE 项、标签中间省略、回弹动画。

## 2026-07-07 午 II：壁纸镜像（机主问"可以换壁纸了吗"，真机双屏验证）

- **真透明依旧不可行**（WPF 分层子窗口不渲染，早期实测硬约束不变）。达成目标的正确路线
  = **壁纸镜像**：`Interop/DesktopWallpaper.cs` 包 IDesktopWallpaper（Win8+ COM），按本窗口
  显示器物理矩形匹配 monitorID（GetMonitorRECT 对号入座；未点亮的历史显示器查询会失败，
  跳过即可），取每屏壁纸路径 + 适配模式 + 背景色。
- **渲染**：RootGrid.Background = 系统背景色（兼命中测试面 + Fit/Center 留边色），壁纸画在
  IconCanvas 之下的 Image 元素（IsHitTestVisible=false，命中自然落回 RootGrid）。适配模式
  全映射：FILL→UniformToFill、FIT→Uniform、STRETCH→Fill、CENTER→None、TILE→ImageBrush
  平铺（绝对 Viewport）、SPAN→按虚拟桌面 cover 几何裁本屏那块（CroppedBitmap）再铺满。
  BitmapCacheOption.OnLoad 读完即关（不锁壁纸文件）。
- **跟随变化**：SystemEvents.UserPreferenceChanged（Desktop/General 类别，包装
  WM_SETTINGCHANGE）+ 400ms 防抖 → 全窗口 ApplyDesktopBackground。真机实测：
  SPI_SETDESKWALLPAPER 广播后 ~1s 双屏（1080p+4K@225%）各自 FILL 正确渲染 img0.jpg；
  还原纯色同样秒级跟回（像素级验证 teal 回归）。幻灯片每次切换同样触发跟随。
- 背景菜单 +"更换壁纸（个性化）…"（0x700C → ms-settings:personalization-background）。
- **限制**：动态壁纸（Wallpaper Engine 等）无法镜像——那需要真透明/合成层大手术（backlog）。

## 2026-07-07 午 III：弹簧打开 + 用所选项目新建文件夹 + Tab 顺移重命名（真机验证）

- **文件夹悬停高亮 + 0.5s 弹簧打开**（Finder 手感调研项，实测参数 0.5s）：拖拽悬停在
  文件夹/回收站图标上即高亮（选中样式，移开恢复真实选中态）；悬停真文件夹 ≥500ms
  弹开该文件夹的 Explorer 窗口（拖拽不中断，可以继续拖进窗口里）。同一目标只弹一次，
  移开重悬停才再弹。**实现要点：OLE 在鼠标静止时仍持续回调 DragOver（WPF 会转发），
  时间戳判断即可、无需定时器**——第一轮测试"没触发"是注入坐标错了（机主自由摆放下
  挪过图标、新文件种子位也变了，拖到了文件夹自己身上被命中排除；教训：注入前必须
  重新定位图标，别用上一轮截图的坐标）。DragEnter 缓存拖拽路径集（命中排除用），
  Leave/Drop 清理。
- **用所选项目新建文件夹（N 项）**（Finder 行为）：≥2 个真实文件的多选菜单末尾追加；
  建夹坐第一个选中项的位置（LayoutFile 预登记）→ 移入全部选中项 → 进入重命名。
  真机：cmd=0x7202 → 两文件全部移入、重命名框开出。
- **Tab 顺移重命名**（Finder 行为）：重命名框里 Tab = 提交当前 + 顺移到视觉顺序
  （右上列流：列从右往左、列内从上往下）下一个图标继续改名；Shift+Tab 反向。
  **Tab 是焦点导航键，必须在 PreviewKeyDown 拦**（KeyDown 已被 KeyboardNavigation 吃掉）。
  真机：Tab 从新建文件夹跳到相邻图标、编辑框全选就位、Esc 安全取消。
- 顺带观测：机主已在用壁纸镜像功能（桌面换上了插画壁纸）——特性上线即被采用。

## 2026-07-07 午 IV：壁纸轮询兜底 + OOBE 原生布局导入 + 黑名单预设选择（真机验证）

- **换壁纸不跟（机主实测反馈）根因**：设置应用换壁纸走 IDesktopWallpaper::SetWallpaper，
  **不广播 WM_SETTINGCHANGE**——我们的 SystemEvents 事件路径收不到（此前 SPI 广播型测试
  能过、掩盖了这条路）。修 = 双通道：事件快路径保留 + **8s 轮询兜底**（ApplyDesktopBackground
  进门先做签名比对 `path|pos|color`，无变化零开销早退）。真机：COM 无广播换壁纸 8s 内
  双屏跟上、还原同样跟回。
- **OOBE 首启导入原生布局**（开源发布关键项，机主早前点头）：`Interop/NativeDesktopLayout.cs`
  跨进程读原生 ListView（VirtualAllocEx + LVM_GETITEMPOSITION/GETITEMTEXTW，方案 A 同款；
  LVITEM 用 x64 布局、MapWindowPoints 客户区→物理屏幕、PMv2 下无虚拟化），按 DisplayName
  匹配桌面项（回收站虚拟项直接命中），中心点 = 图标左上 + SM_CX/CYICONSPACING 半格，
  换算所属显示器的规范锚距（与 PosToCanon 同阈值 0.6）。门槛 = LayoutStore.IsEmpty（只有
  全新安装触发）。真机：**28/28 全命中导入**，布局档删除→重启→原生陈年布局完整重现→
  还原布局档→重启→机主布局逐像素回归（diff=0）。隐藏中的 ListView 照常应答 LVM 消息。
- **黑名单预设选择**（设置 GUI 补完）：主进程每次弹菜单把 shell 项文本收进会话级目录
  （**在追加自定义项之前收**，自家功能/子菜单头不入目录；文本剥 & 加速键符号），设置窗口
  下拉框直接选中屏蔽，不用手打子串。**顺带修了真隐患：黑名单是子串匹配，菜单原文含 &
  （如 P&roperties）会让不含 & 的黑名单词失配——StripBlacklisted 比对前先剥 &**。
- 部署插曲：scp 忘了 publish/ 前缀，旧二进制被重启当新版测（procs 正常但行为是旧的）——
  scp 后必须确认 REDEPLOYED 无报错再下结论。

## 2026-07-07 下午：设置 UI v2（mac 系统设置风）+ 强调色 + About/更新 + 菜单克制化 + App 图标（真机全验证）

机主大工单：性能体检、现代化设置 UI（照 macOS 系统设置的左侧栏+右内容）、强调色、
About（明写由 Claude 开发）、GitHub Releases 检查更新、右键菜单克制化（借鉴 macOS）、
OOBE 首启询问、App 图标。全部交付：

- **性能体检结论：无问题**。空闲 CPU 三进程 0%（壁纸 8s 轮询/FS 监听零负担），内存
  主进程 90MB / host 44MB（第三方扩展的隔离代价）/ 看门狗 4MB。
- **App 图标**：PIL 生成 Big Sur 风圆角方块（蓝紫渐变 + 右上锚定图标网格 + mac 选中框
  意象），`Assets/macdesk.ico`（多尺寸）+ csproj ApplicationIcon + 设置窗口 pack URI 图标。
  版本号起 `<Version>0.9.0</Version>`。
- **设置 UI v2**（`SettingsWindow.cs` 全重写）：左侧栏（通用/外观/右键菜单/关于）+ 右内容
  卡片（白卡圆角、行内提示、分隔线，macOS 系统设置观感）。通用 = 自启/菜单模式/导入原生
  布局（手动触发，确认后覆盖）/显示原生图标（调试用途注明）/红字退出钮。外观 = 强调色
  8 色盘（macOS 调色板，即时生效）+ 壁纸"跟随系统"说明。右键菜单 = 黑名单（列表+目录
  下拉+手输）。关于 = 大图标 + 版本 + **"由 Claude 开发 · Built by Claude (Anthropic)"** +
  "无后端·无遥测·除手动检查更新外不联网" + GitHub/检查更新按钮。
- **强调色**（`Services/Accent.cs`）：macOS 浅色调色板 8 色（蓝=项目原 mac 选中蓝 2B63D9），
  选中标签底色/框选填充与描边全部走 Accent 派生画刷；切换即时（Accent.Changed → 全窗口
  RefreshAccent 刷新已选中项，新选中/新框选自然取新色）。settings.json `AccentColor`。
  真机：紫色一点，选中标签立即变紫。
- **检查更新**（`Services/UpdateCheck.cs`）：GitHub Releases latest tag vs 程序集版本，
  仅手动触发（About 页按钮），失败优雅降级（真机实测私有仓库报"网络问题或仓库未公开"）。
- **右键菜单克制化（macOS 语义）**：自定义区只剩 整理 / 排序方式▸（**"无（自由摆放）"
  = macOS Sort By > None，进子菜单带勾选**）/ 撤销 / 更换壁纸… / MacDesk 设置…。
  自启、退出、显示原生图标全部迁入设置（退出保留 Ctrl+Alt+Q）。旧 host 路径同构更新。
- **OOBE 首启询问**：布局档为空时 MessageBox 问"检测到 N 个图标，要保留摆放吗"，
  是=导入 / 否=整洁开局（否后首次 LayoutAll 落 Canon 存档，不会反复询问）。真机全流程
  验证（含选"否"的列流开局与布局档还原逐像素回归）。
- **"显示/隐藏原生图标"定性**：当前不透明架构下原生图标被我们全盖住，开关视觉无效但
  不能删——将来壁纸透传（真透明）时必须靠它藏原生层。已注明"调试用"住进设置。
- Use Stacks：机主想要，已答复可做但属大件（分堆渲染/展开交互/布局语义），单独排期。

## 2026-07-07 中午：Use Stacks v1 + 设置导航 bug + Give access to 默认屏蔽（真机全验证）

- **设置导航 bug（机主报告）**：`_blacklist` 列表控件是字段单例，重进"右键菜单"页时
  仍是上一页面的逻辑子元素，重挂载抛 "already the logical child" 被 Dispatcher 兜底吞掉
  → 页面点不进去。修 = 页内控件全部每次建页新建。**教训：代码构 UI 的页面切换，控件
  绝不能做字段单例。**
- **Give access to 默认屏蔽**：桌面场景纯噪音的网络共享向导 → 默认黑名单加
  "Give access to"+"授予访问权限"（中英双杀），机主现有 settings.json 已补丁。
- **Use Stacks v1（macOS 桌面叠放）**：
  - 语义 = macOS：叠放是**自动整理模式**——文件按类型聚堆（应用程序/图片/文档/压缩包/
    其他），文件夹与回收站保持独立，右上列流自动排布；**开启期间不写规范布局**，关闭
    即按 Canon 精确恢复原摆放（真机验证零损）。
  - 堆视觉：两片右上错位半透明背板 + 顶层成员图标 + 类型标签（展开时标签强调色高亮），
    tooltip 显示件数。点击堆展开成员到后续列流格（真实图标，可开可拖出），再点收起。
  - **菜单语义随开关切换（机主点名借鉴 macOS）**：未叠放 = 使用叠放/排序方式▸/整理/撤销；
    已叠放 = ✓使用叠放/分组依据▸（v1 只有"类型"）。v2 与旧 host 菜单同构。
  - 守卫：交互路径（命中/框选/全选/键盘导航/首字母/Tab 顺移/拖放目标）统一走
    VisibleIcons（堆内折叠项 Collapsed 不参与）；RepositionAt/PreassignDropPositions
    在叠放模式下不写布局。
  - **坑：pile 只挂 MouseUp 点不动**——按下事件冒泡到画布启动框选并夺走鼠标捕获，
    Up 到不了 pile。修 = pile 的 MouseLeftButtonDown 先 e.Handled=true（与图标同款）。
  - 真机：开启→三堆聚拢；点开"应用程序"堆 20+ 快捷方式列流展开、标签高亮；收起；
    菜单语义切换正确；关闭→布局精确回归。

## 2026-07-07 傍晚：Stacks 展开/收起动画 P0 + 真实图标扇形观感（真机全验证）

机主反馈两件事一并做：P0 动画根因已在上一会话诊断（见本文件 Backlog 历史），本批实现；
机主截图对照 macOS 指出堆叠视觉细节差距（收起该是真实图标叠加，不该是纯色圆角矩形；
"其他"这类无连贯身份的分组该是语义占位图标）。

- **动画根因回顾**：收起的堆成员只设 `Visibility=Collapsed`，Canvas 坐标从未挪到堆位——
  下次展开时是从"叠放前的旧桌面位置"飞向目标格，观感是从天而降而非"从堆里出来"。
- **修法（`LayoutStacks` 状态机化）**：每个成员按"这一帧该不该可见"与"上一帧是否可见"
  （`Visibility` 本身就是唯一需要的状态，不用额外字段）分四路：
  - 展开且原本收起/展开且原本已展开：`Visibility=Visible` 后从**堆位**飞向目标格
    （坐标此前已静默吸附在堆位——见下一条，天然形成"从堆里展开"）。
  - 刚收起（含首次开启叠放，成员原本是桌面上的正常可见图标）：先动画飞回堆位，
    350ms 播完才 `Collapsed`（`DispatcherTimer`，非动画通道/冷启动直接落位不等）。
  - 保持收起：坐标静默吸附到（可能因增删堆而变化的）堆位，不触发可见动画——保证下次
    展开时"停靠点"始终是当前堆位，不是某次历史位置。
  - **重入免疫靠"定时器触发时重新核验"而非取消旧定时器**：收起批次的 350ms 回调触发时
    重新读一次 `_expandedStack`/`Config.UseStacks`，只有此刻仍该收起才真的 `Collapsed`；
    快速展开/收起/展开三连击（真机注入验证）不会把某批成员卡成"飞到堆位后永久卡住可见"。
    多个独立批次的定时器互不干扰（早先设计是共享单一定时器互相 Stop，会误杀无关堆的
    收起——改成每批次各自局部定时器后规避）。
- **真实图标扇形**（替换"两片纯色圆角矩形背板"）：非"其他"分组用最多 3 个**真实成员
  图标**（`IconLoader.Load` 实际缩略图/文件类型图标）斜向叠放（前锚左下 60px 满不透明、
  中 53px 不透明 0.88 右上偏移、后 46px 不透明 0.72 更偏右上），每层小阴影帮衬深浅分离
  （很多图标本身透明底，紧贴壁纸单靠透明度分不出层次）。只在成员数 ≥2/≥3 时显示中/后层。
  加了按路径缓存（`FrontPath/MidPath/BackPath`）避免每次 `LayoutStacks` 重排都重新取图
  （取图是较贵的 shell COM 往返，三层不缓存会是原先开销的 3 倍）。
- **语义占位**（"其他"混杂兜底分组，没有连贯的单一缩略图可代表）：保留原先两片叠层
  卡片背板（这本就是"泛用一沓文档"的观感），新增居中深色圆角徽标 + 白色 V 形展开箭头
  （`System.Windows.Shapes.Path` 手画几何，不依赖字体字形），对照 macOS Finder 的
  "Other · N items" 泛用堆图标。
- **可见双行标签**：堆下方标签从"仅类别名（件数只在 tooltip）"改成
  `"{类别}\n{N} 项"` 两行文本，直接复用现有单个 TextBlock（`\n` 天然换行，无需拆分控件），
  对照 macOS 截图里"Other / 14 items"两行都可见的观感。
- 真机验证（home-win，1080p@100% 主屏）：开启叠放→应用程序(22)/文档(3)/其他(1) 三堆聚拢，
  真实图标扇形清晰可辨（Photoshop 前 + 粉紫/绿色图标背后错落，Sublime 前 + 橙/白背后错落）；
  "其他"堆正确显示卡片+箭头徽标；点开文档堆→3 个真实文件图标落在正确目标格（非旧位置）；
  收起→正确隐藏无残留；展开/收起/展开 350ms 内三连击压力测试→最终态正确落在展开态、
  无卡死/重复/错位图标；关闭叠放→规范布局逐像素精确回归。

## 2026-07-07 傍晚 II：Stacks v2——分组依据（日期/大小）+ hover 刮擦预览（真机全验证）

P1 backlog 里机主已认可方向的两项一并做（菜单 ID 早留了 0x700E 起）：

- **分组依据扩展**：`LayoutStacks` 原先硬编码 `StackKindOf`/`StackKindTable`，抽成
  `StackGrouping()` 返回 `(Classify 函数, 档位顺序)`，按 `Settings.StackGroupBy`
  （"kind"/"date"/"size"，settings.json 持久化）切换。新增：
  - **修改日期**（`StackDateBucketOf`）：今天/昨天/本周/本月/更早，`File.GetLastWriteTime` 距今天数分档。
  - **大小**（`StackSizeBucketOf`）：小型(<1MB)/中型(1–100MB)/大型(100MB–1GB)/超大型(>1GB)。
  - **关键设计**：日期/大小分组下**没有"其他"式的混杂兜底概念**——每一档都是真实文件的
    正常聚合（"其他"专属于"类型"分组里没法归类的文件）。`UpdatePileVisual` 的语义占位
    判据改成 `Config.StackGroupBy=="kind" && kind==OtherKind`，日期/大小的每一档恒用真实
    图标扇形。菜单"分组依据"子菜单三选一打勾（背景菜单两条路径——`ShellContextMenu.cs`
    旧 host 与 `NativeMenuPresenter.cs` 当前主进程路径——同步更新，命令走
    `CommandChannel.Signal("GroupKind"/"GroupDate"/"GroupSize")`）。
  - 切换分组维度时旧堆名字在新维度里不存在，复用 P0 批已加的"展开中堆消失即清空
    `_expandedStack`"逻辑 + 成员按 wasVisible 状态机自动静默重新落位——不用特殊处理。
- **hover 刮擦预览（macOS scrub）**：鼠标横向划过堆时前层图标实时轮换预览成员真实内容，
  移出复位。**零额外取图开销**——直接借用成员桌面图标自己常驻的 `IconPlate.Child`
  (Image).Source，不重新调 `IconLoader.Load`（其余三层扇形已有路径缓存，见上批）。
  `PileVisual` 加 `Members`/`IsGeneric`/`ScrubIndex`/`RestingFrontIcon` 四个状态字段，
  `MouseMove` 按 `e.GetPosition(plate).X / plate.Width` 算比例转 index（仅 index 变化才
  真的换 Source，避免每像素都刷新）；`MouseLeave` 复位到 `RestingFrontIcon`。只对非"其他"
  且成员数 ≥2 的堆生效（语义占位没有真实内容可刮，单件堆没有"轮换"意义）。
- **顺手验证 P1 遗留项**：展开态下新文件加入的重排平滑性——真机往 Desktop 文件夹现场
  写一个 .txt（展开着"文档"堆的时候），新文件按字母序正确插入列流、其余成员平滑下移，
  无叠图/无消失；删除测试文件后堆计数正确回落。**结论：机制确实早就在堆里了，本次是
  真机确认，不是新代码。**
- 真机验证矩阵（home-win）：类型→修改日期（应用程序22/文档3/其他1 → 本周5/更早21，
  真实图标扇形，无占位）→大小（小型25/超大型1，超大型单件无扇形符合设计）→切回类型
  （三堆精确复原，"其他"占位符还在）；刮擦左边缘→右边缘前层图标从猫脸切换成人像头像、
  移出鼠标复位回猫脸；展开态插入新文件平滑；全程收尾→规范布局逐像素回归。**测试插曲**：
  一次菜单坐标复用了上一屏的旧坐标点歪，误开了 Windows 设置的个性化背景页——**验证了
  "每轮测试前重新截图"这条铁律的必要性，不能凭上一次坐标 assume 子菜单状态**。

## 2026-07-07 傍晚 III：设置窗深色主题 + 换模型复查揪出的两处隐患（真机全验证）

backlog 的"设置窗口深色主题适配"（机主系统是深色 app 模式，设置窗此前恒浅色）+
Fable 5 接手后对前两批的批判性复查产出的两处修复，一批交付：

- **设置窗深色主题**：`DetectDarkMode` 读注册表 `AppsUseLightTheme`（开窗时现读一次即可，
  不监听实时切换——主题切换时设置窗通常没开着）；十三个主题画刷元组一次解构（侧栏/内容/
  卡片/描边/次要字/正文/按钮三态/输入底/输入描边/强调环/危险红），深浅两套。
  `Foreground` 是 WPF 继承属性——窗口设一次，子树里没单独设色的 TextBlock 全部自动跟随，
  不用挨个改。标题条走 DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`（attr=20，旧系统 catch 掉）。
  按钮整窗隐式样式（`Resources[typeof(Button)]`）：圆角 Border 模板 + hover 微亮 +
  Padding 绑 TemplatedParent（各按钮原有 padding 全保留，7 处调用点零改动）。
- **深色 ComboBox 必须重模板（像素采样实锤）**：黑名单页的目录下拉在深色下仍是白块——
  眼看不确定，对截图像素采样定案：ListBox 内部 rgb(40,40,43) ✓、TextBox rgb(35,35,38) ✓
  （= FieldBg，原生 TextBox 吃 Background），**ComboBox rgb(233,233,233) ✗——WPF 原生
  ComboBox 的 chrome 不吃 Background 属性**。修 = 最小重模板（ToggleButton 铺满做主体
  Border+手画箭头、ContentPresenter 绑 SelectionBoxItem 不截点击、Popup 深色下拉），
  **只在深色时套用**（浅色保持已验证过的原生观感）。修后采样 rgb(35,35,38) ✓；
  下拉弹开/选项点选/选中回显全链路真机验证。键盘展开等细节从简——该控件只做鼠标挑选。
  **教训：深浅色这类"看得见"的验证要像素采样，肉眼在压缩截图上会把深色 TextBox 看成白的。**
- **复查修复①（刮擦 × 重排的状态捕获 bug）**：`UpdatePileVisual` 原先无条件
  `RestingFrontIcon = IconFront.Source`——若重排发生在刮擦进行中（悬停时来了新文件），
  `SetLayerIcon` 的路径缓存命中会跳过重载，把**刮擦帧**误捕获成静息图，此后前层永久卡在
  错的成员上。修 = 进 SetLayerIcon 前先把前层复位到旧静息图再走正常流程。真机验证：
  悬停刮到 Word 图标 → 造 zz_scrub_probe.txt 触发重排（件数 3→4 实时更新）→ 移开 →
  前层正确回到 members[0]（Sublime txt 图标），不再卡 Word 帧。
- **复查修复②（收起定时器捕获旧堆名）**：350ms 延迟隐藏的核验原先用捕获的堆名字符串，
  窗口内切换分组依据会让旧堆名在新维度失效而误判。修 = 触发时用当前 `StackGrouping()`
  现算成员归属（顺带把 justCollapsing 简化成纯成员列表）。属理论级时序（350ms 内完成
  菜单换分组人手做不到），但修复零成本、逻辑严密性归位。
- 全部收尾后桌面逐像素复原（叠放关、探针文件删、设置窗关）。

## 2026-07-07 下午 II：Stacks 手感返工 + 深色可读性（机主实测反馈全修，真机验证）

机主亲测后五条反馈（日志实锤机主 13:40-45 在真机上连点叠放试玩过——那段"神秘活动"
就是反馈的来源；期间机主还把两个 Jellyfin 拖去了 TV 屏，canon 迁移是有意为之不是损坏）：

- **刮擦改滚轮驱动（语义纠正）**：macOS scrub = 悬停 + 滚轮/双指滚动才轮换，悬停移动
  不轮换（Finder 调研笔记也印证"双指刮擦"）。旧实现 MouseMove 按横向比例轮换 = 鼠标
  轻微移动就疯狂换图。改 `MouseWheel`（Delta 符号定方向，模轮回绕），`e.Handled=true`
  防漏给画布。真机：悬停微移不换 ✓，滚一格 Ps→猫脸 ✓，移开复位 ✓。
- **展开后原堆位 = 收起按钮（语义纠正）**：机主首条消息里红框截图的语义占位（叠层
  卡片+向下箭头）其实是 macOS **展开态**的原位占位，不是"其他"分组的常态图标——
  上一批理解错了。重构：`FanGroup`（三层真图标扇形）+ `CardGroup`（卡片+箭头）两组
  常驻可视树，按展开态 160ms 交叉渐隐切换；**收起的堆一律真图标扇形（含"其他"）**，
  展开的堆显示收起按钮。IsHitTestVisible 跟随切换防误点暗侧。
- **被挤走的堆没动画**：堆 Root 原先直接 `Canvas.SetLeft/SetTop` 瞬移。抽通用
  `MoveElement(el, l, t, animated, ease, ms)`（MoveIcon 变薄壳），堆位移与图标同款
  滑动动画。真机中途帧实锤"其他"堆在向上滑行。
- **收进堆的渐隐（机主点名的 mac 灵魂细节）**：收起飞行 300ms 缓入（加速被吸入），
  行程后 60% 里 Opacity→0（`FadeTo` 带 BeginTime），到位时已无形，Collapsed 无跳变；
  展开反向：0.2→1 渐显 200ms。隐藏后 `ResetOpacity` 归位（剪切态 0.5 例外保留）。
  真机中途帧实锤半透明飞行成员。
- **动画曲线整体重调（机主：生硬、缺 mac 灵魂）**：通用重排 CubicEase→
  `ExponentialEase(5) EaseOut` 400ms（前快后极缓，接近 spring 收尾）；堆展开 =
  `BackEase(0.25) EaseOut` 430ms（轻微过冲的"甩出来"）；收起 = `CubicEase EaseIn`
  300ms。曲线常量集中在 `EaseGlide/EaseSpringOut/EaseInhale`。
- **深色可读性两处**：①侧栏文本不白 = **ListBoxItem 是 Control 自带 Foreground 默认值，
  掐断窗口级 Foreground 继承链**——NavItemStyle 里显式补 Setter；②CheckBox 换成
  **macOS 系统设置同款开关**（胶囊+白钮，开=强调色/关=灰，160ms 滑动+变色动画，
  浅深两套自动适配）——原生 CheckBox 深色不适配且没有 mac 观感。
- **测试方法论教训**：①中途帧抓拍 = `curl /ps &` 后台发点击 + 前台 sleep 0.3 再截屏
  （单次往返 ~1s 抓不到 300-430ms 动画）；②机主现在会随时上真机（13:40 那段），
  注入前必查 idle + 测试后**把桌面还原成机主离开时的状态**（本轮 = 叠放开着）。

## 2026-07-07 下午 III：分辨率原地平滑交接（P2 硬骨头拿下，双向真机验证）

旧方案 = 显示变化 → 主进程自杀 → 看门狗拉新实例 → **~1s 裸桌面闪屏**。活体改尺寸
不可救是定案（WPF 子窗口布局账本卡死，多轮实测）——但"闪屏"根源不是重启本身，而是
**老窗口退场早于新窗口就绪**。新方案（`Services/Handoff.cs` + App/MainWindow/Watchdog 改造）：

- **交接流程**：显示变化（debounce 700ms）→ 老进程写位置种子（每窗口图标 DIU 坐标 +
  工作区尺寸）→ 释放全局热键（新进程才能注册）+ 释放单实例互斥体 → spawn
  `--handoff <oldPid>` 替身 → **老进程窗口原地撑住画面**；替身走"启动时挂载"的可靠
  路径在新分辨率就绪 → 全部窗口挂载+首排完成 → 置 `MacDesk.HandoffReady` 命名事件 →
  老进程 SignalCleanQuit（退休自己的看门狗）后退出；替身等老进程真死后拉起自己的
  看门狗（`EnsureRunning` 改返回 bool，轮询到老看门狗放锁）。替身 15s 未就绪 = 老进程
  按旧路径退出，看门狗兜底，永不比旧方案差。
- **macOS 式 morph**：替身首排前把种子里的图标先放到"按新旧工作区比例映射"的起点
  （非动画），再 `LayoutAll(animated:true)` 滑向锚距推导位——分辨率切换从跳变变成
  图标原地滑移。叠放模式下成员从种子位飞进堆 = 天然聚拢动画。种子文件 30s 时效，
  读完即删。
- **真机双向验证**（TV 4096×2160@225% ↔ 1920×1080@100%，双屏混合 DPI）：
  - 去程：14:21:31.30 变化 → 31.53 替身起（230ms）→ 33.40 两窗口就绪+morph
    （TV 2/2 图标 1.05×1.12 缩放滑移，Dell 26/26 原位）→ Ready → 老进程退。
    **全程 2.1s 老窗口盖着，零裸桌面。**
  - 回程：链式正确——第一代替身自己发起第二次交接，第三代接管；看门狗每代自动换防
    （pid 4956→9076→12224），procs 恒 3，布局/叠放/壁纸镜像全程完好。
- **已知小瑕疵 + 已修未部署**：交接重叠期新旧 MenuHost 抢同名管道刷
  "All pipe instances are busy"（~17 行/10ms）且一个 dir 探针在混战中误判降级。
  修 = 接管方推迟 `MenuHost.EnsureSpawned` 到老进程退场后（代码已提交，**当前部署的
  二进制不含此修**——当时机主回到了真机旁，避免 --quit 重启闪屏打扰，下个部署窗口生效）。
- **单实例互斥体细节**：老进程在 spawn 前主动 ReleaseMutex（同线程约束：UI 线程获取
  UI 线程释放）；恢复/交接实例撞活体互斥体时安静退让不弹框（旧代码无条件 MessageBox）。
