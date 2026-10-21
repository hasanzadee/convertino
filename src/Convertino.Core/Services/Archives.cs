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
/// Nothing is ever written outside the new folder: "../", absolute and drive / UNC names are refused, links in
/// archives are skipped (never made), and a size cap stops archives that would unpack to far more than they hold.
/// Every output is created new (never adopted), so cleaning up after a failure only ever removes our own files.
/// </summary>
public static partial class Archives
{
    enum Format { Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz, Unknown }

    static readonly string[] Suffixes = [".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".tbz", ".txz", ".tar", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz"];

    /// <summary>"backup.tar.gz" → "backup", "notes.txt.gz" → "notes.txt", "photos.part1.rar" → "photos".</summary>
    public static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var s in Suffixes)
            if (name.Length > s.Length && name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                stem = name[..^s.Length];
                if (s == ".rar" && VolumeSuffix().Match(stem) is { Success: true } v && v.Index > 0) stem = stem[..v.Index];
                break;
            }
        // Windows drops trailing dots and spaces, which would make "foo." land on an existing "foo"
        stem = stem.TrimEnd('.', ' ');
        return stem.Length > 0 ? stem : "archive";
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
            await Task.Run(() => ExtractOne(job.Inputs[idx], job, f => job.Report(idx, job.Inputs.Count, Math.Min(1, f)), tally), job.Token);
        }
        job.Summary = tally.Summary();
        if (job.Outputs.Count == 0) job.Title = L.T("Nothing extracted");
    }

    static void ExtractOne(string src, Job job, Action<double> report, Tally tally)
    {
        var fmt = Sniff(src);
        if (fmt == Format.Unknown) throw new InvalidDataException(L.En("{0} isn't an archive Convertino can open", Path.GetFileName(src)));
        var dir = Path.GetDirectoryName(src)!;
        var stem = Stem(src);
        var cap = Cap.For(src, dir);

        if (fmt is Format.Zip or Format.SevenZip or Format.Rar)
        {
            IntoFolder(job, dir, stem, tally, cap, t =>
            {
                try
                {
                    using var archive = ArchiveFactory.OpenArchive(new FileInfo(src));
                    double total = Math.Max(1, archive.TotalUncompressedSize);
                    t.Tick = () => report(t.Written / total);
                    // solid RAR and every 7z decode front to back; other archives entry by entry
                    if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                    {
                        using var reader = archive.ExtractAllEntries();
                        while (reader.MoveToNextEntry()) t.Entry(reader.Entry, reader.OpenEntryStream);
                    }
                    else
                        foreach (var e in archive.Entries) t.Entry(e, e.OpenEntryStream);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or TooBigException) && IsPasswordError(ex)) { throw Locked(); }
            });
            return;
        }

        // TAR, possibly inside gzip / bzip2 / xz, streamed front to back; or a single compressed file
        using var raw = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        double length = Math.Max(1, raw.Length);
        // gzip keeps the size of its content (mod 4 GB) in its last 4 bytes: progress can follow what's written
        long expected = fmt == Format.GZip ? GzipSize(raw) : 0;
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
        double Progress(Target t) => expected > 0 && t.Written <= expected ? t.Written / (double)expected : raw.Position / length;

        if (fmt == Format.Tar || IsTarHeader(head.AsSpan(0, n)))
        {
            IntoFolder(job, dir, stem, tally, cap, t =>
            {
                t.Tick = () => report(Progress(t));
                ExtractTar(all, t);
            });
            return;
        }

        // notes.txt.gz → notes.txt beside it (a folder holding one file would only be in the way)
        var o = FileTools.CreateUnique(dir, stem, out var dst);
        job.Outputs.Add(dst);
        try
        {
            var t = new Target(dir, job.Token, tally, cap);
            t.Tick = () => report(Progress(t));
            using (o) t.Pump(all, o);
            tally.Files++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            o.Dispose();
            FileTools.TryDeleteFile(dst);
            job.Outputs.Remove(dst);
            throw;
        }
    }

    static long GzipSize(FileStream raw)
    {
        if (raw.Length < 18) return 0;
        Span<byte> b = stackalloc byte[4];
        raw.Seek(-4, SeekOrigin.End);
        raw.ReadExactly(b);
        raw.Seek(0, SeekOrigin.Begin);
        long size = BitConverter.ToUInt32(b);
        return size >= raw.Length ? size : 0; // smaller than the gzip itself: past 4 GB (wrapped) or several members
    }

    /// <summary>
    /// Unpacks into a new hidden working folder (a fresh GUID name, so it's ours alone and a failure can remove it
    /// wholesale), then renames it to "stem" — or "stem (2)" … — beside the archive. "foo.zip" holding just "foo/"
    /// gives "foo", not "foo/foo". An archive with nothing left to unpack leaves nothing behind.
    /// </summary>
    static void IntoFolder(Job job, string dir, string stem, Tally tally, Cap cap, Action<Target> work)
    {
        var temp = Path.Combine(dir, $".convertino-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        job.Outputs.Add(temp);
        try
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);
            work(new Target(temp, job.Token, tally, cap));
            var items = Directory.GetFileSystemEntries(temp);
            if (items.Length == 0)
            {
                Directory.Delete(temp);
                job.Outputs.Remove(temp);
                return;
            }
            bool single = items.Length == 1 && Directory.Exists(items[0]) && string.Equals(Path.GetFileName(items[0]), stem, StringComparison.OrdinalIgnoreCase);
            var final = MoveUnique(single ? items[0] : temp, dir, single ? Path.GetFileName(items[0]) : stem);
            job.Outputs.Remove(temp);
            job.Outputs.Add(final);
            if (single) FileTools.TryDeleteDir(temp);
            else if (OperatingSystem.IsWindows()) File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (job.Outputs.Remove(temp)) FileTools.TryDeleteDir(temp);
            throw;
        }
    }

    /// <summary>Renames a folder to dir/name, or "name (2)" … when taken; a rename never replaces what's there.</summary>
    static string MoveUnique(string from, string dir, string name)
    {
        for (int i = 1; i < 10000; i++)
        {
            var dest = Path.Combine(dir, i == 1 ? name : $"{name} ({i})");
            if (File.Exists(dest) || Directory.Exists(dest)) continue;
            try
            {
                Directory.Move(from, dest);
                return dest;
            }
            catch (IOException) when (File.Exists(dest) || Directory.Exists(dest)) { }
        }
        throw new IOException(L.En("No free folder name for {0}", name));
    }

    static bool IsPasswordError(Exception ex) =>
        ex is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException || ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    static Exception Locked() => new InvalidOperationException(L.En("This archive is password-protected"));

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
                    t.SkipLink(e.Name, e.LinkName);
                    break;
                case TarEntryType.GlobalExtendedAttributes:
                    break;
                default:
                    t.Tally.Skipped++; // devices, pipes, sparse files
                    break;
            }
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

    /// <summary>
    /// How much one archive may unpack to: 200 × its own size, at least 4 GB, and never so much that less than 1 GB of
    /// the disk stays free. Past that it's a decompression bomb (or the disk is full), and extracting stops.
    /// </summary>
    sealed record Cap(long Bytes, string Why)
    {
        const long GB = 1L << 30;

        public static Cap For(string archive, string dir)
        {
            long ratio = Math.Max(4 * GB, new FileInfo(archive).Length * 200);
            long free = long.MaxValue;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
                if (drive.IsReady) free = drive.AvailableFreeSpace - GB;
            }
            catch { }
            if (free < ratio) return new(Math.Max(0, free), L.En("unpacking it would leave less than 1 GB free on the disk"));
            return new(ratio, L.En("it unpacks to more than {0}, which looks like a decompression bomb", Paths.HumanSize(ratio)));
        }
    }

    sealed class TooBigException(string message) : IOException(message);

    sealed class Tally
    {
        public int Files, Refused, Links, Skipped;
        public long Bytes;

        public string Summary()
        {
            var s = L.N(Files, "{0} file", "{0} files") + $" · {Paths.HumanSize(Bytes)}";
            if (Refused > 0) s += " · " + L.N(Refused, "{0} unsafe entry refused", "{0} unsafe entries refused");
            if (Links > 0) s += " · " + L.N(Links, "{0} link skipped", "{0} links skipped");
            if (Skipped > 0) s += " · " + L.T("{0} skipped", Skipped);
            return s;
        }
    }

    /// <summary>The new folder an archive is unpacked into, and the rules for what may land in it.</summary>
    sealed class Target(string root, CancellationToken ct, Tally tally, Cap cap)
    {
        static readonly StringComparison Cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        };

        public readonly string Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        public readonly CancellationToken Token = ct;
        public readonly Tally Tally = tally;
        /// <summary>Bytes written for this archive (what the size cap counts).</summary>
        public long Written;
        public Action? Tick;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _lastTick = -1000;

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

        /// <summary>Links are never made: where one points can't be judged safely by its name alone.</summary>
        public void SkipLink(string? name, string? target)
        {
            Tally.Links++;
            Log.Info($"extract: skipped link '{name}' -> '{target}'");
        }

        public void MakeDir(string full)
        {
            if (string.Equals(full, Root, Cmp)) return;
            if (!TryMakeDir(full)) Tally.Skipped++;
        }

        /// <summary>Creates the folder unless a file from an earlier entry is in the way.</summary>
        bool TryMakeDir(string full)
        {
            try
            {
                Directory.CreateDirectory(full);
                return true;
            }
            catch (IOException) when (FileOnTheWay(full)) { return false; }
        }

        bool FileOnTheWay(string full)
        {
            for (var p = full; p != null && p.Length > Root.Length; p = Path.GetDirectoryName(p))
                if (File.Exists(p)) return true;
            return false;
        }

        public void Entry(IEntry e, Func<Stream> open)
        {
            Token.ThrowIfCancellationRequested();
            if (e.IsEncrypted) throw Locked();
            if (!string.IsNullOrEmpty(e.LinkTarget)) { SkipLink(e.Key, e.LinkTarget); return; }
            var full = Resolve(e.Key, allowRoot: e.IsDirectory);
            if (full == null) { Refuse(e.Key); return; }
            if (e.IsDirectory) { MakeDir(full); return; }
            using var s = open();
            WriteFile(full, s, e.LastModifiedTime, null);
        }

        /// <summary>Writes a new file; when two entries end up with the same name (case, tidied characters, a
        /// repeated tar member) the later one becomes "name (2).ext" instead of replacing the first.</summary>
        public void WriteFile(string full, Stream data, DateTime? modified, UnixFileMode? mode)
        {
            var dir = Path.GetDirectoryName(full)!;
            if (!TryMakeDir(dir)) { Tally.Skipped++; return; }
            var o = FileTools.CreateUnique(dir, Path.GetFileName(full), out var path);
            using (o) Pump(data, o);
            Tally.Files++;
            Progress();
            try
            {
                if (modified is { } m && m.Year > 1980) File.SetLastWriteTime(path, m);
                // keep "executable" from tarballs, never setuid / setgid / sticky
                if (mode is { } md && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, (md & (UnixFileMode)0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { }
        }

        public void Pump(Stream from, Stream to)
        {
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    Token.ThrowIfCancellationRequested();
                    if (Written + n > cap.Bytes) throw new TooBigException(L.En("Stopped: {0}", cap.Why));
                    to.Write(buf, 0, n);
                    Written += n;
                    Tally.Bytes += n;
                    Progress();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        void Progress()
        {
            if (_clock.ElapsedMilliseconds - _lastTick < 50) return;
            _lastTick = _clock.ElapsedMilliseconds;
            Tick?.Invoke();
        }
    }

    // ---------- pack ----------

    /// <summary>Files and folders → one .tar.gz beside them (links stay links).</summary>
    public static Task TarGz(Job job) => Pack(job, "tar.gz", (fs, items, read) =>
    {
        using var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true);
        using var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true);
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
    public static Task SevenZip(Job job) => Pack(job, "7z", (fs, items, read) =>
    {
        int skipped = 0;
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

    static async Task Pack(Job job, string ext, Func<FileStream, List<Item>, Func<string, Stream>, int> write)
    {
        // same naming as Zip: "photos/" → photos.7z, one file → its name, several → the folder they're in
        var inputs = job.Inputs.Select(Path.TrimEndingDirectorySeparator).ToList();
        var first = inputs[0];
        var dir = Path.GetDirectoryName(first)!;
        string name = inputs.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : (Path.GetFileName(dir) is { Length: > 0 } d ? d : "Archive");
        int skipped = 0;
        string dst = "";
        await Task.Run(() =>
        {
            var items = new List<Item>();
            foreach (var input in inputs) Collect(input, Path.GetFileName(input), items);
            double total = Math.Max(1, items.Where(i => !i.Dir && i.Link == null).Sum(i => Paths.SizeOf(i.Full)));
            long done = 0;
            Stream Read(string path) => new CountingStream(File.OpenRead(path), n => { done += n; job.Report(done / total); }, job.Token);
            var fs = FileTools.CreateUnique(dir, name, "." + ext, out dst);
            job.Outputs.Add(dst);
            try
            {
                using (fs) skipped = write(fs, items, Read);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FileTools.TryDeleteFile(dst);
                job.Outputs.Remove(dst);
                throw;
            }
        }, job.Token);
        long before = job.Inputs.Sum(Paths.SizeOf), after = Paths.SizeOf(dst);
        job.Summary = $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}" + (skipped > 0 ? " · " + L.N(skipped, "{0} link left out", "{0} links left out") : "");
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
