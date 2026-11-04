using System.IO.Compression;
using System.Security;
using System.Text;

namespace SeenIt.Server.Services;

/// <summary>Zips Assets/Kodi/service.seenit into an installable add-on with the user's settings filled in.</summary>
public static class KodiAddon
{
    public static byte[] Build(string server, string token)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Kodi", "service.seenit");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var entry = zip.CreateEntry("service.seenit/" + rel, CompressionLevel.Optimal);
                using var w = entry.Open();
                var bytes = File.ReadAllBytes(file);
                if (rel == "resources/settings.xml")
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
                        .Replace("{{SERVER}}", SecurityElement.Escape(server))
                        .Replace("{{TOKEN}}", SecurityElement.Escape(token)));
                w.Write(bytes);
            }
        }
        return ms.ToArray();
    }
}
