using System.IO;
using System.Text.Json;

namespace MacDesk.Services;

/// <summary>
/// 图标的规范位置（分辨率无关，macOS 模型）。存的是**逻辑单位(DIU)的锚定距离**：
///  - RightDist：图标中心到所属显示器工作区右缘的 DIU 距离（右锚）。
///  - EdgeDist ：到上缘（FromBottom=false）或下缘（true）的 DIU 距离（近边锚定）。
/// 分辨率变化时不改写，只按当前尺寸现场推导显示位置。
/// </summary>
public record CanonPos(double RightDist, double EdgeDist, bool FromBottom);

/// <summary>
/// 布局持久化 v4：**每显示器一份规范布局区**（macOS 语义：图标归属某台显示器）。
/// 同一图标只属于一台显示器（Set 会从其他区移除）；显示器不在场时其图标由主屏
/// 现场推导显示，但归属与坐标**永不改写**——重新接上即原样回归。
/// 文件：%LOCALAPPDATA%\MacDesk\layout.json
/// 结构：{"version":4,"monitors":{"<monKey>":{"a.lnk":{RightDist,EdgeDist,FromBottom}}}}
/// 迁移：v3（单一 icons 区）与 v2（按分辨率分档）都归入主显示器区。
/// </summary>
internal sealed class LayoutStore
{
    private readonly string _file;
    private readonly string _primaryKey;
    private Dictionary<string, Dictionary<string, CanonPos>> _monitors = new();

