using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace CoreWatch.Services;

public sealed class FinderProgress
{
    public long Files, Bytes, Hashed, HashTotal;
    public volatile string Stage = "Listing files…";
    public volatile string Current = "";
}

public sealed class DupFile : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required DateTime Changed { get; init; }
    public required DupGroup Group { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string ChangedText => Changed.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DupGroup
{
    public long Size { get; init; }
    public List<DupFile> Files { get; } = [];
    public long Wasted => Size * Math.Max(0, Files.Count - 1);
    public string Title => $"{Files.Count} identical copies  ·  {Fmt.Bytes(Size)} each";
    public string WastedText => $"{Fmt.Bytes(Wasted)} could be freed";
}

public sealed record OldFile(string Path, long Size, DateTime Changed)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string SizeText => Fmt.Bytes(Size);
    public string ChangedText => Changed.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Finds files with identical contents, and large files nobody has changed for a long time. Reads only.</summary>
public static class FileFinder
{
    public const long OldMinSize = 50L << 20;
    public const int OldMinMonths = 6;
    const int PartialBytes = 64 * 1024;

    // Files that exist only in the cloud: reading them would download them.
    const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x00400000 | (FileAttributes)0x00040000;

    /// <summary>Folders left out unless the scan starts inside them: deleting "duplicates" there breaks Windows or apps.</summary>
    static bool Skip(DirectoryInfo dir, string root)
    {
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        if ((dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) return true;
        string path = dir.FullName;
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
        {
            string folder = Environment.GetFolderPath(special);
            if (folder.Length > 0 && path.Equals(folder, StringComparison.OrdinalIgnoreCase) && !root.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // AppData inside any user profile
        if (dir.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir.Parent?.FullName ?? "", "NTUSER.DAT"))) return true;
        return false;
    }

    public static (List<DupGroup> Groups, List<OldFile> Old) Scan(string root, long minSize, FinderProgress progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        var bySize = new Dictionary<long, List<(string Path, DateTime Changed)>>();
        var old = new List<OldFile>();
        var oldCutoff = DateTime.Now.AddMonths(-OldMinMonths);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            var dir = pending.Pop();
            progress.Current = dir.FullName;
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (ct.IsCancellationRequested) break;
                    if (entry is DirectoryInfo sub)
                    {
                        if (!Skip(sub, root)) pending.Push(sub);
                        continue;
                    }
                    if (entry is not FileInfo file || (file.Attributes & (FileAttributes.ReparsePoint | CloudOnly)) != 0) continue;
                    long length = file.Length;
                    Interlocked.Increment(ref progress.Files);
                    Interlocked.Add(ref progress.Bytes, length);

                    var changed = file.LastWriteTime > file.CreationTime ? file.LastWriteTime : file.CreationTime;
                    if (length >= OldMinSize && changed < oldCutoff) old.Add(new OldFile(file.FullName, length, changed));
                    if (length < minSize || length == 0) continue;
                    if (!bySize.TryGetValue(length, out var same)) bySize[length] = same = [];
                    same.Add((file.FullName, changed));
                }
            }
            catch { }
        }
        if (ct.IsCancellationRequested) return ([], []);

        // Same size → same first block → same whole content
        var candidates = bySize.Where(g => g.Value.Count > 1).ToList();
        progress.Stage = "Comparing files of the same size…";
        var groups = new List<DupGroup>();
        var needFull = new List<(long Size, List<(string Path, DateTime Changed)> Files)>();
        foreach (var (size, files) in candidates)
        {
            if (ct.IsCancellationRequested) return ([], []);
            foreach (var same in files.GroupBy(f => Hash(f.Path, PartialBytes, null, ct)).Where(g => g.Key != null && g.Count() > 1))
            {
                if (size <= PartialBytes) groups.Add(Make(size, same));
                else needFull.Add((size, same.ToList()));
            }
        }

        progress.Stage = "Reading matching files in full…";
        progress.HashTotal = needFull.Sum(n => n.Size * n.Files.Count);
        foreach (var (size, files) in needFull.OrderByDescending(n => n.Size))
        {
            if (ct.IsCancellationRequested) return ([], []);
            foreach (var same in files.GroupBy(f => Hash(f.Path, long.MaxValue, progress, ct)).Where(g => g.Key != null && g.Count() > 1))
                groups.Add(Make(size, same));
        }
        if (ct.IsCancellationRequested) return ([], []);

        return (groups.OrderByDescending(g => g.Wasted).ToList(), old.OrderByDescending(o => o.Size).ToList());

        static DupGroup Make(long size, IEnumerable<(string Path, DateTime Changed)> files)
        {
            var group = new DupGroup { Size = size };
            foreach (var (path, changed) in files.OrderBy(f => f.Changed).ThenBy(f => f.Path.Length))
                group.Files.Add(new DupFile { Path = path, Changed = changed, Group = group });
            return group;
        }
    }

    /// <summary>SHA-256 of the first <paramref name="limit"/> bytes, or null when the file cannot be read.</summary>
    static string? Hash(string path, long limit, FinderProgress? progress, CancellationToken ct)
    {
        try
        {
            if (progress != null) progress.Current = path;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[(int)Math.Min(limit, 1 << 20)];
            long left = limit;
            int read;
            while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
            {
                if (ct.IsCancellationRequested) return null;
                sha.AppendData(buffer, 0, read);
                left -= read;
                if (progress != null) Interlocked.Add(ref progress.Hashed, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch { return null; }
    }
}
