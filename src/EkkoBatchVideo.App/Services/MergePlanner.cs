using EkkoBatchVideo.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EkkoBatchVideo.Services;

public sealed class MergePlanner
{
    private const double DurationTolerance = 0.05;
    private const double RandomOverflowAllowanceSeconds = 20.0;
    private static readonly HashSet<string> GenericTitleWords = new(
        [
            "video", "vid", "clip", "part", "copy", "edit", "edited", "download",
            "tiktok", "reel", "reels", "short", "shorts", "img", "image", "untitled",
            "the", "a", "an", "of", "to", "and", "or", "in", "on", "at", "for", "is", "are",
            "cái", "con", "một", "những", "các", "của", "và", "là", "ở", "cho", "với"
        ],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MergeOutputPlan> Build(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings)
    {
        return settings.AutoShuffleBatchEnabled
            ? BuildAutoShuffleBatch(jobs, settings)
            : BuildStandard(jobs, settings);
    }

    private IReadOnlyList<MergeOutputPlan> BuildAutoShuffleBatch(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings)
    {
        var target = Math.Clamp(settings.AutoShuffleTargetCount, 1, 100000);
        var videosPerOutput = Math.Clamp(settings.MergeVideosPerOutput, 0, 1000);

        // Lượt đầu phải đi hết nguồn trước khi bắt đầu xào lại.
        // Với 1 clip/output: output 1..N chính là N nguồn.
        // Với nhiều clip/output: clip ĐẦU của output 1..N lần lượt là N nguồn;
        // các clip còn lại mới được chọn ngẫu nhiên/cân bằng. Với Random,
        // thứ tự các nguồn ở clip đầu cũng được xáo nhưng không lặp trong vòng đầu.
        if (videosPerOutput == 1)
            return BuildSingleClipAutoShuffleBatch(jobs, settings, target);

        var plans = new List<MergeOutputPlan>(target);
        var uniqueSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbackSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallback = new List<MergeOutputPlan>();
        var sourceJobs = jobs
            .Where(job => job.Media.DurationSeconds > 0.05)
            .ToArray();
        if (sourceJobs.Length == 0) return [];

        var inputOrder = sourceJobs
            .Select((job, index) => (job.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var videoSpeed = Math.Clamp(settings.VideoSpeed, 0.1, 10);

        // Stream-copy chỉ được phép ghép các nguồn có cùng chữ ký stream.
        // GroupBy giữ nguyên thứ tự nguồn bên trong từng pool. Điều này giúp
        // tùy chọn "Theo thứ tự danh sách" vẫn có ý nghĩa ở thứ tự clip.
        var compatibilityReady = sourceJobs.All(job =>
            !string.IsNullOrWhiteSpace(job.StreamCopyCompatibilityKey));
        var sourcePools = settings.AutoShuffleStreamCopyEnabled && compatibilityReady
            ? sourceJobs
                .GroupBy(job => job.StreamCopyCompatibilityKey, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.ToArray())
                .ToArray()
            : [sourceJobs];
        if (sourcePools.Length == 0) return [];

        // VÒNG ĐẦU:
        // mỗi nguồn hợp lệ phải được neo ít nhất một lần trước khi planner bước
        // sang các combo xào tự do. Với ghép theo số video, nguồn neo là clip đầu.
        // Với ghép theo thời gian, nguồn neo được ghép thêm companion đủ thời lượng.
        var firstCycle = videosPerOutput > 1
            ? BuildAnchoredAutoShuffleFirstCycle(
                sourceJobs,
                sourcePools,
                settings,
                videosPerOutput,
                target,
                inputOrder,
                videoSpeed)
            : BuildAnchoredTimeAutoShuffleFirstCycle(
                sourceJobs,
                sourcePools,
                settings,
                target,
                inputOrder,
                videoSpeed);
        foreach (var anchored in firstCycle)
        {
            var signature = GetAutoShuffleMembershipSignature(anchored);
            uniqueSignatures.Add(signature);
            if (fallbackSignatures.Add(signature))
                fallback.Add(anchored);
            plans.Add(CloneWithIndex(anchored, plans.Count + 1));
            if (plans.Count >= target) return plans;
        }

        // Sau vòng đầu, thành viên của mỗi combo được chọn ngẫu nhiên. Sau khi
        // chọn xong, nếu người dùng chọn thứ tự danh sách / tên file / clip ngắn
        // trước thì chỉ sắp lại THỨ TỰ CLIP bên trong output.
        var maxRounds = Math.Max(64, Math.Min(100000, target * 4));
        for (var round = 0; round < maxRounds && plans.Count < target; round++)
        {
            var roundCandidates = new List<MergeOutputPlan>();
            for (var poolIndex = 0; poolIndex < sourcePools.Length; poolIndex++)
            {
                var pool = sourcePools[poolIndex];
                var pass = new PresetSettings
                {
                    MergeMinSeconds = settings.MergeMinSeconds,
                    MergeMaxSeconds = settings.MergeMaxSeconds,
                    MergeVideosPerOutput = settings.MergeVideosPerOutput,
                    MergeRandomSeed = DeriveSeed(
                        settings.MergeRandomSeed,
                        round,
                        0x1650 + poolIndex * 97),
                    MergeOrder = MergeOrderMode.Random,
                    VideoSpeed = settings.VideoSpeed
                };
                var batch = BuildStandard(pool, pass);
                foreach (var candidate in batch)
                    roundCandidates.Add(NormalizeAutoShuffleClipOrder(
                        candidate,
                        settings.MergeOrder,
                        inputOrder,
                        videoSpeed));
            }

            if (roundCandidates.Count == 0) break;
            var roundRandom = new Random(
                DeriveSeed(settings.MergeRandomSeed, round, 0x62));
            Shuffle(roundCandidates, roundRandom);
            foreach (var candidate in roundCandidates)
            {
                // AB và BA được xem là cùng một combo để giảm cảm giác lặp lại.
                var signature = GetAutoShuffleMembershipSignature(candidate);
                if (fallbackSignatures.Add(signature))
                    fallback.Add(candidate);
                if (!uniqueSignatures.Add(signature)) continue;
                plans.Add(CloneWithIndex(candidate, plans.Count + 1));
                if (plans.Count >= target) break;
            }
        }

        // Nếu số combo duy nhất ít hơn mục tiêu, lặp theo VÒNG thay vì random
        // có hoàn lại. Mỗi combo trong pool fallback được dùng một lần trước khi
        // bước sang vòng mới, nhờ vậy phân bố đều và ít gặp hai combo giống nhau
        // ở sát nhau hơn.
        if (plans.Count < target && fallback.Count > 0)
        {
            var random = new Random(DeriveSeed(settings.MergeRandomSeed, target, 0x49));
            string? previousSignature = plans.Count == 0
                ? null
                : GetAutoShuffleMembershipSignature(plans[^1]);

            while (plans.Count < target)
            {
                var cycle = fallback.ToList();
                Shuffle(cycle, random);
                AvoidSameCycleBoundary(cycle, previousSignature);

                foreach (var source in cycle)
                {
                    var segments = source.Segments.ToArray();
                    if (settings.MergeOrder == MergeOrderMode.Random && segments.Length > 1)
                        Shuffle(segments, random);
                    var repeated = new MergeOutputPlan(
                        plans.Count + 1,
                        segments,
                        source.TitleJob)
                    {
                        VideoSpeed = source.VideoSpeed
                    };
                    plans.Add(repeated);
                    previousSignature = GetAutoShuffleMembershipSignature(repeated);
                    if (plans.Count >= target) break;
                }
            }
        }

        return plans;
    }

    private static IReadOnlyList<MergeOutputPlan> BuildAnchoredTimeAutoShuffleFirstCycle(
        IReadOnlyList<VideoJob> sources,
        IReadOnlyList<VideoJob[]> sourcePools,
        PresetSettings settings,
        int target,
        IReadOnlyDictionary<Guid, int> inputOrder,
        double videoSpeed)
    {
        if (sources.Count == 0 || target <= 0) return [];

        var minimum = Math.Max(1, Math.Min(settings.MergeMinSeconds, settings.MergeMaxSeconds));
        var maximum = Math.Max(minimum, Math.Max(settings.MergeMinSeconds, settings.MergeMaxSeconds));
        var overflowMaximum = maximum + RandomOverflowAllowanceSeconds;
        var anchorOrder = settings.MergeOrder switch
        {
            MergeOrderMode.FileName => sources
                .OrderBy(job => job.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList(),
            MergeOrderMode.ShortestFirst => sources
                .OrderBy(job => OutputDuration(job, videoSpeed))
                .ThenBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList(),
            MergeOrderMode.Random => sources.ToList(),
            _ => sources
                .OrderBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList()
        };

        if (settings.MergeOrder == MergeOrderMode.Random)
        {
            var anchorRandom = new Random(
                DeriveSeed(settings.MergeRandomSeed, 0, 0x7301));
            Shuffle(anchorOrder, anchorRandom);
        }

        var poolByJobId = new Dictionary<Guid, VideoJob[]>();
        foreach (var pool in sourcePools)
        foreach (var job in pool)
            poolByJobId[job.Id] = pool;

        var usage = sources.ToDictionary(job => job.Id, _ => 0);
        var usedMemberships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MergeOutputPlan>(Math.Min(target, sources.Count));

        for (var anchorIndex = 0;
             anchorIndex < anchorOrder.Count && result.Count < target;
             anchorIndex++)
        {
            var anchor = anchorOrder[anchorIndex];
            var anchorDuration = OutputDuration(anchor, videoSpeed);
            if (anchorDuration > overflowMaximum + DurationTolerance)
                continue;
            if (!poolByJobId.TryGetValue(anchor.Id, out var pool))
                continue;

            var random = new Random(
                DeriveSeed(settings.MergeRandomSeed, anchorIndex, 0x7302));
            var members = new List<VideoJob> { anchor };
            var total = anchorDuration;
            var limit = maximum;

            if (total < minimum - DurationTolerance)
            {
                var companions = pool
                    .Where(job => job.Id != anchor.Id)
                    .Select(job => new
                    {
                        Job = job,
                        Duration = OutputDuration(job, videoSpeed),
                        Usage = usage.GetValueOrDefault(job.Id),
                        InputIndex = inputOrder.GetValueOrDefault(job.Id, int.MaxValue),
                        Noise = random.Next()
                    })
                    .Where(item => item.Duration <= overflowMaximum + DurationTolerance)
                    .ToList();

                while (total < minimum - DurationTolerance)
                {
                    var selected = companions
                        .Where(item =>
                            !members.Any(member => member.Id == item.Job.Id) &&
                            total + item.Duration <= limit + DurationTolerance)
                        .OrderBy(item => item.Usage)
                        .ThenBy(item => settings.MergeOrder switch
                        {
                            MergeOrderMode.ShortestFirst => item.Duration,
                            MergeOrderMode.FileName => 0,
                            MergeOrderMode.Random => item.Noise,
                            _ => item.InputIndex
                        })
                        .ThenBy(item => settings.MergeOrder == MergeOrderMode.FileName
                            ? item.Job.FileName
                            : "", StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(item => item.Noise)
                        .FirstOrDefault();

                    if (selected is null && Math.Abs(limit - maximum) < 0.001)
                    {
                        limit = overflowMaximum;
                        continue;
                    }
                    if (selected is null) break;

                    members.Add(selected.Job);
                    total += selected.Duration;
                }
            }

            if (total < minimum - DurationTolerance ||
                total > overflowMaximum + DurationTolerance)
                continue;

            var orderedMembers = OrderAnchoredMembers(
                members,
                anchor,
                settings.MergeOrder,
                inputOrder,
                videoSpeed,
                random);
            var segments = orderedMembers
                .Select(job => new MergeSegment(job, 0, job.Media.DurationSeconds))
                .ToArray();
            var plan = new MergeOutputPlan(
                result.Count + 1,
                segments,
                SelectMeaningfulTitleJob(orderedMembers))
            {
                VideoSpeed = videoSpeed
            };
            var signature = GetAutoShuffleMembershipSignature(plan);
            if (!usedMemberships.Add(signature))
                continue;
            foreach (var member in orderedMembers)
                usage[member.Id] = usage.GetValueOrDefault(member.Id) + 1;
            result.Add(plan);
        }

        return result;
    }

    private static IReadOnlyList<VideoJob> OrderAnchoredMembers(
        IReadOnlyList<VideoJob> members,
        VideoJob anchor,
        MergeOrderMode mergeOrder,
        IReadOnlyDictionary<Guid, int> inputOrder,
        double videoSpeed,
        Random random)
    {
        if (members.Count <= 1) return members;
        if (mergeOrder == MergeOrderMode.Random)
        {
            var shuffled = members.ToList();
            Shuffle(shuffled, random);
            return shuffled;
        }

        return members
            .OrderBy(job => mergeOrder switch
            {
                MergeOrderMode.ShortestFirst => OutputDuration(job, videoSpeed),
                _ => inputOrder.GetValueOrDefault(job.Id, int.MaxValue)
            })
            .ThenBy(job => mergeOrder == MergeOrderMode.FileName
                ? job.FileName
                : "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(job => job.Id == anchor.Id ? 0 : 1)
            .ToArray();
    }

    private static IReadOnlyList<MergeOutputPlan> BuildAnchoredAutoShuffleFirstCycle(
        IReadOnlyList<VideoJob> sources,
        IReadOnlyList<VideoJob[]> sourcePools,
        PresetSettings settings,
        int videosPerOutput,
        int target,
        IReadOnlyDictionary<Guid, int> inputOrder,
        double videoSpeed)
    {
        if (sources.Count == 0 || videosPerOutput <= 1 || target <= 0) return [];

        var anchorOrder = settings.MergeOrder switch
        {
            MergeOrderMode.FileName => sources
                .OrderBy(job => job.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList(),
            MergeOrderMode.ShortestFirst => sources
                .OrderBy(job => OutputDuration(job, videoSpeed))
                .ThenBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList(),
            // Ghép ngẫu nhiên: vòng đầu xáo thứ tự anchor nhưng KHÔNG lặp.
            // Mỗi nguồn được đứng vị trí clip đầu đúng một lần trước khi nguồn nào
            // được phép quay lại vị trí này ở các vòng sau.
            MergeOrderMode.Random => sources.ToList(),
            _ => sources
                .OrderBy(job => inputOrder.GetValueOrDefault(job.Id, int.MaxValue))
                .ToList()
        };

        if (settings.MergeOrder == MergeOrderMode.Random)
        {
            var anchorRandom = new Random(
                DeriveSeed(settings.MergeRandomSeed, 0, 0x7201));
            Shuffle(anchorOrder, anchorRandom);
        }

        var poolByJobId = new Dictionary<Guid, VideoJob[]>();
        foreach (var pool in sourcePools)
        foreach (var job in pool)
            poolByJobId[job.Id] = pool;

        // Đếm tổng số lần đã xuất hiện ở vòng đầu để companion được phân bố đều.
        var usage = sources.ToDictionary(job => job.Id, _ => 0);
        var usedMemberships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MergeOutputPlan>(Math.Min(target, sources.Count));

        for (var anchorIndex = 0;
             anchorIndex < anchorOrder.Count && result.Count < target;
             anchorIndex++)
        {
            var anchor = anchorOrder[anchorIndex];
            if (!poolByJobId.TryGetValue(anchor.Id, out var pool) ||
                pool.Length < videosPerOutput)
                continue;

            var candidates = pool
                .Where(job => job.Id != anchor.Id)
                .ToArray();
            if (candidates.Length < videosPerOutput - 1) continue;

            var random = new Random(
                DeriveSeed(settings.MergeRandomSeed, anchorIndex, 0x7202));
            MergeOutputPlan? selectedPlan = null;
            VideoJob[]? selectedMembers = null;

            // Thử nhiều cách chọn companion để tránh lặp membership trong vòng đầu.
            // Nếu không thể tránh (ví dụ chỉ có đúng 3 nguồn cho 3 clip/output),
            // vẫn giữ anchor để đảm bảo mỗi nguồn được làm clip đầu một lần.
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var companions = candidates
                    .Select(job => new
                    {
                        Job = job,
                        Usage = usage.GetValueOrDefault(job.Id),
                        Noise = random.Next()
                    })
                    .OrderBy(item => item.Usage)
                    .ThenBy(item => item.Noise)
                    .Take(videosPerOutput - 1)
                    .Select(item => item.Job)
                    .ToList();

                if (settings.MergeOrder == MergeOrderMode.Random)
                {
                    Shuffle(companions, random);
                }
                else
                {
                    companions.Sort((left, right) => settings.MergeOrder switch
                    {
                        MergeOrderMode.ShortestFirst =>
                            OutputDuration(left, videoSpeed).CompareTo(
                                OutputDuration(right, videoSpeed)),
                        MergeOrderMode.FileName => string.Compare(
                            left.FileName,
                            right.FileName,
                            StringComparison.CurrentCultureIgnoreCase),
                        _ => inputOrder.GetValueOrDefault(left.Id, int.MaxValue)
                            .CompareTo(inputOrder.GetValueOrDefault(right.Id, int.MaxValue))
                    });
                }

                // Anchor luôn đứng đầu. Đây là điểm khác với việc chỉ sort combo:
                // output #K thực sự bắt đầu bằng nguồn #K ở vòng đầu.
                var members = new[] { anchor }
                    .Concat(companions)
                    .ToArray();
                var segments = members
                    .Select(job => new MergeSegment(job, 0, job.Media.DurationSeconds))
                    .ToArray();
                var candidatePlan = new MergeOutputPlan(
                    result.Count + 1,
                    segments,
                    SelectMeaningfulTitleJob(members))
                {
                    VideoSpeed = videoSpeed
                };

                selectedPlan = candidatePlan;
                selectedMembers = members;
                var membership = GetAutoShuffleMembershipSignature(candidatePlan);
                if (!usedMemberships.Contains(membership)) break;
            }

            if (selectedPlan is null || selectedMembers is null) continue;
            usedMemberships.Add(GetAutoShuffleMembershipSignature(selectedPlan));
            foreach (var member in selectedMembers)
                usage[member.Id] = usage.GetValueOrDefault(member.Id) + 1;
            result.Add(selectedPlan);
        }

        return result;
    }

    private IReadOnlyList<MergeOutputPlan> BuildSingleClipAutoShuffleBatch(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings,
        int target)
    {
        var sources = jobs
            .Where(job => job.Media.DurationSeconds > 0.05)
            .ToArray();
        if (sources.Length == 0) return [];

        var videoSpeed = Math.Clamp(settings.VideoSpeed, 0.1, 10);
        var inputOrder = sources
            .Select((job, index) => (job.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var firstCycle = settings.MergeOrder switch
        {
            MergeOrderMode.FileName => sources
                .OrderBy(job => job.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(job => inputOrder[job.Id])
                .ToList(),
            MergeOrderMode.ShortestFirst => sources
                .OrderBy(job => OutputDuration(job, videoSpeed))
                .ThenBy(job => inputOrder[job.Id])
                .ToList(),
            // 1 clip/output + Ghép ngẫu nhiên: vòng đầu xáo thứ tự nguồn nhưng
            // vẫn đi hết từng nguồn đúng một lần trước khi lặp.
            MergeOrderMode.Random => sources.ToList(),
            _ => sources.OrderBy(job => inputOrder[job.Id]).ToList()
        };

        if (settings.MergeOrder == MergeOrderMode.Random)
        {
            var firstRandom = new Random(
                DeriveSeed(settings.MergeRandomSeed, 0, 0x7101));
            Shuffle(firstCycle, firstRandom);
        }

        var plans = new List<MergeOutputPlan>(target);
        void Add(VideoJob job)
        {
            plans.Add(new MergeOutputPlan(
                plans.Count + 1,
                [new MergeSegment(job, 0, job.Media.DurationSeconds)],
                job)
            {
                VideoSpeed = videoSpeed
            });
        }

        // Lượt đầu: mỗi nguồn đúng một lần, theo lựa chọn thứ tự của người dùng.
        foreach (var source in firstCycle)
        {
            Add(source);
            if (plans.Count >= target) return plans;
        }

        // Từ lượt thứ hai: xào theo vòng, không chọn có hoàn lại.
        var random = new Random(DeriveSeed(settings.MergeRandomSeed, target, 0x7102));
        var previousId = plans[^1].Segments[0].Job.Id;
        while (plans.Count < target)
        {
            var cycle = sources.ToList();
            Shuffle(cycle, random);
            if (cycle.Count > 1 && cycle[0].Id == previousId)
            {
                var swapIndex = cycle.FindIndex(1, job => job.Id != previousId);
                if (swapIndex > 0)
                    (cycle[0], cycle[swapIndex]) = (cycle[swapIndex], cycle[0]);
            }

            foreach (var source in cycle)
            {
                Add(source);
                previousId = source.Id;
                if (plans.Count >= target) break;
            }
        }

        return plans;
    }

    private static MergeOutputPlan NormalizeAutoShuffleClipOrder(
        MergeOutputPlan plan,
        MergeOrderMode mergeOrder,
        IReadOnlyDictionary<Guid, int> inputOrder,
        double videoSpeed)
    {
        if (mergeOrder == MergeOrderMode.Random || plan.Segments.Count <= 1)
            return plan;

        IEnumerable<MergeSegment> ordered = mergeOrder switch
        {
            MergeOrderMode.ShortestFirst => plan.Segments
                .OrderBy(segment => OutputDuration(segment.Job, videoSpeed))
                .ThenBy(segment => inputOrder.GetValueOrDefault(segment.Job.Id, int.MaxValue)),
            MergeOrderMode.FileName => plan.Segments
                .OrderBy(segment => segment.Job.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(segment => inputOrder.GetValueOrDefault(segment.Job.Id, int.MaxValue)),
            _ => plan.Segments
                .OrderBy(segment => inputOrder.GetValueOrDefault(segment.Job.Id, int.MaxValue))
        };

        return new MergeOutputPlan(
            plan.Index,
            ordered.ToArray(),
            plan.TitleJob)
        {
            VideoSpeed = plan.VideoSpeed,
            OutputBaseName = plan.OutputBaseName
        };
    }

    private static string GetAutoShuffleMembershipSignature(MergeOutputPlan plan) =>
        string.Join(
            "|",
            plan.Segments
                .Select(segment =>
                    $"{StableSourceKey(segment.Job).ToUpperInvariant()}@" +
                    $"{segment.StartSeconds.ToString("R", CultureInfo.InvariantCulture)}:" +
                    $"{segment.DurationSeconds.ToString("R", CultureInfo.InvariantCulture)}")
                .OrderBy(value => value, StringComparer.Ordinal));

    private static void AvoidSameCycleBoundary(
        IList<MergeOutputPlan> cycle,
        string? previousSignature)
    {
        if (cycle.Count <= 1 || string.IsNullOrWhiteSpace(previousSignature)) return;
        if (!string.Equals(
                GetAutoShuffleMembershipSignature(cycle[0]),
                previousSignature,
                StringComparison.OrdinalIgnoreCase)) return;

        for (var index = 1; index < cycle.Count; index++)
        {
            if (string.Equals(
                    GetAutoShuffleMembershipSignature(cycle[index]),
                    previousSignature,
                    StringComparison.OrdinalIgnoreCase)) continue;
            (cycle[0], cycle[index]) = (cycle[index], cycle[0]);
            break;
        }
    }

    private static MergeOutputPlan CloneWithIndex(MergeOutputPlan plan, int index) =>
        new(index, plan.Segments.ToArray(), plan.TitleJob)
        {
            VideoSpeed = plan.VideoSpeed
        };

    public MergeOutputPlan? BuildAutoShuffleReplacement(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings,
        MergeOutputPlan failedPlan,
        int replacementRound,
        IReadOnlyCollection<string>? excludedSignatures = null)
    {
        if (!settings.AutoShuffleBatchEnabled || jobs.Count == 0) return null;

        var excluded = new HashSet<string>(
            excludedSignatures ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase)
        {
            GetAutoShuffleCombinationSignature(failedPlan)
        };

        // Chỉ tạo một lô ứng viên nhỏ; không dựng lại toàn bộ 1.650 output.
        // Seed phụ thuộc output + số lần thay combo nên lần thay tiếp theo luôn
        // đi sang một nhánh phối khác nhưng vẫn tái lập được trong cùng lượt chạy.
        for (var seedAttempt = 0; seedAttempt < 8; seedAttempt++)
        {
            var pass = new PresetSettings
            {
                AutoShuffleBatchEnabled = true,
                AutoShuffleStreamCopyEnabled = settings.AutoShuffleStreamCopyEnabled,
                AutoShuffleTargetCount = 32,
                MergeMinSeconds = settings.MergeMinSeconds,
                MergeMaxSeconds = settings.MergeMaxSeconds,
                MergeVideosPerOutput = settings.MergeVideosPerOutput,
                MergeRandomSeed = DeriveSeed(
                    settings.MergeRandomSeed,
                    failedPlan.Index + replacementRound * 1009,
                    0x7800 + seedAttempt * 131),
                MergeOrder = MergeOrderMode.Random,
                VideoSpeed = settings.VideoSpeed
            };

            foreach (var candidate in BuildAutoShuffleBatch(jobs, pass))
            {
                var signature = GetAutoShuffleCombinationSignature(candidate);
                if (excluded.Contains(signature)) continue;
                var replacement = CloneWithIndex(candidate, failedPlan.Index);
                // Giữ nguyên tên/số output để resume và thứ tự 1..N không đổi.
                replacement.OutputBaseName = failedPlan.OutputBaseName;
                return replacement;
            }
        }

        return null;
    }

    public static string GetAutoShuffleCombinationSignature(MergeOutputPlan plan) =>
        string.Join(
            "|",
            plan.Segments.Select(segment =>
                $"{StableSourceKey(segment.Job).ToUpperInvariant()}@" +
                $"{segment.StartSeconds.ToString("R", CultureInfo.InvariantCulture)}:" +
                $"{segment.DurationSeconds.ToString("R", CultureInfo.InvariantCulture)}"));

    private IReadOnlyList<MergeOutputPlan> BuildStandard(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings)
    {
        var sources = jobs
            .Where(x => x.Media.DurationSeconds > 0.05)
            .ToArray();
        if (sources.Length == 0) return [];

        var videoSpeed = Math.Clamp(settings.VideoSpeed, 0.1, 10);
        var videosPerOutput = Math.Clamp(settings.MergeVideosPerOutput, 0, 1000);
        List<List<VideoJob>> groups;

        if (videosPerOutput > 0)
        {
            // Chế độ ghép theo SỐ VIDEO hoàn toàn độc lập với thời lượng.
            // Không lọc clip dài, không kiểm tra Min/Max; chỉ cần đủ đúng số clip.
            groups = BuildFixedCountGroups(
                sources,
                videosPerOutput,
                videoSpeed,
                settings.MergeRandomSeed,
                settings.MergeOrder);
        }
        else
        {
            // Chế độ ghép theo THỜI GIAN giữ nguyên cơ chế cũ.
            var minimum = Math.Max(1, Math.Min(settings.MergeMinSeconds, settings.MergeMaxSeconds));
            var maximum = Math.Max(minimum, Math.Max(settings.MergeMinSeconds, settings.MergeMaxSeconds));
            var eligibleSources = sources
                .Where(job =>
                    OutputDuration(job, videoSpeed) <=
                    maximum + DurationTolerance)
                .ToArray();
            if (eligibleSources.Length == 0) return [];
            var eligibleTotalSeconds = eligibleSources.Sum(x => OutputDuration(x, videoSpeed));

            groups = TryBuildRandomGroupsInRange(
                         eligibleSources,
                         eligibleTotalSeconds,
                         minimum,
                         maximum,
                         videoSpeed,
                         settings.MergeRandomSeed)
                     ?? BuildRandomValidGroups(
                         eligibleSources,
                         minimum,
                         maximum,
                         videoSpeed,
                         settings.MergeRandomSeed);

            // Chỉ khi không tìm được bất kỳ nhóm nào nằm hoàn toàn trong khoảng
            // đã đặt mới cho phép vượt tối đa +20 giây. Không cắt clip cuối;
            // toàn bộ từng video nguồn được giữ nguyên.
            if (groups.Count == 0)
            {
                var overflowMaximum = maximum + RandomOverflowAllowanceSeconds;
                var overflowSources = sources
                    .Where(job => OutputDuration(job, videoSpeed) <=
                                  overflowMaximum + DurationTolerance)
                    .ToArray();
                if (overflowSources.Length > 0)
                {
                    var overflowTotal = overflowSources.Sum(x =>
                        OutputDuration(x, videoSpeed));
                    groups = TryBuildRandomGroupsInRange(
                                 overflowSources,
                                 overflowTotal,
                                 minimum,
                                 overflowMaximum,
                                 videoSpeed,
                                 DeriveSeed(settings.MergeRandomSeed, 0x20, 0x20))
                             ?? BuildRandomValidGroups(
                                 overflowSources,
                                 minimum,
                                 overflowMaximum,
                                 videoSpeed,
                                 DeriveSeed(settings.MergeRandomSeed, 0x20, 0x21));
                }
            }
        }

        if (settings.MergeOrder != MergeOrderMode.Random)
        {
            var inputOrder = jobs
                .Select((job, index) => (job.Id, index))
                .ToDictionary(item => item.Id, item => item.index);
            foreach (var group in groups)
            {
                group.Sort((left, right) => settings.MergeOrder switch
                {
                    MergeOrderMode.ShortestFirst =>
                        OutputDuration(left, videoSpeed).CompareTo(
                            OutputDuration(right, videoSpeed)),
                    MergeOrderMode.FileName => string.Compare(
                        left.FileName,
                        right.FileName,
                        StringComparison.CurrentCultureIgnoreCase),
                    _ => inputOrder[left.Id].CompareTo(inputOrder[right.Id])
                });
            }
            groups = settings.MergeOrder switch
            {
                MergeOrderMode.FileName => groups
                    .OrderBy(
                        group => group.FirstOrDefault()?.FileName,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                MergeOrderMode.ShortestFirst => groups
                    .OrderBy(group => group.Count == 0
                        ? double.MaxValue
                        : OutputDuration(group[0], videoSpeed))
                    .ThenBy(group => group.Sum(job =>
                        OutputDuration(job, videoSpeed)))
                    .ToList(),
                _ => groups
                    .OrderBy(group => group.Count == 0
                        ? int.MaxValue
                        : group.Min(job => inputOrder[job.Id]))
                    .ToList()
            };
        }

        var plans = new List<MergeOutputPlan>(groups.Count);
        foreach (var group in groups)
        {
            var segments = group
                .Select(job => new MergeSegment(job, 0, job.Media.DurationSeconds))
                .ToArray();
            plans.Add(new MergeOutputPlan(
                plans.Count + 1,
                segments,
                SelectMeaningfulTitleJob(group))
            {
                VideoSpeed = videoSpeed
            });
        }

        return plans;
    }

    private static List<List<VideoJob>> BuildFixedCountGroups(
        IReadOnlyList<VideoJob> sources,
        int videosPerOutput,
        double videoSpeed,
        int randomSeed,
        MergeOrderMode mergeOrder)
    {
        videosPerOutput = Math.Max(1, videosPerOutput);
        if (sources.Count < videosPerOutput) return [];

        var baseItems = sources
            .Select((job, index) => new FixedCountItem(
                job,
                index,
                OutputDuration(job, videoSpeed)))
            .ToArray();

        List<List<VideoJob>> BuildFromOrder(IReadOnlyList<FixedCountItem> ordered)
        {
            var result = new List<List<VideoJob>>();
            for (var offset = 0;
                 offset + videosPerOutput <= ordered.Count;
                 offset += videosPerOutput)
            {
                var slice = ordered
                    .Skip(offset)
                    .Take(videosPerOutput)
                    .ToArray();
                result.Add(slice.Select(item => item.Job).ToList());
            }
            return result;
        }

        if (mergeOrder != MergeOrderMode.Random)
        {
            var ordered = mergeOrder switch
            {
                MergeOrderMode.ShortestFirst => baseItems
                    .OrderBy(item => item.Duration)
                    .ThenBy(item => item.InputIndex)
                    .ToArray(),
                MergeOrderMode.FileName => baseItems
                    .OrderBy(item => item.Job.FileName,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.InputIndex)
                    .ToArray(),
                _ => baseItems.OrderBy(item => item.InputIndex).ToArray()
            };
            return BuildFromOrder(ordered);
        }

        // Với ghép ngẫu nhiên, thử vài lần xáo trộn và giữ phương án dùng được
        // nhiều clip nhất. Seed thay đổi khi bấm “Phối lại” hoặc bắt đầu Render.
        var random = new Random(randomSeed);
        List<List<VideoJob>> best = [];
        var maximumPossibleGroups = sources.Count / videosPerOutput;
        const int attempts = 32;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var shuffled = baseItems.ToList();
            Shuffle(shuffled, random);
            var candidate = BuildFromOrder(shuffled);
            if (candidate.Count > best.Count) best = candidate;
            if (best.Count >= maximumPossibleGroups) break;
        }

        Shuffle(best, random);
        foreach (var group in best) Shuffle(group, random);
        return best;
    }

    private static List<List<VideoJob>>? TryBuildRandomGroupsInRange(
        IReadOnlyList<VideoJob> sources,
        double totalSeconds,
        double minimum,
        double maximum,
        double videoSpeed,
        int randomSeed)
    {
        var items = sources
            .Select(job => new RandomItem(job, OutputDuration(job, videoSpeed)))
            .ToArray();
        if (items.Any(item => item.Duration > maximum + DurationTolerance))
            return null;

        var minimumGroupCount = Math.Max(
            1,
            (int)Math.Ceiling((totalSeconds - DurationTolerance) / maximum));
        var maximumGroupCount = Math.Min(
            sources.Count,
            Math.Max(1, (int)Math.Floor((totalSeconds + DurationTolerance) / minimum)));
        if (minimumGroupCount > maximumGroupCount) return null;

        var random = new Random(randomSeed);

        // Ưu tiên ít file đầu ra nhất. Cân bằng clip dài trước giúp các nhóm
        // không vượt trần; các lần thử sau thêm nhiễu để thay đổi cách phối.
        for (var groupCount = minimumGroupCount;
             groupCount <= maximumGroupCount;
             groupCount++)
        {
            const int attemptsPerGroupCount = 8;
            for (var attempt = 0; attempt < attemptsPerGroupCount; attempt++)
            {
                var orderedItems = items
                    .Select(item => new
                    {
                        Item = item,
                        Priority = item.Duration +
                                   (attempt == 0
                                       ? 0
                                       : random.NextDouble() *
                                         Math.Min(5, Math.Max(0.1, item.Duration * 0.2)))
                    })
                    .OrderByDescending(x => x.Priority)
                    .ThenBy(_ => random.Next())
                    .Select(x => x.Item)
                    .ToArray();

                var groups = Enumerable.Range(0, groupCount)
                    .Select(_ => new RandomGroup())
                    .ToArray();
                var lightestGroups = new PriorityQueue<RandomGroup, double>();
                foreach (var group in groups)
                    lightestGroups.Enqueue(group, group.Duration);
                var failed = false;

                foreach (var item in orderedItems)
                {
                    // Chỉ lấy 1–3 nhóm nhẹ nhất từ heap. Trước đây mỗi clip lại
                    // sắp xếp toàn bộ nhóm, khiến 14.000 clip phải thực hiện
                    // hàng triệu phép so sánh trên luồng giao diện.
                    var inspectCount = attempt == 0
                        ? 1
                        : Math.Min(3, lightestGroups.Count);
                    var inspected = new List<RandomGroup>(inspectCount);
                    for (var i = 0; i < inspectCount; i++)
                        inspected.Add(lightestGroups.Dequeue());

                    var eligible = inspected
                        .Where(group =>
                            group.Duration + item.Duration <=
                            maximum + DurationTolerance)
                        .ToArray();
                    if (eligible.Length == 0)
                    {
                        foreach (var group in inspected)
                            lightestGroups.Enqueue(group, group.Duration);
                        failed = true;
                        break;
                    }

                    // Chọn ngẫu nhiên trong vài nhóm nhẹ nhất để cách phối thay
                    // đổi nhưng tổng thời lượng vẫn cân bằng.
                    var selected = eligible[random.Next(eligible.Length)];
                    selected.Jobs.Add(item.Job);
                    selected.Duration += item.Duration;
                    foreach (var group in inspected)
                        lightestGroups.Enqueue(group, group.Duration);
                }

                if (failed ||
                    !RepairMinimumDurations(
                        groups, minimum, maximum, videoSpeed, random) ||
                    groups.Any(group =>
                        group.Duration < minimum - DurationTolerance ||
                        group.Duration > maximum + DurationTolerance))
                    continue;

                RandomizeValidMembership(
                    groups, minimum, maximum, videoSpeed, random, items.Length * 3);
                foreach (var group in groups) Shuffle(group.Jobs, random);
                Shuffle(groups, random);
                return groups.Select(group => group.Jobs).ToList();
            }
        }

        return null;
    }

    private static bool RepairMinimumDurations(
        IReadOnlyList<RandomGroup> groups,
        double minimum,
        double maximum,
        double videoSpeed,
        Random random)
    {
        foreach (var receiver in groups
                     .Where(group => group.Duration < minimum - DurationTolerance)
                     .OrderBy(group => group.Duration))
        {
            while (receiver.Duration < minimum - DurationTolerance)
            {
                var moves = groups
                    .Where(donor => !ReferenceEquals(donor, receiver))
                    .SelectMany(donor => donor.Jobs.Select(job => new
                    {
                        Donor = donor,
                        Job = job,
                        Duration = OutputDuration(job, videoSpeed)
                    }))
                    .Where(move =>
                        move.Donor.Duration - move.Duration >=
                        minimum - DurationTolerance &&
                        receiver.Duration + move.Duration <=
                        maximum + DurationTolerance)
                    .OrderBy(move =>
                        Math.Abs(minimum - (receiver.Duration + move.Duration)))
                    .ThenBy(_ => random.Next())
                    .Take(8)
                    .ToArray();
                if (moves.Length == 0) return false;

                var move = moves[random.Next(moves.Length)];
                move.Donor.Jobs.Remove(move.Job);
                move.Donor.Duration -= move.Duration;
                receiver.Jobs.Add(move.Job);
                receiver.Duration += move.Duration;
            }
        }

        return true;
    }

    private static void RandomizeValidMembership(
        IReadOnlyList<RandomGroup> groups,
        double minimum,
        double maximum,
        double videoSpeed,
        Random random,
        int attempts)
    {
        if (groups.Count < 2) return;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var first = groups[random.Next(groups.Count)];
            var second = groups[random.Next(groups.Count)];
            if (ReferenceEquals(first, second) ||
                first.Jobs.Count == 0 ||
                second.Jobs.Count == 0)
                continue;

            var firstIndex = random.Next(first.Jobs.Count);
            var secondIndex = random.Next(second.Jobs.Count);
            var firstJob = first.Jobs[firstIndex];
            var secondJob = second.Jobs[secondIndex];
            var firstDuration = OutputDuration(firstJob, videoSpeed);
            var secondDuration = OutputDuration(secondJob, videoSpeed);
            var newFirstDuration =
                first.Duration - firstDuration + secondDuration;
            var newSecondDuration =
                second.Duration - secondDuration + firstDuration;
            if (newFirstDuration < minimum - DurationTolerance ||
                newFirstDuration > maximum + DurationTolerance ||
                newSecondDuration < minimum - DurationTolerance ||
                newSecondDuration > maximum + DurationTolerance)
                continue;

            first.Jobs[firstIndex] = secondJob;
            second.Jobs[secondIndex] = firstJob;
            first.Duration = newFirstDuration;
            second.Duration = newSecondDuration;
        }
    }

    private static List<List<VideoJob>> BuildRandomValidGroups(
        IReadOnlyList<VideoJob> sources,
        double minimum,
        double maximum,
        double videoSpeed,
        int randomSeed)
    {
        var groups = new List<List<VideoJob>>();
        var random = new Random(randomSeed);
        var remaining = sources
            .Select(job => new RandomItem(job, OutputDuration(job, videoSpeed)))
            .Where(item => item.Duration <= maximum + DurationTolerance)
            .ToList();
        Shuffle(remaining, random);

        while (remaining.Count > 0)
        {
            var group = new RandomGroup();
            var target = minimum + random.NextDouble() * (maximum - minimum);

            while (group.Duration < target - DurationTolerance)
            {
                var desiredDuration = Math.Max(0, target - group.Duration);
                var candidateIndexes = remaining
                    .Select((item, index) => new
                    {
                        Item = item,
                        Index = index,
                        Difference = Math.Abs(desiredDuration - item.Duration),
                        Noise = random.Next()
                    })
                    .Where(candidate =>
                        group.Duration + candidate.Item.Duration <=
                        maximum + DurationTolerance)
                    .OrderBy(candidate => candidate.Difference)
                    .ThenBy(candidate => candidate.Noise)
                    .Take(12)
                    .Select(candidate => candidate.Index)
                    .ToArray();
                if (candidateIndexes.Length == 0) break;

                var selectedIndex =
                    candidateIndexes[random.Next(candidateIndexes.Length)];
                var selected = remaining[selectedIndex];
                remaining.RemoveAt(selectedIndex);
                group.Jobs.Add(selected.Job);
                group.Duration += selected.Duration;
            }

            if (group.Duration >= minimum - DurationTolerance &&
                group.Duration <= maximum + DurationTolerance)
            {
                Shuffle(group.Jobs, random);
                groups.Add(group.Jobs);
            }
            // Nhóm chưa đạt mức tối thiểu được bỏ qua. Các clip trong nhóm đó
            // không xuất hiện trong kế hoạch nên RenderQueue cũng không xóa chúng.
        }

        Shuffle(groups, random);
        return groups;
    }

    private static string StableSourceKey(VideoJob job)
    {
        try { return Path.GetFullPath(job.InputPath); }
        catch { return job.InputPath ?? ""; }
    }

    private static int DeriveSeed(int seed, int value1, int value2)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)value1 + 0x9E3779B9u + (x << 6) + (x >> 2);
            x ^= (uint)value2 + 0x85EBCA6Bu + (x << 6) + (x >> 2);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (int)x;
        }
    }

    private static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private sealed record FixedCountItem(VideoJob Job, int InputIndex, double Duration);
    private sealed record RandomItem(VideoJob Job, double Duration);

    private sealed class RandomGroup
    {
        public List<VideoJob> Jobs { get; } = [];
        public double Duration { get; set; }
    }

    private static double OutputDuration(VideoJob job, double videoSpeed) =>
        job.Media.DurationSeconds / Math.Clamp(videoSpeed, 0.1, 10);

    private static VideoJob SelectMeaningfulTitleJob(IReadOnlyList<VideoJob> group)
    {
        return group
            .Select((job, index) => new
            {
                Job = job,
                Index = index,
                Score = MeaningfulTitleScore(job.BaseName)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Job)
            .First();
    }

    private static long MeaningfulTitleScore(string fileNameWithoutExtension)
    {
        var title = fileNameWithoutExtension ?? "";
        var hashtagIndex = title.IndexOf('#');
        if (hashtagIndex >= 0) title = title[..hashtagIndex];

        title = Regex.Replace(title, @"https?://\S+|www\.\S+", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"@[^\s#]+", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = title.Replace('_', ' ').Replace('-', ' ');

        var words = Regex.Matches(title, @"[\p{L}\p{N}]+")
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => word.Any(char.IsLetter))
            .Where(word => !GenericTitleWords.Contains(word))
            .ToArray();
        if (words.Length == 0) return 0;

        var distinctWords = words.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var letterCount = words.Sum(word => word.Count(char.IsLetter));
        return words.Length * 1_000_000L +
               distinctWords * 10_000L +
               letterCount * 100L +
               Math.Min(99, title.Trim().Length);
    }
}
