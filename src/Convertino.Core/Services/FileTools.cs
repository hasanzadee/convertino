using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Convertino.Services;

/// <summary>Split / join, the duplicate finder, and checksums (copy MD5 / SHA-1 / SHA-512, verify against a .sha256 or SHA256SUMS).</summary>
public static partial class FileTools
{
    const long MB = 1024 * 1024;

    // ---------- split / join ----------

    public sealed record SplitPreset(string Id, string Label, long Bytes, string Hint);

    static readonly (string Label, long Bytes)[] Sizes =
    [
        ("10 MB", 10 * MB), ("25 MB", 25 * MB), ("50 MB", 50 * MB), ("100 MB", 100 * MB), ("250 MB", 250 * MB),
        ("500 MB", 500 * MB), ("1 GB", 1024 * MB), ("4 GB", 4095 * MB),
    ];

    /// <summary>Part sizes worth offering for a file: the three largest that still cut it in two or more.</summary>
    public static IEnumerable<SplitPreset> SplitPresets(long size) =>
        Sizes.Where(s => s.Bytes < size).TakeLast(3).Select(s => new SplitPreset(
            s.Label.Replace(" ", "").ToLowerInvariant(), s.Label, s.Bytes,
            // 4 GB is really 4095 MB: the largest file FAT32 can hold
            s.Bytes == 4095 * MB ? "FAT32" : $"{(size + s.Bytes - 1) / s.Bytes} parts"));

    /// <summary>
    /// name.ext → name.ext.001, .002 … beside it, plus name.ext.sha256 of the whole file, which Join checks the result
    /// against (so it also knows no trailing part is missing). Never mixed with the parts of an earlier split.
    /// </summary>
    public static async Task Split(Job job, long partSize)
    {
        var src = job.Inputs[0];
        long size = new FileInfo(src).Length;
        int count = (int)Math.Max(1, (size + partSize - 1) / partSize);
        int width = Math.Max(3, count.ToString().Length);
        var dir = Path.GetDirectoryName(src)!;
        var name = Path.GetFileName(src);
        var baseName = name;
        string Part(int i) => Path.Combine(dir, $"{baseName}.{i.ToString().PadLeft(width, '0')}");
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p);
        for (int k = 2; Enumerable.Range(1, count + 1).Any(i => Taken(Part(i))) || Taken(Path.Combine(dir, baseName + ".sha256")); k++)
            baseName = $"{Path.GetFileNameWithoutExtension(name)} ({k}){Path.GetExtension(name)}";

