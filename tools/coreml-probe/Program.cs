// TEMPORARY probe: coreml-probe <model.onnx> <cache dir> [CPU | key=value Core ML options…]
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

var model = args[0];
var cache = args[1];
bool cpu = args.Contains("CPU");
var ep = new Dictionary<string, string> { ["ModelFormat"] = "MLProgram", ["MLComputeUnits"] = "ALL", ["ModelCacheDirectory"] = cache };
foreach (var a in args.Skip(2).Where(a => a.Contains('='))) { var p = a.Split('=', 2); ep[p[0]] = p[1]; }
Console.WriteLine(cpu ? "CPU EP" : "Core ML: " + string.Join(", ", ep.Select(kv => $"{kv.Key}={kv.Value}")));

var patch = typeof(Convertino.Services.Cutout).GetMethod("ExplicitConvPads", BindingFlags.NonPublic | BindingFlags.Static)!;
var pars = new object?[] { File.ReadAllBytes(model), 0 };
var bytes = (byte[])patch.Invoke(null, pars)!;
Directory.CreateDirectory(cache);

using var so = new SessionOptions { EnableProfiling = true, ProfileOutputPathPrefix = Path.Combine(Environment.CurrentDirectory, "ort-profile") };
if (!cpu) so.AppendExecutionProvider("CoreML", ep);
var sw = Stopwatch.StartNew();
using var s = new InferenceSession(bytes, so);
Console.WriteLine($"load {sw.ElapsedMilliseconds} ms");
var rnd = new Random(7);
var data = new float[3 * 1024 * 1024];
for (int i = 0; i < data.Length; i++) data[i] = (float)(rnd.NextDouble() * 4 - 2);
for (int r = 0; r < 3; r++)
{
    sw.Restart();
    using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, 1024, 1024]);
    using var outs = s.Run(new RunOptions(), new Dictionary<string, OrtValue> { [s.InputMetadata.Keys.First()] = input }, [s.OutputMetadata.Keys.First()]);
    Console.WriteLine($"run {r + 1}: {sw.ElapsedMilliseconds} ms");
}

var file = s.EndProfiling();
using var doc = JsonDocument.Parse(File.ReadAllText(file));
var events = doc.RootElement.EnumerateArray().ToList();
var runs = events.Where(e => e.GetProperty("name").GetString() == "model_run").ToList();
foreach (var (run, k) in runs.Select((r, k) => (r, k)))
{
    long t0 = run.GetProperty("ts").GetInt64(), t1 = t0 + run.GetProperty("dur").GetInt64();
    var nodes = events.Where(e => e.GetProperty("cat").GetString() == "Node" && e.GetProperty("name").GetString()!.EndsWith("_kernel_time")
                                  && e.GetProperty("ts").GetInt64() >= t0 && e.GetProperty("ts").GetInt64() <= t1)
        .Select(e => (name: e.GetProperty("name").GetString()!.Replace("_kernel_time", ""),
                      op: e.GetProperty("args").TryGetProperty("op_name", out var o) ? o.GetString() : "?",
                      ep: e.GetProperty("args").TryGetProperty("provider", out var p) ? p.GetString() : "?",
                      ms: e.GetProperty("dur").GetInt64() / 1000.0)).ToList();
    Console.WriteLine($"-- run {k + 1}: {(t1 - t0) / 1000} ms, {nodes.Count} kernels, " +
        string.Join(", ", nodes.GroupBy(n => n.ep).Select(g => $"{g.Key} {g.Count()} kernels {g.Sum(n => n.ms):0} ms")));
    foreach (var g in nodes.GroupBy(n => (n.ep, n.op)).OrderByDescending(g => g.Sum(n => n.ms)).Take(8))
        Console.WriteLine($"   {g.Key.ep} {g.Key.op} ×{g.Count()}: {g.Sum(n => n.ms):0} ms");
    if (k == runs.Count - 1)
        foreach (var n in nodes.OrderByDescending(n => n.ms).Take(15))
            Console.WriteLine($"   top: {n.ms:0} ms {n.ep} {n.op} {n.name}");
}
