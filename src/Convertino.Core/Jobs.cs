namespace Convertino;

/// <summary>Runs a petal's action in the background and drives its progress card.</summary>
public static class Jobs
{
    public static async Task Start(PetalAction action, IReadOnlyList<string> files, ScreenPoint where)
    {
        var ui = Platform.Current.Ui;
        var cts = new CancellationTokenSource();
        IJobView? view = null;
        double lastProgress = 0;
        bool finished = false;
        bool notify = Settings.Current.Notifications;
        files = files.Select(Path.GetFullPath).ToList();

        void EnsureView()
        {
            if (view != null || action.Interactive) return;
            view = ui.Show(action, files, where);
            view.OnCancel = () => cts.Cancel();
            view.Progress(lastProgress);
        }

        var job = new Job
        {
            Inputs = files,
            Action = action,
            Where = where,
            Token = cts.Token,
            OnProgress = p => ui.Post(() => { lastProgress = p; view?.Progress(p); }),
            Place = Output.For(action),
        };

        // only show a progress card if the work is noticeable
        if (!action.Quick && !action.Interactive)
            _ = Task.Delay(450).ContinueWith(_ => ui.Post(() => { if (!finished) EnsureView(); }));

        Exception? error = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (action.Interactive) await action.Run(job);
            else await Task.Run(() => action.Run(job), cts.Token);
        }
        catch (OperationCanceledException) { job.Silent = true; }
        catch (Exception ex) { error = ex; Log.Error($"job {action.Id}", ex); }
        // "Save results": into a subfolder or a fixed folder instead of beside the original
        if (error == null && !cts.IsCancellationRequested && !job.Failed) await Task.Run(() => Output.Place(job));
        finished = true;
        Log.Info($"job {action.Id} {(error == null ? "ok" : "failed")} in {sw.ElapsedMilliseconds} ms → {string.Join(" | ", job.Outputs)}");

        if (cts.IsCancellationRequested)
        {
            foreach (var o in job.Outputs) TryDelete(o);
            view?.Dismiss();
            return;
        }
        if (job.Silent && error == null && job.Outputs.Count == 0) { view?.Dismiss(); return; }

        if (job.Outputs.Count > 0) Settings.Current.AddRecent(job.Outputs[0], action.Verb);
        if (error == null) _ = Task.Run(() => Stats.Record(job));
        if (!notify && error == null && !job.Failed) { view?.Dismiss(); return; }

        EnsureView();
        if (view == null) return;
        if (error != null)
        {
            view.Failed("Couldn't " + Lower(action.Verb), Friendly(error), action.Icon);
            return;
        }
        if (job.Failed)
        {
            view.Failed(job.Title ?? "Couldn't " + Lower(action.Verb), job.Summary ?? "", action.Icon);
            return;
        }
        if (job.Summary == null && job.Outputs.Count > 0) job.Summary = Services.Images.SizeSummary(job);
        string title = job.Title ?? (job.Outputs.Count == 1 ? Path.GetFileName(job.Outputs[0]) : $"{job.Outputs.Count} files ready");
        view.Done(title, job.Summary ?? "", job.Outputs, action.Icon);
    }

    /// <summary>Headless run for the command line. Returns the job (outputs / summary) or throws.</summary>
    public static async Task<Job> RunHeadless(PetalAction action, IReadOnlyList<string> files, Action<double>? progress = null, string? place = null)
    {
        var job = new Job { Inputs = files.Select(Path.GetFullPath).ToList(), Action = action, OnProgress = progress, Place = place };
        await Task.Run(() => action.Run(job));
        if (job.Summary == null && job.Outputs.Count > 0) job.Summary = Services.Images.SizeSummary(job);
        return job;
    }

    static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    public static string Friendly(Exception ex)
    {
        var e = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
        var msg = e.Message;
        if (e is UnauthorizedAccessException) return "No permission to write next to the file";
        if (e is IOException && msg.Contains("being used")) return "The file is open in another app";
        if (msg.Contains("NoDecodeDelegate") || msg.Contains("no decode delegate")) return "This image format isn't supported";
        if (e.HResult == unchecked((int)0x800401D0)) return "The clipboard is locked (another app, or the screen is locked)";   // CLIPBRD_E_CANT_OPEN
        return msg.Length > 140 ? msg[..140] + "…" : msg;
    }

    static void TryDelete(string p)
    {
        try
        {
            if (File.Exists(p)) File.Delete(p);
            else if (Directory.Exists(p)) Directory.Delete(p, true);
        }
        catch { }
    }
}
