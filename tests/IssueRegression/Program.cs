using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MacDesk;
class Checks
{
    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    static BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, F)!.Invoke(o, args)!;
    [STAThread]
    static void Main()
    {
        var a = typeof(MainWindow).Assembly;
        var st = a.GetType("MacDesk.Services.Settings")!;
        var config = Activator.CreateInstance(st, F, null, new object[] { Path.Combine(Path.GetTempPath(), "unused-macdesk-settings.json") }, null)!;
        a.GetType("MacDesk.Desktop")!.GetField("<Config>k__BackingField", F)!.SetValue(null, config);
        typeof(App).GetField("<Transparent>k__BackingField", F)!.SetValue(null, true);
        var win = (MainWindow)Activator.CreateInstance(typeof(MainWindow), F, null, new object?[] { null, false }, null)!;
        string[] names = { "Visual Studio Code.lnk", "这是一个很长很长包含中文的文件名称用于验证末尾不会再次省略.txt", new string('W', 120) + ".psd", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.pdf", "🧑‍💻" + new string('中', 60) + ".ai" };
        int failures = 0, total = 0;
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0, 3.0 }) foreach (string weight in new[] { "regular", "semibold", "bold" }) foreach (int size in new[] { 32, 48, 64, 96, 160 }) foreach (int fontSize in new[] { 9, 12, 16, 24 }) foreach (string font in new[] { "Segoe UI", "Microsoft YaHei UI", "Arial" }) foreach (string name in names)
        {
            VisualTreeHelper.SetRootDpi(win, new DpiScale(dpi, dpi)); st.GetProperty("IconFontWeight")!.SetValue(config, weight);
            st.GetProperty("IconSize")!.SetValue(config, size); st.GetProperty("IconLabelSize")!.SetValue(config, fontSize); st.GetProperty("IconFontFamily")!.SetValue(config, font);
            string text = (string)Call(win, "TruncateLabel", name);
            foreach (var rune in text.EnumerateRunes())
                if (rune.Value == 0xFFFD) throw new Exception("Truncation split a Unicode character");
            if (!text.EndsWith(Path.GetExtension(name), StringComparison.Ordinal))
                throw new Exception("Truncation lost the filename extension");
            double width = (double)typeof(MainWindow).GetProperty("CellW", F)!.GetValue(win)! - 14 * size / 64.0;
            double height = (double)typeof(MainWindow).GetProperty("LabelHeight", F)!.GetValue(win)!;
            var label = new TextBlock { Text = text, FontFamily = new FontFamily(font), FontSize = fontSize, FontWeight = weight == "regular" ? FontWeights.Regular : weight == "semibold" ? FontWeights.SemiBold : FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            VisualTreeHelper.SetRootDpi(label, new DpiScale(dpi, dpi));
            TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display); label.Measure(new Size(width, double.PositiveInfinity)); total++;
            if (label.DesiredSize.Height > height + 0.5) { failures++; if (failures < 8) Console.WriteLine($"overflow size={size} font={font}/{fontSize} height={label.DesiredSize.Height}/{height} text={text}"); }
        }
        Console.WriteLine($"Label layout: {total} cases, {failures} overflow failures");
        VisualTreeHelper.SetRootDpi(win, new DpiScale(1, 1));
        st.GetProperty("IconSize")!.SetValue(config, 64); st.GetProperty("IconLabelSize")!.SetValue(config, 12);
        var grid = (Grid)typeof(MainWindow).GetField("RootGrid", F)!.GetValue(win)!;
        var canvas = (Canvas)typeof(MainWindow).GetField("IconCanvas", F)!.GetValue(win)!;
        grid.Measure(new Size(1280, 800)); grid.Arrange(new Rect(0, 0, 1280, 800));
        string dir = Path.Combine(Path.GetTempPath(), "MacDeskChecks-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string longName = new string('中', 90) + ".txt", path = Path.Combine(dir, longName); File.WriteAllText(path, "");
            var en = new MacDesk.Services.DesktopEntry(path, longName);
            var iv = Call(win, "CreateIconVisual", en, true);
            var root = (Border)iv.GetType().GetField("Root", F)!.GetValue(iv)!;
            canvas.Children.Add(root);
            foreach (double y in new[] { 100.0, 680.0 }) foreach (double x in new[] { 0.0, 500.0, 1184.0 })
            {
                Canvas.SetLeft(root, x); Canvas.SetTop(root, y); grid.UpdateLayout();
                var plate = (Border)iv.GetType().GetField("IconPlate", F)!.GetValue(iv)!;
                Point before = plate.TranslatePoint(new Point(0, 0), grid);
                Call(win, "StartRename", iv); grid.UpdateLayout();
                var box = (TextBox)typeof(MainWindow).GetField("_renameBox", F)!.GetValue(win)!;
                Point pos = box.TranslatePoint(new Point(0, 0), grid);
                if (box.Text != longName || box.ActualWidth < 200 || pos.X < -1 || pos.X + box.ActualWidth > 1281 || pos.Y + box.ActualHeight > 801 || plate.TranslatePoint(new Point(0, 0), grid) != before) throw new Exception("Rename expansion/edge/icon position failed");
                Console.WriteLine($"Rename at ({x},{y}): editor={box.ActualWidth}x{box.ActualHeight}, left={pos.X}, icon stays put");
                Call(win, "CancelRename"); grid.UpdateLayout();
                if (plate.TranslatePoint(new Point(0, 0), grid) != before) throw new Exception("Cancel did not restore layout");
            }
            Call(win, "StartRename", iv);
            ((TextBox)typeof(MainWindow).GetField("_renameBox", F)!.GetValue(win)!).Text = "renamed.txt";
            Call(win, "CommitRename"); if (!File.Exists(Path.Combine(dir, "renamed.txt"))) throw new Exception("Commit failed");
            canvas.Children.Remove(root);
            var loader = a.GetType("MacDesk.Services.IconLoader")!;
            object? Load(string p) => loader.GetMethod("Load", F)!.Invoke(null, new object[] { p, 256 });
            void Png(string p, byte r, byte b) { byte[] pixels = new byte[64 * 64 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 2] = r; pixels[i + 3] = 255; } var bmp = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 256); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var f = File.Create(p); encoder.Save(f); }
            // Use the installed Windows image thumbnail provider on a unique test association.
            const string ext = ".macdeskcheckthumb";
            const string handler = "{E357FCCD-A995-4576-B01F-234630154E96}";
            using var pngKey = Registry.ClassesRoot.OpenSubKey("SystemFileAssociations\\image\\ShellEx\\" + handler);
            string? clsid = pngKey?.GetValue(null) as string;
            if (clsid == null) throw new Exception("PNG provider unavailable");
            string keyPath = "Software\\Classes\\" + ext;
            if (Registry.CurrentUser.OpenSubKey(keyPath) != null) throw new Exception("Test association already exists");
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("PerceivedType", "image");
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath + "\\ShellEx\\" + handler)) key.SetValue("", clsid);
                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
                loader.GetMethod("ClearShared", F)!.Invoke(null, null);
                string red = Path.Combine(dir, "red" + ext), blue = Path.Combine(dir, "blue" + ext); Png(red, 255, 0); Png(blue, 0, 255);
                var r = (BitmapSource?)Load(red); var b = (BitmapSource?)Load(blue);
                if (r == null || b == null || ReferenceEquals(r, b)) throw new Exception("Third-party type thumbnail load/cache failed");
                byte[] rp = new byte[r.PixelWidth * r.PixelHeight * 4], bp = new byte[b.PixelWidth * b.PixelHeight * 4]; r.CopyPixels(rp, r.PixelWidth * 4, 0); b.CopyPixels(bp, b.PixelWidth * 4, 0);
                (int Red, int Blue) CountColors(BitmapSource source)
                {
                    byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                    source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                    int reds = 0, blues = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i + 2] > pixels[i] + 50 && pixels[i + 2] > pixels[i + 1] + 50) reds++;
                        if (pixels[i] > pixels[i + 2] + 50 && pixels[i] > pixels[i + 1] + 50) blues++;
                    }
                    return (reds, blues);
                }
                var redColors = CountColors(r); var blueColors = CountColors(b);
                if (redColors.Red <= redColors.Blue || blueColors.Blue <= blueColors.Red)
                {
                    string native = Path.Combine(dir, "native.png"); Png(native, 255, 0);
                    var nativeSource = (BitmapSource?)Load(native);
                    throw new Exception($"Thumbnail content mismatch: red={redColors}, blue={blueColors}, native PNG={(nativeSource == null ? "null" : CountColors(nativeSource).ToString())}, provider={clsid}");
                }
                Console.WriteLine("Unlisted extension with registered provider: two distinct content thumbnails PASS");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath); SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); loader.GetMethod("ClearShared", F)!.Invoke(null, null); }
            string t1 = Path.Combine(dir, "a.txt"), t2 = Path.Combine(dir, "b.txt"); File.WriteAllText(t1, ""); File.WriteAllText(t2, "");
            var textIcon = Load(t1);
            if (textIcon == null || !ReferenceEquals(textIcon, Load(t2))) throw new Exception("Text icon cache regression");
            Console.WriteLine("Plain text shared icon cache PASS; rename commit/cancel PASS");
        }
        finally { Directory.Delete(dir, true); }
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
