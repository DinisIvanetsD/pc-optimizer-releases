using System.Diagnostics;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Storage;

/// <summary>Uma pasta (ou o conjunto dos ficheiros soltos de uma pasta) com o tamanho total.</summary>
public sealed class DirNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public DirNode? Parent { get; init; }
    public bool IsFilesGroup { get; init; }

    /// <summary>O Windows não deixa ler esta pasta (ex.: WindowsApps, ou pastas de sistema sem administrador).</summary>
    public bool Locked { get; set; }
    public long Size { get; set; }
    public long Files { get; set; }
    public List<DirNode> Children { get; } = new();
}

/// <summary>
/// Percorre um disco inteiro e calcula o tamanho de cada pasta, para mostrar o que ocupa espaço
/// (como o WinDirStat / TreeSize). Só guarda pastas em memória, não ficheiros individuais.
/// </summary>
public static class FolderScanner
{
    public sealed record ScanProgress(long Files, long Bytes, string Current);

    public static Task<DirNode> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            var top = new DirNode { Name = root, FullPath = root };
            long files = 0, bytes = 0;
            var sw = Stopwatch.StartNew();
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0, // incluir ficheiros ocultos/de sistema e comprimidos pelo Windows
            };

            void Walk(DirNode node)
            {
                ct.ThrowIfCancellationRequested();
                long loose = 0, looseCount = 0;
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", opts); }
                catch { return; }
                try
                {
                    foreach (var e in entries)
                    {
                        if (e is FileInfo f)
                        {
                            long len = 0;
                            try { len = f.Length; } catch { }
                            loose += len;
                            looseCount++;
                            files++;
                            bytes += len;
                        }
                        else if (e is DirectoryInfo d)
                        {
                            // não seguir atalhos de pastas (junctions/symlinks): evita contar duas vezes
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var child = new DirNode { Name = d.Name, FullPath = d.FullName, Parent = node };
                            node.Children.Add(child);
                            Walk(child);
                        }
                        if (sw.ElapsedMilliseconds > 300)
                        {
                            progress?.Report(new ScanProgress(files, bytes, node.FullPath));
                            sw.Restart();
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                if (looseCount == 0 && node.Children.Count == 0) node.Locked = IsLocked(node.FullPath);

                if (looseCount > 0 && node.Children.Count > 0)
                    node.Children.Add(new DirNode { Name = Loc.T($"(ficheiros soltos: {looseCount:N0})"), FullPath = node.FullPath, Parent = node, IsFilesGroup = true, Size = loose, Files = looseCount });
                node.Size = loose + node.Children.Where(c => !c.IsFilesGroup).Sum(c => c.Size);
                node.Files = looseCount + node.Children.Where(c => !c.IsFilesGroup).Sum(c => c.Files);
                node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            }

            Walk(top);
            top.Locked = false;
            progress?.Report(new ScanProgress(files, bytes, Loc.T("Concluído")));
            return top;
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    static bool IsLocked(string path)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
