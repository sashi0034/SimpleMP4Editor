using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SimpleMP4Editor;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Clip> clips = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, string> proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "SimpleMP4Editor", Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? frameJob, proxyJob, exportJob;
    private Task? exporting;
    private Task? preparingProxy;
    private Clip? current;
    private double position;
    private bool importing, playing, playRequested, playerReady, usingProxy, closing, canClose;
    private bool Ready => IsInitialized && !closing;

    public MainWindow()
    {
        InitializeComponent();
        ClipList.ItemsSource = clips;
        Overlay.Changed += UpdateDetails;
        Track.Seek += Seek;
        Track.Boundary += SetBoundary;
        timer.Tick += (_, _) =>
        {
            if (!playing || current == null) return;
            position = Math.Clamp(Player.Position.TotalSeconds, 0, current.Media.Duration);
            if (position >= current.End) { Seek(current.End); return; }
            UpdateTime();
        };
        UpdateDetails();
    }

    private void FilesOver(object sender, DragEventArgs e)
    {
        e.Effects = !importing && exportJob == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddFilesAsync(paths);
    }

    private async void OpenFiles(object sender, RoutedEventArgs e)
    {
        if (importing || exportJob != null) return;
        var dialog = new OpenFileDialog { Filter = "MP4 動画|*.mp4", Multiselect = true, Title = "動画を追加" };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FileNames);
    }

    private async Task AddFilesAsync(string[] paths)
    {
        if (importing || exportJob != null || closing) return;
        importing = true;
        UpdateDetails();
        List<string> errors = [];
        Clip? first = null;
        try
        {
            foreach (string path in paths)
            {
                try
                {
                    Status.Text = $"読み込み中  {Path.GetFileName(path)}";
                    if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("MP4 ファイルを選んでください。");
                    var existing = clips.FirstOrDefault(c => string.Equals(c.Media.Path, path, StringComparison.OrdinalIgnoreCase));
                    var media = existing?.Media ?? await MediaTools.ProbeAsync(path, lifetime.Token);
                    var clip = new Clip(media);
                    clips.Add(clip);
                    first ??= clip;
                    if (current == null) ClipList.SelectedItem = first;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}\n{ex.Message}"); }
            }
            if (first != null) ClipList.SelectedItem = first;
            Status.Text = first != null ? "動画を追加しました" : "読み込める動画がありません";
            if (errors.Count > 0 && !closing) ShowError("読み込みエラー", string.Join("\n\n", errors));
        }
        finally { importing = false; UpdateDetails(); }
    }

    private void ClipSelected(object sender, SelectionChangedEventArgs e)
    {
        Pause();
        frameJob?.Cancel();
        proxyJob?.Cancel();
        Player.Close();
        playerReady = usingProxy = false;
        current = ClipList.SelectedItem as Clip;
        position = current?.Start ?? 0;
        Still.Source = null;
        Still.Visibility = Visibility.Visible;
        Track.CurrentClip = current;
        Overlay.ResetSelection();
        EmptyState.Visibility = current == null ? Visibility.Visible : Visibility.Collapsed;
        PreviewTitle.Text = current?.Name ?? "プレビュー";
        UpdateDetails();
        if (current != null) RequestStill();
    }

    private void UpdateDetails()
    {
        if (!Ready || OutputLabel == null) return;
        bool any = current != null;
        int index = ClipList.SelectedIndex;
        AddButton.IsEnabled = !importing && exportJob == null;
        ExportButton.IsEnabled = clips.Count > 0 && !importing && exportJob == null;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index + 1 < clips.Count;
        CopyButton.IsEnabled = RemoveButton.IsEnabled = ResultMode.IsEnabled = Transport.IsEnabled = any;
        CropOptions.IsEnabled = any && ResultMode.IsChecked != true;
        CountLabel.Text = $"{clips.Count} 本";
        CropLabel.Text = any ? $"{current!.Crop.Width} × {current.Crop.Height} px" : "—";
        if (clips.Count > 0)
        {
            var size = Output.Size(clips[0]);
            OutputLabel.Text = $"{size.Width} × {size.Height}   ·   合計 {Output.Clock(clips.Sum(c => c.Length))}";
        }
        else OutputLabel.Text = "—";
        UpdateTime();
        LayoutVideo();
    }

    private void UpdateTime()
    {
        Track.Position = position;
        Track.InvalidateVisual();
        TimeLabel.Text = $"{Output.Clock(position)} / {Output.Clock(current?.Media.Duration ?? 0)}";
        RangeLabel.Text = current == null ? "" : $"{Output.Clock(current.Start)} – {Output.Clock(current.End)}";
    }

    private void StageResized(object sender, SizeChangedEventArgs e) => LayoutVideo();
    private void ViewChanged(object sender, RoutedEventArgs e) => UpdateDetails();
    private void FitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Ready && current != null) { ResultMode.IsChecked = true; UpdateDetails(); }
    }

    private void LayoutVideo()
    {
        if (Overlay == null || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0) return;
        if (current == null)
        {
            Viewport.Visibility = Visibility.Collapsed;
            Overlay.SetFrame(null, Rect.Empty);
            return;
        }
        Viewport.Visibility = Visibility.Visible;
        var m = current.Media;
        bool result = ResultMode.IsChecked == true;
        var (w, h) = result ? Output.Size(clips[0]) : (m.Width, m.Height);
        double fit = Math.Min(Stage.ActualWidth / w, Stage.ActualHeight / h);
        double vw = w * fit, vh = h * fit;
        var frame = new Rect((Stage.ActualWidth - vw) / 2, (Stage.ActualHeight - vh) / 2, vw, vh);
        Canvas.SetLeft(Viewport, frame.X); Canvas.SetTop(Viewport, frame.Y);
        Viewport.Width = vw; Viewport.Height = vh;
        var crop = result ? current.Crop : PixelRect.Full(w, h);
        double scale = result && FitMode.SelectedIndex == 0 ? Math.Max(vw / crop.Width, vh / crop.Height)
            : Math.Min(vw / crop.Width, vh / crop.Height);
        SourceVisual.Width = m.Width * scale; SourceVisual.Height = m.Height * scale;
        Canvas.SetLeft(SourceVisual, (vw - crop.Width * scale) / 2 - crop.Left * scale);
        Canvas.SetTop(SourceVisual, (vh - crop.Height * scale) / 2 - crop.Top * scale);
        SourceVisual.Clip = result ? new RectangleGeometry(new Rect(crop.Left * scale, crop.Top * scale, crop.Width * scale, crop.Height * scale)) : null;
        Overlay.Visibility = result ? Visibility.Collapsed : Visibility.Visible;
        Overlay.SetFrame(current, frame);
    }

    private void MirrorChanged(object sender, RoutedEventArgs e)
    {
        Overlay.Mirror = Mirror.IsChecked == true;
        Overlay.Focus();
    }

    private void ResetCrop(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        current.Crop = PixelRect.Full(current.Media.Width, current.Media.Height);
        Overlay.ResetSelection();
        UpdateDetails();
    }

    private void Duplicate(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        var copy = current.Copy();
        clips.Insert(ClipList.SelectedIndex + 1, copy);
        ClipList.SelectedItem = copy;
        ClipList.ScrollIntoView(copy);
    }

    private void Remove(object sender, RoutedEventArgs e)
    {
        int index = ClipList.SelectedIndex;
        if (index < 0) return;
        clips.RemoveAt(index);
        if (clips.Count > 0) ClipList.SelectedIndex = Math.Min(index, clips.Count - 1);
        UpdateDetails();
    }

    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        int index = ClipList.SelectedIndex, target = index + delta;
        if (index < 0 || target < 0 || target >= clips.Count) return;
        clips.Move(index, target);
        ClipList.SelectedIndex = target;
        UpdateDetails();
    }

    private void MarkStart(object sender, RoutedEventArgs e) => SetBoundary(true, position);
    private void MarkEnd(object sender, RoutedEventArgs e) => SetBoundary(false, position);

    private void SetBoundary(bool start, double time)
    {
        if (current == null) return;
        current.SetBoundary(start, time);
        Seek(start ? current.Start : current.End);
        UpdateDetails();
    }

    private void Seek(double time)
    {
        if (current == null) return;
        Pause();
        position = Math.Clamp(time, 0, current.Media.Duration);
        if (playerReady) Player.Position = TimeSpan.FromSeconds(position);
        UpdateTime();
        RequestStill();
    }

    private async void RequestStill()
    {
        if (current == null || closing) return;
        frameJob?.Cancel();
        var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        frameJob = job;
        var media = current.Media;
        double at = position;
        try
        {
            // Coalesce pointer updates before starting an external decoder.
            await Task.Delay(45, job.Token);
            var data = await MediaTools.StillAsync(media, at, 1280, job.Token);
            job.Token.ThrowIfCancellationRequested();
            if (data.Length == 0) return;
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Still.Source = image;
            Still.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) Status.Text = $"プレビューを読み込めません  {ex.Message}"; }
        finally { if (frameJob == job) frameJob = null; job.Dispose(); }
    }

    private void TogglePlay(object sender, RoutedEventArgs e) => TogglePlayback();
    private void TogglePlayback()
    {
        if (current == null) return;
        if (playing || playRequested) { Seek(position); return; }
        if (position >= current.End - .001 || position < current.Start) position = current.Start;
        playRequested = true;
        PlayButton.Content = "Ⅱ  一時停止";
        if (playerReady) StartPlayback();
        else if (proxies.TryGetValue(current.Media.Path, out var path)) OpenPlayer(path, true);
        else if (current.Media.NeedsProxy) StartProxy();
        else OpenPlayer(current.Media.Path, false);
    }

    private void OpenPlayer(string path, bool proxy)
    {
        usingProxy = proxy;
        Player.Source = new Uri(path);
        Player.Pause();
    }

    private void PlayerOpened(object sender, RoutedEventArgs e)
    {
        if (current == null || closing) return;
        playerReady = true;
        if (playRequested) StartPlayback();
    }

    private void StartPlayback()
    {
        frameJob?.Cancel();
        Player.Position = TimeSpan.FromSeconds(position);
        Player.Play();
        Still.Visibility = Visibility.Collapsed;
        playing = true;
        timer.Start();
        Status.Text = "選択区間を再生中";
    }

    private void Pause()
    {
        playRequested = false;
        if (playing) Player.Pause();
        timer.Stop();
        playing = false;
        PlayButton.Content = "▶  再生";
    }

    private void PlayerEnded(object sender, RoutedEventArgs e)
    {
        if (playing && current != null) Seek(current.End);
    }

    private void PlayerFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (current == null || closing) return;
        playerReady = false;
        bool retry = !usingProxy && playRequested;
        Player.Close();
        if (retry) StartProxy();
        else { Pause(); RequestStill(); Status.Text = "動画を再生できません。 " + e.ErrorException.Message; }
    }

    private void StartProxy()
    {
        if (proxyJob != null && !proxyJob.IsCancellationRequested) return;
        preparingProxy = PrepareProxyAsync();
    }

    private async Task PrepareProxyAsync()
    {
        var media = current!.Media;
        var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        proxyJob = job;
        string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            Directory.CreateDirectory(tempDir);
            Status.Text = "再生用プレビューを準備中…";
            await MediaTools.MakeProxyAsync(media, path, job.Token);
            job.Token.ThrowIfCancellationRequested();
            proxies[media.Path] = path;
            if (playRequested) OpenPlayer(path, true);
            else Status.Text = "プレビューを準備しました";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Pause(); Status.Text = "再生用プレビューを作成できません。 " + ex.Message; }
        finally
        {
            if (proxyJob == job) proxyJob = null;
            job.Dispose();
            if (!proxies.ContainsValue(path)) TryDelete(path);
        }
    }

    private void WindowKey(object sender, KeyEventArgs e)
    {
        if (!Ready || exportJob != null) return;
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        { OpenFiles(sender, e); e.Handled = true; return; }
        if (current == null || Keyboard.Modifiers != ModifierKeys.None || FitMode.IsDropDownOpen) return;
        switch (e.Key)
        {
            case Key.I: SetBoundary(true, position); break;
            case Key.O: SetBoundary(false, position); break;
            case Key.Space: TogglePlayback(); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        if (exportJob != null || importing || clips.Count == 0) return;
        string suggested = Output.DefaultPath(clips);
        var dialog = new SaveFileDialog { Filter = "MP4 動画|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = Path.GetFileName(suggested), InitialDirectory = Path.GetDirectoryName(suggested),
            OverwritePrompt = true, Title = "MP4 を別名で書き出す" };
        if (dialog.ShowDialog(this) != true) return;
        string path = dialog.FileName;
        bool overwrite = File.Exists(path);
        if (current != null) Seek(position);
        using var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        exportJob = job;
        Workspace.IsEnabled = false;
        ExportProgress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Status.Text = "書き出し中…";
        UpdateDetails();
        var progress = new Progress<double>(value =>
        {
            if (exportJob != job) return;
            Progress.Value = value;
            Status.Text = $"書き出し中  {value:P0}";
        });
        try
        {
            exporting = MediaTools.ExportAsync(clips.Select(c => c.Copy()).ToArray(), FitMode.SelectedIndex == 1, path, progress, job.Token, overwrite);
            await exporting;
            Status.Text = $"保存しました  {path}";
        }
        catch (OperationCanceledException) { Status.Text = "書き出しをキャンセルしました"; }
        catch (Exception ex) { Status.Text = "書き出しに失敗しました"; if (!closing) ShowError(Status.Text, ex.Message); }
        finally
        {
            exporting = null; exportJob = null;
            ExportProgress.Visibility = Visibility.Collapsed;
            Workspace.IsEnabled = true;
            UpdateDetails();
        }
    }

    private void CancelExport(object sender, RoutedEventArgs e) => exportJob?.Cancel();
    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (canClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        var pending = new[] { exporting, preparingProxy }.OfType<Task>().ToArray();
        lifetime.Cancel();
        Pause();
        Player.Close();
        await Dispatcher.Yield();
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Jobs report their own errors and clean their output. */ }
        foreach (var path in proxies.Values) TryDelete(path);
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir); } catch (IOException) { }
        canClose = true;
        Close();
    }
}
