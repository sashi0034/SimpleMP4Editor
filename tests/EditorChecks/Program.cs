using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimpleMP4Editor;

namespace EditorChecks;

internal static class Program
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Directory.CreateTempSubdirectory("simplemp4-check-");
        try
        {
            CheckGeometry();
            var sample = CheckVideosAsync(folder.FullName).GetAwaiter().GetResult();
            CheckWindow(sample, args.Length == 1 ? args[0] : null);
            Console.WriteLine("PASS  crop geometry, WebM input, decoded MP4 output, audio, file handling, WPF layout");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { folder.Delete(true); }
    }

    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }

    private static void CheckGeometry()
    {
        var r = new PixelRect(10, 20, 80, 60);
        Check(r.Adjust(Grip.Left, 1, 9, 100, 80, false) == r with { Left = 11 }, "single left edge");
        Check(r.Adjust(Grip.Right, -10, 0, 100, 80, true) == new PixelRect(20, 20, 70, 60), "mirror horizontally");
        Check(r.Adjust(Grip.Left|Grip.Bottom, -100, 100, 100, 80, true) == new PixelRect(0, 0, 90, 80), "corner mirror at bounds");
        Check(r.Adjust(Grip.Top, 0, 1, 100, 80, false) == r with { Top = 21 }, "single top edge");
        Check(r.Adjust(Grip.Top, 0, 1000, 100, 80, true).Height == 2, "prevent inversion");
        Check(r.Adjust(Grip.Move, -100, 100, 100, 80, false) == new PixelRect(0, 40, 70, 80), "move clamped");
    }

    private static Task<byte[]> Ffmpeg(params string[] args) =>
        MediaTools.RunAsync("ffmpeg", ["-nostdin", "-v", "error", .. args], Token);

    private static async Task<byte[]> Pixels(string path) =>
        await Ffmpeg("-i", path, "-an", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");

    private static void Color(byte[] rgb, int w, int h, int frame, int x, int y, int red, int green, int blue)
    {
        int p = ((frame * h + y) * w + x) * 3;
        Check(Math.Abs(rgb[p] - red) < 30 && Math.Abs(rgb[p+1] - green) < 30 && Math.Abs(rgb[p+2] - blue) < 30,
            $"Frame {frame} ({x},{y}) expected RGB({red},{green},{blue}), got ({rgb[p]},{rgb[p+1]},{rgb[p+2]})");
    }

    private static async Task<Clip> CheckVideosAsync(string dir)
    {
        string source = Path.Combine(dir, "日本語 テスト映像.mp4"), tall = Path.Combine(dir, "縦.mp4");
        await Ffmpeg("-f", "lavfi", "-i", "color=red:s=96x64:r=12:d=2,drawbox=x=48:y=0:w=48:h=32:c=lime:t=fill,drawbox=x=0:y=32:w=48:h=32:c=white:t=fill,drawbox=x=48:y=32:w=48:h=32:c=blue:t=fill",
            "-f", "lavfi", "-i", "sine=f=500:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        await Ffmpeg("-f", "lavfi", "-i", "color=yellow:s=40x80:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", tall);
        var first = new Clip(await MediaTools.ProbeAsync(source, Token)) { Crop = new(1, 0, 49, 32) };
        first.SetBoundary(true, .5); first.SetBoundary(false, 1.5);
        var second = new Clip(await MediaTools.ProbeAsync(tall, Token));
        second.SetBoundary(false, .5);
        var last = first.Copy(); last.Crop = new(48, 32, 96, 64); last.SetBoundary(true, 1.25);
        Check(first.Length == 1 && last.Length == .25 && first.Crop != last.Crop, "independent duplicate");
        Clip[] sequence = [first, second, last];
        string outFile = Output.DefaultPath(sequence);
        Check(outFile.Contains("_ほか2本_結合"), "join filename");
        Check(Output.DefaultPath([first]).Contains("00m00s-00m01s"), "range filename");
        foreach (bool contain in new[] { true, false })
        {
            await MediaTools.ExportAsync(sequence, contain, outFile, null, Token, overwrite: !contain);
            var info = await MediaTools.ProbeAsync(outFile, Token);
            Check(info.Width == 48 && info.Height == 32 && info.HasAudio, "output dimensions and audio");
            var pixels = await Pixels(outFile);
            Check(pixels.Length == 21 * 48 * 32 * 3, "exact length at mixed fps");
            for (int i = 0; i < 21; i++)
            {
                if (i < 12) Color(pixels, 48, 32, i, 20, 12, 255, 0, 0);
                else if (i < 18) Color(pixels, 48, 32, i, 20, 12, 255, 255, 0);
                else Color(pixels, 48, 32, i, 20, 12, 0, 0, 255);
            }
            Color(pixels, 48, 32, 12, 2, 10, contain ? 0 : 255, contain ? 0 : 255, 0);
            var pcm = await Ffmpeg("-i", outFile, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1");
            Check(MeanVolume(pcm, .25) > 500 && MeanVolume(pcm, 1.1) < 30 && MeanVolume(pcm, 1.55) > 500, "audio alignment across silent clip");
        }

        string rotated = Path.Combine(dir, "回転.mp4");
        await Ffmpeg("-display_rotation:v:0", "90", "-i", source, "-c", "copy", rotated);
        var rot = new Clip(await MediaTools.ProbeAsync(rotated, Token));
        Check(rot.Media.Width == 64 && rot.Media.Height == 96, "rotation metadata");
        rot.Crop = new(32, 0, 64, 48); rot.SetBoundary(false, .5);
        string rotOut = Output.DefaultPath([rot]);
        await MediaTools.ExportAsync([rot], false, rotOut, null, Token);
        var rotatedPixels = await Pixels(rotOut);
        Color(rotatedPixels, 32, 48, 0, 16, 24, 0, 0, 255);
        Check((await MediaTools.StillAsync(rot.Media, 0, 128, Token)).Length > 0, "rotation preview");
        string proxy = Path.Combine(dir, "proxy.mp4");
        await MediaTools.MakeProxyAsync(rot.Media, proxy, Token);
        var proxyInfo = await MediaTools.ProbeAsync(proxy, Token);
        Check(proxyInfo.Width == 64 && proxyInfo.Height == 96 && !proxyInfo.NeedsProxy, "playback proxy orientation");

        // A one-pixel white column and a change halfway through distinguish the exact
        // crop origin and the chosen time interval from a rounded or untrimmed export.
        string stripe = Path.Combine(dir, "pixel-and-time.mp4");
        await Ffmpeg("-f", "lavfi", "-i", "color=black:s=48x32:r=10:d=2,format=yuv444p,drawbox=x=7:y=0:w=1:h=32:c=white:t=fill:enable='gte(t,1)'",
            "-c:v", "libx264", "-crf", "0", stripe);
        var edge = new Clip(await MediaTools.ProbeAsync(stripe, Token)) { Crop = new(7, 0, 23, 16) };
        edge.SetBoundary(true, 1); edge.SetBoundary(false, 1.5);
        string stripeOut = Output.DefaultPath([edge]);
        await MediaTools.ExportAsync([edge], false, stripeOut, null, Token);
        var columns = await Pixels(stripeOut);
        Check(columns.Length == 5 * 16 * 16 * 3, "exact trimmed interval");
        for (int frame = 0; frame < 5; frame++)
        {
            Color(columns, 16, 16, frame, 0, 8, 255, 255, 255);
            Color(columns, 16, 16, frame, 1, 8, 0, 0, 0);
        }

        Check(Output.DefaultPath(sequence).EndsWith("_2.mp4"), "collision naming");
        try { await MediaTools.ExportAsync([first], true, source, null, Token, overwrite: true); throw new Exception("input overwritten"); }
        catch (IOException) { }
        byte[] previous = await File.ReadAllBytesAsync(outFile);
        using var stop = new CancellationTokenSource();
        var interrupted = MediaTools.ExportAsync(sequence, true, outFile, null, stop.Token, overwrite: true);
        stop.Cancel();
        try { await interrupted; throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        byte[] retained = await File.ReadAllBytesAsync(outFile);
        Check(previous.SequenceEqual(retained), "existing file preserved");
        Check(!Directory.EnumerateFiles(dir, ".simplemp4-*").Any(), "partial file cleaned");
        await CheckWebMAsync(dir, second);
        return first;
    }

    private static async Task CheckWebMAsync(string dir, Clip silentMp4)
    {
        string source = Path.Combine(dir, "画面録画 テスト.WEBM");
        await Ffmpeg("-f", "lavfi", "-i", "color=red:s=64x32:r=12:d=2,drawbox=x=32:y=0:w=32:h=32:c=lime:t=fill",
            "-f", "lavfi", "-i", "sine=f=700:d=2", "-c:v", "libvpx-vp9", "-c:a", "libopus", "-live", "1", source);
        var media = await MediaTools.ProbeAsync(source, Token);
        Check(media.NeedsProxy && media.HasAudio && Math.Abs(media.Duration - 2) < .02, "WebM recording without header duration");
        Check((await MediaTools.StillAsync(media, .5, 128, Token)).Length > 0, "WebM seek preview");
        string proxy = Path.Combine(dir, "webm-preview.mp4");
        await MediaTools.MakeProxyAsync(media, proxy, Token);
        var preview = await MediaTools.ProbeAsync(proxy, Token);
        Check(!preview.NeedsProxy && preview.HasAudio, "WebM playback proxy");

        var clip = new Clip(media) { Crop = new(32, 0, 64, 32) };
        clip.SetBoundary(true, 1.25); clip.SetBoundary(false, 1.75);
        Clip[] mixed = [clip, silentMp4];
        string output = Output.DefaultPath(mixed);
        Check(Path.GetExtension(output) == ".mp4", "WebM default export extension");
        await MediaTools.ExportAsync(mixed, false, output, null, Token);
        var pixels = await Pixels(output);
        Check(pixels.Length == 12 * 32 * 32 * 3, "trimmed WebM and MP4 duration");
        Color(pixels, 32, 32, 5, 16, 16, 0, 255, 0);
        Color(pixels, 32, 32, 6, 16, 16, 255, 255, 0);
        var pcm = await Ffmpeg("-i", output, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1");
        Check(MeanVolume(pcm, .25) > 500 && MeanVolume(pcm, .75) < 30, "WebM audio followed by silent MP4");
    }

    private static double MeanVolume(byte[] pcm, double second)
    {
        int start = (int)(second * 48000);
        return Enumerable.Range(start, 1000).Average(i => Math.Abs((int)BitConverter.ToInt16(pcm, i * 2)));
    }

    private static void CheckWindow(Clip sample, string? screenshot)
    {
        var app = new App();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        app.InitializeComponent();
        var window = new MainWindow();
        window.ApplyTemplate();
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1140, 760)); content.Arrange(new Rect(0, 0, 1140, 760)); content.UpdateLayout();
        var list = (ListBox)window.FindName("ClipList");
        var items = (System.Collections.ObjectModel.ObservableCollection<Clip>)list.ItemsSource;
        items.Add(sample); list.SelectedIndex = 0;
        window.UpdateLayout();
        Check(((TextBlock)window.FindName("CropLabel")).Text == "48 × 32 px", "selection details");
        Check(((Button)window.FindName("ExportButton")).IsEnabled, "export enabled");
        var preview = (CheckBox)window.FindName("ResultMode");
        preview.IsChecked = true; preview.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(((CropOverlay)window.FindName("Overlay")).Visibility == Visibility.Collapsed, "result preview");
        if (screenshot != null)
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(Task.Run(() => MediaTools.StillAsync(sample.Media, 0, 1280, Token)).GetAwaiter().GetResult());
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
            ((Image)window.FindName("Still")).Source = image;
            preview.IsChecked = false; preview.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            content.Measure(new Size(1140, 760)); content.Arrange(new Rect(0, 0, 1140, 760)); content.UpdateLayout();
            var bmp = new RenderTargetBitmap(1140, 760, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var output = File.Create(screenshot); png.Save(output);
        }
        app.Shutdown();
    }
}