    public LayoutStore(string primaryKey)
    {
        _primaryKey = primaryKey;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "layout.json");
        Load();
    }

    private Dictionary<string, CanonPos> Section(string monKey) =>
        _monitors.TryGetValue(monKey, out var m) ? m : (_monitors[monKey] = new());

    /// <summary>任何显示器区都没有条目（首启/新装，OOBE 导入的门槛）。</summary>
    public bool IsEmpty => _monitors.All(m => m.Value.Count == 0);

    /// <summary>图标当前归属的显示器 key（不在任何区 → null）。</summary>
    public string? FindOwner(string name) =>
        _monitors.FirstOrDefault(kv => kv.Value.ContainsKey(name)).Key;

    public CanonPos? Get(string monKey, string name) =>
        _monitors.TryGetValue(monKey, out var m) && m.TryGetValue(name, out var p) ? p : null;

    /// <summary>写入图标位置并归属到 monKey（单一归属：从其他显示器区移除）。</summary>
    public void Set(string monKey, string name, CanonPos pos)
    {
        foreach (var (k, sec) in _monitors)
            if (k != monKey) sec.Remove(name);
        Section(monKey)[name] = pos;
    }

    public void Remove(string name)
    {
        foreach (var sec in _monitors.Values) sec.Remove(name);
        _missingListed.Remove(name);
    }

    /// <summary>全部布局条目名（跨显示器去重）。</summary>
    public IEnumerable<string> AllNames() => _monitors.SelectMany(m => m.Value.Keys).Distinct();

    // ── 问号占位名单（机主 spec：跨机导入的 missing 项渲染成 macOS 式问号，绝不擅自清）──
    // 只有"导入布局"动作会把当时缺文件的条目记进名单（persist 在 layout.json "missing"）；
    // 历史遗留的孤儿条目（正常使用中累积的）不进名单、保持隐形——真机检查发现现役档里
    // 已有 15 个这类孤儿，无差别渲染会让用户桌面突然冒一排问号。
    private HashSet<string> _missingListed = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> MissingListed => _missingListed;

    public void SetMissingList(IEnumerable<string> names)
    {
        _missingListed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        Save();
    }

    /// <summary>文件回来了（用户把东西拷到桌面）→ 除名，占位让位给真图标。</summary>
    public void UnlistMissing(string name) => _missingListed.Remove(name);

    public void ClearMonitor(string monKey) => Section(monKey).Clear();

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var root = doc.RootElement;

            if (root.TryGetProperty("monitors", out var mons) &&
                root.TryGetProperty("version", out var ver) && ver.GetInt32() >= 4)
            {
                _monitors = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CanonPos>>>(mons.GetRawText()) ?? new();
                if (root.TryGetProperty("missing", out var miss))
                    _missingListed = new HashSet<string>(
                        JsonSerializer.Deserialize<List<string>>(miss.GetRawText()) ?? new(),
                        StringComparer.OrdinalIgnoreCase);
            }
            else if (root.TryGetProperty("icons", out var icons))
            {
                // v3 单一规范布局 → 全部归入主显示器
                var flat = JsonSerializer.Deserialize<Dictionary<string, CanonPos>>(icons.GetRawText()) ?? new();
                _monitors[_primaryKey] = flat;
                Save();
            }
            else if (root.TryGetProperty("profiles", out var profs))
            {
                MigrateFromV2Profiles(root, profs); // v2 分档 → 主显示器
                Save();
            }
        }
        catch { _monitors = new(); }
    }

    private void MigrateFromV2Profiles(JsonElement root, JsonElement profs)
    {
        var profiles = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, LegacyNorm>>>(profs.GetRawText());
        if (profiles == null || profiles.Count == 0) return;
        string key = root.TryGetProperty("last", out var last) ? (last.GetString() ?? "") : "";
        if (!profiles.ContainsKey(key)) key = profiles.Keys.First();
        if (!TryParseSize(key, out double w, out double h)) { w = 1920; h = 1080; }
        var dst = Section(_primaryKey);
        foreach (var (name, p) in profiles[key])
            dst[name] = new CanonPos(w - p.X * w, p.Y * h, FromBottom: false);
    }

    private sealed record LegacyNorm(double X, double Y);

    private static bool TryParseSize(string key, out double w, out double h)
    {
        w = h = 0;
        var parts = key.Split('x');
        return parts.Length == 2 && double.TryParse(parts[0], out w) && double.TryParse(parts[1], out h) && w > 0 && h > 0;
    }

    public void Save()
    {
        try
        {
            var payload = new Dictionary<string, object> { ["version"] = 4, ["monitors"] = _monitors };
            if (_missingListed.Count > 0) payload["missing"] = _missingListed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(_file, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 磁盘写失败不致命 */ }
    }

    // ── 备份 / 导出导入（机主 spec，2026-07-07）────────────────

    private string BackupDir
    {
        get
        {
            var dir = Path.Combine(Path.GetDirectoryName(_file)!, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>滚动备份：当日尚无备份就落一份（layout-yyyyMMdd.json），保留最近 7 份。
    /// 启动时 + 每小时检查各调一次，防崩溃/升级兼容事故毁掉唯一的布局档。</summary>
    public void DailyBackup()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var today = Path.Combine(BackupDir, $"layout-{DateTime.Now:yyyyMMdd}.json");
            if (File.Exists(today)) return;
            File.Copy(_file, today);
            foreach (var old in Directory.GetFiles(BackupDir, "layout-????????.json")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(7))
                File.Delete(old);
            Log.Write($"layout daily backup -> {Path.GetFileName(today)}");
        }
        catch { /* 备份失败不致命 */ }
    }

    /// <summary>导出当前布局（先落盘再拷贝，导出的就是权威状态）。</summary>
    public void Export(string path)
    {
        Save();
        File.Copy(_file, path, overwrite: true);
    }

    /// <summary>导入布局文件：校验能解析出任一已知版本（v2/v3/v4）→ 当前布局先存
    /// 时间戳备份 → 替换 → 原地重载（Load 自带旧版本迁移）。失败返回 false 且不动现状。</summary>
    public bool TryImport(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("monitors", out _) &&
                    !root.TryGetProperty("icons", out _) &&
                    !root.TryGetProperty("profiles", out _)) return false;
            }
            Save();
            if (File.Exists(_file))
                File.Copy(_file, Path.Combine(BackupDir, $"layout-before-import-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
            File.WriteAllText(_file, text);
            _monitors = new();
            _missingListed = new(StringComparer.OrdinalIgnoreCase);
            Load();
            Log.Write($"layout imported from {path}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>破坏性操作（一键整理/排序）前调用：把当前布局存成撤销点。</summary>
    public void SnapshotForUndo()
    {
        try
        {
            Save();
            if (File.Exists(_file)) File.Copy(_file, _file + ".undo", overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// 撤销 = **交换**当前布局与撤销点（再点一次 = 恢复/redo）。
    /// 事故教训（2026-07-06）：旧实现直接用 .undo 覆盖当前布局且不备份——.undo 若是陈年
    /// 快照，一点撤销就把用户的现役布局毁掉且不可逆。交换语义永不丢数据。
    /// </summary>
    public bool TryUndo()
    {
        try
        {
            var undo = _file + ".undo";
            if (!File.Exists(undo)) return false;
            Save(); // 当前内存状态先落盘
            string current = File.ReadAllText(_file);
            File.Copy(undo, _file, overwrite: true);
            File.WriteAllText(undo, current);
            _monitors = new();
            Load();
            return true;
        }
        catch { return false; }
    }
}
