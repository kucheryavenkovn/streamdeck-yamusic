using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static class AppIcons
{
    internal sealed record Result(string DataUri, string Source);

    internal static Result? Load(string executable)
    {
        try
        {
            var packaged = PackageIconPath(executable);
            if (packaged != null)
            {
                using var image = Image.FromFile(packaged);
                return Encode(image, packaged);
            }
        }
        catch { /* Missing/restricted package resources fall back to the desktop EXE icon. */ }
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executable);
            using var image = icon?.ToBitmap();
            return image == null ? null : Encode(image, executable);
        }
        catch { return null; }
    }

    private static Result Encode(Image image, string source)
    {
        using var bitmap = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(128f / image.Width, 128f / image.Height);
            float width = image.Width * scale, height = image.Height * scale;
            graphics.DrawImage(image, (128 - width) / 2, (128 - height) / 2, width, height);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new Result("data:image/png;base64," + Convert.ToBase64String(stream.ToArray()), source);
    }

    internal static string? PackageIconPath(string executable)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executable))!);
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
            var app = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
                && string.Equals((string?)e.Attribute("Executable"), relative, StringComparison.OrdinalIgnoreCase));
            var visual = app?.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            var logo = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
            foreach (var asset in new[] { (string?)visual?.Attribute("Square44x44Logo"), (string?)visual?.Attribute("Square150x150Logo"), logo })
            {
                var resolved = ResolveAsset(directory.FullName, asset);
                if (resolved != null) return resolved;
            }
            return null;
        }
        return null;
    }

    private static string? ResolveAsset(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return null;
        string asset = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        // Manifest resources must stay within their own package directory.
        if (!asset.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        string? folder = Path.GetDirectoryName(asset);
        if (folder == null || !Directory.Exists(folder)) return null;
        string stem = Path.GetFileNameWithoutExtension(asset), extension = Path.GetExtension(asset);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return null;
        return Directory.EnumerateFiles(folder, stem + "*" + extension).Take(64)
            .Where(path => Path.GetFileName(path).Equals(stem + extension, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Score).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static int Score(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        int score = name.Contains("altform-unplated") ? 1000 : 0;
        if (name.Contains("lightunplated")) score -= 100;
        if (name.Contains("devicefamily-")) score -= 10000;
        var target = Regex.Match(name, @"targetsize-(\d+)");
        if (target.Success) score += Math.Min(512, int.Parse(target.Groups[1].Value));
        else if (name.Contains("scale-200")) score += 200;
        else if (name.Contains("scale-100")) score += 100;
        return score;
    }
}
