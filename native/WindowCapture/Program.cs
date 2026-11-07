using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Media.Control;
using System.Windows.Automation;
using Windows.Storage.Streams;

// Separate process: an unresponsive PrintWindow must never block the audio bridge.
internal static class Program
{
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private record Window(string handle, string title, int pid);
    private record MediaInfo(string title, string artist, double? elapsed, double duration, bool playing);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    private static List<Window> Windows()
    {
        var result = new List<Window>();
        EnumWindows((hwnd, _) => {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(1024);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            result.Add(new Window(hwnd.ToInt64().ToString(), title.ToString(), (int)pid));
            return true;
        }, 0);
        return result;
    }

    private static bool IsYandex(Window w) => w.title.Contains("Яндекс Музы", StringComparison.OrdinalIgnoreCase)
        || w.title.Contains("Яндекс.Музы", StringComparison.OrdinalIgnoreCase)
        || w.title.Contains("Yandex Music", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> PlayerIds = new() {
        ["previous"] = "PreviousButton", ["play"] = "PlayButton", ["next"] = "NextButton",
        ["repeat"] = "Repeat", ["shuffle"] = "Shuffle", ["like"] = "LikeButton"
    };

    private static AutomationElement PlayerRoot()
    {
        var windows = Windows().Where(IsYandex).ToList();
        // Never guess between multiple Yandex windows, or send global media keys.
        if (windows.Count != 1) throw new InvalidOperationException(windows.Count == 0
            ? "Откройте окно Яндекс Музыки." : "Найдено несколько окон Яндекс Музыки. Оставьте одно окно плеера.");
        var root = AutomationElement.FromHandle((nint)long.Parse(windows[0].handle));
        if (root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TitleButton")) == null)
            throw new InvalidOperationException("Эта версия Яндекс Музыки не предоставляет известные кнопки плеера через UI Automation.");
        return root;
    }

    private static AutomationElement? PlayerButton(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static object PlayerState()
    {
        var root = PlayerRoot();
        var buttons = new Dictionary<string, object>();
        foreach (var (command, id) in PlayerIds)
        {
            var button = PlayerButton(root, id);
            bool supported = button != null && (button.TryGetCurrentPattern(InvokePattern.Pattern, out _)
                || button.TryGetCurrentPattern(TogglePattern.Pattern, out _));
            string? toggle = button != null && button.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                ? ((TogglePattern)pattern).Current.ToggleState.ToString() : null;
            buttons[command] = new { enabled = supported && button!.Current.IsEnabled, toggle };
        }
        bool? playing = null;
        var title = PlayerButton(root, "TitleButton")?.Current.Name ?? "";
        var artist = PlayerButton(root, "ArtistButton")?.Current.Name ?? "";
        string? cover = null;
        string? coverError = null;
        try
        {
            var manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            var sessions = manager.GetSessions().Where(s => s.SourceAppUserModelId.Contains("yandex", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sessions.Count == 1)
            {
                playing = sessions[0].GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var properties = sessions[0].TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                // The window is authoritative: never display artwork from a different track/session.
                if (properties.Title == title && properties.Artist == artist)
                    cover = Cover(properties, sessions[0].SourceAppUserModelId);
            }
        }
        catch (Exception ex) { coverError = ex.Message; } // Controls remain available without artwork/SMTC.
        return new { buttons, playing, title, artist, cover, coverError };
    }

    private static string? coverKey;
    private static string? coverImage;
    private static string? Cover(GlobalSystemMediaTransportControlsSessionMediaProperties properties, string source)
    {
        var key = JsonSerializer.Serialize(new[] { source, properties.Title, properties.Artist, properties.AlbumTitle });
        if (key == coverKey && coverImage != null) return coverImage;
        coverKey = key; coverImage = null;
        if (properties.Thumbnail == null) return null;
        using var stream = properties.Thumbnail.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = checked((uint)stream.Size);
        if (reader.LoadAsync(size).AsTask().GetAwaiter().GetResult() != size) return null;
        var bytes = new byte[size]; reader.ReadBytes(bytes);
        using var input = new MemoryStream(bytes);
        using var bitmap = new Bitmap(input);
        if (bitmap.Width > 8192 || bitmap.Height > 8192) return null;
        using var output = new Bitmap(144, 144, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var side = Math.Min(bitmap.Width, bitmap.Height);
            graphics.DrawImage(bitmap, new Rectangle(0, 0, 144, 144),
                new Rectangle((bitmap.Width - side) / 2, (bitmap.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        using var png = new MemoryStream(); output.Save(png, ImageFormat.Png);
        coverImage = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        return coverImage;
    }

    private static object PlayerControl(JsonElement request)
    {
        var command = request.GetProperty("command").GetString() ?? "";
        if (!PlayerIds.TryGetValue(command, out var id)) throw new InvalidOperationException("Неизвестная команда плеера.");
        var button = PlayerButton(PlayerRoot(), id) ?? throw new InvalidOperationException("Кнопка недоступна в интерфейсе Яндекс Музыки.");
        if (!button.Current.IsEnabled) throw new InvalidOperationException("Эта кнопка сейчас отключена в Яндекс Музыке.");
        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
        else if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else throw new InvalidOperationException("Приложение не поддерживает вызов этой кнопки через UI Automation.");
        return new { command, applied = true }; // Do not auto-retry a potentially applied command.
    }

    private static double? ClockSeconds(string? value)
    {
        if (value == null || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,3}:\d{2}(:\d{2})?$")) return null;
        var parts = value.Split(':').Select(int.Parse).ToArray();
        return parts.Length == 2 ? parts[0] * 60 + parts[1] : parts[0] * 3600 + parts[1] * 60 + parts[2];
    }

    private static object PlayerSeek(JsonElement request)
    {
        var seconds = request.GetProperty("seconds").GetDouble();
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 60) throw new InvalidOperationException("Шаг перемотки вне диапазона ±60 секунд.");
        var root = PlayerRoot();
        var title = PlayerButton(root, "TitleButton")?.Current.Name;
        var time = PlayerButton(root, "Time");
        var next = time == null ? null : TreeWalker.RawViewWalker.GetNextSibling(time);
        var duration = ClockSeconds(next?.Current.Name);
        if (duration == null || duration <= 0) throw new InvalidOperationException("Длительность трека неизвестна — перемотка не отправлена.");
        var slider = PlayerButton(root, "Slider");
        if (slider == null || !slider.Current.IsEnabled || !slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("Приложение не предоставляет ползунок перемотки.");
        var range = (RangeValuePattern)pattern;
        var state = range.Current;
        if (state.IsReadOnly || state.Maximum <= state.Minimum) throw new InvalidOperationException("Перемотка сейчас недоступна.");
        if (PlayerButton(root, "TitleButton")?.Current.Name != title) throw new InvalidOperationException("Трек сменился: повторите перемотку.");
        var value = Math.Clamp(state.Value + seconds / duration.Value * (state.Maximum - state.Minimum), state.Minimum, state.Maximum);
        range.SetValue(value);
        return new { applied = true, seconds };
    }

    private static object Capture(JsonElement request)
    {
        var windows = Windows();
        var handle = request.TryGetProperty("handle", out var h) ? h.GetString() : "";
        var window = string.IsNullOrEmpty(handle)
            ? windows.FirstOrDefault(IsYandex)
            : windows.FirstOrDefault(w => w.handle == handle && request.TryGetProperty("pid", out var p) && w.pid == p.GetInt32());
        if (window == null) throw new InvalidOperationException("Окно не найдено. Откройте Яндекс Музыку или выберите окно в настройках.");
        var hwnd = (nint)long.Parse(window.handle);
        if (IsIconic(hwnd)) throw new InvalidOperationException("Разверните окно приложения: захват свёрнутого окна недоступен.");
        if (!GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("Не удалось получить размер окна.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 32_000_000)
            throw new InvalidOperationException("Размер окна вне допустимого диапазона.");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            bool ok;
            try { ok = PrintWindow(hwnd, dc, 3); } // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(dc); }
            if (!ok) throw new InvalidOperationException("Приложение не поддерживает захват окна (PrintWindow).");
        }
        int outputWidth = request.TryGetProperty("width", out var ow) && ow.GetInt32() == 200 ? 200 : 800;
        var mode = request.TryGetProperty("mode", out var m) ? m.GetString() : "bottom";
        using var output = new Bitmap(outputWidth, 100, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (mode == "fit")
            {
                float scale = Math.Min(outputWidth / (float)width, 100f / height);
                float w = width * scale, ht = height * scale;
                graphics.DrawImage(bitmap, new RectangleF((outputWidth - w) / 2, (100 - ht) / 2, w, ht));
            }
            else
            {
                float sourceHeight = Math.Min(height, width * 100f / outputWidth);
                float y = mode == "top" ? 0 : mode == "center" ? (height - sourceHeight) / 2 : height - sourceHeight;
                graphics.DrawImage(bitmap, new RectangleF(0, 0, outputWidth, 100), new RectangleF(0, y, width, sourceHeight), GraphicsUnit.Pixel);
            }
        }
        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        object? media = null;
        string? mediaError = null;
        // Never substitute another player's current track for Yandex Music.
        if (window.title.Contains("Яндекс", StringComparison.OrdinalIgnoreCase) || window.title.Contains("Yandex", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(handle))
        {
            try { media = Media(hwnd); }
            catch (Exception ex) { mediaError = "Медиаданные недоступны: " + ex.Message; }
        }
        return new { image = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()), title = window.title, width = outputWidth,
            media, mediaError = mediaError ?? (media == null ? "Яндекс Музыка не передаёт данные трека в медиасессию Windows." : null) };
    }

    private static MediaInfo? Media(nint hwnd)
    {
        var manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        var sessions = manager.GetSessions().Where(s => s.SourceAppUserModelId.Contains("yandex", StringComparison.OrdinalIgnoreCase)).ToList();
        var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions.FirstOrDefault();
        MediaInfo? media = null;
        if (session != null)
        {
            var properties = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var timeline = session.GetTimelineProperties();
            var playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            double? elapsed = null;
            if (duration > 0 && timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch)
            {
                var position = (timeline.Position - timeline.StartTime).TotalSeconds;
                if (playing) position += Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds);
                elapsed = Math.Clamp(position, 0, duration);
            }
            media = new MediaInfo(properties.Title, properties.Artist, elapsed, duration, playing);
        }
        if (media?.elapsed != null && media.duration > 0) return media;
        // Legacy Yandex UWP publishes title/artist but no SMTC timeline.
        // Read only the known player controls, not playlist durations or another window.
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElement? Find(string id) => root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            var title = Find("TitleButton")?.Current.Name;
            if (string.IsNullOrEmpty(title)) return media;
            var artist = Find("ArtistButton")?.Current.Name ?? "";
            var time = Find("Time");
            var next = time == null ? null : TreeWalker.RawViewWalker.GetNextSibling(time);
            return new MediaInfo(title, artist, ClockSeconds(time?.Current.Name), ClockSeconds(next?.Current.Name) ?? 0,
                media?.title == title && media.playing);
        }
        catch { return media; } // Keep SMTC title if this app has no accessible UI tree.
    }

    private static void Main()
    {
        SetProcessDpiAwarenessContext((nint)(-4));
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            object? id = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement;
                id = request.GetProperty("id").GetInt64();
                object result = request.GetProperty("op").GetString() switch {
                    "list" => new { windows = Windows() },
                    "capture" => Capture(request),
                    "player-state" => PlayerState(),
                    "player-control" => PlayerControl(request),
                    "player-seek" => PlayerSeek(request),
                    _ => throw new InvalidOperationException("Unknown operation")
                };
                Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
            }
            catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { id, error = ex.Message })); }
        }
    }
}
