using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

// Windows policy ABI references: EarTrumpet (see plugin/licenses/EarTrumpet.txt).
// Per-app routing uses an undocumented Windows interface; probe known IIDs only.
internal static class AudioRouting
{
    private const string Token = @"\\?\SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private static IntPtr factory;
    private static bool probed;
    internal static string? SupportError { get; private set; }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, uint length, out IntPtr result);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr result);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetAppDelegate(IntPtr self, uint pid, int flow, int role, IntPtr endpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAppDelegate(IntPtr self, uint pid, int flow, int role, out IntPtr endpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetSystemDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string endpoint, int role);
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static bool AppSupported()
    {
        if (probed) return factory != IntPtr.Zero;
        probed = true;
        IntPtr name = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoInitialize(1));
            const string runtime = "Windows.Media.Internal.AudioPolicyConfig";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtime, (uint)runtime.Length, out name));
            foreach (var text in new[] { "ab3d4648-e242-459f-b02f-541c70306324", "2a59116d-6c4f-45e0-a74f-707e3fef9258" })
            {
                var iid = new Guid(text);
                int hr = RoGetActivationFactory(name, ref iid, out factory);
                if (hr >= 0 && factory != IntPtr.Zero) return true;
                if (factory != IntPtr.Zero) { Marshal.Release(factory); factory = IntPtr.Zero; }
            }
            SupportError = "Эта версия Windows не предоставляет поддерживаемый API выхода приложения.";
        }
        catch (Exception ex) { SupportError = ex.Message; }
        finally { if (name != IntPtr.Zero) WindowsDeleteString(name); }
        return false;
    }

    internal static string? SystemOutput(Role role = Role.Console)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
        return device.ID;
    }

    internal static string? AppOutput(uint pid, int role = 1)
    {
        if (pid == 0 || !AppSupported()) return null;
        // IUnknown (3) + IInspectable (3) + 19 preceding methods.
        int hr = Method<GetAppDelegate>(factory, 26)(factory, pid, 0, role, out var value);
        try
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (value == IntPtr.Zero) return null;
            var buffer = WindowsGetStringRawBuffer(value, out var length);
            string? id = Marshal.PtrToStringUni(buffer, checked((int)length));
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (id.StartsWith(Token, StringComparison.OrdinalIgnoreCase)) id = id[Token.Length..];
            if (id.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^RenderSuffix.Length];
            return id;
        }
        finally { if (value != IntPtr.Zero) WindowsDeleteString(value); }
    }

    private static void SetApp(uint pid, int role, string? endpoint)
    {
        IntPtr value = IntPtr.Zero;
        try
        {
            if (endpoint != null)
            {
                string id = Token + endpoint + RenderSuffix;
                Marshal.ThrowExceptionForHR(WindowsCreateString(id, (uint)id.Length, out value));
            }
            Marshal.ThrowExceptionForHR(Method<SetAppDelegate>(factory, 25)(factory, pid, 0, role, value));
        }
        finally { if (value != IntPtr.Zero) WindowsDeleteString(value); }
    }

    internal static void SetApplicationOutput(IEnumerable<uint> processes, string? endpoint)
    {
        if (!AppSupported()) throw new NotSupportedException(SupportError);
        var pids = processes.Where(pid => pid != 0).Distinct().ToArray();
        if (pids.Length == 0) throw new InvalidOperationException("У приложения нет доступной аудиосессии. Начните воспроизведение звука.");
        var before = pids.SelectMany(pid => new[] { 0, 1 }.Select(role => (Pid: pid, Role: role, Endpoint: AppOutput(pid, role)))).ToArray();
        var changed = new List<(uint Pid, int Role, string? Endpoint)>();
        try
        {
            foreach (var entry in before) { SetApp(entry.Pid, entry.Role, endpoint); changed.Add(entry); }
        }
        catch (Exception ex)
        {
            bool rollbackFailed = false;
            foreach (var entry in changed.AsEnumerable().Reverse()) { try { SetApp(entry.Pid, entry.Role, entry.Endpoint); } catch { rollbackFailed = true; } }
            throw new InvalidOperationException(rollbackFailed ? $"Выход изменён частично; проверьте микшер Windows. {ex.Message}" : $"Не удалось изменить выход приложения; прежние настройки восстановлены. {ex.Message}", ex);
        }
    }

    internal static void SetSystemOutput(string endpoint)
    {
        var clsid = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        var iid = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var client));
        try
        {
            var set = Method<SetSystemDelegate>(client, 13);
            string? previous = SystemOutput(Role.Console);
            Marshal.ThrowExceptionForHR(set(client, endpoint, 0));
            try { Marshal.ThrowExceptionForHR(set(client, endpoint, 1)); }
            catch
            {
                if (previous != null) Marshal.ThrowExceptionForHR(set(client, previous, 0));
                throw;
            }
            // Communications default is intentionally not changed.
        }
        finally { Marshal.Release(client); }
    }
}
