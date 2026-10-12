using System.Security.Principal;
using System.Text;

namespace PCOptimizer.Core.History;

/// <summary>
/// Recupera ficheiros da Reciclagem lendo os ficheiros $I (que guardam o caminho original e a data)
/// e devolvendo o $R correspondente ao sítio de onde veio.
/// </summary>
public static class RecycleBin
{
    public sealed record Item(string OriginalPath, DateTime Deleted, string DataPath, string InfoPath);

    public static IEnumerable<Item> Items()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) yield break;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var dir = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            string[] infos;
            try { infos = Directory.GetFiles(dir, "$I*"); }
            catch { continue; }
            foreach (var info in infos)
            {
                var item = Parse(info);
                if (item != null) yield return item;
            }
        }
    }

    /// <summary>Formato $I: versão (8), tamanho (8), data FILETIME (8), [v2: nº de caracteres (4)], caminho UTF-16.</summary>
    static Item? Parse(string infoPath)
    {
        try
        {
            var b = File.ReadAllBytes(infoPath);
            if (b.Length < 28) return null;
            var version = BitConverter.ToInt64(b, 0);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            string path;
            if (version == 2)
            {
                var chars = BitConverter.ToInt32(b, 24);
                path = Encoding.Unicode.GetString(b, 28, Math.Min(chars * 2, b.Length - 28));
            }
            else
            {
                path = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            }
            path = path.TrimEnd('\0');
            var data = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + Path.GetFileName(infoPath)[2..]);
            return new Item(path, deleted, data, infoPath);
        }
        catch { return null; }
    }

    /// <summary>Repõe tudo o que foi apagado de dentro de <paramref name="folder"/> a partir de <paramref name="since"/>.</summary>
    public static (int Restored, int Missing) Restore(string folder, DateTime since)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        int restored = 0, missing = 0;
        foreach (var item in Items().Where(i =>
                     i.OriginalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && i.Deleted >= since.AddMinutes(-1)))
        {
            try
            {
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) { missing++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (Directory.Exists(item.DataPath)) Directory.Move(item.DataPath, item.OriginalPath);
                else if (File.Exists(item.DataPath)) File.Move(item.DataPath, item.OriginalPath);
                else { missing++; continue; }
                File.Delete(item.InfoPath);
                restored++;
            }
            catch { missing++; }
        }
        return (restored, missing);
    }
}
