using System.Diagnostics;
using System.IO;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace Convertino.Services;

/// <summary>
/// Offline background removal: BiRefNet-lite (MIT) on ONNX Runtime — DirectML GPU on Windows, Core ML on Apple silicon,
/// CPU fallback.
/// The model's 1024² mask is upscaled smoothly with its edge re-tightened, then edge colours are decontaminated
/// (blur-fusion foreground estimation) so no background tint survives in hair, fur or soft edges.
/// </summary>
public static class Cutout
{
    public const string ModelFile = "birefnet-lite.onnx";
    public const string ModelUrl = "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx";
    public const string ModelSha256 = "5600024376F572A557870A5EB0AFB1E5961636BEF4E1E22132025467D0F03333";
    public const long ModelBytes = 224_005_088;
    public static string ModelPath => Path.Combine(Paths.AppData, "models", ModelFile);
    /// <summary>ONNX Runtime ships no native library for Intel Macs.</summary>
    public static bool Supported => !OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
    public static bool Available => Supported && File.Exists(ModelPath);

    // one inference at a time (DirectML sessions don't take concurrent runs); the model unloads after a few idle minutes
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Timer Idle = new(_ => Unload());
    static InferenceSession? _session;
    static string _device = "";
    // DirectML on Windows, Core ML on Apple silicon (Intel Macs never get here); Linux runs on the CPU
    static bool _cpuOnly = !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) || Environment.GetEnvironmentVariable("CONVERTINO_CUTOUT_CPU") == "1";

    public static async Task Run(Job job)
    {
        var sw = Stopwatch.StartNew();
        int n = job.Inputs.Count;
        await Gate.WaitAsync(job.Token);
        try
        {
            for (int i = 0; i < n; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                var src = job.Inputs[i];
                var dst = Paths.Beside(src, "png", "(cutout)");
                int k = i;
                await Task.Run(() => CutOne(src, dst, job.Token, f => job.Report(k, n, f)), job.Token);
                job.Outputs.Add(dst);
            }
        }
        finally
        {
            Idle.Change(TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
            Gate.Release();
        }
        job.Summary = $"Transparent PNG · {_device} · {sw.Elapsed.TotalSeconds:0.0} s";
    }

    static void CutOne(string src, string dst, CancellationToken ct, Action<double> report)
    {
        report(0.02);
        var sw = Stopwatch.StartNew();
        using var img = Images.Load(src);
        var icc = img.ColorSpace == ColorSpace.CMYK ? null : img.GetColorProfile();
        if (img.ColorSpace != ColorSpace.sRGB) img.ColorSpace = ColorSpace.sRGB;
        img.Depth = 8;
        int w = (int)img.Width, h = (int)img.Height;

        // 1. mask from the network (squashed square input, logits → probability)
        report(0.08);
        long tLoad = sw.ElapsedMilliseconds;
        var (mask, mw, mh) = Infer(img, ct, report);
        long tMask = sw.ElapsedMilliseconds;
        ct.ThrowIfCancellationRequested();
        report(0.72);

        // 2. full-resolution alpha: smooth cubic upscale of the mask, edge re-tightened by the upscale factor
        //    (a 1024² mask stretched to 4K would otherwise leave a 8–10 px blur around everything)
        float k = (float)Math.Max(1, Math.Max((double)w / mw, (double)h / mh) / EdgeSoftness);
        var rgba = img.ToByteArray(MagickFormat.Rgba);
        var (cx, wx) = CubicTaps(w, mw);
        var (cy, wy) = CubicTaps(h, mh);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int j = 0; j < 4; j++)
                {
                    int row = cy[y * 4 + j] * mw;
                    float sum = 0;
                    for (int i = 0; i < 4; i++) sum += mask[row + cx[x * 4 + i]] * wx[x * 4 + i];
                    v += sum * wy[y * 4 + j];
                }
                int o = (y * w + x) * 4;
                rgba[o + 3] = (byte)(Remap(0.5f + (v - 0.5f) * k) * rgba[o + 3] + 0.5f);
            }
        });
        ct.ThrowIfCancellationRequested();
        report(0.82);

        // 3. pull the background colour out of semi-transparent edge pixels (wide pass on a ≤1024 px copy)
        double sc = Math.Min(1.0, 1024.0 / Math.Max(w, h));
        int lw = Math.Max(1, (int)Math.Round(w * sc)), lh = Math.Max(1, (int)Math.Round(h * sc));
        byte[] lowPx;
        using (var low = (MagickImage)img.Clone())
        {
            if (sc < 1) low.Resize(new MagickGeometry((uint)lw, (uint)lh) { IgnoreAspectRatio = true });
            lowPx = low.ToByteArray(MagickFormat.Rgb);
        }
        var (lR, lG, lB) = Planes(lowPx, lw * lh);
        var lowA = Resample(mask, mw, mh, lw, lh);
        for (int i = 0; i < lowA.Length; i++) lowA[i] = Remap(lowA[i]);
        Decontaminate(rgba, w, h, lR, lG, lB, lowA, lw, lh, ct);
        report(0.92);
        long tEdges = sw.ElapsedMilliseconds;

        using var outImg = new MagickImage(rgba, new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Rgba });
        outImg.Density = img.Density;
        if (icc != null) outImg.SetProfile(icc);
        outImg.Quality = 50; // zlib level 5: half the save time of the default for ~4% more bytes
        outImg.Write(dst, MagickFormat.Png32);
        Log.Info($"cutout: {w}×{h} load {tLoad} · mask {tMask - tLoad} · edges {tEdges - tMask} · save {sw.ElapsedMilliseconds - tEdges} ms");
    }

    // Edge softness in mask pixels after upscaling, and the alpha clean-up curve: faint haze → 0, near-solid → 1.
    const double EdgeSoftness = 2;
    const float AlphaLo = 0.04f, AlphaHi = 0.96f;
    static float Remap(float q) => Math.Clamp((q - AlphaLo) / (AlphaHi - AlphaLo), 0f, 1f);

    /// <summary>One-time model download (Hugging Face), verified against its SHA-256.</summary>
    public static async Task Download(Action<long, long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var part = ModelPath + ".part";
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Convertino/" + Cli.Version);
            using var resp = await http.GetAsync(ModelUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? ModelBytes;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                progress?.Invoke(done, total);
            }
        }
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
        if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new IOException("model download is corrupted (SHA-256 mismatch)");
        }
        File.Move(part, ModelPath, overwrite: true);
    }

    #region model

    static (float[] mask, int w, int h) Infer(MagickImage img, CancellationToken ct, Action<double> report)
    {
        var s = Open();
        report(0.3);
        try { return InferOn(s, img, ct); }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (_device != "CPU" && ex is not OperationCanceledException)
        {
            Log.Error($"cutout: {_device} run failed, retrying on CPU", ex);
            _session?.Dispose();
            _session = null;
            _cpuOnly = true;
            return InferOn(Open(), img, ct);
        }
    }

    static (float[] mask, int w, int h) InferOn(InferenceSession s, MagickImage img, CancellationToken ct)
    {
        var (inName, meta) = s.InputMetadata.First();
        int size = meta.Dimensions.Length == 4 && meta.Dimensions[3] > 0 ? meta.Dimensions[3] : 1024;
        int n = size * size;

        byte[] px;
        using (var m = (MagickImage)img.Clone())
        {
            if (m.HasAlpha) { m.BackgroundColor = MagickColors.White; m.Alpha(AlphaAction.Remove); m.Alpha(AlphaAction.Off); }
            m.FilterType = FilterType.Triangle;
            m.Resize(new MagickGeometry((uint)size, (uint)size) { IgnoreAspectRatio = true });
            px = m.ToByteArray(MagickFormat.Rgb);
        }
        var data = new float[3 * n];
        for (int i = 0; i < n; i++)
        {
            data[i] = (px[i * 3] / 255f - 0.485f) / 0.229f;
            data[n + i] = (px[i * 3 + 1] / 255f - 0.456f) / 0.224f;
            data[2 * n + i] = (px[i * 3 + 2] / 255f - 0.406f) / 0.225f;
        }

        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, size, size]);
        using var ro = new RunOptions();
        using var reg = ct.Register(() => ro.Terminate = true);
        var sw = Stopwatch.StartNew();
        using var outs = s.Run(ro, new Dictionary<string, OrtValue> { [inName] = input }, [s.OutputMetadata.Keys.First()]);
        var shape = outs[0].GetTensorTypeAndShape().Shape;
        int oh = (int)shape[^2], ow = (int)shape[^1];
        var mask = outs[0].GetTensorDataAsSpan<float>()[..(ow * oh)].ToArray();
        Log.Info($"cutout: {size}² inference on {_device} in {sw.ElapsedMilliseconds} ms");

        // BiRefNet returns logits; models that already end in a sigmoid stay in [0, 1]
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var v in mask)
        {
            // half-precision overflow on a GPU / Neural Engine shows up as NaN or ∞: let the caller retry on the CPU
            if (!float.IsFinite(v)) throw new InvalidDataException($"{_device} returned a non-finite mask");
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        if (mn < -0.01f || mx > 1.01f)
            for (int i = 0; i < mask.Length; i++) mask[i] = 1f / (1f + MathF.Exp(-mask[i]));
        return (mask, ow, oh);
    }

    static InferenceSession Open()
    {
        if (_session != null) return _session;
        OrtLogging();
        var sw = Stopwatch.StartNew();
        string note = "";
        if (!_cpuOnly)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var o = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                    // DML's whole-graph fusion runs out of GPU memory on BiRefNet's deformable convs (Intel Arc iGPU);
                    // op-by-op dispatch works and still runs ~5× faster than the CPU
                    o.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                    o.AppendExecutionProvider_DML(0);
                    _session = new InferenceSession(ModelPath, o);
                    _device = "GPU";
                }
                else
                {
                    _session = OpenCoreML(out note);
                    _device = "CoreML";
                }
            }
            catch (Exception ex) { Log.Error($"cutout: {(OperatingSystem.IsWindows() ? "DirectML" : "Core ML")} unavailable, using CPU", ex); }
        }
        if (_session == null)
        {
            using var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(ModelPath, o);
            _device = "CPU";
            note = "";
        }
        Log.Info($"cutout: model loaded on {_device} in {sw.ElapsedMilliseconds} ms{note}");
        return _session;
    }

    /// <summary>
    /// Apple silicon: ONNX Runtime hands Core ML every subgraph it can run, compiled to an ML Program that Core ML may
    /// place on the GPU, the Neural Engine or the CPU; the rest (the decoder's deformable-conv gathers) stays on
    /// ONNX Runtime's CPU kernels. The compiled programs are cached, so only the first load pays for the conversion.
    /// </summary>
    static InferenceSession OpenCoreML(out string note)
    {
        if (!OrtEnv.Instance().GetAvailableProviders().Contains("CoreMLExecutionProvider"))
            throw new PlatformNotSupportedException("this ONNX Runtime build has no Core ML provider");
        var cache = CoreMLCache();
        note = Directory.EnumerateFileSystemEntries(cache).Any() ? " (compiled Core ML program from the cache)" : " (first load: compiling the Core ML program)";
        // INFO on this session only, so Core ML's partition summary reaches OrtLogging (it's a warning only when split)
        using var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO };
        o.AppendExecutionProvider("CoreML", new Dictionary<string, string>
        {
            ["ModelFormat"] = "MLProgram",
            ["MLComputeUnits"] = "ALL",
            ["ModelCacheDirectory"] = cache,
        });
        return new InferenceSession(ModelPath, o);
    }

    // ONNX Runtime trusts whatever sits in the Core ML cache, so the folder is keyed on the model and the runtime version;
    // a new key clears the old folders
    const string CoreMLCachePrefix = "coreml-";
    static string CoreMLCache()
    {
        var dir = Path.Combine(Path.GetDirectoryName(ModelPath)!, $"{CoreMLCachePrefix}{ModelSha256[..12].ToLowerInvariant()}-ort{OrtEnv.Instance().GetVersionString()}");
        if (!Directory.Exists(dir))
        {
            DeleteCoreMLCaches();
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    static void DeleteCoreMLCaches()
    {
        var models = Path.GetDirectoryName(ModelPath)!;
        if (!Directory.Exists(models)) return;
        foreach (var d in Directory.EnumerateDirectories(models, CoreMLCachePrefix + "*"))
            try { Directory.Delete(d, true); } catch (Exception ex) { Log.Error("cutout: couldn't clear " + d, ex); }
    }

    /// <summary>Deletes the model and anything compiled from it.</summary>
    public static void Remove()
    {
        if (File.Exists(ModelPath)) File.Delete(ModelPath);
        DeleteCoreMLCaches();
    }

    // ONNX Runtime's warnings go to our log rather than the stderr of a menu-bar app; set once, before the first session
    static DOrtLoggingFunction? _ortLog;
    static void OrtLogging()
    {
        if (_ortLog != null) return;
        _ortLog = (_, level, _, _, _, msg) =>
        {
            if (level >= OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR) Log.Error("cutout: onnxruntime: " + msg);
            else if (level == OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING || msg.Contains("supported by CoreML")) Log.Info("cutout: onnxruntime: " + msg);
        };
        try
        {
            var env = new EnvironmentCreationOptions { logId = "convertino", logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, loggingFunction = _ortLog };
            OrtEnv.CreateInstanceWithOptions(ref env);
        }
        catch (Exception ex) { Log.Error("cutout: ONNX Runtime keeps logging to stderr", ex); }
    }

    static void Unload()
    {
        if (!Gate.Wait(0)) return; // busy: the running job re-arms the timer
        try
        {
            if (_session == null) return;
            _session.Dispose();
            _session = null;
            Log.Info("cutout: model unloaded");
        }
        finally { Gate.Release(); }
    }

    #endregion

    #region image math

    /// <summary>
    /// Forte &amp; Pitié blur-fusion foreground estimation. Pass 1 (wide blur) runs on the small copy; pass 2 (narrow)
    /// runs at full size in bands, so memory stays flat even for 50 MP photos. Writes the cleaned RGB into rgba.
    /// </summary>
    static void Decontaminate(byte[] rgba, int w, int h, float[] lR, float[] lG, float[] lB, float[] lA, int lw, int lh, CancellationToken ct)
    {
        int ln = lw * lh;
        int r1 = Math.Max(3, (int)Math.Round(Math.Max(lw, lh) / 32.0));
        var tmp = new float[ln];
        var bA = (float[])lA.Clone();
        Box(bA, lw, lh, r1, tmp);
        var lowF = new float[3][];
        var lowB = new float[3][];
        float[][] lI = [lR, lG, lB];
        for (int c = 0; c < 3; c++)
        {
            var f = new float[ln];
            var g = new float[ln];
            for (int i = 0; i < ln; i++) { f[i] = lI[c][i] * lA[i]; g[i] = lI[c][i] * (1 - lA[i]); }
            Box(f, lw, lh, r1, tmp);
            Box(g, lw, lh, r1, tmp);
            for (int i = 0; i < ln; i++) { f[i] /= bA[i] + 1e-5f; g[i] /= 1 - bA[i] + 1e-5f; }
            lowF[c] = f;
            lowB[c] = g;
        }

        int r2 = Math.Max(3, (int)Math.Round(Math.Max(w, h) / 600.0));
        const int Band = 96;
        int bands = (h + Band - 1) / Band;
        var outRgb = new byte[w * h * 3];
        var (xi, xt) = Taps(w, lw);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount), CancellationToken = ct };
        Parallel.For(0, bands, po, bi =>
        {
            int y0 = bi * Band, y1 = Math.Min(h, y0 + Band);
            int e0 = Math.Max(0, y0 - r2), e1 = Math.Min(h, y1 + r2);

            // solid or empty band: nothing to blend
            bool any = false;
            for (int y = e0; y < e1 && !any; y++)
                for (int x = 0; x < w; x++)
                {
                    byte a = rgba[(y * w + x) * 4 + 3];
                    if (a is > 0 and < 255) { any = true; break; }
                }
            if (!any)
            {
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, o = i * 4;
                        bool keep = rgba[o + 3] == 255;
                        outRgb[i * 3] = keep ? rgba[o] : (byte)0;
                        outRgb[i * 3 + 1] = keep ? rgba[o + 1] : (byte)0;
                        outRgb[i * 3 + 2] = keep ? rgba[o + 2] : (byte)0;
                    }
                return;
            }

            int bh = e1 - e0, m = w * bh;
            var A = new float[m];
            var XF = new[] { new float[m], new float[m], new float[m] };
            var XB = new[] { new float[m], new float[m], new float[m] };
            var t = new float[m];
            for (int y = e0; y < e1; y++)
            {
                var (ly0, ly1, ty) = Tap(y, h, lh);
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4, j = (y - e0) * w + x;
                    float a = rgba[o + 3] / 255f;
                    A[j] = a;
                    for (int c = 0; c < 3; c++)
                    {
                        float I = rgba[o + c] / 255f;
                        float bf = Bi(lowF[c], lw, xi[x], ly0, ly1, xt[x], ty), bb = Bi(lowB[c], lw, xi[x], ly0, ly1, xt[x], ty);
                        float f1 = Math.Clamp(bf + a * (I - a * bf - (1 - a) * bb), 0f, 1f);
                        XF[c][j] = f1 * a;
                        XB[c][j] = bb * (1 - a);
                    }
                }
            }
            Box(A, w, bh, r2, t);
            for (int c = 0; c < 3; c++) { Box(XF[c], w, bh, r2, t); Box(XB[c], w, bh, r2, t); }

            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, o = i * 4, j = (y - e0) * w + x;
                    float a = rgba[o + 3] / 255f, ba = A[j];
                    for (int c = 0; c < 3; c++)
                    {
                        float I = rgba[o + c] / 255f;
                        float f2 = XF[c][j] / (ba + 1e-5f), b2 = XB[c][j] / (1 - ba + 1e-5f);
                        float f = a >= 1 ? I : Math.Clamp(f2 + a * (I - a * f2 - (1 - a) * b2), 0f, 1f);
                        outRgb[i * 3 + c] = (byte)(f * 255 + 0.5f);
                    }
                }
        });
        for (int i = 0, n = w * h; i < n; i++)
        {
            rgba[i * 4] = outRgb[i * 3];
            rgba[i * 4 + 1] = outRgb[i * 3 + 1];
            rgba[i * 4 + 2] = outRgb[i * 3 + 2];
        }
    }

    /// <summary>Mean over a (2r+1)² window, clipped at the borders. In place; tmp must hold w·h.</summary>
    static void Box(float[] a, int w, int h, int r, float[] tmp)
    {
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            double s = 0;
            for (int x = 0; x <= Math.Min(r, w - 1); x++) s += a[o + x];
            for (int x = 0; x < w; x++)
            {
                int lo = x - r, hi = x + r;
                tmp[o + x] = (float)(s / (Math.Min(hi, w - 1) - Math.Max(lo, 0) + 1));
                if (hi + 1 < w) s += a[o + hi + 1];
                if (lo >= 0) s -= a[o + lo];
            }
        }
        var acc = new double[w];
        for (int y = 0; y <= Math.Min(r, h - 1); y++)
            for (int x = 0; x < w; x++) acc[x] += tmp[y * w + x];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            double inv = 1.0 / (Math.Min(y + r, h - 1) - Math.Max(y - r, 0) + 1);
            for (int x = 0; x < w; x++) a[o + x] = (float)(acc[x] * inv);
            int add = y + r + 1, rem = y - r;
            if (add < h) for (int x = 0, ao = add * w; x < w; x++) acc[x] += tmp[ao + x];
            if (rem >= 0) for (int x = 0, ro = rem * w; x < w; x++) acc[x] -= tmp[ro + x];
        }
    }

    static (float[] r, float[] g, float[] b) Planes(byte[] rgb, int n)
    {
        float[] r = new float[n], g = new float[n], b = new float[n];
        for (int i = 0; i < n; i++) { r[i] = rgb[i * 3] / 255f; g[i] = rgb[i * 3 + 1] / 255f; b[i] = rgb[i * 3 + 2] / 255f; }
        return (r, g, b);
    }

    static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        var (xi, xt) = Taps(dw, sw);
        for (int y = 0; y < dh; y++)
        {
            var (y0, y1, ty) = Tap(y, dh, sh);
            for (int x = 0; x < dw; x++) dst[y * dw + x] = Bi(src, sw, xi[x], y0, y1, xt[x], ty);
        }
        return dst;
    }

    /// <summary>Catmull-Rom taps (4 source indices + weights per destination sample, pixel-centre aligned).</summary>
    static (int[] i, float[] w) CubicTaps(int dn, int sn)
    {
        var idx = new int[dn * 4];
        var wt = new float[dn * 4];
        for (int d = 0; d < dn; d++)
        {
            double s = (d + 0.5) * sn / dn - 0.5;
            int i0 = (int)Math.Floor(s);
            float t = (float)(s - i0), t2 = t * t, t3 = t2 * t;
            wt[d * 4] = -0.5f * t3 + t2 - 0.5f * t;
            wt[d * 4 + 1] = 1.5f * t3 - 2.5f * t2 + 1;
            wt[d * 4 + 2] = -1.5f * t3 + 2 * t2 + 0.5f * t;
            wt[d * 4 + 3] = 0.5f * t3 - 0.5f * t2;
            for (int j = 0; j < 4; j++) idx[d * 4 + j] = Math.Clamp(i0 - 1 + j, 0, sn - 1);
        }
        return (idx, wt);
    }

    /// <summary>Bilinear source taps for every destination column (pixel-centre aligned).</summary>
    static (int[] i, float[] t) Taps(int dn, int sn)
    {
        var idx = new int[dn];
        var t = new float[dn];
        for (int d = 0; d < dn; d++)
        {
            var (a, _, f) = Tap(d, dn, sn);
            idx[d] = a;
            t[d] = f;
        }
        return (idx, t);
    }

    static (int i0, int i1, float t) Tap(int d, int dn, int sn)
    {
        double s = Math.Clamp((d + 0.5) * sn / dn - 0.5, 0, sn - 1);
        int i0 = (int)s;
        return (i0, Math.Min(i0 + 1, sn - 1), (float)(s - i0));
    }

    static float Bi(float[] p, int pw, int x0, int y0, int y1, float tx, float ty)
    {
        int x1 = Math.Min(x0 + 1, pw - 1);
        float a = p[y0 * pw + x0] + (p[y0 * pw + x1] - p[y0 * pw + x0]) * tx;
        float b = p[y1 * pw + x0] + (p[y1 * pw + x1] - p[y1 * pw + x0]) * tx;
        return a + (b - a) * ty;
    }

    #endregion
}
