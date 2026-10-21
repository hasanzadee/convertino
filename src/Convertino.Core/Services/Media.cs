using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Convertino.Services;

/// <summary>ffmpeg-powered video & audio conversions with real progress.</summary>
public static class Media
{
    static string? _ffmpeg, _ffprobe;
    static bool _searched;

    public static string? FFmpeg { get { Locate(); return _ffmpeg; } }
    public static bool Available => FFmpeg != null;
    public static string? FFprobe { get { Locate(); return _ffprobe; } }

    static void Locate()
    {
        if (_searched) return;
        _searched = true;
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe) };
        if (Paths.Which("ffmpeg") is { } onPath) candidates.Add(onPath);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
                if (Directory.Exists(winget))
                    candidates.AddRange(Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories));
            }
            catch { }
            candidates.Add(@"C:fmpeginfmpeg.exe");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ffmpeginfmpeg.exe"));
        }
        else candidates.AddRange(["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/snap/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg"]);
        _ffmpeg = candidates.FirstOrDefault(File.Exists);
        if (_ffmpeg != null)
        {
            var probe = Path.Combine(Path.GetDirectoryName(_ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            _ffprobe = File.Exists(probe) ? probe : Paths.Which("ffprobe");
        }
        Log.Info($"ffmpeg: {_ffmpeg ?? "not found"}");
    }

    public static double Duration(string path)
    {
        if (_ffprobe == null) return 0;
        try
        {
            var psi = new ProcessStartInfo(_ffprobe, $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    public static bool HasAudio(string path)
    {
        if (_ffprobe == null) return true;
        try
        {
            var psi = new ProcessStartInfo(_ffprobe, $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return s.Length > 0;
        }
        catch { return true; }
    }

    /// <summary>Run ffmpeg; progress parsed from -progress pipe:1.</summary>
    public static async Task Run(string args, double duration, Action<double>? progress, CancellationToken ct)
    {
        var exe = FFmpeg ?? throw new InvalidOperationException(OperatingSystem.IsWindows() ? L.T("ffmpeg not found — install it with: {0}", "winget install Gyan.FFmpeg") : OperatingSystem.IsMacOS() ? L.T("ffmpeg not found — install it with: {0}", "brew install ffmpeg") : L.T("ffmpeg not found — install it with your package manager (e.g. sudo apt install ffmpeg)"));
        var psi = new ProcessStartInfo(exe, $"-hide_banner -nostdin -y -progress pipe:1 -nostats {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        var errTail = new Queue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errTail) { errTail.Enqueue(e.Data); if (errTail.Count > 12) errTail.Dequeue(); }
        };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || duration <= 0) return;
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                if (long.TryParse(e.Data[(e.Data.IndexOf('=') + 1)..], out var us))
                    progress?.Invoke(Math.Clamp(us / 1e6 / duration, 0, 1));
            }
        };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (errTail) tail = string.Join(" ", errTail.TakeLast(3));
            throw new IOException(L.T("ffmpeg failed: {0}", tail));
        }
    }

    static string Q(string p) => $"\"{p}\"";

    const string H264 = "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -movflags +faststart";

    public static async Task Convert(Job job, string ext)
    {
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            var src = job.Inputs[i];
            int idx = i;
            var dst = Paths.Beside(src, ext);
            double dur = Duration(src);
            var srcKind = Kinds.Of(src);
            string a = ext switch
            {
                "mp4" => $"-i {Q(src)} {H264} -c:a aac -b:a 160k {Q(dst)}",
                "mov" => $"-i {Q(src)} {H264} -c:a aac -b:a 160k {Q(dst)}",
                "mkv" => $"-i {Q(src)} -map 0 -c copy {Q(dst)}",
                "webm" => $"-i {Q(src)} -c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -deadline good -cpu-used 4 -c:a libopus -b:a 128k {Q(dst)}",
                "gif" => $"-i {Q(src)} -vf \"fps=15,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle\" -loop 0 {Q(dst)}",
                "mp3" => $"-i {Q(src)} -vn -c:a libmp3lame -q:a 2 {Q(dst)}",
                "wav" => $"-i {Q(src)} -vn -c:a pcm_s16le {Q(dst)}",
                "flac" => $"-i {Q(src)} -vn -c:a flac {Q(dst)}",
                "m4a" => $"-i {Q(src)} -vn -c:a aac -b:a 192k {Q(dst)}",
                "ogg" => $"-i {Q(src)} -vn -c:a libvorbis -q:a 5 {Q(dst)}",
                "opus" => $"-i {Q(src)} -vn -c:a libopus -b:a 128k {Q(dst)}",
                _ => $"-i {Q(src)} {Q(dst)}",
            };
            try
            {
                // MP4 ⇄ MOV ⇄ MKV of H.264 is just a re-wrap: try a lossless, instant remux first
                if (ext is "mp4" or "mov" && srcKind == Kind.Video)
                {
                    try
                    {
                        await Run($"-i {Q(src)} -map 0:v:0 -map 0:a? -c copy -movflags +faststart {Q(dst)}", dur, p => job.Report(idx, n, p), job.Token);
                        if (IsPlayable(dst)) { job.Outputs.Add(dst); continue; }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    TryDelete(dst);
                }
                await Run(a, dur, p => job.Report(idx, n, p), job.Token);
                job.Outputs.Add(dst);
            }
            catch { TryDelete(dst); throw; }
        }
        job.Summary = Images.SizeSummary(job);
    }

    static bool IsPlayable(string path)
    {
        // a remux of an incompatible codec (e.g. VP9 into MOV) leaves a tiny or broken file
        try { return new FileInfo(path).Length > 1024 && Duration(path) > 0; } catch { return false; }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    public static Task Compress(Job job) => Each(job, "(compressed)", src => $"-i {Q(src)} -vf \"scale='if(gt(iw,ih),min(1920,iw),-2)':'if(gt(iw,ih),-2,min(1920,ih))'\" -c:v libx264 -preset medium -crf 28 -pix_fmt yuv420p -c:a aac -b:a 96k -movflags +faststart", "mp4", sizeSummary: true);

    public static Task To720(Job job) => Each(job, "(720p)", src => $"-i {Q(src)} -vf \"scale=-2:'min(720,ih)'\" {H264} -c:a aac -b:a 128k", "mp4");

    public static Task Mute(Job job) => Each(job, "(muted)", src => $"-i {Q(src)} -map 0:v -c copy -an", null);

    public static async Task Snapshot(Job job)
    {
        foreach (var src in job.Inputs)
        {
            double dur = Duration(src);
            double at = dur > 0 ? Math.Min(dur * 0.1, Math.Max(0, dur - 0.1)) : 0;
            var dst = Paths.Beside(src, "png", "(frame)");
            await Run($"-ss {at.ToString("0.###", CultureInfo.InvariantCulture)} -i {Q(src)} -frames:v 1 {Q(dst)}", 0, null, job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = L.T("Frame at 10%");
    }

    public static Task Normalize(Job job) => Each(job, "(normalized)", src => $"-i {Q(src)} -af loudnorm=I=-16:TP=-1.5:LRA=11 -ar 48000", null, audio: true);

    public static Task TrimSilence(Job job) => Each(job, "(trimmed)", src =>
        $"-i {Q(src)} -af \"silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.2,areverse,silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.2,areverse\"", null, audio: true);

    static async Task Each(Job job, string suffix, Func<string, string> args, string? ext, bool sizeSummary = false, bool audio = false)
    {
        int n = job.Inputs.Count;
        long before = 0, after = 0;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var src = job.Inputs[i];
            var outExt = ext ?? Kinds.Ext(src);
            var dst = Paths.Beside(src, outExt, suffix);
            string codec = audio ? AudioCodecFor(outExt) : "";
            try
            {
                await Run($"{args(src)} {codec} {Q(dst)}", Duration(src), p => job.Report(idx, n, p), job.Token);
            }
            catch { TryDelete(dst); throw; }
            job.Outputs.Add(dst);
            before += Paths.SizeOf(src);
            after += Paths.SizeOf(dst);
        }
        job.Summary = sizeSummary
            ? $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}  (−{Math.Max(0, 100 - after * 100.0 / Math.Max(1, before)):0}%)"
            : Images.SizeSummary(job);
    }

    static string AudioCodecFor(string ext) => ext switch
    {
        "mp3" => "-c:a libmp3lame -q:a 2",
        "m4a" or "aac" => "-c:a aac -b:a 192k",
        "ogg" or "oga" => "-c:a libvorbis -q:a 5",
        "opus" => "-c:a libopus -b:a 128k",
        "flac" => "-c:a flac",
        "wav" => "-c:a pcm_s16le",
        _ => "",
    };
}
