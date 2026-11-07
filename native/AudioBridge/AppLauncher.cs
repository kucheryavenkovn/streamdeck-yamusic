using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static class AppLauncher
{
    internal sealed record Plan(string Kind, string Target, string? ApplicationId = null);
    internal sealed record Result(string Status);

    internal static Plan Resolve(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\")
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Запуск поддерживается только для локального EXE назначенного приложения.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Приложение удалено или изменило путь. Назначьте его кнопке заново.", executable);
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        for (int depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
        {
            string manifest = Path.Combine(directory.FullName, "AppxManifest.xml");
            if (!File.Exists(manifest)) continue;
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
            });
            var document = XDocument.Load(reader);
            string relative = Path.GetRelativePath(directory.FullName, executable).Replace('/', '\\');
            var application = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
                && string.Equals((string?)e.Attribute("Executable"), relative, StringComparison.OrdinalIgnoreCase));
            string? id = (string?)application?.Attribute("Id");
            string? name = (string?)document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name");
            string publisher = directory.Name.Split('_').Last();
            if (id == null || name == null || !Regex.IsMatch(name, @"^[A-Za-z0-9.-]+$")
                || !Regex.IsMatch(id, @"^[A-Za-z0-9.-]+$") || !Regex.IsMatch(publisher, @"^[a-z0-9]{13}$"))
                throw new InvalidOperationException("Не удалось определить идентификатор Store-приложения. Назначьте его заново.");
            string appId = $"{name}_{publisher}!{id}";
            return new Plan("package", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), appId);
        }
        return new Plan("desktop", executable);
    }

    internal static Result Start(string executable, Func<uint, string> executablePath)
    {
        var plan = Resolve(executable);
        bool inaccessible = false;
        // An existing process without an audio session must not produce a second window/instance.
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(executablePath((uint)process.Id), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                        return new Result("already-running");
                }
                catch { try { if (!process.HasExited) inaccessible = true; } catch { } }
            }
        }
        if (inaccessible) throw new InvalidOperationException("Не удалось проверить запущенный процесс. Повторный запуск отменён, чтобы не открыть лишнюю копию.");
        var start = new ProcessStartInfo(plan.Target) { UseShellExecute = true };
        if (plan.Kind == "package") start.ArgumentList.Add(@"shell:AppsFolder\" + plan.ApplicationId);
        else start.WorkingDirectory = Path.GetDirectoryName(plan.Target)!;
        // No cmd/PowerShell, custom arguments, elevation verb or remote URLs.
        using var launched = Process.Start(start);
        return new Result("started");
    }
}
