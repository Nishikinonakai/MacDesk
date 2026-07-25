using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;

namespace MacDesk.Services;

/// <summary>
/// MacWidget 联动（feature/widget-avoid 原型）：命名管道接收小组件占用矩形
/// （屏幕物理像素、虚拟桌面坐标），图标层据此做 display-only 避让——Canon 神圣不动，
/// 所以组件挪走图标自动回位（macOS 行为 #4 白拿）。
/// 协议：行分隔 JSON {"rects":[[x,y,w,h],...]}；客户端断开 = 组件全撤 = 图标回位。
/// </summary>
public static class WidgetAvoid
{
    public const string PipeName = "MacDesk.WidgetAvoid.v1";

    private static volatile IReadOnlyList<System.Windows.Rect> _rects = Array.Empty<System.Windows.Rect>();
    public static IReadOnlyList<System.Windows.Rect> Rects => _rects;
    public static event Action? Changed;

    public static void Start() =>
        new Thread(ServerLoop) { IsBackground = true, Name = "WidgetAvoidPipe" }.Start();

    private static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                Log.Write("[widgetavoid] client connected");
                using var reader = new StreamReader(server);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var list = new List<System.Windows.Rect>();
                        foreach (var r in doc.RootElement.GetProperty("rects").EnumerateArray())
                            list.Add(new System.Windows.Rect(
                                r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble()));
                        Set(list);
                    }
                    catch { /* 坏行忽略，连接保持 */ }
                }
            }
            catch { Thread.Sleep(300); }
            finally
            {
                if (_rects.Count > 0) { Log.Write("[widgetavoid] client gone, clearing"); Set(new()); }
            }
        }
    }

    private static void Set(List<System.Windows.Rect> list)
    {
        // WidgetProto 每 3 秒送一次心跳，拖拽结束后矩形会原样重复。相同快照不应触发
        // 全桌面 LayoutAll（否则空闲时也会不断重排/重启动画）；断开清空仍会变化一次。
        var old = _rects;
        if (old.Count == list.Count)
        {
            bool same = true;
            for (int i = 0; i < list.Count; i++)
            {
                if (old[i] != list[i]) { same = false; break; }
            }
            if (same) return;
        }
        _rects = list;
        try { Changed?.Invoke(); } catch { /* 订阅者异常不掀桌 */ }
    }
}