        await Task.Run(() => Guarded(job, () =>
        {
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                long done = 0;
                for (int i = 1; i <= count; i++)
                {
                    var p = Part(i);
                    // CreateNew first: a part only counts as ours (and is cleaned up on failure) once we made it
                    using var o = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    job.Outputs.Add(p);
                    for (long left = Math.Min(partSize, size - done); left > 0;)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        int n = input.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n == 0) throw new IOException("The file got shorter while it was being split");
                        o.Write(buf, 0, n);
                        sha.AppendData(buf, 0, n);
                        left -= n;
                        done += n;
                        job.Report(done / (double)size);
                    }
                }
                var sum = Path.Combine(dir, baseName + ".sha256");
                using (var f = new FileStream(sum, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    job.Outputs.Add(sum);
                    f.Write(Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(sha.GetHashAndReset())}  {baseName}\n"));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }), job.Token);
        job.Title = $"Split into {count} parts";
        job.Summary = $"{Paths.HumanSize(size)} → {count} × {Paths.HumanSize(Math.Min(partSize, size))} · + SHA-256";
    }

    [GeneratedRegex(@"^(?<base>.+)\.(?<n>\d{3,})$")]
    private static partial Regex PartName();

    /// <summary>
    /// Every part of a split file in order, starting from its .001. Throws when one is missing, or when a part
    /// is a different size from the first (only the last one may be smaller).
    /// </summary>
    public static List<string> Parts(string first)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(first))!;
        var m = PartName().Match(Path.GetFileName(first));
        if (!m.Success || int.Parse(m.Groups["n"].Value) != 1) throw new InvalidOperationException("Drop the first part (.001) to join");
        var baseName = m.Groups["base"].Value;
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var found = new SortedDictionary<int, string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var mm = PartName().Match(Path.GetFileName(f));
            if (mm.Success && string.Equals(mm.Groups["base"].Value, baseName, cmp) && int.TryParse(mm.Groups["n"].Value, out var i) && i > 0)
                found.TryAdd(i, f);
        }
        int last = found.Keys.Max();
        for (int i = 1; i <= last; i++)
            if (!found.ContainsKey(i)) throw new InvalidOperationException($"Part {i:D3} of {baseName} is missing");
        var parts = found.Values.ToList();
        long size = new FileInfo(parts[0]).Length;
        for (int i = 1; i < parts.Count; i++)
        {
            long s = new FileInfo(parts[i]).Length;
            bool ok = i < parts.Count - 1 ? s == size : s > 0 && s <= size;
            if (!ok) throw new InvalidOperationException($"{Path.GetFileName(parts[i])} is {Paths.HumanSize(s)} but the first part is {Paths.HumanSize(size)}: is it incomplete?");
        }
        return parts;
    }

    /// <summary>
    /// name.ext.001 (+ .002 …) → name.ext beside them ("name (2).ext" if that's taken). With a name.ext.sha256 beside the
    /// parts (Split writes one) the result must match it; without one, a last part as big as the others is refused,
    /// since the parts after it may be missing.
    /// </summary>
    public static async Task Join(Job job)
    {
        long total = 0;
        int count = 0;
        foreach (var first in job.Inputs)
        {
            var parts = Parts(first);
            var dir = Path.GetDirectoryName(Path.GetFullPath(first))!;
            var baseName = PartName().Match(Path.GetFileName(first)).Groups["base"].Value;
            var expected = ExpectedSha256(Path.Combine(dir, baseName + ".sha256"), baseName);
            long partSize = new FileInfo(parts[0]).Length, lastSize = new FileInfo(parts[^1]).Length;
            if (expected == null && lastSize == partSize)
                throw new InvalidOperationException(parts.Count == 1
                    ? $"Only {Path.GetFileName(parts[0])} is here: put all the parts in this folder"
                    : $"{Path.GetFileName(parts[^1])} is a full-size part, so parts after it may be missing ({baseName}.sha256 beside them would settle it)");
            long size = parts.Sum(p => new FileInfo(p).Length);
            await Task.Run(() => Guarded(job, () =>
            {
                var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    long done = 0;
                    using (var o = CreateUnique(dir, baseName, out var dst))
                    {
                        job.Outputs.Add(dst);
                        foreach (var p in parts)
                        {
                            using var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                job.Token.ThrowIfCancellationRequested();
                                o.Write(buf, 0, n);
                                if (expected != null) sha.AppendData(buf, 0, n);
                                done += n;
                                job.Report(done / (double)Math.Max(1, size));
                            }
                        }
                        if (o.Length != size) throw new IOException("The joined file came out the wrong size");
                    }
                    if (expected != null && !string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The joined file doesn't match {baseName}.sha256: a part is missing or damaged");
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }), job.Token);
            total += size;
            count += parts.Count;
        }
        job.Summary = $"{count} parts → {Paths.HumanSize(total)}";
    }

    /// <summary>The SHA-256 a "name.sha256" beside the parts gives for "name" (its own line, or its only line).</summary>
    static string? ExpectedSha256(string sumFile, string name)
    {
        try
        {
            if (!File.Exists(sumFile) || new FileInfo(sumFile).Length > 1 << 20) return null;
            var sums = ParseSums(sumFile).Where(s => s.Algo?.Name == HashAlgorithmName.SHA256).ToList();
            var hit = sums.FirstOrDefault(s => s.Name != null && string.Equals(FileNameOf(s.Name), name, StringComparison.OrdinalIgnoreCase));
            return (hit ?? (sums.Count == 1 ? sums[0] : null))?.Hex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------- duplicates ----------

    // online-only OneDrive / iCloud placeholders: reading them would download everything
    const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    /// <summary>
    /// Groups identical files (same size, then same SHA-256) in the dropped folders / files and writes an HTML report beside
    /// them. Deletes nothing. Hidden and system files, links and online-only cloud files are left out.
    /// </summary>
    public static async Task Duplicates(Job job)
    {
        job.Saved = 0;
        var inputs = job.Inputs.Select(Path.TrimEndingDirectorySeparator).ToList();
        var groups = new List<(string Hash, long Size, List<FileInfo> Files)>();
        int checkedFiles = 0;
        await Task.Run(async () =>
        {
            var all = new Dictionary<string, FileInfo>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                if (Directory.Exists(input)) Walk(input, all, job.Token);
                else if (File.Exists(input)) all.TryAdd(input, new FileInfo(input));
            }
            checkedFiles = all.Count;
            var sameSize = all.Values.Where(f => f.Length > 0).GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();

            // a cheap look at the first 64 KB first, so same-sized files that differ early aren't read in full
            long headTotal = Math.Max(1, sameSize.Sum(g => g.Count() * Math.Min(g.Key, 64 * 1024))), headDone = 0;
            var candidates = new List<List<FileInfo>>();
            foreach (var g in sameSize)
                foreach (var byHead in await GroupBy(g, f => HashHead(f.FullName, job.Token), n => job.Report(0.1 * (headDone += n) / headTotal)))
                    if (byHead.Count > 1) candidates.Add(byHead);

            long fullTotal = Math.Max(1, candidates.Sum(c => c.Sum(f => f.Length))), fullDone = 0;
            foreach (var c in candidates)
                foreach (var same in await GroupByHash(c, job.Token, n => job.Report(0.1 + 0.9 * (fullDone += n) / fullTotal)))
                    if (same.Files.Count > 1) groups.Add((same.Hash, c[0].Length, same.Files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList()));
        }, job.Token);

        long wasted = groups.Sum(g => (g.Files.Count - 1) * g.Size);
        if (groups.Count == 0)
        {
            job.Title = "No duplicates";
            job.Summary = $"{checkedFiles} file{(checkedFiles == 1 ? "" : "s")} checked";
            return;
        }
        groups.Sort((a, b) => ((b.Files.Count - 1) * b.Size).CompareTo((a.Files.Count - 1) * a.Size));

        // "Photos" → "Photos duplicates.html" beside the folder; loose files → named after the folder they're in
        var first = inputs[0];
        bool folder = inputs.Count == 1 && Directory.Exists(first);
        var dir = folder ? Path.GetDirectoryName(first) ?? first : Path.GetDirectoryName(first)!;
        var title = (folder ? Path.GetFileName(first) : Path.GetFileName(dir)) is { Length: > 0 } t ? t : "Files";
        var html = Report(title, folder ? first : null, groups, wasted, checkedFiles);
        await using (var o = CreateUnique(dir, $"{title} duplicates", ".html", out var dst))
        {
            job.Outputs.Add(dst);
            await o.WriteAsync(Encoding.UTF8.GetBytes(html), job.Token);
        }
        job.Summary = $"{groups.Count} duplicate group{(groups.Count == 1 ? "" : "s")} · {Paths.HumanSize(wasted)}";
    }

    static void Walk(string root, Dictionary<string, FileInfo> into, CancellationToken ct)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var fsi in stack.Pop().EnumerateFileSystemInfos("*", opts))
            {
                bool link = (fsi.Attributes & FileAttributes.ReparsePoint) != 0 && fsi.LinkTarget != null;
                if (link) continue; // junctions and symlinks would count the same files twice, or loop
                if (fsi is DirectoryInfo d) stack.Push(d);
                else if (fsi is FileInfo f && (f.Attributes & CloudOnly) == 0) into.TryAdd(f.FullName, f);
            }
        }
    }

    static async Task<List<List<FileInfo>>> GroupBy(IEnumerable<FileInfo> files, Func<FileInfo, Task<(string Key, int Read)>> key, Action<int> progress)
    {
        var map = new Dictionary<string, List<FileInfo>>();
        foreach (var f in files)
        {
            string k;
            try
            {
                var (kk, read) = await key(f);
                progress(read);
                k = kk;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // locked or gone: not a duplicate we can prove
            if (!map.TryGetValue(k, out var list)) map[k] = list = [];
            list.Add(f);
        }
        return [.. map.Values];
    }

    static async Task<List<(string Hash, List<FileInfo> Files)>> GroupByHash(List<FileInfo> files, CancellationToken ct, Action<int> progress)
    {
        var map = new Dictionary<string, List<FileInfo>>();
        foreach (var f in files)
        {
            string h;
            try { h = await HashFile(f.FullName, HashAlgorithmName.SHA256, ct, progress); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!map.TryGetValue(h, out var list)) map[h] = list = [];
            list.Add(f);
        }
        return [.. map.Select(kv => (kv.Key, kv.Value))];
    }

    static async Task<(string, int)> HashHead(string path, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
        int n = await s.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
        return (Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n))), n);
    }

    const string ReportCss = """
        :root{--bg:#eff1f5;--card:#fff;--text:#4c4f69;--sub:#6c6f85;--accent:#8839ef;--line:#dce0e8}
        @media (prefers-color-scheme:dark){:root{--bg:#1e1e2e;--card:#313244;--text:#cdd6f4;--sub:#a6adc8;--accent:#cba6f7;--line:#45475a}}
        body{font-family:'Segoe UI Variable','Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);max-width:960px;margin:0 auto;padding:32px 20px;line-height:1.5}
        h1{font-size:1.7em;margin:0 0 4px}.sum{color:var(--sub);margin:0 0 6px}.sum b{color:var(--accent)}.note{color:var(--sub);font-size:.9em;margin:0 0 24px}
        section{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 18px;margin:0 0 14px}
        h2{font-size:1em;margin:0;display:flex;gap:12px;align-items:baseline;flex-wrap:wrap}h2 .w{margin-left:auto;color:var(--accent)}
        code{font-family:'Cascadia Code',Consolas,monospace;font-size:.78em;color:var(--sub)}
        ol{margin:8px 0 0;padding-left:22px}li{margin:2px 0;word-break:break-all}a{color:inherit;text-decoration:none}a:hover{color:var(--accent);text-decoration:underline}
        small{color:var(--sub);margin-left:8px;white-space:nowrap}
        """;

    static string Report(string title, string? root, List<(string Hash, long Size, List<FileInfo> Files)> groups, long wasted, int checkedFiles)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Duplicates in {E(title)}</title><style>{ReportCss}</style></head><body>");
        sb.Append($"<h1>Duplicates in {E(title)}</h1>");
        sb.Append($"<p class=\"sum\"><b>{groups.Count} group{(groups.Count == 1 ? "" : "s")} · {Paths.HumanSize(wasted)} wasted</b> · {checkedFiles} files checked · {DateTime.Now:yyyy-MM-dd HH:mm}</p>");
        sb.Append("<p class=\"note\">Files in a group are byte-for-byte identical (same size and SHA-256). Keep one of each and the rest is space you can free. Convertino deleted nothing.</p>");
        foreach (var (hash, size, files) in groups)
        {
            sb.Append($"<section><h2><span>{files.Count} copies · {Paths.HumanSize(size)} each</span><span class=\"w\">{Paths.HumanSize((files.Count - 1) * size)} wasted</span></h2>");
            sb.Append($"<code>SHA-256 {hash.ToLowerInvariant()}</code><ol>");
            foreach (var f in files)
            {
                var shown = root != null ? Path.GetRelativePath(root, f.FullName) : f.FullName;
                sb.Append($"<li><a href=\"{E(new Uri(f.FullName).AbsoluteUri)}\">{E(shown)}</a><small>{f.LastWriteTime:yyyy-MM-dd HH:mm}</small></li>");
            }
            sb.Append("</ol></section>\n");
        }
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    // ---------- checksums ----------

    public sealed record Algo(string Label, HashAlgorithmName Name, int HexLength);

    static readonly Algo[] Algos =
    [
        new("MD5", HashAlgorithmName.MD5, 32), new("SHA-1", HashAlgorithmName.SHA1, 40), new("SHA-256", HashAlgorithmName.SHA256, 64),
        new("SHA-384", HashAlgorithmName.SHA384, 96), new("SHA-512", HashAlgorithmName.SHA512, 128),
    ];

    /// <summary>The algorithm behind a hex digest, by its length (what sha256sum & co. write).</summary>
    public static Algo? AlgoFor(int hexLength) => Algos.FirstOrDefault(a => a.HexLength == hexLength);

    /// <summary>"SHA256", "sha-256", "SHA1" … → the algorithm; null for ones we can't compute (SHA-224, SHA3, BLAKE2 …).</summary>
    static Algo? AlgoNamed(string tag) => Algos.FirstOrDefault(a => string.Equals(a.Label.Replace("-", ""), tag.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

    public static async Task<string> HashFile(string path, HashAlgorithmName alg, CancellationToken ct, Action<int>? onRead = null)
    {
        using var h = IncrementalHash.CreateHash(alg);
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
            int n;
            while ((n = await s.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                h.AppendData(buf, 0, n);
                onRead?.Invoke(n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary>Copy MD5 / SHA-1 / SHA-512 of the dropped files ("hash  name" per line for several, like md5sum).</summary>
    public static async Task CopyHash(Job job, string label)
    {
        var alg = label switch { "MD5" => HashAlgorithmName.MD5, "SHA-1" => HashAlgorithmName.SHA1, _ => HashAlgorithmName.SHA512 };
        var files = job.Inputs.Where(File.Exists).ToList();
        long total = Math.Max(1, files.Sum(f => new FileInfo(f).Length)), done = 0;
        var lines = new List<string>();
        foreach (var f in files)
        {
            var hash = await HashFile(f, alg, job.Token, n => job.Report((done += n) / (double)total));
            lines.Add(files.Count == 1 ? hash : $"{hash}  {Path.GetFileName(f)}");
        }
        Platform.Current.SetClipboardText(string.Join(Environment.NewLine, lines));
        job.Title = $"{label} copied";
        job.Summary = lines.Count == 1 ? lines[0][..24] + "…" : $"{lines.Count} hashes";
    }

    [GeneratedRegex(@"^(?:(?:sha(?:1|224|256|384|512)|md5)sums?|checksums?)(?:\.txt)?$|[._-]checksums?\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex ListName();

    /// <summary>SHA256SUMS, MD5SUMS, checksums.txt, app_1.0_checksums.txt … (single .sha256 / .md5 files go by extension).</summary>
    public static bool IsChecksumList(string path) => ListName().IsMatch(Path.GetFileName(path));

    // the algorithm a checksum file's name promises: "x.sha256", "x.md5sum", "SHA512SUMS", "SHA1SUMS.txt"
    [GeneratedRegex(@"(?:^|\.)(?<a>md5|sha1|sha224|sha256|sha384|sha512)(?:sums?)?(?:\.txt)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedAlgo();

    /// <summary>One line of a checksum file. <see cref="Algo"/> is null when we can't check it, and <see cref="Problem"/> says why.</summary>
    public sealed record Sum(string? Name, string Hex, Algo? Algo, string? Problem);

    // GNU: "hash  name" / "hash *name" (a leading "\" means the name is escaped); BSD: "SHA256 (name) = hash"; or the bare hash
    [GeneratedRegex(@"^\\?(?<h>[0-9a-fA-F]{32,128})(?:\s+\*?(?<n>.+?))?\s*$")]
    private static partial Regex GnuLine();

    [GeneratedRegex(@"^(?<a>[A-Za-z0-9/_-]+)\s*\((?<n>.+)\)\s*=\s*(?<h>[0-9a-fA-F]+)\s*$")]
    private static partial Regex BsdLine();

    public static List<Sum> ParseSums(string path)
    {
        // what the file's name says (".sha256", "SHA512SUMS") wins over guessing from the hash length
        var named = NamedAlgo().Match(Path.GetFileName(path)) is { Success: true } nm ? nm.Groups["a"].Value : null;
        var sums = new List<Sum>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim('\uFEFF', ' ', '\t', '\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string tag;
            var m = BsdLine().Match(line);
            if (m.Success) tag = m.Groups["a"].Value; // BSD lines name their algorithm: SHA3-256, BLAKE2b … are "unsupported", not a mismatch
            else if ((m = GnuLine().Match(line)).Success) tag = named ?? AlgoFor(m.Groups["h"].Length)?.Label ?? "";
            else continue;
            var hex = m.Groups["h"].Value.ToLowerInvariant();
            if (tag.Length == 0) continue; // a bare hex run of no known length: not a checksum line
            var algo = AlgoNamed(tag);
            string? problem = null;
            if (algo == null) problem = $"{tag.ToUpperInvariant()} checksums aren't supported (MD5, SHA-1, SHA-256, SHA-384 and SHA-512 are)";
            else if (algo.HexLength != hex.Length)
            {
                problem = $"{Path.GetFileName(path)} has a {hex.Length}-digit hash where a {algo.Label} has {algo.HexLength}";
                algo = null;
            }
            string? name = m.Groups["n"].Success ? m.Groups["n"].Value : null;
            if (name != null && raw.StartsWith('\\')) name = name.Replace("\\n", "\n").Replace("\\\\", "\\");
            sums.Add(new(name, hex, algo, problem));
        }
        return sums;
    }

    static string FileNameOf(string listed) => listed.Replace('\\', '/').Split('/')[^1];

    /// <summary>
    /// Whether a checksum list was dropped with what it covers: a lone hash (no file name) with a single file, or an
    /// entry naming each of the files. Read here to pick the ring, so it gives up on anything big or unreadable.
    /// </summary>
    public static bool Covers(string list, IReadOnlyList<string> files)
    {
        try
        {
            if (files.Count == 0 || files.Any(f => !File.Exists(f)) || new FileInfo(list).Length > 4 << 20) return false;
            var sums = ParseSums(list);
            if (sums.Count == 1 && sums[0].Name == null && files.Count == 1) return true;
            var names = sums.Where(s => s.Name != null).Select(s => FileNameOf(s.Name!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return files.All(f => names.Contains(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// A file a list names, inside the list's own folder; null for anything that could reach elsewhere: an absolute
    /// path, a drive ("C:x"), a UNC share ("\\host\share", which on Windows would send our credentials there), an
    /// alternate data stream, or a ".." step. Checked before the file system is touched.
    /// </summary>
    static string? Listed(string listDir, string name)
    {
        var n = name.Replace('\\', '/');
        if (n.Length == 0 || n.StartsWith('/') || (OperatingSystem.IsWindows() && n.Contains(':'))) return null;
        if (n.Split('/').Any(s => s == "..")) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(listDir)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar)));
            return !Path.IsPathRooted(n) && full.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// A checksum file dropped with the file(s) it covers: hash them and say "Matches ✓" / "Doesn't match ✗".
    /// Dropped on its own, it checks the files it lists in its own folder (like sha256sum -c, but never outside it).
    /// </summary>
    public static async Task Verify(Job job)
    {
        var lists = job.Inputs.Where(f => Kinds.Of(f) == Kind.Checksum).ToList();
        var others = job.Inputs.Where(f => !lists.Contains(f) && File.Exists(f)).ToList();
        var sums = lists.SelectMany(l => ParseSums(l).Select(s => (List: l, Sum: s))).ToList();
        if (sums.Count == 0) throw new InvalidOperationException($"No checksums found in {string.Join(", ", lists.Select(Path.GetFileName))}");

        var checks = new List<(string File, Sum Sum)>();
        int unknown = 0, outside = 0;
        if (others.Count > 0)
        {
            foreach (var f in others)
            {
                var fn = Path.GetFileName(f);
                var hit = sums.FirstOrDefault(s => s.Sum.Name != null && string.Equals(FileNameOf(s.Sum.Name), fn, StringComparison.OrdinalIgnoreCase)).Sum;
                // a lone hash, with no file name, is for whatever was dropped with it
                if (hit == null && sums.Count == 1 && sums[0].Sum.Name == null) hit = sums[0].Sum;
                if (hit == null) { unknown++; continue; }
                checks.Add((f, hit));
            }
        }
        else
        {
            foreach (var (list, sum) in sums)
            {
                var dir = Path.GetDirectoryName(list)!;
                // "ubuntu.iso.sha256" with just a hash in it is about "ubuntu.iso"
                var f = sum.Name != null ? Listed(dir, sum.Name) : Path.Combine(dir, Path.GetFileNameWithoutExtension(list));
                if (f == null) { outside++; Log.Info($"verify: ignored '{sum.Name}' (outside {dir})"); continue; }
                if (File.Exists(f)) checks.Add((f, sum)); else unknown++;
            }
        }

        var unsupported = checks.Where(c => c.Sum.Algo == null).ToList();
        checks.RemoveAll(c => c.Sum.Algo == null);
        if (checks.Count == 0)
            throw new InvalidOperationException(
                unsupported.Count > 0 ? unsupported[0].Sum.Problem!
                : others.Count > 0 ? $"{Path.GetFileName(lists[0])} has no checksum for {Path.GetFileName(others[0])}"
                : outside > 0 && unknown == 0 ? $"{Path.GetFileName(lists[0])} only lists files outside its folder"
                : $"None of the files in {Path.GetFileName(lists[0])} are next to it");

        long total = Math.Max(1, checks.Sum(c => new FileInfo(c.File).Length)), done = 0;
        var bad = new List<string>();
        foreach (var (f, sum) in checks)
        {
            var got = await HashFile(f, sum.Algo!.Name, job.Token, n => job.Report((done += n) / (double)total));
            if (!string.Equals(got, sum.Hex, StringComparison.OrdinalIgnoreCase)) bad.Add(Path.GetFileName(f));
        }

        var algos = string.Join(" / ", checks.Select(c => c.Sum.Algo!.Label).Distinct());
        if (bad.Count > 0)
        {
            job.Failed = true;
            job.Title = checks.Count == 1 ? "Doesn't match ✗" : $"{bad.Count} of {checks.Count} {(bad.Count == 1 ? "doesn't" : "don't")} match ✗";
            job.Summary = $"{algos} · {string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? "…" : "")}";
        }
        else
        {
            job.Title = checks.Count == 1 ? "Matches ✓" : $"All {checks.Count} match ✓";
            job.Summary = checks.Count == 1 ? $"{algos} · {Path.GetFileName(checks[0].File)}" : algos;
        }
        if (unknown > 0) job.Summary += others.Count > 0 ? $" · {unknown} not in the list" : $" · {unknown} not found";
        if (outside > 0) job.Summary += $" · {outside} outside the folder ignored";
        if (unsupported.Count > 0) job.Summary += $" · {unsupported.Count} can't be checked";
    }

    // ---------- helpers ----------

    /// <summary>
    /// Creates dir/stem+ext — or "stem (2)ext" … when that's taken — with CreateNew, so nothing already there (a file, or a
    /// folder of the same name) is ever opened, overwritten or later cleaned up as if it were ours, even if it appears
    /// between the check and the create.
    /// </summary>
    public static FileStream CreateUnique(string dir, string stem, string ext, out string path)
    {
        for (int i = 1; i < 10000; i++)
        {
            path = Path.Combine(dir, i == 1 ? stem + ext : $"{stem} ({i}){ext}");
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16); }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && (File.Exists(path) || Directory.Exists(path))) { }
        }
        throw new IOException($"No free name for {stem}{ext}");
    }

    /// <summary><see cref="CreateUnique(string, string, string, out string)"/> for a whole name: "notes.txt" → "notes (2).txt", "README" → "README (2)".</summary>
    public static FileStream CreateUnique(string dir, string name, out string path)
    {
        var ext = Path.GetExtension(name);
        return ext.Length is > 1 and <= 8 && ext.Length < name.Length
            ? CreateUnique(dir, name[..^ext.Length], ext, out path)
            : CreateUnique(dir, name, "", out path);
    }

    /// <summary>Runs the work; if it fails (not cancelled) the files it created (its outputs so far) are removed.</summary>
    static void Guarded(Job job, Action work)
    {
        try { work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var o in job.Outputs) TryDeleteFile(o);
            job.Outputs.Clear();
            throw;
        }
    }

    public static void TryDeleteFile(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); }
        catch { }
    }

    /// <summary>Only for a working folder this job made itself.</summary>
    public static void TryDeleteDir(string p)
    {
        try { if (Directory.Exists(p)) Directory.Delete(p, true); }
        catch { }
    }
}
