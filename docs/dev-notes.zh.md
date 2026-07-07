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
