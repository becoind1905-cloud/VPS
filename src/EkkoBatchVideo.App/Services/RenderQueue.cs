using System.Collections.Concurrent;
using System.Windows;
using EkkoBatchVideo.Infrastructure;
using EkkoBatchVideo.Models;

namespace EkkoBatchVideo.Services;

public sealed class RenderQueue(
    FFmpegEngine ffmpeg,
    FaceFocusDetectionService faceFocusDetector,
    ExistingSubtitleDetectionEngine existingSubtitleDetector,
    TypographyEngine typography,
    TitleSourceService titleSource,
    SubtitleAiService subtitles,
    ManualSubtitleService manualSubtitles,
    MergePlanner mergePlanner,
    ProjectService projectService,
    RecycleBinService recycleBin,
    DiskSpaceService diskSpace,
    AppLogger logger) : IDisposable
{
    private const long ProgressSaveIntervalMilliseconds = 30_000;
    private readonly AsyncManualResetEvent _pauseGate = new(true);
    private readonly SemaphoreSlim _sourceCleanupGate = new(1, 1);
    private readonly SemaphoreSlim _diskCleanupGate = new(1, 1);
    private CancellationTokenSource? _runSource;
    private Task? _runningTask;
    private bool _isRunning;
    private long _nextProgressSaveAt;
    private long _nextProgressNotificationAt;
    private string _progressStage = "Đang edit";
    private int _liveWorkerCount = 1;

    private static int ResolveWorkerCount(PresetSettings settings)
    {
        if (settings.WorkerCount > 0) return Math.Clamp(settings.WorkerCount, 1, 8);
        var cores = Environment.ProcessorCount;
        return Math.Clamp(cores >= 16 ? 6 : cores >= 8 ? 4 : 2, 1, 8);
    }

    public bool IsRunning => _isRunning;
    public bool LastAutoShuffleTargetComplete { get; private set; }
    public bool IsPaused { get; private set; }
    public event Action? StateChanged;
    public event Action<RenderQueueProgress>? ProgressChanged;
    public event Action<IReadOnlyList<VideoJob>>? SourcesDeleted;

    public Task StartAsync(
        IList<VideoJob> jobs,
        PresetSettings settings,
        CancellationToken token = default,
        string progressStage = "Đang edit")
    {
        if (IsRunning) return _runningTask!;
        _runSource?.Dispose();
        _runSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        _progressStage = progressStage;
        LastAutoShuffleTargetComplete = false;
        Interlocked.Exchange(ref _liveWorkerCount, ResolveWorkerCount(settings));
        _pauseGate.Set();
        IsPaused = false;
        _isRunning = true;
        Interlocked.Exchange(
            ref _nextProgressSaveAt,
            Environment.TickCount64 + ProgressSaveIntervalMilliseconds);
        Interlocked.Exchange(ref _nextProgressNotificationAt, 0);
        subtitles.ResetRunSkips();
        _runningTask = settings.MergeVideosEnabled
            ? RunMergedAsync(jobs, settings, _runSource.Token)
            : RunAsync(jobs, settings, _runSource.Token);
        StateChanged?.Invoke();
        return _runningTask;
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true;
        _pauseGate.Reset();
        ffmpeg.PauseAll();
        StateChanged?.Invoke();
        logger.Info("Danh sách render đã tạm dừng.");
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        ffmpeg.ResumeAll();
        IsPaused = false;
        _pauseGate.Set();
        StateChanged?.Invoke();
        logger.Info("Danh sách render tiếp tục.");
    }

    public void Cancel()
    {
        _runSource?.Cancel();
        _pauseGate.Set();
        ffmpeg.CancelAll();
    }

    public void UpdateWorkerCount(int workerCount)
    {
        var next = Math.Clamp(workerCount, 1, 8);
        var previous = Interlocked.Exchange(ref _liveWorkerCount, next);
        if (IsRunning && previous != next)
            logger.Info($"Đã đổi số luồng live: {previous} → {next}. Luồng dư sẽ nghỉ sau output hiện tại; luồng mới được kích hoạt ngay.");
    }

    private async Task<bool> WaitForLiveWorkerAsync(
        int workerId,
        Func<bool> noMoreWork,
        bool forceSingleWorker,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (noMoreWork()) return false;
            var limit = forceSingleWorker
                ? 1
                : Math.Clamp(Volatile.Read(ref _liveWorkerCount), 1, 8);
            if (workerId <= limit) return true;
            await Task.Delay(120, token);
        }
        return false;
    }

    private async Task RunAsync(IList<VideoJob> jobs, PresetSettings settings, CancellationToken token)
    {
        // Hàng trong giao diện có thể bị gỡ ngay khi nguồn được đưa vào
        // Thùng rác. Mọi worker dùng snapshot cố định để không duyệt đồng
        // thời ObservableCollection đang được UI cập nhật.
        var runJobs = jobs.ToArray();
        var deletedSources = new ConcurrentDictionary<Guid, byte>();
        var retryExhaustedJobs = new ConcurrentDictionary<Guid, byte>();
        try
        {
        var items = await Task.Run(() =>
        {
            FFmpegEngine.AssignUniqueOutputNames(runJobs, settings);
            // Part numbering belongs to each source video, never to the global queue.
            foreach (var job in runJobs)
                for (var i = 0; i < job.Parts.Count; i++)
                    job.Parts[i].Index = i + 1;

            return runJobs.SelectMany((job, queueIndex) => job.Parts
                    .Where(part => part.Status is PartStatus.Pending or PartStatus.Failed or PartStatus.Cancelled)
                    .Select(part => new WorkItem(job, part, queueIndex)))
                .ToArray();
        }, token);
        var queuedJobs = items
            .Select(item => item.Job)
            .DistinctBy(job => job.Id)
            .ToArray();
        var finishedJobs = new ConcurrentDictionary<Guid, byte>();
        var completedJobs = 0;
        var failedJobs = 0;
        var activeItems = 0;
        var subtitleJobs = settings.AutoSubtitlesEnabled
            ? items.Select(item => item.Job)
                .Where(job => job.Media.HasAudio)
                .DistinctBy(job => job.Id)
                .ToArray()
            : Array.Empty<VideoJob>();
        var subtitleCompleted = 0;
        var subtitleSkipped = 0;

        void PublishSubtitleProgress(string stage, int active)
        {
            PublishProgress(
                stage,
                subtitleCompleted,
                subtitleJobs.Length,
                active,
                0,
                CalculateCombinedQueuePercent(
                    subtitleCompleted,
                    subtitleJobs.Length,
                    0,
                    queuedJobs.Length));
        }

        void PublishRenderProgress(
            int completed,
            int active,
            int failed,
            double? renderPercent = null)
        {
            if (subtitleJobs.Length == 0 ||
                renderPercent is null && completed >= queuedJobs.Length)
            {
                PublishProgress(
                    _progressStage,
                    completed,
                    queuedJobs.Length,
                    active,
                    failed,
                    renderPercent);
                return;
            }

            var phasePercent = renderPercent ??
                (queuedJobs.Length <= 0
                    ? 100
                    : completed * 100d / queuedJobs.Length);
            PublishProgress(
                _progressStage,
                completed,
                queuedJobs.Length,
                active,
                failed,
                CalculateCombinedQueuePercent(
                    subtitleJobs.Length,
                    subtitleJobs.Length,
                    phasePercent,
                    queuedJobs.Length));
        }

        if (subtitleJobs.Length == 0)
            PublishRenderProgress(0, 0, 0);
        else
            PublishSubtitleProgress("Phụ đề AI • đang chuẩn bị", 0);

        if (settings.AutoSubtitlesEnabled)
        {
            logger.Info("Phụ đề AI: phân tích từng video trước khi render để không tranh GPU với NVENC.");
            foreach (var job in subtitleJobs)
            {
                var subtitleNumber = subtitleCompleted + 1;
                string SubtitleStage(string stage) =>
                    $"Phụ đề AI {subtitleNumber}/{subtitleJobs.Length} • {stage}";
                try
                {
                    await UiAsync(() => job.ProcessingStage = "Chuẩn bị phụ đề");
                    PublishSubtitleProgress(
                        SubtitleStage("đang chuẩn bị"),
                        1);
                    await subtitles.EnsureJobAsync(job, settings,
                        stage =>
                        {
                            _ = UiAsync(() => job.ProcessingStage = stage);
                            PublishSubtitleProgress(SubtitleStage(stage), 1);
                        }, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Exception? failure = ex;
                    if (await TryRecoverFromDiskFullAsync(
                            ex,
                            settings,
                            $"khâu chuẩn bị phụ đề của {job.FileName}",
                            token))
                    {
                        try
                        {
                            await subtitles.EnsureJobAsync(
                                job,
                                settings,
                                stage =>
                                {
                                    _ = UiAsync(() => job.ProcessingStage = stage);
                                    PublishSubtitleProgress(SubtitleStage(stage), 1);
                                },
                                token);
                            failure = null;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception retryException)
                        {
                            failure = retryException;
                        }
                    }

                    if (failure is not null)
                    {
                        var failureMessage = failure.Message;
                        await UiAsync(() =>
                        {
                            job.Status = JobStatus.Failed;
                            job.ProcessingStage = "Lỗi phụ đề";
                            job.Error = failureMessage;
                            foreach (var part in job.Parts.Where(x => x.Status is PartStatus.Pending or PartStatus.Failed or PartStatus.Cancelled))
                            {
                                part.Status = PartStatus.Failed;
                                part.Error = failureMessage;
                            }
                        });
                        logger.Error($"Phụ đề AI {job.FileName}: {failureMessage}");
                    }
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                    {
                        subtitleCompleted++;
                        if (subtitles.TryGetRunSkipReason(
                                job,
                                settings,
                                out _))
                            subtitleSkipped++;
                        var skippedText = subtitleSkipped > 0
                            ? $" • bỏ sub {subtitleSkipped} video"
                            : "";
                        PublishSubtitleProgress(
                            $"Phụ đề AI • đã xử lý {subtitleCompleted}/" +
                            $"{subtitleJobs.Length}{skippedText}",
                            0);
                    }
                }
            }
            items = items.Where(x => x.Part.Status != PartStatus.Failed).ToArray();
            foreach (var job in queuedJobs.Where(job => job.Status == JobStatus.Failed))
            {
                if (!finishedJobs.TryAdd(job.Id, 0)) continue;
                completedJobs++;
                failedJobs++;
            }
            PublishRenderProgress(
                completedJobs,
                activeItems,
                failedJobs);
        }
        var next = -1;
        var maxWorkerSlots = Math.Min(8, Math.Max(1, items.Length));
        var workers = Enumerable.Range(1, maxWorkerSlots)
            .Select(workerId => Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (!await WaitForLiveWorkerAsync(
                            workerId,
                            () => Volatile.Read(ref next) >= items.Length - 1,
                            forceSingleWorker: false,
                            token))
                        break;
                    await _pauseGate.WaitAsync(token);
                    var index = Interlocked.Increment(ref next);
                    if (index >= items.Length) break;
                    var current = items[index];
                    Interlocked.Increment(ref activeItems);
                    PublishRenderProgress(
                        Volatile.Read(ref completedJobs),
                        Volatile.Read(ref activeItems),
                        Volatile.Read(ref failedJobs));
                    var retriesExhausted = false;
                    try
                    {
                        try
                        {
                            retriesExhausted = await ProcessAsync(
                                current,
                                runJobs,
                                settings,
                                workerId,
                                () => PublishRenderProgress(
                                    Volatile.Read(ref completedJobs),
                                    Volatile.Read(ref activeItems),
                                    Volatile.Read(ref failedJobs),
                                    CalculateNormalQueuePercent(queuedJobs)),
                                token);
                            if (retriesExhausted)
                                retryExhaustedJobs.TryAdd(
                                    current.Job.Id,
                                    0);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Exception? failure = ex;
                            if (await TryRecoverFromDiskFullAsync(
                                    ex,
                                    settings,
                                    $"{current.Job.FileName} / phần {current.Part.Index}",
                                    token))
                            {
                                try
                                {
                                    retriesExhausted = await ProcessAsync(
                                        current,
                                        runJobs,
                                        settings,
                                        workerId,
                                        () => PublishRenderProgress(
                                            Volatile.Read(ref completedJobs),
                                            Volatile.Read(ref activeItems),
                                            Volatile.Read(ref failedJobs),
                                            CalculateNormalQueuePercent(queuedJobs)),
                                        token);
                                    if (retriesExhausted)
                                        retryExhaustedJobs.TryAdd(
                                            current.Job.Id,
                                            0);
                                    failure = null;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception retryException)
                                {
                                    failure = retryException;
                                }
                            }

                            if (failure is not null)
                            {
                                var failureMessage = failure.Message;
                                await UiAsync(() =>
                                {
                                    current.Part.Status = PartStatus.Failed;
                                    current.Part.Error = failureMessage;
                                    current.Job.Status = JobStatus.Failed;
                                    current.Job.Error = failureMessage;
                                    current.Job.ProcessingStage = "Lỗi";
                                });
                                logger.Error(
                                    $"{current.Job.FileName} / phần {current.Part.Index}: {failureMessage}");
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeItems);
                        var jobFinishedNow = false;
                        if (IsJobFinished(current.Job) &&
                            finishedJobs.TryAdd(current.Job.Id, 0))
                        {
                            jobFinishedNow = true;
                            Interlocked.Increment(ref completedJobs);
                            if (current.Job.Status == JobStatus.Failed)
                                Interlocked.Increment(ref failedJobs);
                        }
                        PublishRenderProgress(
                            Volatile.Read(ref completedJobs),
                            Volatile.Read(ref activeItems),
                            Volatile.Read(ref failedJobs));
                        if (jobFinishedNow &&
                            !SubtitleAiService.IsSubtitleOnly(settings))
                        {
                            if (current.Job.Status == JobStatus.Failed &&
                                settings.DeleteFailedSourceAfterRetries &&
                                retryExhaustedJobs.ContainsKey(current.Job.Id))
                                await DeleteFailedSourceImmediatelyAsync(
                                    current.Job,
                                    retryExhaustedJobs,
                                    deletedSources);
                            else if (settings.DeleteSourceAfterSuccess)
                                await DeleteSuccessfulSourceImmediatelyAsync(
                                    current.Job,
                                    settings,
                                    deletedSources);
                        }
                    }
                }
            }, token)).ToArray();

            logger.Info($"Bắt đầu {items.Length} phần với {workers.Length} luồng xử lý.");
            await Task.WhenAll(workers);
            if (settings.DeleteSourceAfterSuccess && !SubtitleAiService.IsSubtitleOnly(settings))
            {
                var remainingSuccessfulSources = runJobs
                    .Where(job =>
                        !deletedSources.ContainsKey(job.Id) &&
                        File.Exists(job.InputPath))
                    .ToArray();
                if (remainingSuccessfulSources.Length > 0)
                {
                    var deleted = await RunSourceCleanupAsync(
                        () => DeleteSuccessfulSources(
                            remainingSuccessfulSources,
                            settings,
                            deletedSources),
                        "kiểm tra nguồn hoàn thành còn lại");
                    LogSourceCleanupResult(deleted, "edit thành công");
                    await NotifySourcesDeletedAsync(deleted);
                }
            }
            if (settings.DeleteFailedSourceAfterRetries &&
                !SubtitleAiService.IsSubtitleOnly(settings))
            {
                var deleted = await RunSourceCleanupAsync(
                    () => DeleteFailedSources(
                        queuedJobs,
                        retryExhaustedJobs,
                        deletedSources),
                    "xóa video nguồn bị lỗi sau khi đã thử lại");
                LogSourceCleanupResult(deleted, "edit lỗi sau khi đã thử lại");
                await NotifySourcesDeletedAsync(deleted);
            }
            logger.Success("Danh sách render đã hoàn tất.");
        }
        catch (OperationCanceledException) { logger.Warning("Danh sách render đã hủy."); }
        finally
        {
            await UiAsync(() =>
            {
                foreach (var job in runJobs)
                {
                    if (token.IsCancellationRequested && job.Status is (JobStatus.Rendering or JobStatus.Paused))
                    {
                        job.Status = JobStatus.Cancelled;
                        job.ProcessingStage = "Đã hủy";
                    }
                    else
                    {
                        UpdateJob(job);
                        if (job.Status == JobStatus.Succeeded) job.ProcessingStage = "Hoàn thành";
                        else if (job.Status == JobStatus.Failed) job.ProcessingStage = "Lỗi";
                    }
                }
            });
            try
            {
                await projectService.SaveAutoAsync(
                    new ProjectState
                    {
                        Settings = settings,
                        Jobs = RemainingJobs(runJobs)
                    });
            }
            catch (Exception ex) { logger.Warning($"Tự lưu: {ex.Message}"); }
            IsPaused = false;
            _isRunning = false;
            StateChanged?.Invoke();
        }
    }

    private async Task RunMergedAsync(
        IList<VideoJob> jobs,
        PresetSettings settings,
        CancellationToken token)
    {
        var runJobs = jobs.ToArray();
        var plans = Array.Empty<MergeOutputPlan>();
        var results = new ConcurrentDictionary<int, RenderResult>();
        var outputProgress = new ConcurrentDictionary<int, double>();
        var deletedSources = new ConcurrentDictionary<Guid, byte>();
        var retryExhaustedOutputs = new ConcurrentDictionary<int, byte>();
        IReadOnlyDictionary<Guid, int[]> jobOutputs =
            new Dictionary<Guid, int[]>();

        try
        {
            // Lập kế hoạch, đặt tên và tạo ánh xạ một lần ở nền. Ánh xạ cũ quét
            // toàn bộ kế hoạch cho từng video (O(n²)) và làm UI treo với 14.000 file.
            var prepared = await Task.Run(
                () => PrepareMergedRun(runJobs, settings),
                token);
            plans = prepared.Plans;
            results = prepared.Results;
            outputProgress = prepared.OutputProgress;
            jobOutputs = prepared.JobOutputs;
            var subtitleJobs = settings.AutoSubtitlesEnabled
                ? plans.SelectMany(plan => plan.Segments)
                    .Select(segment => segment.Job)
                    .Where(job => job.Media.HasAudio)
                    .DistinctBy(job => job.Id)
                    .ToArray()
                : Array.Empty<VideoJob>();
            var subtitleCompleted = 0;
            var subtitleSkipped = 0;
            var subtitleStarted = 0;

            void PublishSubtitleProgress(string stage, int active)
            {
                var processed = Volatile.Read(ref subtitleCompleted);
                PublishProgress(
                    stage,
                    processed,
                    subtitleJobs.Length,
                    active,
                    0,
                    CalculateCombinedQueuePercent(
                        processed,
                        subtitleJobs.Length,
                        CalculateMergedQueuePercent(
                            outputProgress,
                            plans.Length),
                        plans.Length));
            }

            void PublishMergedRenderProgress(
                int completed,
                int active,
                int failed,
                double? renderPercent = null)
            {
                if (subtitleJobs.Length == 0 ||
                    renderPercent is null && completed >= plans.Length)
                {
                    PublishProgress(
                        _progressStage,
                        completed,
                        plans.Length,
                        active,
                        failed,
                        renderPercent);
                    return;
                }

                var phasePercent = renderPercent ??
                    (plans.Length <= 0
                        ? 100
                        : completed * 100d / plans.Length);
                PublishProgress(
                    _progressStage,
                    completed,
                    plans.Length,
                    active,
                    failed,
                    CalculateCombinedQueuePercent(
                        Volatile.Read(ref subtitleCompleted),
                        subtitleJobs.Length,
                        phasePercent,
                        plans.Length));
            }

            if (subtitleJobs.Length == 0)
                PublishMergedRenderProgress(0, 0, 0);
            else
                PublishSubtitleProgress("Phụ đề AI ghép • đang chuẩn bị", 0);

            await UiAsync(() =>
            {
                foreach (var job in runJobs)
                {
                    job.Status = JobStatus.Ready;
                    var resumeIndexes = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
                    job.Progress = resumeIndexes.Length == 0
                        ? 0
                        : resumeIndexes.Average(index => outputProgress.GetValueOrDefault(index));
                    job.Error = "";
                    job.ProcessingStage = job.Progress > 0
                        ? "Đã khôi phục tiến độ xào"
                        : "Chờ ghép";
                    foreach (var part in job.Parts)
                    {
                        part.Status = PartStatus.Pending;
                        part.Progress = job.Progress;
                        part.Error = "";
                    }
                }
            });

            var preparedSubtitleJobs =
                new ConcurrentDictionary<Guid, byte>();

            async Task EnsurePlanSubtitlesAsync(MergeOutputPlan plan)
            {
                if (!settings.AutoSubtitlesEnabled) return;

                var jobsToPrepare = plan.Segments
                    .Select(segment => segment.Job)
                    .Where(job => job.Media.HasAudio)
                    .DistinctBy(job => job.Id)
                    .Where(job =>
                        preparedSubtitleJobs.TryAdd(job.Id, 0))
                    .ToArray();

                await Task.WhenAll(jobsToPrepare.Select(async job =>
                {
                    var subtitleNumber =
                        Interlocked.Increment(ref subtitleStarted);
                    string SubtitleStage(string stage) =>
                        $"Phụ đề AI ghép {subtitleNumber}/{subtitleJobs.Length} • {stage}";
                    try
                    {
                        await UiAsync(() =>
                            job.ProcessingStage =
                                $"AI cho video ghép {plan.Index}");
                        PublishSubtitleProgress(
                            SubtitleStage($"chuẩn bị file ghép {plan.Index}"),
                            1);
                        await subtitles.EnsureJobAsync(
                            job,
                            settings,
                            stage =>
                            {
                                _ = UiAsync(() => job.ProcessingStage = stage);
                                PublishSubtitleProgress(SubtitleStage(stage), 1);
                            },
                            token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Exception? failure = ex;
                        if (await TryRecoverFromDiskFullAsync(
                                ex,
                                settings,
                                $"khâu chuẩn bị phụ đề ghép của {job.FileName}",
                                token))
                        {
                            try
                            {
                                await subtitles.EnsureJobAsync(
                                    job,
                                    settings,
                                    stage =>
                                    {
                                        _ = UiAsync(() => job.ProcessingStage = stage);
                                        PublishSubtitleProgress(SubtitleStage(stage), 1);
                                    },
                                    token);
                                failure = null;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception retryException)
                            {
                                failure = retryException;
                            }
                        }

                        if (failure is not null)
                            throw new InvalidOperationException(
                                $"{job.FileName}: {failure.Message}",
                                failure);
                    }
                    finally
                    {
                        if (!token.IsCancellationRequested)
                        {
                            var processed =
                                Interlocked.Increment(ref subtitleCompleted);
                            if (subtitles.TryGetRunSkipReason(
                                    job,
                                    settings,
                                    out _))
                                Interlocked.Increment(ref subtitleSkipped);
                            var skipped = Volatile.Read(ref subtitleSkipped);
                            var skippedText = skipped > 0
                                ? $" • bỏ sub {skipped} video"
                                : "";
                            PublishSubtitleProgress(
                                $"Phụ đề AI ghép • đã xử lý {processed}/" +
                                $"{subtitleJobs.Length}{skippedText}",
                                0);
                        }
                    }
                }));
            }

            var next = -1;
            var completedOutputs = results.Count;
            var failedOutputs = results.Values.Count(result => !result.Success);
            var activeOutputs = 0;
            var initialWorkerCount = settings.AutoSubtitlesEnabled
                ? 1
                : Math.Min(
                    Volatile.Read(ref _liveWorkerCount),
                    Math.Max(1, plans.Length));
            var maxWorkerSlots = settings.AutoSubtitlesEnabled
                ? 1
                : Math.Min(8, Math.Max(1, plans.Length));
            if (settings.AutoShuffleBatchEnabled && completedOutputs > 0)
            {
                logger.Info(
                    $"Tiếp tục tự xào: đã có {completedOutputs:N0}/{plans.Length:N0} output hoàn thành; " +
                    "chỉ xử lý các output còn thiếu.");
                PublishMergedRenderProgress(
                    completedOutputs,
                    0,
                    failedOutputs,
                    CalculateMergedQueuePercent(outputProgress, plans.Length));
            }

            var workers = Enumerable
                .Range(1, maxWorkerSlots)
                .Select(workerId => Task.Run(async () =>
                {
                    Task? prefetchedSubtitleTask = null;
                    var prefetchedSubtitlePlanIndex = -1;
                    while (!token.IsCancellationRequested)
                    {
                        if (!await WaitForLiveWorkerAsync(
                                workerId,
                                () => Volatile.Read(ref next) >= plans.Length - 1,
                                forceSingleWorker: settings.AutoSubtitlesEnabled,
                                token))
                            break;
                        await _pauseGate.WaitAsync(token);
                        var index = Interlocked.Increment(ref next);
                        if (index >= plans.Length) break;
                        var plan = plans[index];
                        // Phiên tự xào tiếp tục: output đã hoàn thành từ lần trước
                        // được đánh dấu sẵn trong results, không gọi FFmpeg lại.
                        if (results.ContainsKey(plan.Index))
                            continue;
                        await _pauseGate.WaitAsync(token);
                        Interlocked.Increment(ref activeOutputs);
                        PublishMergedRenderProgress(
                            Volatile.Read(ref completedOutputs),
                            Volatile.Read(ref activeOutputs),
                            Volatile.Read(ref failedOutputs));
                        var finished = false;
                        try
                        {
                            if (settings.AutoSubtitlesEnabled &&
                                prefetchedSubtitleTask is not null &&
                                prefetchedSubtitlePlanIndex == index)
                            {
                                var currentSubtitleTask =
                                    prefetchedSubtitleTask;
                                prefetchedSubtitleTask = null;
                                prefetchedSubtitlePlanIndex = -1;
                                await currentSubtitleTask;
                            }
                            else
                                await EnsurePlanSubtitlesAsync(plan);

                            // Double buffer: khi NVENC đang ghép file hiện tại,
                            // Whisper/Argos chuẩn bị trước đúng một file kế tiếp.
                            // Vì chỉ nhìn trước một kế hoạch nên cache không tăng
                            // hàng loạt và video kế tiếp có thể chạy ngay.
                            var nextPlanIndex = index + 1;
                            if (settings.AutoSubtitlesEnabled &&
                                nextPlanIndex < plans.Length)
                            {
                                prefetchedSubtitlePlanIndex = nextPlanIndex;
                                prefetchedSubtitleTask =
                                    EnsurePlanSubtitlesAsync(
                                        plans[nextPlanIndex]);
                                logger.Info(
                                    $"Pipeline AI: đang chuẩn bị trước video ghép " +
                                    $"{plans[nextPlanIndex].Index} trong khi ghép " +
                                    $"video {plan.Index}.");
                            }

                            var retriesExhausted = await ProcessMergedAsync(
                                plan,
                                runJobs,
                                settings,
                                workerId,
                                outputProgress,
                                jobOutputs,
                                results,
                                () => PublishMergedRenderProgress(
                                    Volatile.Read(ref completedOutputs),
                                    Volatile.Read(ref activeOutputs),
                                    Volatile.Read(ref failedOutputs),
                                    CalculateMergedQueuePercent(
                                        outputProgress,
                                        plans.Length)),
                                token,
                                replacePlan: replacement => plans[index] = replacement);
                            if (retriesExhausted)
                                retryExhaustedOutputs.TryAdd(plan.Index, 0);
                            finished = true;
                        }
                        catch (OperationCanceledException)
                        {
                            if (prefetchedSubtitleTask is not null)
                            {
                                try { await prefetchedSubtitleTask; }
                                catch (OperationCanceledException) { }
                                catch { /* Lỗi sẽ được ghi nếu kế hoạch đó chạy. */ }
                            }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            results[plan.Index] = new RenderResult(
                                false,
                                false,
                                "",
                                "Không chuẩn bị được phụ đề cho video ghép: " +
                                ex.Message,
                                IsDiskFull:
                                DiskSpaceErrorDetector.IsDiskFull(ex));
                            outputProgress[plan.Index] = 100;
                            logger.Error(
                                $"Video ghép {plan.Index}: {ex.Message}");
                            finished = true;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeOutputs);
                            if (finished)
                            {
                                Interlocked.Increment(ref completedOutputs);
                                if (!results.TryGetValue(plan.Index, out var result) ||
                                    !result.Success)
                                    Interlocked.Increment(ref failedOutputs);
                            }
                            PublishMergedRenderProgress(
                                Volatile.Read(ref completedOutputs),
                                Volatile.Read(ref activeOutputs),
                                Volatile.Read(ref failedOutputs));
                        }
                        if (finished &&
                            settings.DeleteSourceAfterSuccess &&
                            !settings.AutoShuffleBatchEnabled &&
                            !SubtitleAiService.IsSubtitleOnly(settings))
                            await DeleteSuccessfulMergedSourcesImmediatelyAsync(
                                plan.Segments
                                    .Select(segment => segment.Job)
                                    .DistinctBy(job => job.Id)
                                    .ToArray(),
                                jobOutputs,
                                results,
                                settings,
                                deletedSources);
                        if (finished &&
                            settings.DeleteFailedSourceAfterRetries &&
                            !SubtitleAiService.IsSubtitleOnly(settings))
                            await DeleteFailedMergedSourcesImmediatelyAsync(
                                plan.Segments
                                    .Select(segment => segment.Job)
                                    .DistinctBy(job => job.Id)
                                    .ToArray(),
                                jobOutputs,
                                results,
                                retryExhaustedOutputs,
                                deletedSources);
                    }
                }, token))
                .ToArray();

            logger.Info(
                $"Bắt đầu ghép {runJobs.Length} video nguồn thành {plans.Length} video đầu ra " +
                (settings.AutoSubtitlesEnabled
                    ? "theo pipeline đệm đôi: ghép 1 video + chuẩn bị AI trước 1 video."
                    : $"với {initialWorkerCount} luồng xử lý (có thể đổi live 1–8 khi đang chạy)."));
            if (settings.IsAutoShuffleStreamCopyActive)
                logger.Info(
                    "⚡ Xào siêu nhanh: dùng resume/marker riêng; đã tắt autosave project theo từng output để tránh worker kẹt sau FFmpeg.");
            await Task.WhenAll(workers);
            var allMergeOutputsSucceeded =
                results.Count == plans.Length && results.Values.All(x => x.Success);
            var autoShuffleTargetComplete =
                IsAutoShuffleTargetComplete(plans, results, settings);
            LastAutoShuffleTargetComplete = autoShuffleTargetComplete;
            if (allMergeOutputsSucceeded)
                logger.Success("Danh sách ghép video đã hoàn tất.");
            else
                logger.Warning("Danh sách ghép video đã kết thúc nhưng còn file bị lỗi.");

            if (settings.AutoShuffleBatchEnabled)
            {
                if (autoShuffleTargetComplete)
                {
                    logger.Success(
                        $"Tự xào đã đủ {settings.AutoShuffleTargetCount:N0}/{settings.AutoShuffleTargetCount:N0} output. " +
                        "Đã đóng trạng thái tiếp tục; lần Render sau sẽ tạo lượt xào mới.");
                    AutoShuffleResumeStore.Clear(settings);
                }
                else
                {
                    logger.Warning(
                        $"Tự xào chưa đủ mục tiêu {settings.AutoShuffleTargetCount:N0}; " +
                        "giữ nguyên video nguồn và trạng thái tiếp tục.");
                }
            }

            if (settings.DeleteSourceAfterSuccess &&
                (!settings.AutoShuffleBatchEnabled || autoShuffleTargetComplete) &&
                !SubtitleAiService.IsSubtitleOnly(settings))
            {
                var remainingSuccessfulSources = runJobs
                    .Where(job =>
                        !deletedSources.ContainsKey(job.Id) &&
                        File.Exists(job.InputPath))
                    .ToArray();
                if (remainingSuccessfulSources.Length > 0)
                {
                    var deleted = await RunSourceCleanupAsync(
                        () => DeleteSuccessfulMergedSources(
                            remainingSuccessfulSources,
                            jobOutputs,
                            results,
                            settings,
                            deletedSources,
                            logPending: true),
                        "kiểm tra nguồn ghép hoàn thành còn lại");
                    LogSourceCleanupResult(deleted, "ghép thành công");
                    await NotifySourcesDeletedAsync(deleted);
                }
            }
            else if (settings.AutoShuffleBatchEnabled &&
                     settings.DeleteSourceAfterSuccess &&
                     !autoShuffleTargetComplete)
            {
                logger.Info(
                    "Chưa xóa video nguồn: chỉ xóa sau khi tự xào đạt đủ toàn bộ số lượng mục tiêu.");
            }

            if (settings.DeleteFailedSourceAfterRetries &&
                !SubtitleAiService.IsSubtitleOnly(settings))
            {
                var deleted = await RunSourceCleanupAsync(
                    () => DeleteFailedMergedSources(
                        runJobs,
                        jobOutputs,
                        results,
                        retryExhaustedOutputs,
                        deletedSources),
                    "xóa video nguồn ghép bị lỗi sau khi đã thử lại");
                LogSourceCleanupResult(deleted, "ghép lỗi sau khi đã thử lại");
                await NotifySourcesDeletedAsync(deleted);
            }

            // Marker chỉ cần trong lúc lượt xào còn dang dở để resume an toàn.
            // Dọn SAU mọi kiểm tra/xử lý nguồn, vì các bước phía trên vẫn dùng
            // marker để xác nhận output stream-copy là đúng và hoàn chỉnh.
            if (settings.IsAutoShuffleStreamCopyActive && autoShuffleTargetComplete)
            {
                var removedMarkers = FFmpegEngine.CleanupCompletedStreamCopyMarkers(
                    plans, settings);
                var removedCache = StreamCopyPreflightCache.CleanupForSources(runJobs);
                var removedTemp = FFmpegEngine.CleanupStreamCopyTempFiles();
                logger.Info(
                    $"Đã dọn metadata tạm của lượt xào hoàn tất • " +
                    $"marker {removedMarkers:N0} • cache preflight {removedCache:N0} • concat tạm {removedTemp:N0}.");
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Danh sách ghép video đã hủy.");
        }
        finally
        {
            await UiAsync(() =>
            {
                foreach (var job in runJobs)
                {
                    if (job.Status == JobStatus.Failed &&
                        job.ProcessingStage == "Lỗi phụ đề")
                        continue;
                    var related = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
                    var relatedResults = related
                        .Select(index => results.TryGetValue(index, out var result) ? result : null)
                        .ToArray();
                    var unused = related.Length == 0;
                    var success = related.Length > 0 &&
                                  relatedResults.All(x => x is { Success: true });
                    var failed = relatedResults.Any(x => x is { Success: false });
                    job.Status = token.IsCancellationRequested
                        ? JobStatus.Cancelled
                        : unused ? JobStatus.Skipped
                        : success ? JobStatus.Succeeded
                        : failed ? JobStatus.Failed
                        : JobStatus.Cancelled;
                    job.Progress = success ? 100 : job.Progress;
                    job.ProcessingStage = job.Status switch
                    {
                        JobStatus.Succeeded => "Ghép hoàn thành",
                        JobStatus.Failed => "Lỗi ghép",
                        JobStatus.Skipped => settings.MergeVideosPerOutput > 0
                            ? "Bỏ qua - chưa đủ nhóm"
                            : "Bỏ qua - không đủ thời lượng",
                        _ => "Đã hủy"
                    };
                    foreach (var part in job.Parts)
                    {
                        part.Status = job.Status switch
                        {
                            JobStatus.Succeeded => PartStatus.Succeeded,
                            JobStatus.Failed => PartStatus.Failed,
                            JobStatus.Skipped => PartStatus.Skipped,
                            _ => PartStatus.Cancelled
                        };
                        if (job.Status == JobStatus.Succeeded) part.Progress = 100;
                    }
                }
            });
            try
            {
                await projectService.SaveAutoAsync(
                    new ProjectState
                    {
                        Settings = settings,
                        Jobs = RemainingJobs(runJobs)
                    });
            }
            catch (Exception ex)
            {
                logger.Warning($"Tự lưu: {ex.Message}");
            }
            IsPaused = false;
            _isRunning = false;
            StateChanged?.Invoke();
        }
    }

    private async Task EnsureExistingSubtitleRegionsAsync(
        IEnumerable<VideoJob> sourceJobs,
        PresetSettings settings,
        string readyStage,
        CancellationToken token)
    {
        if (!settings.ExistingSubtitleBlurEnabled ||
            !settings.AutoDetectExistingSubtitleRegion)
            return;

        var jobs = sourceJobs
            .DistinctBy(job => job.Id)
            .Where(job => File.Exists(job.InputPath))
            .ToArray();
        if (jobs.Length == 0) return;

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Min(
                    2,
                    Math.Max(1, settings.WorkerCount))
            },
            async (job, currentToken) =>
            {
                await UiAsync(() => job.ProcessingStage = "Tự dò vùng sub cũ");
                try
                {
                    var region = await existingSubtitleDetector.EnsureDetectedAsync(
                        job,
                        settings,
                        false,
                        currentToken);
                    await UiAsync(() => job.ProcessingStage = region is null
                        ? "Dùng vùng mờ thủ công"
                        : "Đã nhận vùng sub cũ");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.Warning(
                        $"Tự dò vùng sub cũ {job.FileName}: {ex.Message}. " +
                        "Tiếp tục bằng vùng thủ công.");
                    await UiAsync(() =>
                        job.ProcessingStage = "Dùng vùng mờ thủ công");
                }
                finally
                {
                    await UiAsync(() => job.ProcessingStage = readyStage);
                }
            });
    }

    private async Task EnsureFaceFocusAsync(
        IEnumerable<VideoJob> sourceJobs,
        PresetSettings settings,
        string readyStage,
        CancellationToken token)
    {
        if (!settings.IsFaceCropMainVideo)
            return;

        foreach (var job in sourceJobs
                     .DistinctBy(item => item.Id)
                     .Where(item => File.Exists(item.InputPath)))
        {
            await UiAsync(() => job.ProcessingStage = "Đang dò khuôn mặt");
            await faceFocusDetector.EnsureDetectedAsync(
                job,
                settings,
                false,
                token);
            await UiAsync(() => job.ProcessingStage = readyStage);
        }
    }

    private async Task<bool> ProcessMergedAsync(
        MergeOutputPlan plan,
        IList<VideoJob> allJobs,
        PresetSettings settings,
        int workerId,
        ConcurrentDictionary<int, double> outputProgress,
        IReadOnlyDictionary<Guid, int[]> jobOutputs,
        ConcurrentDictionary<int, RenderResult> results,
        Action notifyQueueProgress,
        CancellationToken token,
        bool allowPreparationDiskFullRecovery = true,
        Action<MergeOutputPlan>? replacePlan = null,
        HashSet<string>? rejectedStreamCopyPlans = null,
        int replacementRound = 0)
    {
        var relatedJobs = plan.Segments
            .Select(x => x.Job)
            .DistinctBy(x => x.Id)
            .ToArray();
        var streamCopy = settings.AutoShuffleBatchEnabled &&
                         settings.AutoShuffleStreamCopyEnabled;
        void MarkPlanStarted()
        {
            foreach (var job in relatedJobs)
            {
                job.Status = IsPaused ? JobStatus.Paused : JobStatus.Rendering;
                job.Worker = $"L{workerId}";
                job.ProcessingStage = streamCopy
                    ? $"⚡ Đang xào file {plan.Index}"
                    : $"Đang ghép file {plan.Index}";
            }
        }
        if (streamCopy)
            UiPost(MarkPlanStarted);
        else
            await UiAsync(MarkPlanStarted);
        if (!streamCopy)
        {
            await EnsureFaceFocusAsync(
                relatedJobs,
                settings,
                $"Đang ghép file {plan.Index}",
                token);
            await EnsureExistingSubtitleRegionsAsync(
                relatedJobs,
                settings,
                $"Đang ghép file {plan.Index}",
                token);
        }
        else
        {
            UiPost(() =>
            {
                foreach (var job in relatedJobs)
                {
                    job.ProcessingStage = $"⚡ Xào siêu nhanh {plan.Index}";
                    job.Gpu = "Stream copy";
                }
            });
        }

        long lastStreamCopyUiProgressAt = 0;
        var progress = new Progress<RenderProgress>(value =>
        {
            outputProgress[plan.Index] = value.Percent;
            notifyQueueProgress();

            if (streamCopy)
            {
                var now = Environment.TickCount64;
                var previous = Volatile.Read(ref lastStreamCopyUiProgressAt);
                if (now - previous < 250 ||
                    Interlocked.CompareExchange(
                        ref lastStreamCopyUiProgressAt,
                        now,
                        previous) != previous)
                    return;
                UiPost(() =>
                {
                    foreach (var job in relatedJobs)
                    {
                        var indexes = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
                        job.Progress = indexes.Length == 0
                            ? 0
                            : indexes.Average(index => outputProgress.GetValueOrDefault(index));
                        job.RenderFps = value.Fps;
                        job.Eta = value.Eta?.ToString(@"hh\:mm\:ss") ?? "—";
                        job.Worker = $"L{value.WorkerId}";
                        job.Gpu = value.Encoder;
                    }
                });
                return;
            }

            _ = UiAsync(() =>
            {
                foreach (var job in relatedJobs)
                {
                    var indexes = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
                    job.Progress = indexes.Length == 0
                        ? 0
                        : indexes.Average(index => outputProgress.GetValueOrDefault(index));
                    job.RenderFps = value.Fps;
                    job.Eta = value.Eta?.ToString(@"hh\:mm\:ss") ?? "—";
                    job.Worker = $"L{value.WorkerId}";
                    job.Gpu = value.Encoder;
                }
            });
        });

        var renderPlan = streamCopy ? plan : CreateNormalEditPlan(plan, settings);
        RenderResult result;
        IReadOnlyList<string?>? subtitlePaths = null;
        string? mergedSrtPath = null;
        string? titleOverlayPath = null;
        var retriesExhausted = false;
        var preparationFailed = false;
        try
        {
            if (!streamCopy && settings.TitleTextEnabled)
            {
                await UiAsync(() =>
                {
                    foreach (var job in relatedJobs)
                        job.ProcessingStage = $"Tạo chữ file ghép {plan.Index}";
                });
                var title = await titleSource.GetTitleAsync(
                    plan.TitleJob,
                    allJobs.IndexOf(plan.TitleJob),
                    settings,
                    token);
                plan.TitleJob.CleanTitle = title;
                if (!string.IsNullOrWhiteSpace(title))
                    titleOverlayPath = await typography.RenderAsync(
                        title, 1, false, settings, token);
            }
            if (!streamCopy && (SubtitleAiService.ShouldBurn(settings) ||
                plan.Segments.Any(segment => segment.Job.HasManualSubtitle)))
            {
                await UiAsync(() =>
                {
                    foreach (var job in relatedJobs)
                        job.ProcessingStage = $"Tạo sub file ghép {plan.Index}";
                });
                var paths = new string?[renderPlan.Segments.Count];
                for (var i = 0; i < renderPlan.Segments.Count; i++)
                {
                    var segment = renderPlan.Segments[i];
                    paths[i] = segment.Job.HasManualSubtitle
                        ? await manualSubtitles.PreparePartAsync(
                            segment.Job,
                            new PartPlan
                            {
                                Index = i + 1,
                                StartSeconds = segment.StartSeconds,
                                DurationSeconds = segment.DurationSeconds
                            },
                            plan.VideoSpeed,
                            token)
                        : SubtitleAiService.ShouldBurn(settings)
                            ? await subtitles.PrepareMergeSegmentAsync(
                                segment, settings, plan.VideoSpeed, token)
                            : null;
                }
                subtitlePaths = paths;
            }
            if (!streamCopy && SubtitleAiService.ShouldExportSrt(settings))
                mergedSrtPath = await subtitles.PrepareMergedSrtAsync(renderPlan, settings, token);

            result = new RenderResult(false, false, "", "Chưa ghép.");
            var diskFullRecoveryAttempted = false;
            var timelineMismatchAttempts = 0;
            var maxRetries = streamCopy
                ? Math.Min(settings.MaxRetries, 1)
                : settings.MaxRetries;
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await _pauseGate.WaitAsync(token);
                await EnsureDiskSpaceThresholdAsync(settings, $"video ghép {plan.Index}", token);
                result = await ffmpeg.RenderMergedAsync(
                    renderPlan,
                    settings,
                    subtitlePaths,
                    titleOverlayPath,
                    workerId,
                    progress,
                    token);
                if (!result.Success &&
                    result.Error.StartsWith(
                        "Timeline stream-copy sai:",
                        StringComparison.OrdinalIgnoreCase))
                    timelineMismatchAttempts++;
                if (result.Success || token.IsCancellationRequested) break;
                if (!diskFullRecoveryAttempted &&
                    await TryRecoverFromDiskFullAsync(
                        result,
                        settings,
                        $"video ghép {plan.Index}",
                        token))
                {
                    diskFullRecoveryAttempted = true;
                    attempt--;
                    continue;
                }
                if (attempt >= maxRetries)
                    retriesExhausted = true;
                if (attempt < maxRetries)
                {
                    logger.Warning(
                        $"Thử lại video ghép {plan.Index} ({attempt + 1}/{maxRetries}).");
                    if (streamCopy)
                    {
                        UiPost(() =>
                        {
                            foreach (var job in relatedJobs)
                                job.ProcessingStage =
                                    $"⚠ Thử lại file {plan.Index} sau timeout/lỗi";
                        });
                        // Tránh 8 worker timeout cùng lúc rồi retry đồng loạt trên HDD.
                        await Task.Delay(Math.Min(1_500, 120 * workerId), token);
                    }
                }
            }
            if (streamCopy &&
                !result.Success &&
                retriesExhausted &&
                timelineMismatchAttempts >= 2)
            {
                rejectedStreamCopyPlans ??= new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                rejectedStreamCopyPlans.Add(
                    MergePlanner.GetAutoShuffleCombinationSignature(plan));

                const int maxReplacementRounds = 12;
                if (replacementRound < maxReplacementRounds)
                {
                    var replacement = mergePlanner.BuildAutoShuffleReplacement(
                        allJobs.ToArray(),
                        settings,
                        plan,
                        replacementRound + 1,
                        rejectedStreamCopyPlans);
                    if (replacement is not null)
                    {
                        logger.Warning(
                            $"Video ghép {plan.Index}: cùng combo lệch timeline 2 lần; " +
                            $"bỏ combo cũ và phối combo mới ({replacementRound + 1}/{maxReplacementRounds}).");
                        outputProgress[plan.Index] = 0;
                        replacePlan?.Invoke(replacement);
                        return await ProcessMergedAsync(
                            replacement,
                            allJobs,
                            settings,
                            workerId,
                            outputProgress,
                            jobOutputs,
                            results,
                            notifyQueueProgress,
                            token,
                            allowPreparationDiskFullRecovery,
                            replacePlan,
                            rejectedStreamCopyPlans,
                            replacementRound + 1);
                    }

                    logger.Warning(
                        $"Video ghép {plan.Index}: không tìm được combo thay thế tương thích sau khi " +
                        "combo hiện tại lệch timeline 2 lần.");
                }
                else
                {
                    logger.Warning(
                        $"Video ghép {plan.Index}: đã thử {maxReplacementRounds} combo thay thế nhưng " +
                        "vẫn gặp lệch timeline; dừng output này để tránh vòng lặp vô hạn.");
                }
            }

            if (result.Success &&
                SubtitleAiService.ShouldExportSrt(settings) &&
                !string.IsNullOrWhiteSpace(mergedSrtPath))
            {
                var outputSrt = Path.ChangeExtension(result.OutputPath, ".srt");
                File.Copy(mergedSrtPath, outputSrt, true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            preparationFailed = true;
            result = new RenderResult(
                false,
                false,
                "",
                "Không chuẩn bị được chữ/phụ đề cho video ghép: " + ex.Message,
                IsDiskFull: DiskSpaceErrorDetector.IsDiskFull(ex));
        }
        if (preparationFailed &&
            allowPreparationDiskFullRecovery &&
            await TryRecoverFromDiskFullAsync(
                result,
                settings,
                $"khâu chuẩn bị video ghép {plan.Index}",
                token))
            return await ProcessMergedAsync(
                plan,
                allJobs,
                settings,
                workerId,
                outputProgress,
                jobOutputs,
                results,
                notifyQueueProgress,
                token,
                allowPreparationDiskFullRecovery: false,
                replacePlan: replacePlan,
                rejectedStreamCopyPlans: rejectedStreamCopyPlans,
                replacementRound: replacementRound);

        results[plan.Index] = result;
        if (result.Success) outputProgress[plan.Index] = 100;

        void PublishPlanResultToList()
        {
            foreach (var job in relatedJobs)
            {
                var indexes = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
                job.Progress = indexes.Length == 0
                    ? 0
                    : indexes.Average(index => outputProgress.GetValueOrDefault(index));
                if (result.Success)
                {
                    job.Error = "";
                    if (streamCopy)
                        job.ProcessingStage = result.Skipped
                            ? $"✓ Đã có file {plan.Index}"
                            : $"✓ Xào xong file {plan.Index}";
                }
                else if (streamCopy)
                {
                    job.Error = result.Error;
                    job.ProcessingStage = token.IsCancellationRequested ||
                                          result.Error == "Đã hủy."
                        ? $"Đã hủy file {plan.Index}"
                        : result.Error.StartsWith("Watchdog 10s", StringComparison.OrdinalIgnoreCase)
                            ? $"⚠ Timeout 10s file {plan.Index}"
                            : $"⚠ Lỗi xào file {plan.Index}";
                    // Một nguồn được tái sử dụng ở rất nhiều output; một output lỗi không
                    // được phép đánh dấu toàn bộ Part của nguồn là Failed.
                }
                else if (!result.Success)
                {
                    job.Error = result.Error;
                    foreach (var part in job.Parts)
                    {
                        part.Status = PartStatus.Failed;
                        part.Error = result.Error;
                    }
                }
            }
        }
        if (streamCopy)
            UiPost(PublishPlanResultToList);
        else
            await UiAsync(PublishPlanResultToList);

        if (result.Success)
            logger.Success(
                $"Video ghép {plan.Index} ({FormatDuration(plan.DurationSeconds)}) → " +
                Path.GetFileName(result.OutputPath));
        else if (token.IsCancellationRequested || result.Error == "Đã hủy.")
            logger.Warning($"Video ghép {plan.Index}: Đã hủy.");
        else
            logger.Error(
                $"Video ghép {plan.Index}: {result.Error}",
                result.TechnicalDetails);

        // Xào siêu nhanh đã có AutoShuffleResumeStore + marker an toàn + cache
        // preflight riêng. Không autosave toàn bộ project sau từng output: trên lượt
        // 1.650 file, SaveAutoAsync có thể giữ worker sau khi FFmpeg đã báo Thành
        // công (đặc biệt khi nhiều worker cùng chạy), khiến thanh tiến độ trông như
        // bị kẹt ở các output cuối. Project vẫn được autosave ở cấp toàn lượt chạy.
        if (streamCopy)
            return retriesExhausted && !result.Success;

        if (!ShouldSaveProgress())
            return retriesExhausted && !result.Success;
        try
        {
            await projectService.SaveAutoAsync(
                new ProjectState
                {
                    Settings = settings,
                    Jobs = RemainingJobs(allJobs)
                },
                token);
        }
        catch (Exception ex)
        {
            logger.Warning($"Tự lưu: {ex.Message}");
        }
        return retriesExhausted && !result.Success;
    }

    private async Task<bool> ProcessAsync(
        WorkItem item,
        IList<VideoJob> allJobs,
        PresetSettings settings,
        int workerId,
        Action notifyQueueProgress,
        CancellationToken token)
    {
        await UiAsync(() =>
        {
            item.Part.Status = PartStatus.Rendering;
            item.Part.Error = "";
            item.Job.Status = IsPaused ? JobStatus.Paused : JobStatus.Rendering;
            item.Job.Worker = $"L{workerId}";
            item.Job.ProcessingStage = SubtitleAiService.IsSubtitleOnly(settings)
                ? "Đang xuất SRT" : "Đang render";
            if (SubtitleAiService.IsSubtitleOnly(settings))
            {
                item.Job.Gpu = "Không encode";
                item.Job.Eta = "—";
            }
        });
        if (!SubtitleAiService.IsSubtitleOnly(settings))
            await EnsureFaceFocusAsync(
                [item.Job],
                settings,
                "Đang render",
                token);
        if (!SubtitleAiService.IsSubtitleOnly(settings))
            await EnsureExistingSubtitleRegionsAsync(
                [item.Job],
                settings,
                "Đang render",
                token);
        var renderPart = CreateNormalEditPart(item.Part, settings);
        string? subtitlePath = null;
        string? sidecarPath = null;
        string? overlay = null;
        if (!SubtitleAiService.IsSubtitleOnly(settings))
        {
            var title = await titleSource.GetTitleAsync(item.Job, item.QueueIndex, settings, token);
            item.Job.CleanTitle = title;
            overlay = await typography.RenderAsync(
                title, item.Part.Index, item.Part.IsSplitPart, settings, token);
            if (item.Job.HasManualSubtitle)
                subtitlePath = await manualSubtitles.PreparePartAsync(
                    item.Job,
                    renderPart,
                    settings.VideoSpeed,
                    token);
            else if (SubtitleAiService.ShouldBurn(settings))
                subtitlePath = await subtitles.PreparePartAsync(
                    item.Job,
                    renderPart,
                    settings,
                    token);
        }
        if (SubtitleAiService.ShouldExportSrt(settings))
            sidecarPath = await subtitles.PreparePartSrtAsync(item.Job, renderPart, settings, token);

        RenderResult result = new(false, false, "", "Chưa render.");
        var retriesExhausted = false;
        if (SubtitleAiService.IsSubtitleOnly(settings))
        {
            result = await ExportSubtitleAsync(item.Job, renderPart, sidecarPath, settings, token);
            if (await TryRecoverFromDiskFullAsync(
                    result,
                    settings,
                    $"phụ đề của {item.Job.FileName} / phần {item.Part.Index}",
                    token))
                result = await ExportSubtitleAsync(
                    item.Job,
                    renderPart,
                    sidecarPath,
                    settings,
                    token);
        }
        else
        {
            var diskFullRecoveryAttempted = false;
            for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
            {
                await _pauseGate.WaitAsync(token);
                await EnsureDiskSpaceThresholdAsync(settings, $"{item.Job.FileName} / phần {item.Part.Index}", token);
                var progress = new Progress<RenderProgress>(value => _ = UiAsync(() =>
                {
                    item.Part.Progress = value.Percent;
                    item.Job.RenderFps = value.Fps;
                    item.Job.Eta = value.Eta?.ToString(@"hh\:mm\:ss") ?? "—";
                    item.Job.Worker = $"L{value.WorkerId}";
                    item.Job.Gpu = value.Encoder;
                    UpdateJob(item.Job);
                    notifyQueueProgress();
                }));
                result = await ffmpeg.RenderPartAsync(item.Job, renderPart, overlay!, subtitlePath, settings, workerId, progress, token);
                if (result.Success || token.IsCancellationRequested) break;
                if (!diskFullRecoveryAttempted &&
                    await TryRecoverFromDiskFullAsync(
                        result,
                        settings,
                        $"{item.Job.FileName} / phần {item.Part.Index}",
                        token))
                {
                    diskFullRecoveryAttempted = true;
                    attempt--;
                    continue;
                }
                if (attempt >= settings.MaxRetries)
                    retriesExhausted = true;
                if (attempt < settings.MaxRetries)
                    logger.Warning($"Thử lại {item.Job.FileName} / phần {item.Part.Index} ({attempt + 1}/{settings.MaxRetries}).");
            }
        }

        if (result.Success && SubtitleAiService.ShouldExportSrt(settings) &&
            !SubtitleAiService.IsSubtitleOnly(settings))
        {
            var subtitleResult = await ExportSubtitleAsync(item.Job, renderPart, sidecarPath, settings, token);
            if (await TryRecoverFromDiskFullAsync(
                    subtitleResult,
                    settings,
                    $"phụ đề của {item.Job.FileName} / phần {item.Part.Index}",
                    token))
                subtitleResult = await ExportSubtitleAsync(
                    item.Job,
                    renderPart,
                    sidecarPath,
                    settings,
                    token);
            if (!subtitleResult.Success) result = subtitleResult;
        }

        await UiAsync(() =>
        {
            item.Part.Error = result.Error;
            item.Part.Status = result.Success
                ? result.Skipped ? PartStatus.Skipped : PartStatus.Succeeded
                : token.IsCancellationRequested ? PartStatus.Cancelled : PartStatus.Failed;
            item.Part.Progress = result.Success ? 100 : item.Part.Progress;
            UpdateJob(item.Job);
            item.Job.ProcessingStage = item.Job.Status switch
            {
                JobStatus.Succeeded => "Hoàn thành",
                JobStatus.Failed => "Lỗi",
                JobStatus.Cancelled => "Đã hủy",
                _ => "Đang render"
            };
        });
        if (result.Success) logger.Success($"{item.Job.FileName} → {Path.GetFileName(result.OutputPath)}");
        else
            logger.Error(
                $"{item.Job.FileName} / phần {item.Part.Index}: {result.Error}",
                result.TechnicalDetails);

        if (!ShouldSaveProgress())
            return retriesExhausted && !result.Success;
        try
        {
            await projectService.SaveAutoAsync(
                new ProjectState
                {
                    Settings = settings,
                    Jobs = RemainingJobs(allJobs)
                },
                token);
        }
        catch (Exception ex) { logger.Warning($"Tự lưu: {ex.Message}"); }
        return retriesExhausted && !result.Success;
    }

    private async Task EnsureDiskSpaceThresholdAsync(
        PresetSettings settings,
        string itemName,
        CancellationToken token)
    {
        if (!settings.EmptyRecycleBinWhenDiskFull ||
            string.IsNullOrWhiteSpace(settings.OutputRoot) ||
            token.IsCancellationRequested)
            return;

        await _diskCleanupGate.WaitAsync(token);
        try
        {
            DiskSpaceSnapshot snapshot;
            try
            {
                snapshot = diskSpace.Inspect(
                    settings.OutputRoot,
                    0,
                    settings.MinimumFreeSpaceGb);
            }
            catch (Exception ex)
            {
                logger.Warning($"Không kiểm tra được dung lượng trước {itemName}: {ex.Message}");
                return;
            }

            var thresholdBytes = (long)(Math.Max(0.5, settings.MinimumFreeSpaceGb) * 1024 * 1024 * 1024);
            if (snapshot.FreeBytes > thresholdBytes)
                return;

            logger.Warning(
                $"Ổ {snapshot.DriveName} còn {DiskSpaceService.FormatBytes(snapshot.FreeBytes)} " +
                $"(ngưỡng {settings.MinimumFreeSpaceGb:0.##} GB); đang dọn Thùng rác trước khi tiếp tục {itemName}.");

            if (settings.AutoCleanPartialFiles)
                await Task.Run(() => diskSpace.CleanupPartialFiles(settings.OutputRoot), token);

            await recycleBin.EmptyAsync(
                $"dung lượng còn dưới ngưỡng {settings.MinimumFreeSpaceGb:0.##} GB khi render {itemName}",
                token);

            try
            {
                snapshot = diskSpace.Inspect(settings.OutputRoot, 0, settings.MinimumFreeSpaceGb);
                logger.Info(
                    $"Sau khi dọn Thùng rác: ổ {snapshot.DriveName} còn " +
                    $"{DiskSpaceService.FormatBytes(snapshot.FreeBytes)}.");
            }
            catch (Exception ex)
            {
                logger.Warning($"Không kiểm tra lại được dung lượng sau khi dọn: {ex.Message}");
            }
        }
        finally
        {
            _diskCleanupGate.Release();
        }
    }

    private async Task<bool> TryRecoverFromDiskFullAsync(
        RenderResult result,
        PresetSettings settings,
        string itemName,
        CancellationToken token)
    {
        if (result.Success ||
            !settings.EmptyRecycleBinWhenDiskFull ||
            !DiskSpaceErrorDetector.IsDiskFull(result) ||
            token.IsCancellationRequested)
            return false;

        logger.Warning(
            $"Phát hiện ổ đĩa đầy khi xử lý {itemName}. " +
            "Ứng dụng sẽ dọn sạch Thùng rác rồi tự chạy lại tác vụ này.");
        var emptied = await recycleBin.EmptyAsync(
            $"ổ đĩa đầy khi xử lý {itemName}",
            token);
        if (emptied)
            logger.Info($"Đã giải phóng Thùng rác; đang chạy lại {itemName}.");
        else
            logger.Warning(
                $"Không dọn được Thùng rác; {itemName} sẽ dùng cơ chế thử lại thông thường.");
        return emptied;
    }

    private Task<bool> TryRecoverFromDiskFullAsync(
        Exception exception,
        PresetSettings settings,
        string itemName,
        CancellationToken token) =>
        TryRecoverFromDiskFullAsync(
            new RenderResult(
                false,
                false,
                "",
                exception.Message,
                IsDiskFull: DiskSpaceErrorDetector.IsDiskFull(exception)),
            settings,
            itemName,
            token);


    private static PartPlan CreateNormalEditPart(
        PartPlan source,
        PresetSettings settings)
    {
        var (start, duration) = settings.TrimOneSecondEdgesEnabled
            ? TrimOneSecondFromEdges(source.StartSeconds, source.DurationSeconds)
            : (source.StartSeconds, source.DurationSeconds);
        return new PartPlan
        {
            Index = source.Index,
            StartSeconds = start,
            DurationSeconds = duration,
            IsSplitPart = source.IsSplitPart,
            Status = source.Status,
            Progress = source.Progress,
            ThumbnailPath = source.ThumbnailPath,
            Error = source.Error
        };
    }

    private static MergeOutputPlan CreateNormalEditPlan(
        MergeOutputPlan source,
        PresetSettings settings)
    {
        var segments = source.Segments
            .Select(segment =>
            {
                var (start, duration) = settings.TrimOneSecondEdgesEnabled
                    ? TrimOneSecondFromEdges(segment.StartSeconds, segment.DurationSeconds)
                    : (segment.StartSeconds, segment.DurationSeconds);
                return new MergeSegment(segment.Job, start, duration);
            })
            .ToArray();
        return new MergeOutputPlan(source.Index, segments, source.TitleJob)
        {
            VideoSpeed = source.VideoSpeed,
            OutputBaseName = source.OutputBaseName
        };
    }

    private static (double Start, double Duration) TrimOneSecondFromEdges(
        double startSeconds,
        double durationSeconds)
    {
        if (durationSeconds <= 2.1)
            return (startSeconds, durationSeconds);
        return (startSeconds + 1d, Math.Max(0.1, durationSeconds - 2d));
    }

    private MergedPreparation PrepareMergedRun(
        IList<VideoJob> jobs,
        PresetSettings settings)
    {
        var plans = mergePlanner.Build(jobs.ToArray(), settings).ToArray();
        FFmpegEngine.AssignUniqueMergeOutputNames(plans, settings);
        var results = new ConcurrentDictionary<int, RenderResult>();
        var outputProgress = new ConcurrentDictionary<int, double>(
            plans.Select(plan =>
                new KeyValuePair<int, double>(plan.Index, 0)));

        // Resume tự xào: vì tên output phụ thuộc seed + index, các file đã hoàn
        // thành ở phiên trước có thể được nhận diện chính xác và bỏ qua hoàn toàn.
        if (settings.AutoShuffleBatchEnabled && settings.SkipExisting)
        {
            foreach (var plan in plans)
            {
                var outputPath = FFmpegEngine.GetMergeOutputPath(plan, settings);
                if (!IsUsableCompletedOutput(outputPath, settings, plan)) continue;
                // Đây là output của đúng seed/kế hoạch đang resume và đã qua
                // kiểm tra tồn tại + kích thước tối thiểu, nên coi là hoàn thành
                // của kế hoạch (không phải SkipExisting mơ hồ) để cho phép xóa
                // nguồn sau khi đạt đủ toàn bộ mục tiêu.
                results[plan.Index] = new RenderResult(true, false, outputPath);
                outputProgress[plan.Index] = 100;
            }
        }

        var outputSets = jobs.ToDictionary(
            job => job.Id,
            _ => new HashSet<int>());
        foreach (var plan in plans)
        {
            foreach (var jobId in plan.Segments
                         .Select(segment => segment.Job.Id)
                         .Distinct())
                if (outputSets.TryGetValue(jobId, out var indexes))
                    indexes.Add(plan.Index);
        }
        var jobOutputs = outputSets.ToDictionary(
            item => item.Key,
            item => item.Value.OrderBy(index => index).ToArray());
        return new MergedPreparation(
            plans,
            results,
            outputProgress,
            jobOutputs);
    }

    private static bool IsAutoShuffleTargetComplete(
        IReadOnlyList<MergeOutputPlan> plans,
        IReadOnlyDictionary<int, RenderResult> results,
        PresetSettings settings)
    {
        if (!settings.AutoShuffleBatchEnabled ||
            plans.Count != settings.AutoShuffleTargetCount ||
            results.Count != plans.Count)
            return false;

        foreach (var plan in plans)
        {
            if (!results.TryGetValue(plan.Index, out var result) ||
                !result.Success ||
                string.IsNullOrWhiteSpace(result.OutputPath) ||
                !IsUsableCompletedOutput(result.OutputPath, settings, plan))
                return false;
        }
        return true;
    }

    private static bool IsUsableCompletedOutput(
        string path,
        PresetSettings settings,
        MergeOutputPlan plan)
    {
        if (settings.AutoShuffleBatchEnabled &&
            settings.AutoShuffleStreamCopyEnabled)
            return FFmpegEngine.IsVerifiedStreamCopyOutput(
                path,
                plan,
                settings);

        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldSaveProgress()
    {
        var now = Environment.TickCount64;
        var due = Volatile.Read(ref _nextProgressSaveAt);
        if (now < due) return false;
        return Interlocked.CompareExchange(
                   ref _nextProgressSaveAt,
                   now + ProgressSaveIntervalMilliseconds,
                   due) == due;
    }

    private static void UpdateJob(VideoJob job)
    {
        job.Progress = job.Parts.Count == 0 ? 0 : job.Parts.Average(x => x.Progress);
        if (job.Parts.All(x => x.Status is PartStatus.Succeeded or PartStatus.Skipped)) job.Status = JobStatus.Succeeded;
        else if (job.Parts.Any(x => x.Status == PartStatus.Rendering)) job.Status = JobStatus.Rendering;
        else if (job.Parts.Any(x => x.Status == PartStatus.Failed)) job.Status = JobStatus.Failed;
    }

    private static bool IsJobFinished(VideoJob job) =>
        job.Parts.Count > 0 &&
        job.Parts.All(part =>
            part.Status is PartStatus.Succeeded or PartStatus.Skipped or
                PartStatus.Failed or PartStatus.Cancelled);

    private static double CalculateNormalQueuePercent(
        IReadOnlyCollection<VideoJob> jobs)
    {
        if (jobs.Count == 0) return 0;
        return jobs.Average(job =>
            IsJobFinished(job)
                ? 100
                : Math.Clamp(job.Progress, 0, 100));
    }

    private static double CalculateMergedQueuePercent(
        IReadOnlyDictionary<int, double> outputProgress,
        int total)
    {
        if (total <= 0) return 0;
        var progress = outputProgress.Values.Sum(value =>
            Math.Clamp(value, 0, 100));
        return progress / total;
    }

    private static double CalculateCombinedQueuePercent(
        int subtitleCompleted,
        int subtitleTotal,
        double renderPercent,
        int renderTotal)
    {
        subtitleTotal = Math.Max(0, subtitleTotal);
        renderTotal = Math.Max(0, renderTotal);
        var totalWork = subtitleTotal + renderTotal;
        if (totalWork <= 0) return 0;

        var completedWork =
            Math.Clamp(subtitleCompleted, 0, subtitleTotal) +
            Math.Clamp(renderPercent, 0, 100) / 100d * renderTotal;
        return Math.Clamp(completedWork * 100d / totalWork, 0, 100);
    }

    private void PublishProgress(
        string stage,
        int completed,
        int total,
        int active,
        int failed,
        double? exactPercent = null)
    {
        var now = Environment.TickCount64;
        var force = exactPercent is null &&
                    (total <= 0 || completed <= 0 || completed >= total);
        if (!force)
        {
            var due = Volatile.Read(ref _nextProgressNotificationAt);
            if (now < due ||
                Interlocked.CompareExchange(
                    ref _nextProgressNotificationAt,
                    now + 250,
                    due) != due)
                return;
        }

        try
        {
            ProgressChanged?.Invoke(
                new RenderQueueProgress(
                    stage,
                    Math.Clamp(completed, 0, Math.Max(0, total)),
                    Math.Max(0, total),
                    Math.Max(0, active),
                    Math.Max(0, failed),
                    exactPercent is { } percent && double.IsFinite(percent)
                        ? Math.Clamp(percent, 0, 100)
                        : total <= 0
                            ? 0
                            : Math.Clamp(completed * 100d / total, 0, 100)));
        }
        catch
        {
            // Hiển thị tiến độ không được phép làm gián đoạn FFmpeg.
        }
    }

    private static async Task<RenderResult> ExportSubtitleAsync(
        VideoJob job,
        PartPlan part,
        string? cachedSubtitlePath,
        PresetSettings settings,
        CancellationToken token)
    {
        var output = FFmpegEngine.GetSubtitleOutputPath(job, part, settings);
        job.OutputFolder = settings.OutputRoot;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (settings.SkipExisting && File.Exists(output))
            return new RenderResult(true, true, output);
        var partial = Path.ChangeExtension(output, ".partial.srt");
        try
        {
            if (cachedSubtitlePath is not null && File.Exists(cachedSubtitlePath))
                File.Copy(cachedSubtitlePath, partial, true);
            else
                await File.WriteAllTextAsync(partial, "", token);
            File.Move(partial, output, true);
            return new RenderResult(true, false, output);
        }
        catch (Exception ex)
        {
            return new RenderResult(
                false,
                false,
                output,
                "Không xuất được SRT: " + ex.Message,
                IsDiskFull: DiskSpaceErrorDetector.IsDiskFull(ex));
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    private static void UiPost(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _ = dispatcher.BeginInvoke(action);
    }

    private static Task UiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"hh\:mm\:ss")
            : span.ToString(@"mm\:ss");
    }

    private async Task<IReadOnlyList<VideoJob>> RunSourceCleanupAsync(
        Func<IReadOnlyList<VideoJob>> cleanup,
        string operation,
        bool logStart = true)
    {
        if (logStart)
            logger.Info(
                $"Hậu xử lý: đang {operation}; chỉ gỡ khỏi danh sách sau khi Windows xác nhận file nguồn đã biến mất.");
        try
        {
            var completion =
                new TaskCompletionSource<IReadOnlyList<VideoJob>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(cleanup());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Ekko source cleanup"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task;
        }
        catch (Exception ex)
        {
            logger.Warning($"Hậu xử lý không hoàn tất ({operation}): {ex.Message}");
            return [];
        }
    }

    private async Task DeleteSuccessfulSourceImmediatelyAsync(
        VideoJob job,
        PresetSettings settings,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        await _sourceCleanupGate.WaitAsync(CancellationToken.None);
        try
        {
            if (deletedSources.ContainsKey(job.Id) ||
                !File.Exists(job.InputPath))
                return;
            var deleted = await RunSourceCleanupAsync(
                () => DeleteSuccessfulSources(
                    [job],
                    settings,
                    deletedSources),
                $"xóa ngay nguồn vừa render xong: {job.FileName}",
                logStart: false);
            await NotifySourcesDeletedAsync(deleted);
        }
        finally
        {
            _sourceCleanupGate.Release();
        }
    }

    private async Task DeleteSuccessfulMergedSourcesImmediatelyAsync(
        IReadOnlyList<VideoJob> jobs,
        IReadOnlyDictionary<Guid, int[]> jobOutputs,
        IReadOnlyDictionary<int, RenderResult> results,
        PresetSettings settings,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        await _sourceCleanupGate.WaitAsync(CancellationToken.None);
        try
        {
            var candidates = jobs
                .Where(job =>
                    !deletedSources.ContainsKey(job.Id) &&
                    File.Exists(job.InputPath))
                .ToArray();
            if (candidates.Length == 0) return;
            var deleted = await RunSourceCleanupAsync(
                () => DeleteSuccessfulMergedSources(
                    candidates,
                    jobOutputs,
                    results,
                    settings,
                    deletedSources,
                    logPending: false),
                "xóa ngay nguồn vừa ghép xong",
                logStart: false);
            await NotifySourcesDeletedAsync(deleted);
        }
        finally
        {
            _sourceCleanupGate.Release();
        }
    }

    private async Task DeleteFailedSourceImmediatelyAsync(
        VideoJob job,
        IReadOnlyDictionary<Guid, byte> retryExhaustedJobs,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        await _sourceCleanupGate.WaitAsync(CancellationToken.None);
        try
        {
            if (deletedSources.ContainsKey(job.Id)) return;
            var deleted = await RunSourceCleanupAsync(
                () => DeleteFailedSources(
                    [job],
                    retryExhaustedJobs,
                    deletedSources),
                $"xóa ngay nguồn render lỗi đã hết lượt thử: {job.FileName}",
                logStart: false);
            await NotifySourcesDeletedAsync(deleted);
        }
        finally
        {
            _sourceCleanupGate.Release();
        }
    }

    private async Task DeleteFailedMergedSourcesImmediatelyAsync(
        IReadOnlyList<VideoJob> jobs,
        IReadOnlyDictionary<Guid, int[]> jobOutputs,
        IReadOnlyDictionary<int, RenderResult> results,
        IReadOnlyDictionary<int, byte> retryExhaustedOutputs,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        await _sourceCleanupGate.WaitAsync(CancellationToken.None);
        try
        {
            var candidates = jobs
                .Where(job => !deletedSources.ContainsKey(job.Id))
                .ToArray();
            if (candidates.Length == 0) return;
            var deleted = await RunSourceCleanupAsync(
                () => DeleteFailedMergedSources(
                    candidates,
                    jobOutputs,
                    results,
                    retryExhaustedOutputs,
                    deletedSources),
                "xóa ngay nguồn ghép lỗi đã hết lượt thử",
                logStart: false);
            await NotifySourcesDeletedAsync(deleted);
        }
        finally
        {
            _sourceCleanupGate.Release();
        }
    }

    private void LogSourceCleanupResult(
        IReadOnlyCollection<VideoJob> deleted,
        string reason)
    {
        if (deleted.Count > 0)
        {
            logger.Success(
                $"Hậu xử lý xong: đã đưa {deleted.Count:N0} video nguồn vào Thùng rác " +
                $"({reason}) và cập nhật danh sách.");
            return;
        }

        logger.Info(
            $"Hậu xử lý xong: không có video nguồn nào đủ điều kiện xóa ({reason}).");
    }

    private IReadOnlyList<VideoJob> DeleteSuccessfulSources(
        IEnumerable<VideoJob> jobs,
        PresetSettings settings,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        var deleted = new List<VideoJob>();
        foreach (var job in jobs)
        {
            if (job.Parts.Count == 0 ||
                job.Parts.Any(part =>
                    part.Status != PartStatus.Succeeded &&
                    !(part.Status == PartStatus.Skipped &&
                      settings.VerifyOutputAfterRender)))
            {
                if (job.Parts.Any(x => x.Status == PartStatus.Skipped))
                    logger.Warning(
                        settings.VerifyOutputAfterRender
                            ? $"Không xóa video gốc vì vẫn còn phần chưa hoàn thành: {job.FileName}"
                            : $"Không xóa video gốc vì đầu ra đã tồn tại nhưng chức năng kiểm tra đầu ra đang tắt: {job.FileName}");
                continue;
            }
            if (job.Parts.Any(part => !File.Exists(FFmpegEngine.GetOutputPath(job, part, settings))))
            {
                logger.Warning($"Không xóa video gốc vì chưa đủ file xuất: {job.FileName}");
                continue;
            }
            if (!deletedSources.TryAdd(job.Id, 0)) continue;
            if (MoveSourceToRecycleBin(job, "render thành công"))
                deleted.Add(job);
            else
                deletedSources.TryRemove(job.Id, out _);
        }
        return deleted;
    }

    private IReadOnlyList<VideoJob> DeleteSuccessfulMergedSources(
        IEnumerable<VideoJob> jobs,
        IReadOnlyDictionary<Guid, int[]> jobOutputs,
        IReadOnlyDictionary<int, RenderResult> results,
        PresetSettings settings,
        ConcurrentDictionary<Guid, byte> deletedSources,
        bool logPending)
    {
        var deleted = new List<VideoJob>();
        foreach (var job in jobs)
        {
            var requiredOutputs = jobOutputs.GetValueOrDefault(job.Id, Array.Empty<int>());
            if (requiredOutputs.Length == 0) continue;

            var completedOutputs = requiredOutputs
                .Select(index => results.TryGetValue(index, out var result) ? result : null)
                .ToArray();
            if (completedOutputs.Any(result => result is not { Success: true }))
            {
                if (logPending)
                    logger.Warning(
                        $"Giữ lại video nguồn vì file ghép liên quan chưa thành công: {job.FileName}");
                continue;
            }
            if (!settings.VerifyOutputAfterRender &&
                !settings.IsAutoShuffleStreamCopyActive &&
                completedOutputs.Any(result => result!.Skipped))
            {
                if (logPending)
                    logger.Warning(
                        $"Giữ lại video nguồn vì đầu ra ghép đã tồn tại nhưng chức năng kiểm tra đầu ra đang tắt: {job.FileName}");
                continue;
            }
            if (completedOutputs.Any(result =>
                    string.IsNullOrWhiteSpace(result!.OutputPath) ||
                    !File.Exists(result.OutputPath)))
            {
                if (logPending)
                    logger.Warning(
                        $"Giữ lại video nguồn vì chưa tìm thấy đủ file ghép: {job.FileName}");
                continue;
            }
            if (!deletedSources.TryAdd(job.Id, 0)) continue;
            if (MoveSourceToRecycleBin(job, "ghép thành công"))
                deleted.Add(job);
            else
                deletedSources.TryRemove(job.Id, out _);
        }
        return deleted;
    }

    private IReadOnlyList<VideoJob> DeleteFailedSources(
        IEnumerable<VideoJob> jobs,
        IReadOnlyDictionary<Guid, byte> retryExhaustedJobs,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        var deleted = new List<VideoJob>();
        foreach (var job in jobs)
        {
            if (job.Status != JobStatus.Failed ||
                !IsJobFinished(job) ||
                !retryExhaustedJobs.ContainsKey(job.Id))
                continue;
            if (!deletedSources.TryAdd(job.Id, 0)) continue;
            if (MoveSourceToRecycleBin(
                    job,
                    "render lỗi sau khi đã thử lại hết số lần"))
                deleted.Add(job);
            else
                deletedSources.TryRemove(job.Id, out _);
        }
        return deleted;
    }

    private IReadOnlyList<VideoJob> DeleteFailedMergedSources(
        IEnumerable<VideoJob> jobs,
        IReadOnlyDictionary<Guid, int[]> jobOutputs,
        IReadOnlyDictionary<int, RenderResult> results,
        IReadOnlyDictionary<int, byte> retryExhaustedOutputs,
        ConcurrentDictionary<Guid, byte> deletedSources)
    {
        var deleted = new List<VideoJob>();
        foreach (var job in jobs)
        {
            var requiredOutputs = jobOutputs.GetValueOrDefault(
                job.Id,
                Array.Empty<int>());
            if (requiredOutputs.Length == 0 ||
                requiredOutputs.Any(index => !results.ContainsKey(index)) ||
                !requiredOutputs.Any(retryExhaustedOutputs.ContainsKey))
                continue;

            if (!deletedSources.TryAdd(job.Id, 0)) continue;
            if (MoveSourceToRecycleBin(
                    job,
                    "ghép lỗi sau khi đã thử lại hết số lần"))
                deleted.Add(job);
            else
                deletedSources.TryRemove(job.Id, out _);
        }
        return deleted;
    }

    private bool MoveSourceToRecycleBin(VideoJob job, string reason)
    {
        if (!File.Exists(job.InputPath))
        {
            logger.Info(
                $"Nguồn không còn trên ổ đĩa; gỡ khỏi danh sách ({reason}): {job.FileName}");
            TryMoveEmptySourceFolderToRecycleBin(job.InputPath);
            return true;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    job.InputPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                if (!File.Exists(job.InputPath))
                {
                    logger.Success(
                        $"Đã đưa nguồn vào Thùng rác ({reason}) và gỡ khỏi danh sách: {job.FileName}");
                    TryMoveEmptySourceFolderToRecycleBin(job.InputPath);
                    return true;
                }

                lastError = new IOException(
                    "Windows chưa xóa file sau khi lệnh đưa vào Thùng rác hoàn tất.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < 3)
                Thread.Sleep(attempt * 250);
        }

        logger.Warning(
            $"Không thể đưa video gốc vào Thùng rác sau 3 lần thử ({job.FileName}): " +
            (lastError?.Message ?? "Không rõ nguyên nhân."));
        return false;
    }

    private void TryMoveEmptySourceFolderToRecycleBin(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(directory),
                Path.TrimEndingDirectorySeparator(
                    Path.GetPathRoot(directory) ?? ""),
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                directory,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            logger.Info(
                $"Đã đưa thư mục nguồn trống vào Thùng rác: " +
                $"{Path.GetFileName(directory)}");
        }
        catch (DirectoryNotFoundException)
        {
            // Luồng khác vừa xóa cùng thư mục trống.
        }
        catch (IOException)
        {
            // Thư mục vừa có thêm dữ liệu hoặc đang được tiến trình khác sử dụng.
        }
        catch (Exception ex)
        {
            logger.Warning(
                $"Không thể xóa thư mục nguồn trống ({directory}): {ex.Message}");
        }
    }

    private Task NotifySourcesDeletedAsync(IReadOnlyList<VideoJob> jobs)
    {
        if (jobs.Count == 0) return Task.CompletedTask;
        return UiAsync(() =>
        {
            try { SourcesDeleted?.Invoke(jobs); }
            catch (Exception ex)
            {
                logger.Warning(
                    $"Không cập nhật được bảng sau khi xóa nguồn: {ex.Message}");
            }
        });
    }

    private static List<VideoJob> RemainingJobs(IEnumerable<VideoJob> jobs) =>
        jobs.Where(job => File.Exists(job.InputPath)).ToList();

    public void Dispose()
    {
        Cancel();
        _diskCleanupGate.Dispose();
        _runSource?.Dispose();
    }

    private sealed record WorkItem(VideoJob Job, PartPlan Part, int QueueIndex);
    private sealed record MergedPreparation(
        MergeOutputPlan[] Plans,
        ConcurrentDictionary<int, RenderResult> Results,
        ConcurrentDictionary<int, double> OutputProgress,
        IReadOnlyDictionary<Guid, int[]> JobOutputs);
}
