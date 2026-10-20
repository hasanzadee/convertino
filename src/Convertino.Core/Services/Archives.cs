using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Writers.SevenZip;

namespace Convertino.Services;

/// <summary>
/// Unpacks ZIP, 7Z, RAR, TAR (plain, .gz, .bz2, .xz) and single gzip / bzip2 / xz files; packs TAR.GZ and 7Z.
/// Nothing is ever written outside the new folder: "../" and absolute names are refused, and links are checked
/// and only made once every file is out, so no later entry can be written through one.
/// </summary>
public static partial class Archives
{
    enum Format { Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz, Unknown }

    static readonly string[] Suffixes = [".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".tbz", ".txz", ".tar", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz"];

    /// <summary>"backup.tar.gz" → "backup", "notes.txt.gz" → "notes.txt", "photos.part1.rar" → "photos".</summary>
    public static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var s in Suffixes)
            if (name.Length > s.Length && name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                var stem = name[..^s.Length];
                return s == ".rar" && VolumeSuffix().Match(stem) is { Success: true } v && v.Index > 0 ? stem[..v.Index] : stem;
            }
        return Path.GetFileNameWithoutExtension(name) is { Length: > 0 } st ? st : "archive";
    }

    [GeneratedRegex(@"\.part\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeSuffix();

    // ---------- extract ----------

    public static async Task Extract(Job job)
    {
        var tally = new Tally();
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            int idx = i;
            await Task.Run(() => ExtractOne(job.Inputs[idx], job, f => job.Report(idx, job.Inputs.Count, f), tally), job.Token);
        }
        job.Summary = tally.Summary();
        if (job.Outputs.Count == 0) job.Title = "Nothing extracted";
    }

    static void ExtractOne(string src, Job job, Action<double> report, Tally tally)
    {
        var fmt = Sniff(src);
        if (fmt == Format.Unknown) throw new InvalidDataException($"{Path.GetFileName(src)} isn't an archive Convertino can open");
        var dir = Path.GetDirectoryName(src)!;
        var stem = Stem(src);

        if (fmt is Format.Zip or Format.SevenZip or Format.Rar)
        {
            var root = Paths.UniqueDir(dir, stem);
            Guard(job, root, () =>
            {
                try
                {
                    using var archive = ArchiveFactory.OpenArchive(new FileInfo(src));
                    double total = Math.Max(1, archive.TotalUncompressedSize);
                    var t = new Target(root, job.Token, tally);
                    t.Tick = () => report(t.Written / total);
                    // solid RAR and every 7z decode front to back; other archives entry by entry
                    if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                    {
                        using var reader = archive.ExtractAllEntries();
                        while (reader.MoveToNextEntry()) t.Entry(reader.Entry, reader.OpenEntryStream);
                    }
                    else
                        foreach (var e in archive.Entries) t.Entry(e, e.OpenEntryStream);
                    t.Finish();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && IsPasswordError(ex)) { throw Locked(); }
                Collapse(root, stem);
            });
            return;
        }

        // TAR, possibly inside gzip / bzip2 / xz, streamed front to back; or a single compressed file
        using var raw = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        double length = Math.Max(1, raw.Length);
        using Stream data = fmt switch
        {
            Format.GZip => new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true),
            Format.BZip2 => BZip2Stream.Create(raw, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true),
            Format.Xz => new XZStream(raw),
            _ => raw,
        };
        var head = new byte[512];
        int n = data.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var all = new PrefixedStream(head.AsMemory(0, n), data);

        if (fmt == Format.Tar || IsTarHeader(head.AsSpan(0, n)))
        {
            var root = Paths.UniqueDir(dir, stem);
            Guard(job, root, () =>
            {
                var t = new Target(root, job.Token, tally) { Tick = () => report(raw.Position / length) };
                ExtractTar(all, t);
                t.Finish();
                Collapse(root, stem);
            });
        }
        else
        {
            // notes.txt.gz → notes.txt beside it (a folder holding one file would only be in the way)
            var dst = FileTools.UniqueFile(dir, stem);
            Guard(job, dst, () =>
            {
                var t = new Target(dir, job.Token, tally) { Tick = () => report(raw.Position / length) };
                t.WriteFile(dst, all, null, null);
            });
        }
    }

    static bool IsPasswordError(Exception ex) =>
        ex is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException || ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    static Exception Locked() => new InvalidOperationException("This archive is password-protected");

    static void ExtractTar(Stream s, Target t)
    {
        using var tr = new TarReader(s, leaveOpen: true);
        while (tr.GetNextEntry() is { } e)
        {
            t.Token.ThrowIfCancellationRequested();
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    if (t.Resolve(e.Name, allowRoot: true) is { } d) t.MakeDir(d); else t.Refuse(e.Name);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (t.Resolve(e.Name) is { } f) t.WriteFile(f, e.DataStream ?? Stream.Null, e.ModificationTime.LocalDateTime, e.Mode);
                    else t.Refuse(e.Name);
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    if (t.Resolve(e.Name) is { } l) t.Link(l, e.LinkName, e.EntryType == TarEntryType.HardLink);
                    else t.Refuse(e.Name);
                    break;
                case TarEntryType.GlobalExtendedAttributes:
                    break;
                default:
                    t.Tally.Skipped++; // devices, pipes, sparse files
                    break;
            }
        }
    }

    /// <summary>"foo.zip" holding just "foo/" gives "foo", not "foo/foo".</summary>
    static void Collapse(string root, string stem)
    {
        try
        {
            var items = Directory.GetFileSystemEntries(root);
            if (items.Length != 1 || !Directory.Exists(items[0]) || new DirectoryInfo(items[0]).LinkTarget != null) return;
            if (!string.Equals(Path.GetFileName(items[0]), stem, StringComparison.OrdinalIgnoreCase)) return;
            var tmp = $"{root}.{Guid.NewGuid():N}";
            Directory.Move(items[0], tmp);
            Directory.Delete(root);
            Directory.Move(tmp, root);
        }
        catch (Exception ex) { Log.Error("extract: collapse", ex); }
    }

    /// <summary>Runs one output's work; a failure removes what was half-written, and so does an empty result
    /// (every entry refused). Cancelled jobs are cleaned up by <see cref="Jobs"/>.</summary>
    static void Guard(Job job, string output, Action work)
    {
        job.Outputs.Add(output);
        try { work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileTools.TryDelete(output);
            job.Outputs.Remove(output);
            throw;
        }
        bool nothing = Directory.Exists(output) ? !Directory.EnumerateFileSystemEntries(output).Any() : !File.Exists(output);
        if (nothing)
        {
            FileTools.TryDelete(output);
            job.Outputs.Remove(output);
        }
    }

    static Format Sniff(string path)
    {
        var h = new byte[512];
        int n;
        using (var f = File.OpenRead(path)) n = f.ReadAtLeast(h, h.Length, throwOnEndOfStream: false);
        bool At(params byte[] sig) => n >= sig.Length && h.AsSpan(0, sig.Length).SequenceEqual(sig);
        if (At(0x50, 0x4B, 0x03, 0x04) || At(0x50, 0x4B, 0x05, 0x06) || At(0x50, 0x4B, 0x07, 0x08)) return Format.Zip;
        if (At(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return Format.SevenZip;
        if (At(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)) return Format.Rar;
        if (At(0x1F, 0x8B)) return Format.GZip;
        if (At(0x42, 0x5A, 0x68)) return Format.BZip2;
        if (At(0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return Format.Xz;
        if (IsTarHeader(h.AsSpan(0, n))) return Format.Tar;
        return Format.Unknown;
    }

    /// <summary>A POSIX tar header ("ustar" magic), or an old v7 one with a valid checksum.</summary>
    static bool IsTarHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < 512 || h[0] == 0) return false;
        if (h.Slice(257, 5).SequenceEqual("ustar"u8)) return true;
        long stored = 0;
        int i = 148;
        while (i < 156 && h[i] == ' ') i++;
        int digits = 0;
        for (; i < 156 && h[i] is >= (byte)'0' and <= (byte)'7'; i++, digits++) stored = stored * 8 + (h[i] - '0');
        if (digits == 0) return false;
        long sum = 0;
        for (int k = 0; k < 512; k++) sum += k is >= 148 and < 156 ? ' ' : h[k];
        return sum == stored;
    }

    sealed class Tally
    {
        public int Files, Refused, Skipped;
        public long Bytes;

        public string Summary()
        {
            var s = $"{Files} file{(Files == 1 ? "" : "s")} · {Paths.HumanSize(Bytes)}";
            if (Refused > 0) s += $" · {Refused} unsafe {(Refused == 1 ? "entry" : "entries")} refused";
            if (Skipped > 0) s += $" · {Skipped} skipped";
            return s;
        }
    }

    /// <summary>The new folder an archive is unpacked into, and the rules for what may land in it.</summary>
    sealed class Target(string root, CancellationToken ct, Tally tally)
    {
        static readonly StringComparison Cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public readonly string Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        public readonly CancellationToken Token = ct;
        public readonly Tally Tally = tally;
        public long Written;
        public Action? Tick;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _lastTick = -1000;
        readonly List<(string Path, string Target, bool Hard)> _links = [];

        /// <summary>Full path for an entry name inside the folder, or null when it would land outside it
        /// ("../x", "/etc/x", "C:\x", "\\server\x"). Names that Windows can't store are tidied, not refused.</summary>
        public string? Resolve(string? name, bool allowRoot = false)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Replace('\\', '/');
            if (n.StartsWith('/') || (n.Length > 1 && n[1] == ':' && char.IsAsciiLetter(n[0]))) return null;
            var segs = new List<string>();
            foreach (var seg in n.Split('/'))
            {
                if (seg is "" or ".") continue;
                if (seg == "..") return null;
                segs.Add(Clean(seg));
            }
            if (segs.Count == 0) return allowRoot ? Root : null;
            var full = Path.GetFullPath(Path.Combine([Root, .. segs]));
            return Inside(full) ? full : null;
        }

        bool Inside(string full) => full.Length > Root.Length + 1 && full.StartsWith(Root, Cmp) && full[Root.Length] == Path.DirectorySeparatorChar;
        bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), Cmp);

        static string Clean(string seg)
        {
            if (!OperatingSystem.IsWindows()) return seg;
            var sb = new StringBuilder(seg.Length);
            foreach (var ch in seg) sb.Append(ch < ' ' || "<>:\"|?*".Contains(ch) ? '_' : ch);
            var s = sb.ToString().TrimEnd(' ', '.'); // Windows silently drops these
            if (s.Length == 0) return "_";
            return Reserved.Contains(s.Split('.')[0].TrimEnd(' ')) ? "_" + s : s;
        }

        public void Refuse(string? name)
        {
            Tally.Refused++;
            Log.Info($"extract: refused unsafe entry '{name}'");
        }

        public void MakeDir(string full)
        {
            if (!Same(full, Root) && !File.Exists(full)) Directory.CreateDirectory(full);
        }

        public void Entry(IEntry e, Func<Stream> open)
        {
            Token.ThrowIfCancellationRequested();
            if (e.IsEncrypted) throw Locked();
            var full = Resolve(e.Key, allowRoot: e.IsDirectory);
            if (full == null) { Refuse(e.Key); return; }
            if (e.IsDirectory) { MakeDir(full); return; }
            if (!string.IsNullOrEmpty(e.LinkTarget)) { Link(full, e.LinkTarget, hard: false); return; }
            using var s = open();
            WriteFile(full, s, e.LastModifiedTime, null);
        }

        public void WriteFile(string full, Stream data, DateTime? modified, UnixFileMode? mode)
        {
            if (Directory.Exists(full)) { Tally.Skipped++; return; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                using var o = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                int n;
                while ((n = data.Read(buf, 0, buf.Length)) > 0)
                {
                    Token.ThrowIfCancellationRequested();
                    o.Write(buf, 0, n);
                    Written += n;
                    Tally.Bytes += n;
                    Progress();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            Tally.Files++;
            Progress();
            try
            {
                if (modified is { } m && m.Year > 1980) File.SetLastWriteTime(full, m);
                // keep "executable" from tarballs, never setuid / setgid / sticky
                if (mode is { } md && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(full, (md & (UnixFileMode)0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { }
        }

        void Progress()
        {
            if (_clock.ElapsedMilliseconds - _lastTick < 50) return;
            _lastTick = _clock.ElapsedMilliseconds;
            Tick?.Invoke();
        }

        public void Link(string full, string? target, bool hard) => _links.Add((full, target ?? "", hard));

        /// <summary>
        /// Links go in last, once every file is out, and only when they stay inside the folder without passing
        /// through another link (whose target can't be judged by name). Hard links become copies, before any
        /// symbolic link exists; symbolic links Windows won't let us make (no Developer Mode) are skipped.
        /// </summary>
        public void Finish()
        {
            var linkPaths = new HashSet<string>(_links.Select(l => l.Path), OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            bool Through(string path)
            {
                for (var p = Path.GetDirectoryName(path); p != null && p.Length > Root.Length; p = Path.GetDirectoryName(p))
                    if (linkPaths.Contains(p)) return true;
                return false;
            }

            foreach (var (path, target, _) in _links.Where(l => l.Hard))
            {
                Token.ThrowIfCancellationRequested();
                var src = Resolve(target);
                if (src == null || Through(src) || Through(path) || linkPaths.Contains(src)) { Refuse($"{path} => {target}"); continue; }
                if (!File.Exists(src) || Directory.Exists(path)) { Tally.Skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(src, path, overwrite: true);
                Tally.Files++;
            }

            foreach (var (path, target, _) in _links.Where(l => !l.Hard))
            {
                Token.ThrowIfCancellationRequested();
                var dest = LinkDestination(path, target, linkPaths);
                if (dest == null || Through(path)) { Refuse($"{path} -> {target}"); continue; }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(path)!, dest);
                    if (Directory.Exists(dest)) Directory.CreateSymbolicLink(path, rel);
                    else File.CreateSymbolicLink(path, rel);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Tally.Skipped++;
                    Log.Info($"extract: link {path} -> {target} not created: {ex.Message}");
                }
            }
        }

        /// <summary>Where a symbolic link would point, worked out from its own folder; null if that leaves the folder.</summary>
        string? LinkDestination(string link, string target, HashSet<string> linkPaths)
        {
            var t = target.Replace('\\', '/');
            if (t.Length == 0 || t.StartsWith('/') || (t.Length > 1 && t[1] == ':')) return null;
            var cur = Path.GetDirectoryName(link)!;
            foreach (var seg in t.Split('/'))
            {
                if (seg is "" or ".") continue;
                if (seg == "..")
                {
                    if (Same(cur, Root)) return null;
                    cur = Path.GetDirectoryName(cur)!;
                    continue;
                }
                cur = Path.Combine(cur, Clean(seg));
                if (linkPaths.Contains(cur)) return null;
            }
            return Same(cur, Root) || Inside(cur) ? cur : null;
        }
    }

    // ---------- pack ----------

    /// <summary>Files and folders → one .tar.gz beside them (links stay links).</summary>
    public static Task TarGz(Job job) => Pack(job, "tar.gz", (dst, items, read) =>
    {
        using var fs = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var it in items)
        {
            job.Token.ThrowIfCancellationRequested();
            PaxTarEntry e;
            if (it.Link != null) e = new PaxTarEntry(TarEntryType.SymbolicLink, it.Name) { LinkName = it.Link.Replace('\\', '/') };
            else if (it.Dir) e = new PaxTarEntry(TarEntryType.Directory, it.Name + "/");
            else e = new PaxTarEntry(TarEntryType.RegularFile, it.Name) { DataStream = read(it.Full) };
            try
            {
                e.ModificationTime = File.GetLastWriteTimeUtc(it.Full);
                if (!OperatingSystem.IsWindows() && it.Link == null) e.Mode = File.GetUnixFileMode(it.Full);
            }
            catch { }
            tw.WriteEntry(e);
            e.DataStream?.Dispose();
        }
        return 0;
    });

    /// <summary>Files and folders → one .7z beside them (LZMA2; links are left out, 7z has no portable form for them).</summary>
    public static Task SevenZip(Job job) => Pack(job, "7z", (dst, items, read) =>
    {
        int skipped = 0;
        using var fs = new FileStream(dst, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var w = new SevenZipWriter(fs, new SevenZipWriterOptions());
        foreach (var it in items)
        {
            job.Token.ThrowIfCancellationRequested();
            if (it.Link != null) { skipped++; continue; }
            var when = it.Dir ? Directory.GetLastWriteTime(it.Full) : File.GetLastWriteTime(it.Full);
            if (it.Dir) { w.WriteDirectory(it.Name, when); continue; }
            using var s = read(it.Full);
            w.Write(it.Name, s, when);
        }
        return skipped;
    });

    sealed record Item(string Full, string Name, bool Dir, string? Link);

    static async Task Pack(Job job, string ext, Func<string, List<Item>, Func<string, Stream>, int> write)
    {
        // same naming as Zip: "photos/" → photos.7z, one file → its name, several → the folder they're in
        var inputs = job.Inputs.Select(Path.TrimEndingDirectorySeparator).ToList();
        var first = inputs[0];
        var dir = Path.GetDirectoryName(first)!;
        string name = inputs.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : (Path.GetFileName(dir) is { Length: > 0 } d ? d : "Archive");
        var dst = Paths.Unique(dir, name, ext);
        int skipped = 0;
        await Task.Run(() =>
        {
            var items = new List<Item>();
            foreach (var input in inputs) Collect(input, Path.GetFileName(input), items);
            double total = Math.Max(1, items.Where(i => !i.Dir && i.Link == null).Sum(i => Paths.SizeOf(i.Full)));
            long done = 0;
            Stream Read(string path) => new CountingStream(File.OpenRead(path), n => { done += n; job.Report(done / total); }, job.Token);
            Guard(job, dst, () => skipped = write(dst, items, Read));
        }, job.Token);
        long before = job.Inputs.Sum(Paths.SizeOf), after = Paths.SizeOf(dst);
        job.Summary = $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}" + (skipped > 0 ? $" · {skipped} link{(skipped == 1 ? "" : "s")} left out" : "");
    }

    static void Collect(string path, string name, List<Item> into)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        // a link is stored as a link and never followed, so a loop or a link to C:\ can't blow the archive up
        if (info.LinkTarget is { } link) { into.Add(new(path, name, false, link)); return; }
        if (info is not DirectoryInfo dir) { into.Add(new(path, name, false, null)); return; }
        into.Add(new(path, name, true, null));
        foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            Collect(child.FullName, name + "/" + child.Name, into);
    }
}

/// <summary>Read-only pass-through that reports bytes read and stops when the job is cancelled.</summary>
sealed class CountingStream(Stream inner, Action<int> onRead, CancellationToken ct) : Stream
{
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        int n = inner.Read(buffer);
        if (n > 0) onRead(n);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Gives back bytes already peeked at, then the rest of the stream.</summary>
sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
{
    int _pos;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= prefix.Length) return rest.Read(buffer);
        int n = Math.Min(buffer.Length, prefix.Length - _pos);
        prefix.Span.Slice(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
