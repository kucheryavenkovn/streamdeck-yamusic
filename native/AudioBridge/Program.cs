using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

// Local JSON-lines IPC only. No network listener, elevation, or audio capture.
internal static class Program
{
    private static readonly Dictionary<string, (string Name, string? Icon)> Metadata = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static int scans;
    private static readonly object Gate = new();
    private static List<Entry> CachedEntries = new();
    private static List<string> CachedWarnings = new();
    private sealed record OutputDevice(string Id, string Name);
    private static List<OutputDevice> CachedOutputs = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);

    private static string ExecutablePath(uint pid)
    {
        // Unlike Process.MainModule this does NOT request VM_READ/PROCESS_QUERY_INFORMATION.
        // Limited query access can identify many elevated and sandboxed audio processes.
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    [MTAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--icon-info")
        {
            var icon = AppIcons.Load(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new { source = icon?.Source, bytes = icon?.DataUri.Length }, Json));
            return;
        }
        if (args.Contains("--self-test")) { SelfTest(); return; }
        if (args.Length == 2 && args[0] == "--launch-info")
        {
            Console.WriteLine(JsonSerializer.Serialize(AppLauncher.Resolve(args[1]), Json));
            return;
        }
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        // Test-only silent source permits IPC latency/readback checks against our own EXE.
        using var testOutput = args.Contains("--test-session") ? new WasapiOut() : null;
        if (testOutput != null)
        {
            testOutput.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
            testOutput.Play();
        }
        using var cancellation = new CancellationTokenSource();
        // Discovery is slow (about 1 second on machines with many endpoints).
        // Never run it on the interactive command thread or while holding Gate.
        var discovery = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var warnings = new List<string>();
                    var outputs = new List<OutputDevice>();
                    var entries = Scan(warnings, outputs);
                    List<Entry> previous;
                    lock (Gate)
                    {
                        previous = CachedEntries;
                        CachedEntries = entries;
                        CachedWarnings = warnings;
                        CachedOutputs = outputs;
                    }
                    Release(previous);
                    if (++scans % 40 == 0) GC.Collect();
                }
                catch (Exception ex) { lock (Gate) CachedWarnings = new List<string> { $"Discovery: {ex.Message}" }; }
                try { await Task.Delay(2000, cancellation.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        try
        {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            int id = 0;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement;
                id = request.GetProperty("id").GetInt32();
                var op = request.GetProperty("op").GetString();
                if (op is not ("list" or "adjust" or "mute" or "launch" or "set-system-output" or "set-app-output")) throw new ArgumentException("Unknown operation");
                string? key = op is "list" or "set-system-output" ? null : request.GetProperty("key").GetString();
                if (op == "launch")
                {
                    var launch = AppLauncher.Start(key ?? "", ExecutablePath);
                    Console.WriteLine(JsonSerializer.Serialize(new { id, launch }, Json));
                    continue;
                }
                float delta = op == "adjust" ? request.GetProperty("delta").GetSingle() : 0;
                if (!float.IsFinite(delta) || Math.Abs(delta) > 100) throw new ArgumentException("Invalid delta");
                lock (Gate)
                {
                    var entries = CachedEntries;
                    var warnings = new List<string>(CachedWarnings);
                    if (op is "set-system-output" or "set-app-output")
                    {
                        string? endpoint = request.GetProperty("deviceId").GetString();
                        if (endpoint != null && !CachedOutputs.Any(d => d.Id == endpoint)) throw new ArgumentException("Выход отключён или недоступен. Выберите активное устройство заново.");
                        if (op == "set-system-output")
                        {
                            if (endpoint == null) throw new ArgumentException("Для системного выхода нужно выбрать устройство.");
                            AudioRouting.SetSystemOutput(endpoint);
                        }
                        else
                        {
                            var pids = entries.Where(e => e.Key == key && e.Pid != 0).Select(e => e.Pid).Distinct().ToArray();
                            foreach (var pid in pids)
                                if (!string.Equals(ExecutablePath(pid), key, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("Процесс приложения изменился. Повторите выбор после обновления списка.");
                            AudioRouting.SetApplicationOutput(pids, endpoint);
                        }
                    }
                    if (op is "adjust" or "mute")
                    {
                        var targets = entries.Where(e => e.Key == key).ToList();
                        if (targets.Count == 0) throw new InvalidOperationException("Application has no audio sessions");
                        float value = Math.Clamp(targets.Max(e => e.Session.SimpleAudioVolume.Volume) + delta / 100, 0, 1);
                        bool mute = !targets.All(e => e.Session.SimpleAudioVolume.Mute);
                        int changed = 0;
                        foreach (var e in targets)
                        {
                            try
                            {
                                if (op == "adjust") e.Session.SimpleAudioVolume.Volume = value;
                                else e.Session.SimpleAudioVolume.Mute = mute;
                                changed++;
                            }
                            catch (Exception ex) { warnings.Add($"Cannot update {e.Name}: {ex.Message}"); }
                        }
                        if (changed == 0) throw new InvalidOperationException("Could not update any session");
                    }
                    // Read each live volume once; no device enumeration/property-store reads here.
                    var live = new List<(Entry Entry, float Volume, bool Muted)>();
                    foreach (var entry in entries)
                    {
                        try
                        {
                            if (entry.Session.State != AudioSessionState.AudioSessionStateExpired)
                                live.Add((entry, entry.Session.SimpleAudioVolume.Volume, entry.Session.SimpleAudioVolume.Mute));
                        }
                        catch (Exception ex) { warnings.Add($"Unavailable session {entry.Name}: {ex.Message}"); }
                    }
                    var apps = live.GroupBy(e => e.Entry.Key).Select(g => new
                    {
                        key = g.Key, name = g.First().Entry.Name, icon = g.First().Entry.Icon,
                        volume = (int)Math.Round(g.Max(e => e.Volume) * 100),
                        muted = g.All(e => e.Muted),
                        mixed = g.Select(e => Math.Round(e.Volume * 100)).Distinct().Count() > 1
                            || g.Select(e => e.Muted).Distinct().Count() > 1,
                        sessions = g.Count(), devices = g.Select(e => e.Entry.DeviceName).Distinct().ToArray(), online = true,
                        outputIds = g.Select(e => e.Entry.DeviceId).Distinct().ToArray(),
                        policyOutputId = PolicyOutput(g.Select(e => e.Entry.Pid), warnings)
                    }).OrderBy(a => a.name).ToArray();
                    string? systemOutputId = null;
                    try { systemOutputId = AudioRouting.SystemOutput(); } catch (Exception ex) { warnings.Add($"Системный выход: {ex.Message}"); }
                    bool appRoutingSupported = AudioRouting.AppSupported();
                    Console.WriteLine(JsonSerializer.Serialize(new { id, apps, warnings, outputs = CachedOutputs, systemOutputId, appRoutingSupported, routingError = AudioRouting.SupportError }, Json));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = ex.Message }, Json));
            }
        }
        }
        finally
        {
            cancellation.Cancel();
            discovery.GetAwaiter().GetResult();
            Release(CachedEntries);
        }
    }

    private static void Release(List<Entry> entries)
    {
        foreach (var entry in entries) { try { entry.Session.Dispose(); } catch { } }
        foreach (var device in entries.Select(e => e.Device).Distinct()) { try { device.Dispose(); } catch { } }
    }
    private static string? PolicyOutput(IEnumerable<uint> pids, List<string> warnings)
    {
        foreach (var pid in pids.Where(p => p != 0).Distinct())
        {
            try { var id = AudioRouting.AppOutput(pid); if (id != null) return id; }
            catch (Exception ex) { warnings.Add($"Выход приложения (PID {pid}): {ex.Message}"); }
        }
        return null;
    }

    private static void SelfTest()
    {
        // Create silence in OUR process. Never change any user's application or master volume.
        using var output = new WasapiOut();
        output.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        output.Play();
        Thread.Sleep(250);
        var warnings = new List<string>();
        var entries = Scan(warnings);
        var targets = entries.Where(e => e.Session.GetProcessID == Environment.ProcessId).ToArray();
        var saved = targets.Select(e => (e.Session, e.Session.SimpleAudioVolume.Volume, e.Session.SimpleAudioVolume.Mute)).ToArray();
        try
        {
            if (targets.Length == 0) throw new InvalidOperationException("Self-test audio session not found");
            foreach (var entry in targets)
            {
                entry.Session.SimpleAudioVolume.Volume = .37f;
                if (Math.Abs(entry.Session.SimpleAudioVolume.Volume - .37f) > .005f) throw new Exception("Volume readback failed");
                entry.Session.SimpleAudioVolume.Mute = true;
                if (!entry.Session.SimpleAudioVolume.Mute) throw new Exception("Mute readback failed");
                entry.Session.SimpleAudioVolume.Mute = false;
                if (entry.Session.SimpleAudioVolume.Mute) throw new Exception("Unmute readback failed");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, ownSessions = targets.Length, test = "Silent self-session volume/mute/unmute" }, Json));
        }
        finally
        {
            foreach (var (session, volume, mute) in saved) { session.SimpleAudioVolume.Volume = volume; session.SimpleAudioVolume.Mute = mute; }
            foreach (var entry in entries) entry.Session.Dispose();
            foreach (var device in entries.Select(e => e.Device).Distinct()) device.Dispose();
            output.Stop();
        }
    }

    private sealed record Entry(string Key, string Name, string? Icon, MMDevice Device, string DeviceName, string DeviceId, uint Pid, AudioSessionControl Session);

    private static List<Entry> Scan(List<string> warnings, List<OutputDevice>? outputs = null)
    {
        var result = new List<Entry>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            int before = result.Count;
            try
            {
                outputs?.Add(new OutputDevice(device.ID, device.FriendlyName));
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl? session = null;
                    uint pid = 0;
                    string stage = "Чтение аудиосессии";
                    try
                    {
                        session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                        string key, name;
                        string? icon = null;
                        if (session.IsSystemSoundsSession) { key = "system-sounds"; name = "Системные звуки"; }
                        else
                        {
                            pid = session.GetProcessID;
                            stage = "Определение EXE приложения";
                            // Full executable path is stable across PID changes and avoids conflating same-named binaries.
                            string path = ExecutablePath(pid);
                            key = path.ToLowerInvariant();
                            if (!Metadata.TryGetValue(key, out var metadata))
                            {
                                name = Path.GetFileNameWithoutExtension(path);
                                try { name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? name; }
                                catch { /* Metadata is optional; inaccessible version resources must not hide the audio session. */ }
                                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                                icon = AppIcons.Load(path)?.DataUri;
                                Metadata[key] = (name, icon);
                            }
                            else { name = metadata.Name; icon = metadata.Icon; }
                        }
                        stage = "Чтение громкости приложения";
                        _ = session.SimpleAudioVolume.Volume;
                        result.Add(new Entry(key, name, icon, device, device.FriendlyName, device.ID, pid, session));
                        session = null; // Ownership transferred to snapshot.
                    }
                    catch (Exception ex) { warnings.Add($"{stage} (PID {pid}, {device.FriendlyName}): {ex.Message}"); }
                    finally { session?.Dispose(); }
                }
            }
            catch (Exception ex) { warnings.Add($"Device {device.ID}: {ex.Message}"); }
            finally { if (result.Count == before) device.Dispose(); }
        }
        return result;
    }
}
