using System.Globalization;

namespace SimpleMP4Editor;

[Flags]
internal enum Grip { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16, Draw = 32 }

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static PixelRect Full(int width, int height) => new(0, 0, width, height);

    public PixelRect Adjust(Grip grip, int dx, int dy, int width, int height, bool mirror)
    {
        if (grip == Grip.Move)
        {
            dx = Math.Clamp(dx, -Left, width - Right);
            dy = Math.Clamp(dy, -Top, height - Bottom);
            return new(Left + dx, Top + dy, Right + dx, Bottom + dy);
        }
        var (left, right) = ResizeAxis(Left, Right, width, grip.HasFlag(Grip.Left), grip.HasFlag(Grip.Right), dx, mirror);
        var (top, bottom) = ResizeAxis(Top, Bottom, height, grip.HasFlag(Grip.Top), grip.HasFlag(Grip.Bottom), dy, mirror);
        return new(left, top, right, bottom);
    }

    private static (int, int) ResizeAxis(int start, int end, int limit, bool near, bool far, int delta, bool mirror)
    {
        if (!near && !far) return (start, end);
        if (!mirror)
            return near ? (Math.Clamp(start + delta, 0, end - 1), end)
                        : (start, Math.Clamp(end + delta, start + 1, limit));
        int inward = Math.Clamp(near ? delta : -delta, -Math.Min(start, limit - end), (end - start - 1) / 2);
        return (start + inward, end - inward);
    }
}

internal sealed record MediaInfo(string Path, int Width, int Height, double Duration, double Fps,
    bool HasAudio, bool NeedsProxy, int VideoIndex, int AudioIndex, double VideoOffset);

internal sealed class Clip(MediaInfo media) : System.ComponentModel.INotifyPropertyChanged
{
    public MediaInfo Media { get; } = media;
    public string Name => Path.GetFileName(Media.Path);
    public PixelRect Crop { get; set; } = PixelRect.Full(media.Width, media.Height);
    public double Start { get; private set; }
    public double End { get; private set; } = media.Duration;
    public double Length => End - Start;
    public string Summary => $"{Media.Width} × {Media.Height}   ·   {Output.Clock(Length)}";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public Clip Copy() => new(Media) { Crop = Crop, Start = Start, End = End };

    public void SetBoundary(bool start, double time)
    {
        double minimum = Math.Min(Media.Duration, 1 / Media.Fps);
        if (start) Start = Math.Clamp(time, 0, Math.Max(0, End - minimum));
        else End = Math.Clamp(time, Math.Min(Media.Duration, Start + minimum), Media.Duration);
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
}

internal static class Output
{
    public static (int Width, int Height) Size(Clip first) =>
        ((first.Crop.Width + 1) & ~1, (first.Crop.Height + 1) & ~1);

    public static string DefaultPath(IReadOnlyList<Clip> clips)
    {
        Clip first = clips[0];
        string stem = Path.GetFileNameWithoutExtension(first.Media.Path);
        string suffix = clips.Count > 1 ? $"ほか{clips.Count - 1}本_結合"
            : first.Start > 0 || first.End < first.Media.Duration ? $"{FileTime(first.Start)}-{FileTime(first.End)}" : "切り抜き";
        string prefix = Path.Combine(Path.GetDirectoryName(first.Media.Path)!, $"{stem}_{suffix}");
        string candidate = prefix + ".mp4";
        for (int i = 2; File.Exists(candidate); i++) candidate = $"{prefix}_{i}.mp4";
        return candidate;
    }

    private static string FileTime(double seconds) => $"{(int)(seconds / 60):00}m{(int)seconds % 60:00}s";
    public static string Clock(double seconds) =>
        FormattableString.Invariant($"{(int)(seconds / 60):00}:{seconds % 60:00.000}");
    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
