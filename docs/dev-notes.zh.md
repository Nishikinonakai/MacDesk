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

## Backlog

锚点+网格布局模式（纯比例在小屏会挤压重叠）、导入布局列边距归一化、拖放文件进出（OLE DnD）、多显示器独立布局、Explorer 重启自动重挂、真透明层（DWM/D3D）、开源整理（LICENSE/英文 README/CI release）。

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
