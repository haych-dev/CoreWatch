using System.IO;
using System.IO.Enumeration;

namespace CoreWatch.Services;

public sealed class DirNode
{
    public string Name = "";
    public string Path = "";
    public DirNode? Parent;
    public long Size;          // total, including children
    public long OwnFileBytes;  // files directly inside
    public int OwnFileCount;
    public long FileCount;     // total, including children
    public bool IsFile;
    public List<DirNode> Children = [];
}

public sealed record FileHit(string Path, long Size);

public sealed class ScanProgress
{
    public long Files, Bytes;
    public volatile string Current = "";
}

/// <summary>Walks a folder tree in parallel and builds a size tree plus a list of the largest files.</summary>
public static class DiskScanner
{
    const int TopFiles = 150;
    const int FilesPerFolder = 24; // largest files kept as individual nodes per folder

    readonly record struct Entry(string Name, bool IsDir, long Length);

    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static (DirNode Root, List<FileHit> Largest) Scan(string rootPath, ScanProgress progress, CancellationToken ct)
    {
        var root = new DirNode { Name = rootPath, Path = rootPath };
        var largest = new PriorityQueue<FileHit, long>();
        var gate = new object();

        ScanDir(root, 0);
        Finish(root);

        var top = new List<FileHit>(largest.Count);
        while (largest.Count > 0) top.Add(largest.Dequeue());
        top.Reverse();
        return (root, top);

        void ScanDir(DirNode node, int depth)
        {
            if (ct.IsCancellationRequested) return;
            progress.Current = node.Path;
            var subdirs = new List<DirNode>();
            var files = new List<(string Name, long Size)>();
            try
            {
                var entries = new FileSystemEnumerable<Entry>(node.Path,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length), Options);
                foreach (var e in entries)
                {
                    if (e.IsDir)
                    {
                        subdirs.Add(new DirNode { Name = e.Name, Path = System.IO.Path.Combine(node.Path, e.Name), Parent = node });
                    }
                    else
                    {
                        node.OwnFileBytes += e.Length;
                        node.OwnFileCount++;
                        files.Add((e.Name, e.Length));
                    }
                }
            }
            catch { /* unreadable folder */ }

            Interlocked.Add(ref progress.Files, node.OwnFileCount);
            Interlocked.Add(ref progress.Bytes, node.OwnFileBytes);

            if (files.Count > 0)
            {
                files.Sort((a, b) => b.Size.CompareTo(a.Size));
                lock (gate)
                {
                    foreach (var (name, size) in files)
                    {
                        if (largest.Count < TopFiles) largest.Enqueue(new FileHit(System.IO.Path.Combine(node.Path, name), size), size);
                        else if (size > largest.Peek().Size) { largest.Dequeue(); largest.Enqueue(new FileHit(System.IO.Path.Combine(node.Path, name), size), size); }
                        else break; // sorted descending: nothing further can qualify
                    }
                }
                foreach (var (name, size) in files.Take(FilesPerFolder))
                    node.Children.Add(new DirNode { Name = name, Path = System.IO.Path.Combine(node.Path, name), Parent = node, Size = size, FileCount = 1, IsFile = true });
                long rest = files.Skip(FilesPerFolder).Sum(f => f.Size);
                if (rest > 0)
                    node.Children.Add(new DirNode { Name = $"{files.Count - FilesPerFolder:N0} smaller files", Path = node.Path, Parent = node, Size = rest, FileCount = files.Count - FilesPerFolder, IsFile = true });
            }

            node.Children.AddRange(subdirs);
            if (depth < 2 && subdirs.Count > 1)
                Parallel.ForEach(subdirs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, d => ScanDir(d, depth + 1));
            else
                foreach (var d in subdirs) ScanDir(d, depth + 1);
        }

        static void Finish(DirNode node)
        {
            if (node.IsFile) return;
            long size = node.OwnFileBytes, count = node.OwnFileCount;
            foreach (var c in node.Children)
            {
                if (c.IsFile) continue;
                Finish(c);
                size += c.Size;
                count += c.FileCount;
            }
            node.Size = size;
            node.FileCount = count;
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }
}
