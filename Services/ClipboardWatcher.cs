using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinIsland.Interop;
using WinIsland.Models;

namespace WinIsland.Services;

public sealed class ClipboardWatcher : IDisposable
{
    private IntPtr _hwnd;
    private bool _listening;
    private bool _enabled = true;
    private bool _retryScheduled;
    private DateTime _suppressUntil = DateTime.MinValue;
    private string? _lastText;

    public ObservableCollection<ClipboardItem> Items { get; } = new();

    public event Action<ClipboardItem>? ItemAdded;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        if (hwnd != IntPtr.Zero)
        {
            _listening = Native.AddClipboardFormatListener(hwnd);
        }
    }

    public void OnClipboardUpdate()
    {
        if (!_enabled)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(Read), DispatcherPriority.Background);
    }

    private void Read()
    {
        if (!_enabled || DateTime.Now < _suppressUntil)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (text.Length > 4000)
                {
                    text = text[..4000];
                }

                if (text == _lastText)
                {
                    return;
                }

                _lastText = text;
                Add(new ClipboardItem
                {
                    Kind = ClipboardKind.Text,
                    Text = text,
                    Preview = MakePreview(text)
                });
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                var files = list.Cast<string>()
                    .Where(path => File.Exists(path) || Directory.Exists(path))
                    .ToArray();
                if (files.Length == 0)
                {
                    return;
                }

                Add(new ClipboardItem
                {
                    Kind = ClipboardKind.Files,
                    Preview = string.Join("  ·  ", files.Select(Path.GetFileName)),
                    Files = files
                });
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null)
                {
                    return;
                }

                image.Freeze();
                var originalWidth = image.PixelWidth;
                var originalHeight = image.PixelHeight;

                // Clipboard screenshots can be huge. Keep a compressed, capped copy
                // instead of holding full uncompressed bitmaps for every history item.
                BitmapSource stored = image;
                if (image.PixelWidth > 1920)
                {
                    var scale = 1920d / image.PixelWidth;
                    var resized = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
                    resized.Freeze();
                    stored = resized;
                }

                var thumbnail = stored;
                if (stored.PixelWidth > 480)
                {
                    var scale = 480d / stored.PixelWidth;
                    var resized = new TransformedBitmap(stored, new System.Windows.Media.ScaleTransform(scale, scale));
                    resized.Freeze();
                    thumbnail = resized;
                }

                Add(new ClipboardItem
                {
                    Kind = ClipboardKind.Image,
                    Preview = $"图片 · {originalWidth} × {originalHeight}",
                    ImageBytes = EncodePng(stored),
                    Thumbnail = thumbnail
                });
            }
        }
        catch (COMException)
        {
            ScheduleRetry();
        }
        catch
        {
            // Clipboard can be locked by another process; ignore.
        }
    }

    private void ScheduleRetry()
    {
        if (_retryScheduled)
        {
            return;
        }

        _retryScheduled = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _retryScheduled = false;
            Read();
        };
        timer.Start();
    }

    private void Add(ClipboardItem item)
    {
        if (item.Kind == ClipboardKind.Text)
        {
            var duplicate = Items.FirstOrDefault(i => i.Kind == ClipboardKind.Text && i.Text == item.Text);
            if (duplicate is not null)
            {
                Items.Remove(duplicate);
                Items.Insert(0, duplicate);
                return;
            }
        }

        Items.Insert(0, item);

        while (Items.Count > 30)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        while (Items.Count(i => i.Kind == ClipboardKind.Image) > 4)
        {
            var oldestImage = Items.Last(i => i.Kind == ClipboardKind.Image);
            Items.Remove(oldestImage);
        }

        ItemAdded?.Invoke(item);
    }

    public void Copy(ClipboardItem item)
    {
        try
        {
            _suppressUntil = DateTime.Now.AddMilliseconds(600);
            switch (item.Kind)
            {
                case ClipboardKind.Image when item.ImageBytes is { Length: > 0 }:
                {
                    var image = DecodeImage(item.ImageBytes);
                    if (image is not null)
                    {
                        Clipboard.SetImage(image);
                    }

                    break;
                }

                case ClipboardKind.Files when item.Files.Count > 0:
                {
                    var collection = new System.Collections.Specialized.StringCollection();
                    collection.AddRange(item.Files.ToArray());
                    Clipboard.SetFileDropList(collection);
                    break;
                }

                default:
                    Clipboard.SetText(item.Text);
                    _lastText = item.Text;
                    break;
            }
        }
        catch
        {
            // Ignore clipboard write failures.
        }
    }

    public void Remove(ClipboardItem item) => Items.Remove(item);

    public void Clear() => Items.Clear();

    private static string MakePreview(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (singleLine.Contains("  ", StringComparison.Ordinal))
        {
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return singleLine.Length > 220 ? singleLine[..220] + "…" : singleLine;
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? DecodeImage(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_listening && _hwnd != IntPtr.Zero)
        {
            Native.RemoveClipboardFormatListener(_hwnd);
            _listening = false;
        }
    }
}
