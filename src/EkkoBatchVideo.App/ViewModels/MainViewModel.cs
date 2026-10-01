using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EkkoBatchVideo.Infrastructure;
using EkkoBatchVideo.Models;
using EkkoBatchVideo.Services;
using EkkoBatchVideo.Views;

namespace EkkoBatchVideo.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MetadataCheckpointIntervalSeconds = 45;
    private static readonly int MetadataProbeConcurrency =
        Math.Clamp(Environment.ProcessorCount, 4, 8);

    private readonly InputEngine _input;
    private readonly StreamCopyCompatibilityService _streamCopyCompatibility;
    private readonly SplitEngine _split;
    private readonly MergePlanner _mergePlanner;
    private readonly TitleSourceService _titleSource;
    private readonly TypographyEngine _typography;
    private readonly SubtitleAiService _subtitles;
    private readonly RenderQueue _renderQueue;
    private readonly PreviewService _preview;
    private readonly WatchFolderService _watchFolder;
    private readonly ProjectService _projects;
    private readonly PresetService _presets;
    private readonly RecycleBinService _recycleBin;
    private readonly DiskSpaceService _diskSpace;
    private readonly SmartTrimService _smartTrim;
    private readonly WatermarkImageService _watermarks;
    private readonly FileDialogService _dialogs;
    private readonly AppLogger _logger;
    private readonly AuthService _auth;
    private PresetSettings _settings = new();
    private VideoJob? _selectedJob;
    private PartPlan? _selectedPart;
    private Uri? _previewUri;
    private Uri? _previewOverlayUri;
    private ImageSource? _watermarkPreviewSource;
    private double _watermarkPreviewAspectRatio = 1;
    private bool _watermarkBackgroundRemoved;
    private string _projectName = "Ekko Project";
    private string _statusText = "Sẵn sàng";
    private string _detectedGpuText = "Đang dò GPU…";
    private string _subtitleAiStatusText = "Đang kiểm tra bộ phụ đề AI…";
    private string _subtitleCacheSizeText = "Đang tính…";
    private string _diskSpaceStatusText = "Chưa kiểm tra dung lượng";
    private string? _selectedSavedPresetName;
    private bool _isBusy;
    private LogEntry? _selectedLog;
    private bool _isSubtitlePositionEditMode;
    private bool _isCustomCropEditMode;
    private bool _isMouseCropEditMode;
    private bool _isExistingSubtitleBlurEditMode;
    private bool _isWatermarkPositionEditMode;
    private bool _ffmpegReady;
    private double _lastPreviewPositionPercent;
    private CancellationTokenSource _lifetime = new();
    private IReadOnlyList<MergeOutputPlan>? _cachedMergePlans;
    private CancellationTokenSource? _mergePlanBuildSource;
    private CancellationTokenSource? _sourceDeletionSaveSource;
    private CancellationTokenSource? _settingsAutoSaveSource;
    private CancellationTokenSource? _metadataAnalysisSource;
    private RenderQueueProgress? _currentQueueProgress;
    private DateTimeOffset? _queueProgressStartedAt;
    private bool _isMetadataAnalysisRunning;
    private int _metadataAnalysisTotal;
    private int _metadataAnalysisCompleted;
    private int _metadataAnalysisFailed;
    private readonly ConcurrentDictionary<string, byte> _pendingWatchedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private int _watchDrainActive;
    private int _subtitleCacheRefreshRunning;
    private int _subtitleCacheRefreshRequested;
    // v62: mỗi nguồn chỉ probe một lần theo file-signature để lấy duration hình
    // thật + chữ ký tương thích stream-copy. 1.650 output dùng lại kết quả này.
    private readonly Dictionary<Guid, (long Length, long LastWriteTicks, string Key, string Summary)>
        _streamCopySourceValidated = new();

    public MainViewModel(
        InputEngine input,
        StreamCopyCompatibilityService streamCopyCompatibility,
        SplitEngine split,
        MergePlanner mergePlanner,
        TitleSourceService titleSource,
        TypographyEngine typography,
        SubtitleAiService subtitles,
        RenderQueue renderQueue,
        PreviewService preview,
        WatchFolderService watchFolder,
        ProjectService projectService,
        PresetService presetService,
        RecycleBinService recycleBin,
        DiskSpaceService diskSpace,
        SmartTrimService smartTrim,
        WatermarkImageService watermarks,
        FileDialogService dialogs,
        AppLogger logger,
        AuthService auth)
    {
        _input = input;
        _streamCopyCompatibility = streamCopyCompatibility;
        _split = split;
        _mergePlanner = mergePlanner;
        _titleSource = titleSource;
        _typography = typography;
        _subtitles = subtitles;
        _renderQueue = renderQueue;
        _preview = preview;
        _watchFolder = watchFolder;
        _projects = projectService;
        _presets = presetService;
        _recycleBin = recycleBin;
        _diskSpace = diskSpace;
        _smartTrim = smartTrim;
        _watermarks = watermarks;
        _dialogs = dialogs;
        _logger = logger;
        _auth = auth;

        PickFolderCommand = new AsyncRelayCommand(
            PickFolderAsync,
            () => !_renderQueue.IsRunning &&
                  !IsBusy &&
                  !IsMetadataAnalysisRunning);
        ChangePasswordCommand = new AsyncRelayCommand(ChangePasswordAsync);
        AddVideosCommand = new AsyncRelayCommand(
            AddVideosAsync,
            () => !_renderQueue.IsRunning &&
                  !IsBusy &&
                  !IsMetadataAnalysisRunning);
        RefreshListCommand = new AsyncRelayCommand(
            RefreshListAsync,
            () => !_renderQueue.IsRunning &&
                  !IsBusy &&
                  !IsMetadataAnalysisRunning);
        ChooseOutputCommand = new RelayCommand(ChooseOutput);
        OpenLatestOutputCommand = new RelayCommand(OpenLatestOutput);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        LogoutCommand = new RelayCommand(Logout);
        CopySelectedLogCommand = new RelayCommand(
            CopySelectedLog,
            () => SelectedLog is not null);
        CopyAllErrorsCommand = new RelayCommand(
            CopyAllErrors,
            () => Logs.Any(entry => entry.Severity == LogSeverity.Error));
        ChooseBackgroundImageCommand = new RelayCommand(ChooseBackgroundImage);
        ChooseWatermarkImageCommand = new RelayCommand(ChooseWatermarkImage);
        ClearWatermarkImageCommand = new RelayCommand(
            ClearWatermarkImage,
            () => !string.IsNullOrWhiteSpace(Settings.WatermarkPath));
        ChooseIntroImageCommand = new RelayCommand(ChooseIntroImage);
        ChooseOutroImageCommand = new RelayCommand(ChooseOutroImage);
        ChooseManualSubtitleCommand = new RelayCommand(
            ChooseManualSubtitle,
            () => SelectedJob is not null &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        EditManualSubtitleCommand = new RelayCommand(
            EditManualSubtitle,
            () => SelectedJob?.HasManualSubtitle == true);
        ClearManualSubtitleCommand = new RelayCommand(
            ClearManualSubtitle,
            () => SelectedJob is not null &&
                  !string.IsNullOrWhiteSpace(SelectedJob.ManualSubtitlePath) &&
                  !_renderQueue.IsRunning);
        SetBlurZoomCommand = new ParameterRelayCommand((object? value) =>
        {
            if (double.TryParse(value?.ToString(), out var zoom)) Settings.BlurZoom = zoom;
        });
        SetCustomRatioCommand = new ParameterRelayCommand((object? value) =>
        {
            if (double.TryParse(value?.ToString(), out var percent))
                Settings.CustomMainPercent = percent;
        });
        ResetVideoEffectsCommand = new RelayCommand(() =>
        {
            Settings.VideoEffectsEnabled = false;
            Settings.VideoShakeEnabled = false;
            Settings.VideoOffsetEnabled = false;
            Settings.VideoColorEnabled = false;
            Settings.VideoNoiseEnabled = false;
            Settings.VideoShakeStrength = 0;
            Settings.VideoShakeSpeed = 3;
            Settings.VideoShakePeriodic = false;
            Settings.VideoShakeIntervalSeconds = 5;
            Settings.VideoShakeDurationSeconds = 0.35;
            Settings.VideoOffsetX = 0;
            Settings.VideoOffsetY = 0;
            Settings.VideoBrightness = 0;
            Settings.VideoContrast = 1;
            Settings.VideoSaturation = 1;
            Settings.VideoNoiseStrength = 0;
        });
        SetLightShakePresetCommand = new RelayCommand(() =>
        {
            Settings.VideoEffectsEnabled = true;
            Settings.VideoShakeEnabled = true;
            Settings.VideoShakeStrength = 5;
            Settings.VideoShakeSpeed = 3;
            Settings.VideoShakePeriodic = true;
            Settings.VideoShakeIntervalSeconds = 5;
            Settings.VideoShakeDurationSeconds = 0.35;
        });
        ResetAudioEffectsCommand = new RelayCommand(() =>
        {
            Settings.AudioEffectsEnabled = false;
            Settings.AudioVolumeEnabled = false;
            Settings.AudioPitchEnabled = false;
            Settings.AudioEqEnabled = false;
            Settings.AudioEchoEnabled = false;
            Settings.AudioNoiseReductionEnabled = false;
            Settings.AudioCompressorEnabled = false;
            Settings.AudioNormalizeEnabled = false;
            Settings.AudioVolume = 1;
            Settings.AudioPitchSemitones = 0;
            Settings.AudioBassGain = 0;
            Settings.AudioTrebleGain = 0;
            Settings.AudioEchoDelayMs = 60;
            Settings.AudioEchoStrength = 0;
        });
        SetBrightVoicePresetCommand = new RelayCommand(() =>
        {
            Settings.AudioEffectsEnabled = true;
            Settings.AudioVolumeEnabled = true;
            Settings.AudioPitchEnabled = true;
            Settings.AudioEqEnabled = true;
            Settings.AudioEchoEnabled = true;
            Settings.AudioNoiseReductionEnabled = true;
            Settings.AudioCompressorEnabled = true;
            Settings.AudioNormalizeEnabled = true;
            Settings.AudioVolume = 1;
            Settings.AudioPitchSemitones = 0.6;
            Settings.AudioBassGain = 0;
            Settings.AudioTrebleGain = 1.5;
            Settings.AudioEchoDelayMs = 55;
            Settings.AudioEchoStrength = 0.06;
        });
        SetWarmVoicePresetCommand = new RelayCommand(() =>
        {
            Settings.AudioEffectsEnabled = true;
            Settings.AudioVolumeEnabled = true;
            Settings.AudioPitchEnabled = true;
            Settings.AudioEqEnabled = true;
            Settings.AudioEchoEnabled = true;
            Settings.AudioNoiseReductionEnabled = true;
            Settings.AudioCompressorEnabled = true;
            Settings.AudioNormalizeEnabled = true;
            Settings.AudioVolume = 1;
            Settings.AudioPitchSemitones = -0.4;
            Settings.AudioBassGain = 1.5;
            Settings.AudioTrebleGain = -0.5;
            Settings.AudioEchoDelayMs = 65;
            Settings.AudioEchoStrength = 0.05;
        });
        SetBottomSubtitleBlurPresetCommand = new RelayCommand(() =>
        {
            Settings.ExistingSubtitleBlurEnabled = true;
            Settings.AutoDetectExistingSubtitleRegion = false;
            Settings.ExistingSubtitleBlurX = 60;
            Settings.ExistingSubtitleBlurY = 1580;
            Settings.ExistingSubtitleBlurWidth = 960;
            Settings.ExistingSubtitleBlurHeight = 270;
            Settings.ExistingSubtitleBlurStrength = 22;
            IsSubtitlePositionEditMode = false;
            IsCustomCropEditMode = false;
            IsMouseCropEditMode = false;
            IsExistingSubtitleBlurEditMode = HasPreview;
        });
        RebuildPartsCommand = new AsyncRelayCommand(
            RebuildPartsAsync,
            () => Jobs.Count > 0 &&
                  !IsMetadataAnalysisRunning &&
                  !Jobs.Any(job => job.Status == JobStatus.Analyzing) &&
                  !_renderQueue.IsRunning);
        SmartTrimSelectedCommand = new AsyncRelayCommand(
            SmartTrimSelectedAsync,
            () => SelectedJob is not null &&
                  SelectedJob.Media.DurationSeconds > 0 &&
                  _ffmpegReady &&
                  !IsBusy &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        RenderCommand = new AsyncRelayCommand(
            RenderAsync,
            () => Jobs.Any(HasUsableMetadata) &&
                  !Jobs.Any(job => job.Status == JobStatus.Analyzing) &&
                  _ffmpegReady &&
                  !_renderQueue.IsRunning &&
                  !IsBusy &&
                  !IsMetadataAnalysisRunning);
        PauseResumeCommand = new RelayCommand(PauseResume, () => _renderQueue.IsRunning);
        CancelCommand = new RelayCommand(_renderQueue.Cancel, () => _renderQueue.IsRunning);
        CancelMetadataAnalysisCommand = new RelayCommand(
            CancelMetadataAnalysis,
            () => IsMetadataAnalysisRunning);
        RetryCommand = new AsyncRelayCommand(
            RetryAsync,
            () => !IsMetadataAnalysisRunning &&
                  !Jobs.Any(job => job.Status == JobStatus.Analyzing) &&
                  Jobs.Any(x => x.Status == JobStatus.Failed));
        PreviewCommand = new AsyncRelayCommand(
            () => SafePreviewAtAsync(0),
            () => SelectedJob is not null && SelectedPart is not null);
        PreviewStartCommand = new AsyncRelayCommand(
            () => SafePreviewAtAsync(0),
            () => SelectedJob is not null && SelectedPart is not null);
        PreviewMiddleCommand = new AsyncRelayCommand(
            () => SafePreviewAtAsync(50),
            () => SelectedJob is not null && SelectedPart is not null);
        PreviewEndCommand = new AsyncRelayCommand(
            () => SafePreviewAtAsync(100),
            () => SelectedJob is not null && SelectedPart is not null);
        DetectExistingSubtitleBlurCommand = new AsyncRelayCommand(
            DetectExistingSubtitleBlurAsync,
            () => SelectedJob is not null &&
                  SelectedPart is not null &&
                  !_renderQueue.IsRunning &&
                  !IsBusy);
        ThumbnailsCommand = new AsyncRelayCommand(GenerateThumbnailsAsync, () => SelectedJob is not null);
        RemoveSelectedCommand = new RelayCommand(
            RemoveSelected,
            () => SelectedJob is not null &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        PermanentlyDeleteSelectedCommand = new RelayCommand(
            PermanentlyDeleteSelected,
            () => SelectedJob is not null &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        ClearCommand = new RelayCommand(
            Clear,
            () => Jobs.Count > 0 &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        SaveProjectCommand = new AsyncRelayCommand(
            SaveProjectAsync,
            () => !IsBusy && !_renderQueue.IsRunning);
        OpenProjectCommand = new AsyncRelayCommand(
            OpenProjectAsync,
            () => !IsBusy &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        ApplyPlatformPresetCommand = new RelayCommand(ApplyPlatformPreset);
        RegenerateMergePlanCommand = new RelayCommand(
            RegenerateMergePlan,
            () => Settings.MergeVideosEnabled &&
                  !_renderQueue.IsRunning &&
                  !IsMetadataAnalysisRunning);
        SavePresetCommand = new AsyncRelayCommand(
            SavePresetAsync,
            () => !IsBusy && !_renderQueue.IsRunning);
        LoadPresetCommand = new AsyncRelayCommand(
            LoadPresetAsync,
            () => !string.IsNullOrWhiteSpace(SelectedSavedPresetName) &&
                  !IsBusy &&
                  !_renderQueue.IsRunning);
        DeletePresetCommand = new RelayCommand(
            DeletePreset,
            () => !string.IsNullOrWhiteSpace(SelectedSavedPresetName) &&
                  !_renderQueue.IsRunning);
        InstallSubtitleAiCommand = new AsyncRelayCommand(InstallSubtitleAiAsync);
        RefreshSubtitleAiCommand = new RelayCommand(RefreshSubtitleAiStatus);
        RefreshSubtitleCacheCommand = new RelayCommand(RefreshSubtitleCacheSize);
        ClearSelectedSubtitleCacheCommand = new RelayCommand(ClearSelectedSubtitleCache,
            () => SelectedJob is not null && !_renderQueue.IsRunning && !_subtitles.IsBusy && !IsBusy);
        ClearAllSubtitleCacheCommand = new RelayCommand(ClearAllSubtitleCache,
            () => !_renderQueue.IsRunning && !_subtitles.IsBusy && !IsBusy);
        ToggleSubtitlePositionEditCommand = new RelayCommand(
            () =>
            {
                IsCustomCropEditMode = false;
                IsMouseCropEditMode = false;
                IsExistingSubtitleBlurEditMode = false;
                IsWatermarkPositionEditMode = false;
                IsSubtitlePositionEditMode = !IsSubtitlePositionEditMode;
            },
            () => HasPreview);
        ToggleCustomCropEditCommand = new RelayCommand(
            () =>
            {
                IsSubtitlePositionEditMode = false;
                IsMouseCropEditMode = false;
                IsExistingSubtitleBlurEditMode = false;
                IsWatermarkPositionEditMode = false;
                IsCustomCropEditMode = !IsCustomCropEditMode;
            },
            () => HasPreview && Settings.IsCustomLayout);
        ToggleMouseCropEditCommand = new AsyncRelayCommand(
            ToggleMouseCropEditAsync,
            () => HasPreview && !IsBusy && !_renderQueue.IsRunning);
        AutoMouseCropCommand = new AsyncRelayCommand(
            AutoMouseCropAsync,
            () => SelectedJob is not null && SelectedPart is not null && !IsBusy && !_renderQueue.IsRunning);
        ResetMouseCropCommand = new AsyncRelayCommand(
            ResetMouseCropAsync,
            () => Settings.MouseCropEnabled && !IsBusy && !_renderQueue.IsRunning);
        ToggleExistingSubtitleBlurEditCommand = new RelayCommand(
            () =>
            {
                Settings.ExistingSubtitleBlurEnabled = true;
                if (!IsExistingSubtitleBlurEditMode &&
                    Settings.AutoDetectExistingSubtitleRegion)
                {
                    var region = EffectiveExistingSubtitleBlurRegion;
                    Settings.ExistingSubtitleBlurX = region.X;
                    Settings.ExistingSubtitleBlurY = region.Y;
                    Settings.ExistingSubtitleBlurWidth = region.Width;
                    Settings.ExistingSubtitleBlurHeight = region.Height;
                    Settings.AutoDetectExistingSubtitleRegion = false;
                    StatusText =
                        "Đã chuyển sang chỉnh vùng mờ thủ công; kéo khung cyan rồi thả chuột.";
                }
                IsSubtitlePositionEditMode = false;
                IsCustomCropEditMode = false;
                IsMouseCropEditMode = false;
                IsWatermarkPositionEditMode = false;
                IsExistingSubtitleBlurEditMode = !IsExistingSubtitleBlurEditMode;
            },
            () => HasPreview);
        ToggleWatermarkPositionEditCommand = new RelayCommand(
            () =>
            {
                IsSubtitlePositionEditMode = false;
                IsCustomCropEditMode = false;
                IsMouseCropEditMode = false;
                IsExistingSubtitleBlurEditMode = false;
                IsWatermarkPositionEditMode = !IsWatermarkPositionEditMode;
                StatusText = IsWatermarkPositionEditMode
                    ? "Giữ chuột vào logo trên preview, kéo đến vị trí muốn đặt rồi thả."
                    : "Đã tắt chế độ kéo logo.";
            },
            () => HasWatermarkPreview && !IsBusy);
        Settings.PropertyChanged += SettingsChanged;
        _logger.EntryAdded += LoggerOnEntryAdded;
        _renderQueue.StateChanged += QueueOnStateChanged;
        _renderQueue.ProgressChanged += QueueOnProgressChanged;
        _renderQueue.SourcesDeleted += QueueOnSourcesDeleted;
        _watchFolder.VideoReady += WatchFolderOnVideoReady;
        RefreshSavedPresets();
    }

    public RangeObservableCollection<VideoJob> Jobs { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<string> SavedPresetNames { get; } = [];
    public IReadOnlyList<UiOption<LayoutKind>> LayoutOptions { get; } =
    [
        new("Toàn khung", LayoutKind.Original),
        new("Bố cục 35 – 30 – 35", LayoutKind.Layout353035),
        new("50 – 50 • trên rõ, dưới mờ", LayoutKind.Layout5050),
        new("50 – 50 • trên mờ, dưới rõ", LayoutKind.Layout5050TopBlur),
        new("3 dải video • 10 mờ – 52,5 nét – 37,5 mờ", LayoutKind.Layout10525375),
        new("✂ Crop bằng chuột • khung tự chọn", LayoutKind.Custom)
    ];
    public IReadOnlyList<UiOption<MainVideoFitMode>> MainVideoFitOptions { get; } =
    [
        new("Crop theo khuôn mặt – tự căn tâm", MainVideoFitMode.FaceCrop),
        new("Crop giữa – phủ kín, không méo", MainVideoFitMode.Crop),
        new("Giữ toàn bộ hình – đúng tỷ lệ", MainVideoFitMode.Fit),
        new("Kéo đầy vùng – có thể méo", MainVideoFitMode.Stretch)
    ];
    public IReadOnlyList<UiOption<MouseCropOutsideMode>> MouseCropOutsideOptions { get; } =
    [
        new("Làm mờ phần ngoài khung crop", MouseCropOutsideMode.BlurOutside),
        new("Cắt sạch phần ngoài khung crop", MouseCropOutsideMode.CutOutside)
    ];
    public IReadOnlyList<UiOption<WatermarkPosition>> WatermarkPositionOptions { get; } =
    [
        new("Trên trái", WatermarkPosition.TopLeft),
        new("Trên phải", WatermarkPosition.TopRight),
        new("Dưới trái", WatermarkPosition.BottomLeft),
        new("Dưới phải", WatermarkPosition.BottomRight),
        new("Tùy chỉnh – kéo trên preview", WatermarkPosition.Custom)
    ];
    public IReadOnlyList<UiOption<OutputPlatformPreset>> OutputPlatformOptions { get; } =
    [
        new("Tùy chỉnh", OutputPlatformPreset.Custom),
        new("TikTok 9:16", OutputPlatformPreset.TikTok),
        new("Facebook Reels 9:16", OutputPlatformPreset.FacebookReels),
        new("YouTube Shorts 9:16", OutputPlatformPreset.YouTubeShorts)
    ];
    public IReadOnlyList<UiOption<OutputResolutionPreset>> OutputResolutionOptions { get; } =
    [
        new("1080 × 1920 • dọc 9:16 • Full HD", OutputResolutionPreset.FullHd1080x1920),
        new("720 × 1280 • dọc 9:16 • HD nhẹ / nhanh", OutputResolutionPreset.Hd720x1280),
        new("1920 × 1080 • ngang 16:9 • Full HD", OutputResolutionPreset.FullHd1920x1080),
        new("1280 × 720 • ngang 16:9 • HD nhẹ / nhanh", OutputResolutionPreset.Hd1280x720)
    ];
    public IReadOnlyList<UiOption<MergeOrderMode>> MergeOrderOptions { get; } =
    [
        new("Ghép ngẫu nhiên – xáo trộn video", MergeOrderMode.Random),
        new("Ghép video ngắn trước", MergeOrderMode.ShortestFirst),
        new("Theo thứ tự danh sách", MergeOrderMode.InputOrder),
        new("Theo tên file", MergeOrderMode.FileName)
    ];
    public IReadOnlyList<UiOption<MergeTitleMode>> MergeTitleOptions { get; } =
    [
        new("Dãy số ngẫu nhiên", MergeTitleMode.RandomNumber),
        new("Ghép tất cả tiêu đề", MergeTitleMode.AllVideoTitles),
        new("Giữ nguyên tiêu đề (video đầu tiên)", MergeTitleMode.FirstVideo),
        new("Chọn tiêu đề dài nhất", MergeTitleMode.LongestTitle)
    ];
    public IReadOnlyList<UiOption<SplitMode>> SplitOptions { get; } =
    [
        new("Chia theo số phần", SplitMode.PartCount),
        new("Chia theo thời lượng", SplitMode.Duration),
        new("Chia ngẫu nhiên trong khoảng", SplitMode.RandomRange)
    ];
    public IReadOnlyList<UiOption<GpuEncoderKind>> EncoderOptions { get; } =
    [
        new("Tự động nhận GPU", GpuEncoderKind.Auto),
        new("NVIDIA NVENC", GpuEncoderKind.NvidiaNvenc),
        new("AMD AMF", GpuEncoderKind.AmdAmf),
        new("Intel Quick Sync", GpuEncoderKind.IntelQsv),
        new("CPU x264", GpuEncoderKind.CpuX264)
    ];
    public IReadOnlyList<UiOption<string>> SubtitleSourceOptions { get; } =
    [
        new("Tự nhận diện", "auto"),
        new("Tiếng Việt", "vi"),
        new("Tiếng Anh", "en"),
        new("Tiếng Trung", "zh"),
        new("Tiếng Đức", "de"),
        new("Tiếng Hàn Quốc", "ko"),
        new("Tiếng Nhật Bản", "ja"),
        new("Tiếng Tây Ban Nha (Mexico)", "es"),
        new("Tiếng Thái Lan", "th"),
        new("Tiếng Indonesia", "id"),
        new("Tiếng Malaysia (Malay)", "ms"),
        new("Tiếng Philippines (Tagalog)", "tl")
    ];
    public IReadOnlyList<UiOption<string>> SubtitleTargetOptions { get; } =
    [
        new("Tự chọn: Việt ↔ Anh", "auto-translate"),
        new("Giữ nguyên ngôn ngữ nói", "original"),
        new("Dịch sang tiếng Việt", "vi"),
        new("Dịch sang tiếng Anh", "en"),
        new("Dịch sang tiếng Trung", "zh"),
        new("Dịch sang tiếng Đức", "de"),
        new("Dịch sang tiếng Hàn Quốc", "ko"),
        new("Dịch sang tiếng Nhật Bản", "ja"),
        new("Dịch sang tiếng Tây Ban Nha (Mexico)", "es"),
        new("Dịch sang tiếng Thái Lan", "th"),
        new("Dịch sang tiếng Indonesia", "id"),
        new("Dịch sang tiếng Malaysia (Malay)", "ms"),
        new("Dịch sang tiếng Philippines (Tagalog)", "tl")
    ];
    public IReadOnlyList<UiOption<SubtitleOutputMode>> SubtitleOutputOptions { get; } =
    [
        new("Ghi 1 phụ đề vào video", SubtitleOutputMode.BurnIn),
        new("Chỉ tạo file SRT – không ghi lên video", SubtitleOutputMode.SidecarOnly),
        new("Ghi 1 phụ đề vào video + file SRT", SubtitleOutputMode.BurnInAndSidecar),
        new("Ghi 2 dòng: câu gốc + bản dịch + SRT", SubtitleOutputMode.BilingualBurnInAndSidecar)
    ];
    public IReadOnlyList<UiOption<SubtitleDisplayMode>> SubtitleDisplayOptions { get; } =
    [
        new("Hiện theo cả câu", SubtitleDisplayMode.Sentence),
        new("Hiện theo số từ", SubtitleDisplayMode.WordGroups)
    ];
    public IReadOnlyList<ColorOption> ColorOptions { get; } =
    [
        new("Trắng", "#FFFFFFFF"),
        new("Vàng ấm", "#FFFFD54F"),
        new("Vàng chanh", "#FFFFFF00"),
        new("Xanh cyan", "#FF40E0FF"),
        new("Xanh lime", "#FF7CFF6B"),
        new("Cam", "#FFFF9F43"),
        new("Hồng", "#FFFF6FAE"),
        new("Đỏ", "#FFFF5252"),
        new("Tím nhạt", "#FFB388FF"),
        new("Đen", "#FF000000")
    ];
    public IReadOnlyList<ColorOption> FrameColorOptions { get; } =
    [
        new("Đen", "#FF000000"),
        new("Xám than", "#FF263238"),
        new("Xám", "#FF607D8B"),
        new("Trắng", "#FFFFFFFF"),
        new("Đỏ", "#FFE53935"),
        new("Đỏ rượu", "#FF7A1F35"),
        new("Cam", "#FFFF8A3D"),
        new("Vàng", "#FFFFC928"),
        new("Xanh lá", "#FF22A06B"),
        new("Xanh ngọc", "#FF00A7A7"),
        new("Xanh cyan", "#FF00BCD4"),
        new("Xanh dương", "#FF1976D2"),
        new("Xanh navy", "#FF102A43"),
        new("Tím", "#FF6D50E8"),
        new("Tím đậm", "#FF4527A0"),
        new("Hồng", "#FFE84A9B"),
        new("Nâu", "#FF6D4C41")
    ];
    public IReadOnlyList<UiOption<string>> FontWeightOptions { get; } =
    [
        new("Nét thường", "Regular"),
        new("Nét vừa", "Medium"),
        new("Hơi đậm", "SemiBold"),
        new("Đậm", "Bold"),
        new("Rất đậm", "Black")
    ];
    public IReadOnlyList<string> FontOptions { get; } = LoadFontOptions();

    public PresetSettings Settings
    {
        get => _settings;
        set
        {
            if (ReferenceEquals(_settings, value)) return;
            _settings.PropertyChanged -= SettingsChanged;
            NormalizeSettingChoices(value);
            _settings = value;
            _settings.PropertyChanged += SettingsChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitlePreviewLeft));
            OnPropertyChanged(nameof(SubtitlePreviewTop));
            InvalidateQueueSummary();
            OnPropertyChanged(nameof(CustomVideoTop));
            OnPropertyChanged(nameof(CustomVideoHeight));
            OnPropertyChanged(nameof(CustomVideoBottomHandleTop));
            OnPropertyChanged(nameof(CustomVideoRegionText));
            RaiseMouseCropPreviewProperties();
            OnPropertyChanged(nameof(SubtitleModeSummary));
            RefreshWatermarkPreview();
            RaiseExistingSubtitleBlurPreviewProperties();

            // Khi thay toàn bộ preset/project, preview cũ không còn đại diện cho
            // thiết lập mới. Tắt mọi chế độ kéo/chọn và xóa preview để tránh
            // người dùng chỉnh trên một khung hình đã lỗi thời.
            IsSubtitlePositionEditMode = false;
            IsCustomCropEditMode = false;
            IsMouseCropEditMode = false;
            IsExistingSubtitleBlurEditMode = false;
            IsWatermarkPositionEditMode = false;
            PreviewUri = null;
            PreviewOverlayUri = null;

            ApplyWatchFolder();
            // Nhiều CanExecute phụ thuộc trực tiếp vào Settings (Bỏ crop,
            // watermark, phối lại ghép...). Thay object Settings không phát
            // PropertyChanged riêng cho từng thuộc tính, nên phải refresh ở đây.
            RaiseCommands();
        }
    }

    public VideoJob? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            PreviewUri = null;
            PreviewOverlayUri = null;
            SelectedPart = value?.Parts.FirstOrDefault();
            OnPropertyChanged(nameof(IsAutoLandscapePreviewActive));
            RaiseMouseCropPreviewProperties();
            RaiseExistingSubtitleBlurPreviewProperties();
            RaiseCommands();
        }
    }

    public PartPlan? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (!SetProperty(ref _selectedPart, value)) return;
            PreviewUri = null;
            PreviewOverlayUri = null;
            RaiseCommands();
        }
    }

    public Uri? PreviewUri
    {
        get => _previewUri;
        set
        {
            if (!SetProperty(ref _previewUri, value)) return;
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasWatermarkPreview));
            if (value is null)
            {
                IsSubtitlePositionEditMode = false;
                IsCustomCropEditMode = false;
                IsMouseCropEditMode = false;
                IsExistingSubtitleBlurEditMode = false;
                IsWatermarkPositionEditMode = false;
            }
            RaiseCommands();
        }
    }
    public Uri? PreviewOverlayUri { get => _previewOverlayUri; set => SetProperty(ref _previewOverlayUri, value); }
    public LogEntry? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (!SetProperty(ref _selectedLog, value)) return;
            RaiseCommands();
        }
    }
    public string ProjectName { get => _projectName; set => SetProperty(ref _projectName, value); }

    private string _currentFolderName = string.Empty;
    public string CurrentFolderName
    {
        get => string.IsNullOrWhiteSpace(_currentFolderName) ? "Chưa chọn thư mục" : _currentFolderName;
        set => SetProperty(ref _currentFolderName, value);
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string DetectedGpuText { get => _detectedGpuText; set => SetProperty(ref _detectedGpuText, value); }
    public string SubtitleAiStatusText { get => _subtitleAiStatusText; set => SetProperty(ref _subtitleAiStatusText, value); }
    public string SubtitleCacheSizeText { get => _subtitleCacheSizeText; set => SetProperty(ref _subtitleCacheSizeText, value); }
    public string DiskSpaceStatusText { get => _diskSpaceStatusText; set => SetProperty(ref _diskSpaceStatusText, value); }
    public string? SelectedSavedPresetName
    {
        get => _selectedSavedPresetName;
        set
        {
            if (!SetProperty(ref _selectedSavedPresetName, value)) return;
            RaiseCommands();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            InvalidateQueueSummary();
            RaiseCommands();
        }
    }
    public bool IsMetadataAnalysisRunning
    {
        get => _isMetadataAnalysisRunning;
        private set
        {
            if (!SetProperty(ref _isMetadataAnalysisRunning, value)) return;
            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(MergePlanSummary));
            RaiseCommands();
        }
    }
    public bool HasPreview => PreviewUri is not null;
    public bool IsSubtitlePositionEditMode
    {
        get => _isSubtitlePositionEditMode;
        set
        {
            if (!SetProperty(ref _isSubtitlePositionEditMode, value)) return;
            OnPropertyChanged(nameof(SubtitlePositionButtonText));
        }
    }
    public bool IsCustomCropEditMode
    {
        get => _isCustomCropEditMode;
        set
        {
            if (!SetProperty(ref _isCustomCropEditMode, value)) return;
            OnPropertyChanged(nameof(CustomCropButtonText));
        }
    }
    public bool IsMouseCropEditMode
    {
        get => _isMouseCropEditMode;
        set
        {
            if (!SetProperty(ref _isMouseCropEditMode, value)) return;
            OnPropertyChanged(nameof(MouseCropButtonText));
            RaiseMouseCropPreviewProperties();
        }
    }
    public bool IsExistingSubtitleBlurEditMode
    {
        get => _isExistingSubtitleBlurEditMode;
        set
        {
            if (!SetProperty(ref _isExistingSubtitleBlurEditMode, value)) return;
            OnPropertyChanged(nameof(ExistingSubtitleBlurButtonText));
        }
    }
    public bool IsWatermarkPositionEditMode
    {
        get => _isWatermarkPositionEditMode;
        set
        {
            if (!SetProperty(ref _isWatermarkPositionEditMode, value)) return;
            OnPropertyChanged(nameof(WatermarkPositionButtonText));
        }
    }
public string SubtitlePositionButtonText => IsSubtitlePositionEditMode
        ? "✓ Kéo sub"
        : "Kéo sub";
    public string CustomCropButtonText => IsCustomCropEditMode
        ? "✓ Vùng video"
        : "Vùng video";
    public string MouseCropButtonText => IsMouseCropEditMode
        ? "✓ Xong crop / xem kết quả"
        : "✂ Crop bằng chuột";
    public string MouseCropOutsideText => Settings.MouseCropOutside == MouseCropOutsideMode.CutOutside
        ? "cắt sạch ngoài khung"
        : "làm mờ ngoài khung";
    public string MouseCropSummary => Settings.MouseCropEnabled
        ? $"Crop chuột • khung tự chọn {MouseCropAspectText} • {MouseCropOutsideText} • " +
          $"X {Settings.MouseCropXPercent:0.#}% • Y {Settings.MouseCropYPercent:0.#}% • " +
          $"W {Settings.MouseCropWidthPercent:0.#}% • H {Settings.MouseCropHeightPercent:0.#}%"
        : $"Crop chuột: chưa áp dụng • {MouseCropOutsideText} • kéo khung tự do, app giữ nguyên đúng tỷ lệ bạn chọn";
    private (double Width, double Height) SourceAspectPreviewSize
    {
        get
        {
            var width = SelectedJob?.Media.Width ?? 0;
            var height = SelectedJob?.Media.Height ?? 0;
            if (width <= 0 || height <= 0)
                return (Settings.DesignWidth, Settings.DesignHeight);

            const double maxSide = 1920.0;
            if (width >= height)
                return (maxSide, Math.Max(2, maxSide * height / width));
            return (Math.Max(2, maxSide * width / height), maxSide);
        }
    }
    // Preview nguồn ngang tự động, không còn checkbox bật/tắt.
    // Khi đang crop hoặc đã áp crop, nhường preview cho workflow crop/output
    // để lớp crop luôn nhận chuột và nút "Xong crop" xem đúng thành phẩm.
    public bool IsAutoLandscapePreviewActive =>
        !IsMouseCropEditMode &&
        !Settings.MouseCropEnabled &&
        SelectedJob?.Media is { Width: > 0, Height: > 0 } media &&
        media.Width > media.Height;
    public bool ShowPreviewEditOverlayCanvas => !IsAutoLandscapePreviewActive && !IsMouseCropEditMode;
    private bool UseSourceAspectPreviewCanvas => IsMouseCropEditMode || IsAutoLandscapePreviewActive;
    public double PreviewCanvasWidth => UseSourceAspectPreviewCanvas
        ? SourceAspectPreviewSize.Width
        : Settings.DesignWidth;
    public double PreviewCanvasHeight => UseSourceAspectPreviewCanvas
        ? SourceAspectPreviewSize.Height
        : Settings.DesignHeight;
    public double PreviewBannerLeft => Math.Max(0, (PreviewCanvasWidth - 740) / 2);
    public string PreviewAspectText => IsMouseCropEditMode
        ? $"Nguồn {SelectedJob?.Media.Width ?? 0} × {SelectedJob?.Media.Height ?? 0}"
        : IsAutoLandscapePreviewActive
            ? $"Nguồn ngang • {SelectedJob?.Media.Width ?? 0} × {SelectedJob?.Media.Height ?? 0}"
            : Settings.OutputResolutionText;
    public string MouseCropAspectText
    {
        get
        {
            var ratio = Math.Clamp(
                Settings.MouseCropSourceAspect *
                Math.Max(0.01, Settings.MouseCropWidthPercent) /
                Math.Max(0.01, Settings.MouseCropHeightPercent),
                0.05,
                20);
            var common = new (int W, int H)[]
            {
                (9, 16), (1, 1), (4, 5), (3, 4), (2, 3), (4, 3), (16, 9)
            };
            foreach (var item in common)
            {
                if (Math.Abs(ratio - item.W / (double)item.H) <= 0.02)
                    return $"{item.W}:{item.H}";
            }
            return $"{ratio:0.###}:1";
        }
    }
    public double MainVideoPreviewTop
    {
        get
        {
            if (IsMouseCropEditMode) return 0;
            var height = Settings.DesignHeight;
            return Settings.Layout switch
            {
                LayoutKind.Layout5050TopBlur => height * 0.5,
                LayoutKind.Layout10525375 => height * 0.10,
                LayoutKind.Layout353035 => height * 0.35,
                LayoutKind.Custom => Settings.CustomMainTopPixels,
                _ => 0
            };
        }
    }
    public double MainVideoPreviewWidth => IsMouseCropEditMode
        ? PreviewCanvasWidth
        : Settings.DesignWidth;
    public double MainVideoPreviewHeight
    {
        get
        {
            if (IsMouseCropEditMode) return PreviewCanvasHeight;
            var height = Settings.DesignHeight;
            return Settings.Layout switch
            {
                LayoutKind.Layout5050 => height * 0.5,
                LayoutKind.Layout5050TopBlur => height * 0.5,
                LayoutKind.Layout10525375 => height * 0.525,
                LayoutKind.Layout353035 => height * 0.30,
                LayoutKind.Custom => Settings.CustomMainHeightPixels,
                _ => height
            };
        }
    }
    public double MouseCropPreviewLeft => MainVideoPreviewWidth * Settings.MouseCropXPercent / 100;
    public double MouseCropPreviewTop => MainVideoPreviewHeight * Settings.MouseCropYPercent / 100;
    public double MouseCropPreviewWidth => MainVideoPreviewWidth * Math.Min(
        Settings.MouseCropWidthPercent,
        100 - Settings.MouseCropXPercent) / 100;
    public double MouseCropPreviewHeight => MainVideoPreviewHeight * Math.Min(
        Settings.MouseCropHeightPercent,
        100 - Settings.MouseCropYPercent) / 100;
public string ExistingSubtitleBlurButtonText => IsExistingSubtitleBlurEditMode
        ? "✓ Chọn mờ sub"
        : "Chọn mờ sub";
    public string WatermarkPositionButtonText => IsWatermarkPositionEditMode
        ? "✓ Đang kéo logo"
        : "Kéo logo";
    public ImageSource? WatermarkPreviewSource
    {
        get => _watermarkPreviewSource;
        private set
        {
            if (!SetProperty(ref _watermarkPreviewSource, value)) return;
            OnPropertyChanged(nameof(HasWatermarkPreview));
            RaiseWatermarkPreviewProperties();
            RaiseCommands();
        }
    }
    public bool HasWatermarkPreview =>
        HasPreview &&
        Settings.WatermarkEnabled &&
        WatermarkPreviewSource is not null;
    public double WatermarkPreviewWidth
    {
        get
        {
            var requestedWidth = Settings.DesignWidth * Settings.WatermarkWidthPercent / 100;
            var requestedHeight = requestedWidth /
                                  Math.Max(0.05, _watermarkPreviewAspectRatio);
            var maxHeight = Settings.DesignHeight * 0.80;
            return requestedHeight <= maxHeight
                ? requestedWidth
                : maxHeight * _watermarkPreviewAspectRatio;
        }
    }
    public double WatermarkPreviewHeight =>
        Math.Min(Settings.DesignHeight * 0.80, WatermarkPreviewWidth /
            Math.Max(0.05, _watermarkPreviewAspectRatio));
    public double WatermarkPreviewLeft
    {
        get
        {
            var available = Math.Max(0, Settings.DesignWidth - WatermarkPreviewWidth);
            if (Settings.WatermarkPosition == WatermarkPosition.Custom)
                return available * Settings.WatermarkXPercent / 100;
            return Settings.WatermarkPosition is WatermarkPosition.TopRight or
                WatermarkPosition.BottomRight
                ? Math.Max(0, available - Settings.WatermarkMargin)
                : Math.Min(available, Settings.WatermarkMargin);
        }
    }
    public double WatermarkPreviewTop
    {
        get
        {
            var available = Math.Max(0, Settings.DesignHeight - WatermarkPreviewHeight);
            if (Settings.WatermarkPosition == WatermarkPosition.Custom)
                return available * Settings.WatermarkYPercent / 100;
            return Settings.WatermarkPosition is WatermarkPosition.BottomLeft or
                WatermarkPosition.BottomRight
                ? Math.Max(0, available - Settings.WatermarkMargin)
                : Math.Min(available, Settings.WatermarkMargin);
        }
    }
    private ExistingSubtitleBlurRegion EffectiveExistingSubtitleBlurRegion =>
        ExistingSubtitleDetectionEngine.GetEffectiveRegion(SelectedJob, Settings);
    public double ExistingSubtitleBlurPreviewX =>
        EffectiveExistingSubtitleBlurRegion.X;
    public double ExistingSubtitleBlurPreviewY =>
        EffectiveExistingSubtitleBlurRegion.Y;
    public double ExistingSubtitleBlurPreviewWidth =>
        EffectiveExistingSubtitleBlurRegion.Width;
    public double ExistingSubtitleBlurPreviewHeight =>
        EffectiveExistingSubtitleBlurRegion.Height;
    public string ExistingSubtitleBlurRegionLabel =>
        Settings.AutoDetectExistingSubtitleRegion &&
        SelectedJob?.DetectedExistingSubtitleBlurRegion is not null
            ? "VÙNG SUB TỰ NHẬN DIỆN"
            : "VÙNG LÀM MỜ SUB CŨ";
    public string ExistingSubtitleDetectionStatusText
    {
        get
        {
            if (!Settings.AutoDetectExistingSubtitleRegion)
                return "Đang dùng tọa độ thủ công.";
            if (SelectedJob?.DetectedExistingSubtitleBlurRegion is { } region)
                return $"Đã nhận vùng: X {region.X}, Y {region.Y}, " +
                       $"{region.Width} × {region.Height} px • tin cậy {region.Confidence:P0}.";
            if (SelectedJob?.ExistingSubtitleDetectionCompleted == true)
                return "Không thấy vùng chữ ổn định; app sẽ dùng tọa độ thủ công.";
            return "Sẽ lấy mẫu nhiều khung hình và tự tìm vùng chữ ở phần dưới video.";
        }
    }
    public double SubtitlePreviewLeft =>
        Math.Max(0, (Settings.DesignWidth - 600) / 2.0 + Settings.SubtitleHorizontalOffset);
    public double SubtitlePreviewTop =>
        Math.Max(0, Settings.DesignHeight - 90 - Settings.SubtitleBottomMargin);
    public double CustomVideoHeight => Settings.CustomMainHeightPixels;
    public double CustomVideoTop => Settings.CustomMainTopPixels;
    public double CustomVideoBottomHandleTop => CustomVideoTop + CustomVideoHeight - 12;
    public string CustomVideoRegionText =>
        $"VÙNG VIDEO {Settings.CustomMainPercent:0.#}% • {Settings.CustomMainHeightPixels} px • " +
        $"Y {Settings.CustomMainTopPixels}px • tâm crop X {Settings.CropFocusXPercent:0} / Y {Settings.CropFocusYPercent:0}";
    public string SubtitleModeSummary
    {
        get
        {
            if (!Settings.AutoSubtitlesEnabled)
                return "Phụ đề đang TẮT.";
            var output = SubtitleOutputOptions
                .FirstOrDefault(option => option.Value == Settings.SubtitleOutput)?.Name
                ?? "Chưa chọn cách xuất";
            var source = SubtitleSourceOptions
                .FirstOrDefault(option => option.Value == Settings.SubtitleSourceLanguage)?.Name
                ?? Settings.SubtitleSourceLanguage;
            var target = SubtitleTargetOptions
                .FirstOrDefault(option => option.Value == Settings.SubtitleTargetLanguage)?.Name
                ?? Settings.SubtitleTargetLanguage;
            var merge = Settings.MergeVideosEnabled ? " • áp dụng cho cả video ghép" : "";
            return $"{output} • {source} → {target}{merge}";
        }
    }
    public bool IsRendering => _renderQueue.IsRunning;
    public string PauseButtonText => _renderQueue.IsPaused ? "Tiếp tục" : "Tạm dừng";
    public double OverallProgressPercent =>
        Math.Clamp(_currentQueueProgress?.Percent ?? 0, 0, 100);
    public string OverallProgressPercentText =>
        $"{OverallProgressPercent:0.0}%";
    public string OverallProgressStageText =>
        _currentQueueProgress?.Stage ?? "Đang chuẩn bị render";
    public string OverallProgressDetailsText
    {
        get
        {
            if (_currentQueueProgress is not { } progress)
                return "Đang khởi tạo tiến trình…";
            var succeeded = Math.Max(0, progress.Completed - progress.Failed);
            var isSubtitleStage = progress.Stage.StartsWith(
                "Phụ đề AI",
                StringComparison.OrdinalIgnoreCase);
            return (isSubtitleStage
                       ? $"Đã xử lý AI {succeeded:N0}/{progress.Total:N0} video"
                       : $"Đã xong {succeeded:N0}/{progress.Total:N0} video") +
                   (progress.Active > 0
                       ? isSubtitleStage
                           ? " • AI đang chạy"
                           : $" • đang chạy {progress.Active:N0}"
                       : "") +
                   (progress.Failed > 0
                       ? $" • lỗi {progress.Failed:N0}"
                       : "");
        }
    }
    public string OverallProgressEtaText
    {
        get
        {
            if (_renderQueue.IsPaused) return "Đã tạm dừng";
            if (_currentQueueProgress is not { } progress ||
                _queueProgressStartedAt is not { } startedAt ||
                progress.Percent < 0.5)
                return "Đang tính thời gian còn lại…";
            if (progress.Percent >= 99.95) return "Đang hoàn tất…";

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            if (elapsed.TotalSeconds < 3)
                return "Đang tính thời gian còn lại…";
            var remainingSeconds = elapsed.TotalSeconds *
                                   (100 - progress.Percent) /
                                   progress.Percent;
            return $"Còn khoảng {FormatQueueDuration(remainingSeconds)}";
        }
    }
    public string QueueSummary
    {
        get
        {
            if (IsMetadataAnalysisRunning)
            {
                var remaining = Math.Max(
                    0,
                    _metadataAnalysisTotal - _metadataAnalysisCompleted);
                return $"{Jobs.Count:N0} video • đã đọc {_metadataAnalysisCompleted:N0}/" +
                       $"{_metadataAnalysisTotal:N0} • còn {remaining:N0}" +
                       (_metadataAnalysisFailed > 0
                           ? $" • lỗi {_metadataAnalysisFailed:N0}"
                           : "");
            }
            if (Settings.MergeVideosEnabled)
            {
                if (_renderQueue.IsRunning && _currentQueueProgress is not null)
                {
                    var remaining = Math.Max(
                        0,
                        _currentQueueProgress.Total -
                        _currentQueueProgress.Completed);
                    return $"{Jobs.Count} video nguồn còn lại • " +
                           $"{remaining} video ghép đang chờ" +
                           (_currentQueueProgress.Failed > 0
                               ? $" • {_currentQueueProgress.Failed} lỗi"
                               : "");
                }
                return IsBusy
                    ? $"{Jobs.Count} video nguồn • đang đọc thông tin…"
                    : _cachedMergePlans is null
                        ? $"{Jobs.Count} video nguồn • đang lập kế hoạch ghép…"
                        : $"{Jobs.Count} video nguồn • {_cachedMergePlans.Count} video ghép";
            }
            return $"{Jobs.Count} video • {Jobs.Sum(x => x.Parts.Count)} phần";
        }
    }
    public string MergePlanSummary
    {
        get
        {
            if (Jobs.Count == 0) return "Hãy chọn thư mục hoặc thêm các video cần ghép.";
            if (IsMetadataAnalysisRunning)
                return $"Đã nạp {Jobs.Count:N0} video • đang đọc thời lượng nền " +
                       $"{_metadataAnalysisCompleted:N0}/{_metadataAnalysisTotal:N0}; " +
                       "có thể chuyển tab và tiếp tục chỉnh thiết lập.";
            if (IsBusy)
                return $"Đã đưa {Jobs.Count} video vào danh sách • metadata đang được đọc nền.";
            if (_cachedMergePlans is null)
                return $"Đã đọc {Jobs.Count} video • đang lập kế hoạch ghép ở nền…";
            var plans = _cachedMergePlans;
            if (plans.Count == 0)
                return Settings.MergeVideosPerOutput > 0
                    ? $"Không đủ video hợp lệ để tạo nhóm {Settings.MergeVideosPerOutput} clip."
                    : "Không có video hợp lệ để lập kế hoạch ghép theo thời lượng.";

            if (Settings.AutoShuffleBatchEnabled)
            {
                var modeText = Settings.MergeVideosPerOutput > 0
                    ? $"{Settings.MergeVideosPerOutput} clip/video"
                    : $"{FormatDuration(Math.Min(Settings.MergeMinSeconds, Settings.MergeMaxSeconds))}–" +
                      $"{FormatDuration(Math.Max(Settings.MergeMinSeconds, Settings.MergeMaxSeconds))}/video";
                var usedSources = plans
                    .SelectMany(plan => plan.Segments)
                    .Select(segment => segment.Job.Id)
                    .Distinct()
                    .Count();
                var fastText = Settings.AutoShuffleStreamCopyEnabled
                    ? " • ⚡ STREAM COPY • không render lại"
                    : " • render/edit bình thường";
                var autoShuffleOrderText = MergeOrderOptions.First(
                    option => option.Value == Settings.MergeOrder).Name;
                var firstPassCount = Math.Min(usedSources, Settings.AutoShuffleTargetCount);
                var firstPassText = Settings.MergeOrder switch
                {
                    MergeOrderMode.InputOrder =>
                        $" • clip đầu lượt #1–#{firstPassCount:N0} bắt buộc theo đúng thứ tự danh sách",
                    MergeOrderMode.Random =>
                        $" • clip đầu lượt #1–#{firstPassCount:N0} xáo ngẫu nhiên nhưng không lặp nguồn",
                    _ =>
                        $" • clip đầu lượt #1–#{firstPassCount:N0} đi hết nguồn ({autoShuffleOrderText})"
                };
                return $"TỰ XÀO • {plans.Count:N0}/{Settings.AutoShuffleTargetCount:N0} video đầu ra" +
                       $" • {modeText} • dùng {usedSources:N0}/{Jobs.Count:N0} nguồn" +
                       firstPassText + fastText +
                       " • các vòng sau xào đều, hạn chế lặp sát nhau • không xóa nguồn";
            }

            var total = plans.Sum(x => x.DurationSeconds);
            var durations = plans.Count <= 6
                ? string.Join(", ", plans.Select(x => FormatDuration(x.DurationSeconds)))
                : $"{FormatDuration(plans.Min(x => x.DurationSeconds))}–{FormatDuration(plans.Max(x => x.DurationSeconds))}";
            var usedJobCount = plans
                .SelectMany(plan => plan.Segments)
                .Select(segment => segment.Job.Id)
                .Distinct()
                .Count();
            var countMode = Settings.MergeVideosPerOutput > 0;
            var maximum = Math.Max(Settings.MergeMinSeconds, Settings.MergeMaxSeconds);
            var speed = Math.Clamp(Settings.VideoSpeed, 0.1, 10);
            var longClipCount = countMode
                ? 0
                : Jobs.Count(job =>
                    job.Media.DurationSeconds / speed > maximum + 0.05);
            var remainderCount =
                Math.Max(0, Jobs.Count - usedJobCount - longClipCount);
            var editText = longClipCount > 0
                ? $" • {longClipCount} clip dài chuyển sang Edit"
                : "";
            var skippedText = remainderCount > 0
                ? countMode
                    ? $" • bỏ qua {remainderCount} clip dư chưa đủ nhóm"
                    : $" • bỏ qua {remainderCount} clip dư không đủ thời lượng"
                : "";
            var speedText = Math.Abs(speed - 1) > 0.0001
                ? $" • đã tính tốc độ {speed:0.##}×"
                : "";
            var orderText = MergeOrderOptions.First(
                option => option.Value == Settings.MergeOrder).Name;
            var countText = Settings.MergeVideosPerOutput > 0
                ? $" • đúng {Settings.MergeVideosPerOutput} clip/video ghép"
                : " • số clip tự động theo thời lượng";
            return $"Tổng {FormatDuration(total)} • dự kiến {plans.Count} video: " +
                   $"{durations}{speedText}{countText} • {orderText}{editText}{skippedText}";
        }
    }
    public string MergePlanDetailsText
    {
        get
        {
            if (_cachedMergePlans is null || _cachedMergePlans.Count == 0)
                return "";
            var lines = _cachedMergePlans.Take(12).Select(plan =>
                $"#{plan.Index}: " +
                string.Join("  →  ", plan.Segments.Select(segment =>
                    Path.GetFileNameWithoutExtension(segment.Job.InputPath))));
            var suffix = _cachedMergePlans.Count > 12
                ? $"{Environment.NewLine}… và {_cachedMergePlans.Count - 12:N0} kế hoạch khác"
                : "";
            return string.Join(Environment.NewLine, lines) + suffix;
        }
    }

    public ICommand PickFolderCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand AddVideosCommand { get; }
    public ICommand RefreshListCommand { get; }
    public ICommand ChooseOutputCommand { get; }
    public ICommand OpenLatestOutputCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand CopySelectedLogCommand { get; }
    public ICommand CopyAllErrorsCommand { get; }
    public ICommand ChooseBackgroundImageCommand { get; }
    public ICommand ChooseWatermarkImageCommand { get; }
    public ICommand ClearWatermarkImageCommand { get; }
    public ICommand ChooseIntroImageCommand { get; }
    public ICommand ChooseOutroImageCommand { get; }
    public ICommand ChooseManualSubtitleCommand { get; }
    public ICommand EditManualSubtitleCommand { get; }
    public ICommand ClearManualSubtitleCommand { get; }
    public ICommand SetBlurZoomCommand { get; }
    public ICommand SetCustomRatioCommand { get; }
    public ICommand ResetVideoEffectsCommand { get; }
    public ICommand SetLightShakePresetCommand { get; }
    public ICommand ResetAudioEffectsCommand { get; }
    public ICommand SetBrightVoicePresetCommand { get; }
    public ICommand SetWarmVoicePresetCommand { get; }
    public ICommand SetBottomSubtitleBlurPresetCommand { get; }
    public ICommand RebuildPartsCommand { get; }
    public ICommand SmartTrimSelectedCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CancelMetadataAnalysisCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand PreviewStartCommand { get; }
    public ICommand PreviewMiddleCommand { get; }
    public ICommand PreviewEndCommand { get; }
    public ICommand DetectExistingSubtitleBlurCommand { get; }
    public ICommand ThumbnailsCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand PermanentlyDeleteSelectedCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand ApplyPlatformPresetCommand { get; }
    public ICommand RegenerateMergePlanCommand { get; }
    public ICommand SavePresetCommand { get; }
    public ICommand LoadPresetCommand { get; }
    public ICommand DeletePresetCommand { get; }
    public ICommand InstallSubtitleAiCommand { get; }
    public ICommand RefreshSubtitleAiCommand { get; }
    public ICommand RefreshSubtitleCacheCommand { get; }
    public ICommand ClearSelectedSubtitleCacheCommand { get; }
    public ICommand ClearAllSubtitleCacheCommand { get; }
    public ICommand ToggleSubtitlePositionEditCommand { get; }
    public ICommand ToggleCustomCropEditCommand { get; }
    public ICommand ToggleMouseCropEditCommand { get; }
    public ICommand AutoMouseCropCommand { get; }
    public ICommand ResetMouseCropCommand { get; }
    public ICommand ToggleExistingSubtitleBlurEditCommand { get; }
    public ICommand ToggleWatermarkPositionEditCommand { get; }

    private async Task ChangePasswordAsync()
    {
        var window = new ChangePasswordWindow(_auth) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        await Task.CompletedTask;
    }

    private void Logout()
    {
        if (_renderQueue.IsRunning)
        {
            MessageBox.Show(
                "Đang render nên chưa thể đăng xuất. Hãy dừng hoặc đợi render xong.",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _auth.LogoutLocal();
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Warning("Không tự mở lại màn đăng nhập: " + ex.Message);
        }
        Application.Current.Shutdown();
    }

    public async Task InitializeAsync()
    {
        StatusText = "Đang kiểm tra FFmpeg…";
        var locator = new FfmpegLocator(_logger);
        var ffmpegPath = locator.ResolveFfmpeg(Settings);
        var ffprobePath = locator.ResolveFfprobe(Settings);
        _ffmpegReady =
            File.Exists(ffmpegPath) &&
            File.Exists(ffprobePath);

        // Bản cài luôn mang theo FFmpeg/FFprobe. Khi hai file đã tồn tại thì
        // không cần khởi chạy tiến trình kiểm tra ở lúc mở app.
        // Chỉ dùng probe có timeout nếu người dùng cấu hình FFmpeg từ PATH.
        if (!_ffmpegReady)
        {
            using var ffmpegCheck =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ffmpegCheck.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                _ffmpegReady = await Task.Run(
                    () => locator.ValidateAsync(Settings, ffmpegCheck.Token),
                    ffmpegCheck.Token);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                _ffmpegReady = false;
                _logger.Warning("Kiểm tra FFmpeg quá thời gian; app tiếp tục mở.");
            }
        }

        if (_ffmpegReady)
        {
            // GPU được nhận ngay trong worker khi bấm Render. Không dò NVENC,
            // AMF hay QSV lúc mở app để startup không thể giữ luồng giao diện.
            DetectedGpuText = "GPU tự nhận khi bấm Render";
        }
        else DetectedGpuText = "Không tìm thấy FFmpeg";

        StatusText = "Đang kiểm tra bộ phụ đề AI…";
        try
        {
            var subtitleInfo = await Task.Run(
                () => (
                    Status: _subtitles.GetStatusText(),
                    CacheSize: _subtitles.GetCacheSizeText()),
                _lifetime.Token);
            SubtitleAiStatusText = subtitleInfo.Status;
            SubtitleCacheSizeText = subtitleInfo.CacheSize;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Không đọc được trạng thái phụ đề AI: " + ex.Message);
        }

        if (Settings.AutoResumeProject)
        {
            StatusText = "Đang khôi phục danh sách video đã lưu…";
            var state = await _projects.LoadAutoAsync(_lifetime.Token);
            if (state is not null && state.Jobs.Count > 0)
            {
                StatusText =
                    $"Đang khôi phục {state.Jobs.Count:N0} video và kế hoạch ghép…";
                await LoadStateAsync(state, _lifetime.Token);
                _logger.Info("Đã khôi phục danh sách video từ bản tự lưu. Hãy bấm Bắt đầu Render khi sẵn sàng.");
            }
        }
        if (!IsMetadataAnalysisRunning)
            StatusText = _ffmpegReady ? "Sẵn sàng" : "Thiếu FFmpeg/FFprobe";
        RefreshDiskSpaceStatus();
        RaiseCommands();
    }

    public async Task AddDroppedAsync(IEnumerable<string> paths)
    {
        var droppedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var droppedFolders = droppedPaths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (droppedFolders.Length > 0)
        {
            Settings.InputFolders = Settings.InputFolders
                .Append(Settings.InputFolder)
                .Concat(droppedFolders)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!Directory.Exists(Settings.InputFolder))
                Settings.InputFolder = droppedFolders[0];
                CurrentFolderName = new DirectoryInfo(droppedFolders[0]).Name;
        }
        await AddPathsAsync(droppedPaths);
    }

    private async Task PickFolderAsync()
    {
        var initial = Settings.InputFolders
                          .FirstOrDefault(Directory.Exists)
                      ?? Settings.InputFolder;
        var folders = _dialogs.PickFolders(
            "Chọn một hoặc nhiều thư mục video",
            initial);
        if (folders.Count == 0) return;

        Settings.InputFolders = Settings.InputFolders
            .Append(Settings.InputFolder)
            .Concat(folders)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Settings.InputFolder = folders[0];
        CurrentFolderName = new DirectoryInfo(folders[0]).Name;
        await AddPathsAsync(folders);
    }

    private async Task AddVideosAsync()
    {
        var files = _dialogs.PickVideos();
        if (files.Count > 0) await AddPathsAsync(files);
    }

    private async Task RefreshListAsync()
    {
        if (_renderQueue.IsRunning || IsBusy || IsMetadataAnalysisRunning)
            return;
        var selectedPath = SelectedJob?.InputPath;
        var roots = Settings.InputFolders
            .Append(Settings.InputFolder)
            .Where(Directory.Exists)
            .Concat(Jobs.Select(job => job.InputPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => TryGetFullPath(path))
            .Where(path => path is not null)
            .Select(path => path!)
            .Where(path => !IsUnsafeListScanRoot(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IsBusy = true;
        StatusText = "Đang quét nhanh tên file trong thư mục…";
        var addedCount = 0;
        var removedCount = 0;
        try
        {
            var includeSubfolders = Settings.IncludeSubfolders;
            var paths = await Task.Run(
                () => _input.Discover(roots, includeSubfolders),
                _lifetime.Token);
            if (paths.Count == 0 && Jobs.Count == 0)
            {
                StatusText = "Chưa có thư mục hoặc video để cập nhật.";
                return;
            }

            var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = Jobs
                .Where(job => !pathSet.Contains(job.InputPath))
                .ToArray();
            removedCount = missing.Length;
            Jobs.RemoveRange(missing);

            var existing = Jobs
                .Select(job => job.InputPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newJobs = paths
                .Where(existing.Add)
                .Select(path => new VideoJob
                {
                    InputPath = path,
                    Status = JobStatus.Analyzing,
                    ProcessingStage = "Chờ đọc thông tin"
                })
                .ToArray();
            Jobs.AddRange(newJobs);
            addedCount = newJobs.Length;

            SelectedJob = Jobs.FirstOrDefault(job =>
                              string.Equals(job.InputPath, selectedPath,
                                  StringComparison.OrdinalIgnoreCase))
                          ?? Jobs.FirstOrDefault();
            PreviewUri = null;
            PreviewOverlayUri = null;
            InvalidateQueueSummary();

            StatusText = $"Đã thấy {Jobs.Count} video • đang kiểm tra file thay đổi…";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var jobSnapshot = Jobs.ToArray();
            var jobsToAnalyze = await Task.Run(
                () => jobSnapshot
                    .Where(job => !VideoMetadataCache.IsCurrent(job))
                    .ToArray(),
                _lifetime.Token);

            SelectedPart = SelectedJob?.Parts.FirstOrDefault();
            InvalidateQueueSummary();
            await AutoSaveAsync();

            var completionLabel =
                $"Đã cập nhật {Jobs.Count:N0} video • thêm {addedCount:N0} • " +
                $"bỏ file mất {removedCount:N0}";
            if (jobsToAnalyze.Length > 0)
            {
                StartMetadataAnalysis(
                    jobsToAnalyze,
                    "Đang đọc thông tin",
                    completionLabel);
            }
            else
            {
                StatusText = completionLabel + " • metadata không thay đổi";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã dừng cập nhật danh sách.";
        }
        catch (Exception ex)
        {
            _logger.Error("Cập nhật danh sách: " + ex.Message);
            StatusText = "Không cập nhật được danh sách: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommands();
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_renderQueue.IsRunning || IsBusy || IsMetadataAnalysisRunning)
            return;
        var requestedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (requestedPaths.Length == 0) return;

        IsBusy = true;
        StatusText = "Đang quét nhanh tên file trong thư mục…";
        try
        {
            var includeSubfolders = Settings.IncludeSubfolders;
            var discovered = await Task.Run(
                () => _input.Discover(requestedPaths, includeSubfolders),
                _lifetime.Token);
            var existingPaths = Jobs
                .Select(job => job.InputPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newJobs = discovered
                .Where(existingPaths.Add)
                .Select(path => new VideoJob
                {
                    InputPath = path,
                    Status = JobStatus.Analyzing,
                    ProcessingStage = "Chờ đọc thông tin"
                })
                .ToArray();
            if (newJobs.Length == 0)
            {
                StatusText = "Các video trong thư mục đã có sẵn trong danh sách.";
                return;
            }

            Jobs.AddRange(newJobs);
            SelectedJob ??= newJobs[0];
            InvalidateQueueSummary();
            StatusText =
                $"Đã đưa {newJobs.Length:N0} video vào danh sách • đang lưu danh sách nhanh…";
            await Dispatcher.Yield(DispatcherPriority.Background);

            await AutoSaveAsync();
            StartMetadataAnalysis(
                newJobs,
                "Đang đọc thông tin",
                $"Đã nạp {newJobs.Length:N0} video");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã dừng nạp danh sách.";
        }
        catch (Exception ex)
        {
            _logger.Error("Nạp danh sách: " + ex.Message);
            StatusText = "Không nạp được danh sách: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommands();
        }
    }

    private void StartMetadataAnalysis(
        IReadOnlyList<VideoJob> jobs,
        string progressLabel,
        string completionLabel)
    {
        if (jobs.Count == 0 || IsMetadataAnalysisRunning) return;

        var source = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _metadataAnalysisSource = source;
        _metadataAnalysisTotal = jobs.Count;
        _metadataAnalysisCompleted = 0;
        _metadataAnalysisFailed = 0;
        IsMetadataAnalysisRunning = true;
        _logger.Info(
            $"Đọc metadata nền: {jobs.Count:N0} video • " +
            $"{MetadataProbeConcurrency} luồng FFprobe • tự lưu mỗi " +
            $"{MetadataCheckpointIntervalSeconds} giây.");
        _ = RunMetadataAnalysisAsync(
            jobs.ToArray(),
            progressLabel,
            completionLabel,
            source);
    }

    private async Task RunMetadataAnalysisAsync(
        IReadOnlyList<VideoJob> jobs,
        string progressLabel,
        string completionLabel,
        CancellationTokenSource source)
    {
        try
        {
            var analyzed = await AnalyzeJobsAsync(
                jobs,
                progressLabel,
                source.Token);
            SelectedPart = SelectedJob?.Parts.FirstOrDefault();
            await AutoSaveAsync();
            StatusText = completionLabel +
                         $" • đã đọc {analyzed.Analyzed:N0} metadata" +
                         (analyzed.RemovedNoAudio > 0
                             ? $" • đã đưa {analyzed.RemovedNoAudio:N0} video không tiếng vào Thùng rác"
                             : "") +
                         (analyzed.RemovedInvalid > 0
                             ? $" • đã đưa {analyzed.RemovedInvalid:N0} video lỗi/rỗng vào Thùng rác"
                             : "") +
                         (analyzed.Failed > 0
                             ? $" • lỗi {analyzed.Failed:N0}"
                             : "");
            _logger.Success(StatusText);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            await SaveMetadataCheckpointSafeAsync();
            StatusText =
                $"Đã dừng đọc metadata tại {_metadataAnalysisCompleted:N0}/" +
                $"{_metadataAnalysisTotal:N0} video và lưu tiến độ. " +
                "Bấm Cập nhật danh sách để tiếp tục.";
            _logger.Warning(StatusText);
        }
        catch (Exception ex)
        {
            await SaveMetadataCheckpointSafeAsync();
            StatusText =
                $"Đọc metadata bị gián đoạn tại {_metadataAnalysisCompleted:N0}/" +
                $"{_metadataAnalysisTotal:N0}: {ex.Message}";
            _logger.Error("Đọc metadata nền: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_metadataAnalysisSource, source))
            {
                _metadataAnalysisSource = null;
                IsMetadataAnalysisRunning = false;
            }
            source.Dispose();
            InvalidateQueueSummary();
            StartWatchFolderDrain();
        }
    }

    private void CancelMetadataAnalysis()
    {
        if (!IsMetadataAnalysisRunning) return;
        StatusText = "Đang dừng FFprobe và lưu phần metadata đã đọc…";
        _metadataAnalysisSource?.Cancel();
        RaiseCommands();
    }

    private async Task SaveMetadataCheckpointSafeAsync()
    {
        try
        {
            await AutoSaveAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Ứng dụng đang đóng.
        }
        catch (Exception ex)
        {
            _logger.Warning("Không lưu được tiến độ metadata: " + ex.Message);
        }
    }

    private async Task<(int Analyzed, int Failed, int RemovedNoAudio, int RemovedInvalid)> AnalyzeJobsAsync(
        IReadOnlyList<VideoJob> jobs,
        string progressLabel,
        CancellationToken token)
    {
        if (jobs.Count == 0) return (0, 0, 0, 0);

        var indexes = Jobs
            .Select((job, index) => (job.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index);
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Analyzing;
            job.ProcessingStage = "Chờ FFprobe";
            job.Error = "";
        }

        // Chỉ tạo một nhóm FFprobe cố định. Task.Run bảo đảm toàn bộ
        // Process.Start/đọc JSON và continuation của FFprobe không chiếm UI.
        // Không tạo sẵn hàng chục nghìn Task cho thư mục rất lớn.
        var nextJobIndex = 0;
        Task<VideoAnalysisResult> StartWorker(VideoJob job) =>
            Task.Run(
                () => AnalyzeOneJobAsync(
                    job,
                    indexes.GetValueOrDefault(job.Id),
                    token),
                token);
        var pending = new List<Task<VideoAnalysisResult>>(
            Math.Min(MetadataProbeConcurrency, jobs.Count));
        while (nextJobIndex < jobs.Count &&
               pending.Count < MetadataProbeConcurrency)
            pending.Add(StartWorker(jobs[nextJobIndex++]));
        var analyzedCount = 0;
        var failedCount = 0;
        var removedNoAudioCount = 0;
        var removedInvalidCount = 0;
        var progressTimer = Stopwatch.StartNew();
        var statusTimer = Stopwatch.StartNew();
        var checkpointTimer = Stopwatch.StartNew();

        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                var result = await finished;
                if (nextJobIndex < jobs.Count)
                    pending.Add(StartWorker(jobs[nextJobIndex++]));
                analyzedCount++;

                if (result.RemoveFromQueue)
                {
                    if (result.RemovedBecauseNoAudio)
                        removedNoAudioCount++;
                    if (result.RemovedBecauseInvalid)
                        removedInvalidCount++;
                    var selectedIndex = Jobs.IndexOf(result.Job);
                    var wasSelected = ReferenceEquals(result.Job, SelectedJob);
                    Jobs.Remove(result.Job);
                    if (wasSelected)
                        SelectedJob = Jobs.Count == 0
                            ? null
                            : Jobs[Math.Clamp(selectedIndex, 0, Jobs.Count - 1)];
                }
                else if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    failedCount++;
                    result.Job.Status = JobStatus.Failed;
                    result.Job.ProcessingStage = "Lỗi đọc thông tin";
                    result.Job.Error = result.Error;
                    _logger.Error($"{result.Job.FileName}: {result.Error}");
                }
                else
                {
                    result.Job.Media = result.Media;
                    result.Job.SourceLength = result.SourceLength;
                    result.Job.SourceLastWriteUtcTicks = result.SourceLastWriteUtcTicks;
                    result.Job.Parts.Clear();
                    foreach (var part in _split.Build(
                                 result.Media.DurationSeconds,
                                 Settings,
                                 result.Job.InputPath.GetHashCode()))
                        result.Job.Parts.Add(part);
                    result.Job.CleanTitle = await _titleSource.GetTitleAsync(
                        result.Job,
                        result.QueueIndex,
                        Settings,
                        token);
                    result.Job.Status = JobStatus.Ready;
                    result.Job.Progress = 0;
                    result.Job.RenderFps = 0;
                    result.Job.Eta = "—";
                    result.Job.Worker = "—";
                    result.Job.Gpu = "—";
                    result.Job.Error = "";
                    result.Job.ProcessingStage = "Sẵn sàng";
                    if (ReferenceEquals(result.Job, SelectedJob) &&
                        SelectedPart is null)
                        SelectedPart = result.Job.Parts.FirstOrDefault();
                }

                _metadataAnalysisCompleted = analyzedCount;
                _metadataAnalysisFailed = failedCount;
                if (analyzedCount == jobs.Count ||
                    statusTimer.ElapsedMilliseconds >= 500)
                {
                    statusTimer.Restart();
                    var elapsedSeconds = Math.Max(
                        0.1,
                        progressTimer.Elapsed.TotalSeconds);
                    var videosPerMinute =
                        analyzedCount / elapsedSeconds * 60;
                    var percentage =
                        analyzedCount * 100d / jobs.Count;
                    var remainingSeconds = videosPerMinute > 0
                        ? (jobs.Count - analyzedCount) /
                          videosPerMinute * 60
                        : 0;
                    var remainingText = analyzedCount < jobs.Count
                        ? $" • còn khoảng {FormatRemainingTime(remainingSeconds)}"
                        : "";
                    StatusText =
                        $"{progressLabel} {analyzedCount:N0}/{jobs.Count:N0} video" +
                        $" • {percentage:0.0}% • {videosPerMinute:0} video/phút" +
                        remainingText +
                        (removedNoAudioCount > 0
                            ? $" • bỏ {removedNoAudioCount} video không tiếng"
                            : "") +
                        (removedInvalidCount > 0
                            ? $" • bỏ {removedInvalidCount} video lỗi/rỗng"
                            : "") +
                        (failedCount > 0 ? $" • lỗi {failedCount}" : "");
                    OnPropertyChanged(nameof(QueueSummary));
                    OnPropertyChanged(nameof(MergePlanSummary));
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                if (checkpointTimer.Elapsed.TotalSeconds >=
                    MetadataCheckpointIntervalSeconds)
                {
                    checkpointTimer.Restart();
                    await AutoSaveAsync();
                }
            }
        }
        catch
        {
            try { await Task.WhenAll(pending); }
            catch { /* Giữ nguyên lỗi gốc hoặc trạng thái hủy. */ }
            throw;
        }

        return (
            analyzedCount,
            failedCount,
            removedNoAudioCount,
            removedInvalidCount);
    }

    private async Task<VideoAnalysisResult> AnalyzeOneJobAsync(
        VideoJob job,
        int queueIndex,
        CancellationToken token)
    {
        var signature = VideoMetadataCache.ReadSignature(job.InputPath);
        if (!signature.Exists)
            return new VideoAnalysisResult(
                job, queueIndex, new MediaMetadata(), 0, 0,
                "", true);

        if (signature.Length == 0)
        {
            // Tránh xóa nhầm file vừa được chương trình khác tạo và đang bắt
            // đầu ghi. Chỉ coi là 0 byte khi chữ ký vẫn đứng yên sau một nhịp.
            await Task.Delay(750, token).ConfigureAwait(false);
            var refreshedSignature =
                VideoMetadataCache.ReadSignature(job.InputPath);
            if (!refreshedSignature.Exists)
                return new VideoAnalysisResult(
                    job, queueIndex, new MediaMetadata(), 0, 0, "", true);
            if (refreshedSignature.Length > 0)
                signature = refreshedSignature;
            else if (refreshedSignature.LastWriteUtcTicks !=
                     signature.LastWriteUtcTicks)
                return SourceChangedDuringAnalysis(
                    job,
                    queueIndex,
                    "xác nhận file 0 byte");
        }

        if (signature.Length == 0)
        {
            var moved = await _recycleBin.MoveFileAsync(
                    job.InputPath,
                    "video rỗng (0 byte)",
                    token)
                .ConfigureAwait(false);
            return moved
                ? new VideoAnalysisResult(
                    job, queueIndex, new MediaMetadata(), 0, 0, "", true,
                    RemovedBecauseInvalid: true)
                : new VideoAnalysisResult(
                    job, queueIndex, new MediaMetadata(), 0, 0,
                    "Video rỗng (0 byte) nhưng không thể đưa vào Thùng rác.",
                    false);
        }

        try
        {
            var media = await _input.AnalyzeAsync(job.InputPath, Settings, token)
                .ConfigureAwait(false);
            if (!media.HasAudio)
            {
                var latestSignature =
                    VideoMetadataCache.ReadSignature(job.InputPath);
                if (HasSourceChanged(signature, latestSignature))
                    return SourceChangedDuringAnalysis(
                        job,
                        queueIndex,
                        "kiểm tra âm thanh");
                var moved = await _recycleBin.MoveFileAsync(
                        job.InputPath,
                        "FFprobe xác nhận không có âm thanh",
                        token)
                    .ConfigureAwait(false);
                return moved
                    ? new VideoAnalysisResult(
                        job, queueIndex, media, 0, 0, "", true, true)
                    : new VideoAnalysisResult(
                        job, queueIndex, media, 0, 0,
                        "Video không có âm thanh nhưng không thể đưa vào Thùng rác.",
                        false,
                        false);
            }
            return new VideoAnalysisResult(
                job,
                queueIndex,
                media,
                signature.Length,
                signature.LastWriteUtcTicks,
                "",
                false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (ConfirmedInvalidMediaException ex)
        {
            // AnalyzeEngine đã thử FFprobe nhanh và đầy đủ. Chỉ lỗi dữ liệu được
            // FFprobe xác nhận mới được coi là video hỏng; lỗi hệ thống/timeout
            // đi qua nhánh dưới và được giữ lại để tránh xóa nhầm hàng loạt.
            var latestSignature =
                VideoMetadataCache.ReadSignature(job.InputPath);
            if (HasSourceChanged(signature, latestSignature))
                return SourceChangedDuringAnalysis(
                    job,
                    queueIndex,
                    "xác nhận video lỗi");
            var moved = await _recycleBin.MoveFileAsync(
                    job.InputPath,
                    "FFprobe xác nhận video lỗi: " + ex.Message,
                    token)
                .ConfigureAwait(false);
            return moved
                ? new VideoAnalysisResult(
                    job, queueIndex, new MediaMetadata(), 0, 0, "", true,
                    RemovedBecauseInvalid: true)
                : new VideoAnalysisResult(
                    job, queueIndex, new MediaMetadata(), 0, 0,
                    "Video lỗi nhưng không thể đưa vào Thùng rác: " + ex.Message,
                    false);
        }
        catch (Exception ex)
        {
            return new VideoAnalysisResult(
                job, queueIndex, new MediaMetadata(), 0, 0, ex.Message, false);
        }
    }

    private static bool HasSourceChanged(
        VideoFileSignature original,
        VideoFileSignature latest) =>
        !latest.Exists ||
        latest.Length != original.Length ||
        latest.LastWriteUtcTicks != original.LastWriteUtcTicks;

    private static VideoAnalysisResult SourceChangedDuringAnalysis(
        VideoJob job,
        int queueIndex,
        string stage) =>
        new(
            job,
            queueIndex,
            new MediaMetadata(),
            0,
            0,
            $"File nguồn thay đổi trong lúc {stage}; giữ lại để tránh xóa nhầm. " +
            "Bấm Cập nhật danh sách sau khi file ghi xong.",
            false);

    private sealed record VideoAnalysisResult(
        VideoJob Job,
        int QueueIndex,
        MediaMetadata Media,
        long SourceLength,
        long SourceLastWriteUtcTicks,
        string Error,
        bool RemoveFromQueue,
        bool RemovedBecauseNoAudio = false,
        bool RemovedBecauseInvalid = false);

    private async Task RebuildPartsAsync()
    {
        if (IsMetadataAnalysisRunning ||
            Jobs.Any(job => job.Status == JobStatus.Analyzing))
            return;

        foreach (var job in Jobs)
        {
            job.Parts.Clear();
            foreach (var part in _split.Build(job.Media.DurationSeconds, Settings, job.InputPath.GetHashCode())) job.Parts.Add(part);
            job.Status = JobStatus.Ready;
            job.Progress = 0;
            job.ProcessingStage = "Sẵn sàng";
        }
        SelectedPart = SelectedJob?.Parts.FirstOrDefault();
        InvalidateQueueSummary();
        await AutoSaveAsync();
    }

    private async Task SmartTrimSelectedAsync()
    {
        if (SelectedJob is null) return;
        var job = SelectedJob;
        IsBusy = true;
        StatusText = $"Đang dò im lặng và màn hình đen: {job.FileName}…";
        try
        {
            var result = await _smartTrim.AnalyzeAsync(
                job,
                Settings,
                _lifetime.Token);
            var leading = result.RecommendedLeadingSeconds;
            var trailing = result.RecommendedTrailingSeconds;
            if (leading < 0.1 && trailing < 0.1)
            {
                MessageBox.Show(
                    "Không thấy đoạn im lặng hoặc màn hình đen đủ dài ở đầu/cuối video.",
                    "Ekko Tools - Tự cắt",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            if (leading + trailing >= job.Media.DurationSeconds - 0.5)
                throw new InvalidDataException(
                    "Vùng được phát hiện chiếm gần toàn bộ video nên ứng dụng không tự cắt.");

            var answer = MessageBox.Show(
                $"Kết quả dò cho {job.FileName}:\n\n" +
                $"• Im lặng đầu/cuối: {result.LeadingSilenceSeconds:0.##}s / " +
                $"{result.TrailingSilenceSeconds:0.##}s\n" +
                $"• Màn hình đen đầu/cuối: {result.LeadingBlackSeconds:0.##}s / " +
                $"{result.TrailingBlackSeconds:0.##}s\n\n" +
                $"Áp dụng cắt đầu {leading:0.##}s và cuối {trailing:0.##}s vào danh sách Part? " +
                "Video nguồn không bị thay đổi.",
                "Ekko Tools - Xác nhận tự cắt",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var usableStart = leading;
            var usableEnd = job.Media.DurationSeconds - trailing;
            var adjusted = job.Parts
                .Select(part =>
                {
                    var start = Math.Max(part.StartSeconds, usableStart);
                    var end = Math.Min(part.EndSeconds, usableEnd);
                    return (Part: part, Start: start, End: end);
                })
                .Where(item => item.End - item.Start >= 0.1)
                .ToArray();
            if (adjusted.Length == 0)
                throw new InvalidDataException(
                    "Các Part hiện tại không giao với vùng nội dung được phát hiện. Hãy Tạo lại Part rồi thử lại.");
            job.Parts.Clear();
            for (var i = 0; i < adjusted.Length; i++)
            {
                var source = adjusted[i];
                source.Part.Index = i + 1;
                source.Part.StartSeconds = source.Start;
                source.Part.DurationSeconds = source.End - source.Start;
                source.Part.IsSplitPart =
                    Settings.SplitEnabled && adjusted.Length > 1;
                source.Part.Status = PartStatus.Pending;
                source.Part.Progress = 0;
                source.Part.Error = "";
                job.Parts.Add(source.Part);
            }
            SelectedPart = job.Parts.FirstOrDefault();
            InvalidateQueueSummary();
            StatusText =
                $"Đã tự cắt đầu {leading:0.##}s và cuối {trailing:0.##}s.";
            await AutoSaveAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã hủy dò đoạn thừa.";
        }
        catch (Exception ex)
        {
            _logger.Error($"Tự cắt {job.FileName}: {ex.Message}");
            MessageBox.Show(
                ex.Message,
                "Không tự cắt được video",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            RaiseCommands();
        }
    }

    private async Task<bool> EnsureStreamCopyCompatibilityAsync(CancellationToken token)
    {
        var pending = new List<(VideoJob Job, long Length, long LastWriteTicks)>();
        foreach (var job in Jobs)
        {
            try
            {
                var info = new FileInfo(job.InputPath);
                if (!info.Exists || info.Length <= 0)
                {
                    StatusText = $"Không thể xào siêu nhanh vì video nguồn không tồn tại/rỗng: {job.InputPath}";
                    _logger.Warning(StatusText);
                    return false;
                }

                var signature = (
                    Length: info.Length,
                    LastWriteTicks: info.LastWriteTimeUtc.Ticks);
                if (_streamCopySourceValidated.TryGetValue(job.Id, out var known) &&
                    known.Length == signature.Length &&
                    known.LastWriteTicks == signature.LastWriteTicks &&
                    !string.IsNullOrWhiteSpace(known.Key))
                {
                    job.StreamCopyCompatibilityKey = known.Key;
                    job.StreamCopyCompatibilitySummary = known.Summary;
                    continue;
                }

                // v64: cache preflight bền trên AppData để tắt/mở app giữa lượt xào
                // không phải FFprobe lại toàn bộ nguồn. File thay đổi kích thước/mtime
                // sẽ tự làm cache mất hiệu lực.
                if (StreamCopyPreflightCache.TryLoad(
                        job.InputPath,
                        signature.Length,
                        signature.LastWriteTicks,
                        out var cached) &&
                    cached is not null)
                {
                    job.Media = cached.Media;
                    job.SourceLength = signature.Length;
                    job.SourceLastWriteUtcTicks = signature.LastWriteTicks;
                    job.StreamCopyCompatibilityKey = cached.Profile.Key;
                    job.StreamCopyCompatibilitySummary = cached.Profile.Summary;
                    _streamCopySourceValidated[job.Id] =
                        (signature.Length, signature.LastWriteTicks, cached.Profile.Key, cached.Profile.Summary);
                    continue;
                }

                pending.Add((job, signature.Length, signature.LastWriteTicks));
            }
            catch (Exception ex)
            {
                StatusText = $"Không đọc được video nguồn để kiểm tra xào siêu nhanh: {job.FileName} - {ex.Message}";
                _logger.Warning(StatusText);
                return false;
            }
        }

        if (pending.Count > 0)
        {
            StatusText = $"⚡ Đang kiểm tra duration + tương thích stream-copy cho {pending.Count:N0} video…";
            _logger.Info(StatusText);
            using var gate = new SemaphoreSlim(3, 3);
            var tasks = pending.Select(async item =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var preflight = await _streamCopyCompatibility.ProbeAsync(
                        item.Job.InputPath,
                        Settings,
                        token).ConfigureAwait(false);
                    return (
                        item.Job,
                        item.Length,
                        item.LastWriteTicks,
                        Media: preflight.Media,
                        Profile: preflight.Profile,
                        Error: (Exception?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (
                        item.Job,
                        item.Length,
                        item.LastWriteTicks,
                        Media: (MediaMetadata?)null,
                        Profile: (StreamCopyCompatibilityProfile?)null,
                        Error: ex);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();
            var failed = results.FirstOrDefault(result =>
                result.Error is not null ||
                result.Media is null ||
                result.Profile is null);
            if (failed.Job is not null &&
                (failed.Error is not null || failed.Media is null || failed.Profile is null))
            {
                StatusText = $"Không xác nhận được stream-copy an toàn cho {failed.Job.FileName}: " +
                             $"{failed.Error?.Message ?? "FFprobe không trả đủ thông số."}";
                _logger.Warning(StatusText);
                return false;
            }

            foreach (var result in results)
            {
                var media = result.Media;
                var profile = result.Profile;
                if (media is null || profile is null) continue;
                result.Job.Media = media;
                result.Job.SourceLength = result.Length;
                result.Job.SourceLastWriteUtcTicks = result.LastWriteTicks;
                result.Job.StreamCopyCompatibilityKey = profile.Key;
                result.Job.StreamCopyCompatibilitySummary = profile.Summary;
                _streamCopySourceValidated[result.Job.Id] =
                    (result.Length, result.LastWriteTicks, profile.Key, profile.Summary);
                if (!StreamCopyPreflightCache.Save(
                        result.Job.InputPath,
                        result.Length,
                        result.LastWriteTicks,
                        new StreamCopyPreflightResult(profile, media)))
                    _logger.Warning(
                        $"Không lưu được cache preflight cho {result.Job.FileName}; lượt hiện tại vẫn tiếp tục bình thường.");
            }
        }

        var groups = Jobs
            .Where(job => !string.IsNullOrWhiteSpace(job.StreamCopyCompatibilityKey))
            .GroupBy(job => job.StreamCopyCompatibilityKey, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToArray();
        if (groups.Length == 0)
        {
            StatusText = "Không có nhóm video nào đủ thông tin để xào siêu nhanh an toàn.";
            _logger.Warning(StatusText);
            return false;
        }

        var videosPerOutput = Math.Max(0, Settings.MergeVideosPerOutput);
        if (videosPerOutput > 0 && groups.All(group => group.Count() < videosPerOutput))
        {
            var largest = groups.Max(group => group.Count());
            StatusText =
                $"Không có nhóm stream tương thích nào đủ {videosPerOutput} clip. " +
                $"Nhóm lớn nhất chỉ có {largest} clip; app đã dừng để tránh file đen/lỗi.";
            _logger.Warning(StatusText);
            return false;
        }

        _cachedMergePlans = null;
        InvalidateQueueSummary();
        var groupSummary = string.Join(
            " • ",
            groups.Take(4).Select(group =>
            {
                var sample = group.First();
                var summary = string.IsNullOrWhiteSpace(sample.StreamCopyCompatibilitySummary)
                    ? group.Key[..Math.Min(10, group.Key.Length)]
                    : sample.StreamCopyCompatibilitySummary;
                return $"{group.Count()} clip [{summary}]";
            }));
        var extra = groups.Length > 4 ? $" • +{groups.Length - 4} nhóm khác" : "";
        _logger.Info(
            $"⚡ Stream-copy preflight OK: {Jobs.Count:N0} nguồn → {groups.Length:N0} nhóm tương thích. " +
            groupSummary + extra);
        StatusText =
            $"⚡ Đã kiểm tra {Jobs.Count:N0} video • {groups.Length:N0} nhóm stream tương thích • đang lập kế hoạch xào…";
        return true;
    }

    private async Task RenderAsync()
    {
        if (!_ffmpegReady ||
            Jobs.Count == 0 ||
            IsBusy ||
            IsMetadataAnalysisRunning ||
            Jobs.Any(job => job.Status == JobStatus.Analyzing))
            return;
        if (!await EnsureDiskSpaceBeforeRenderAsync())
            return;
        var sourceFoldersToCleanup = CaptureRunSourceFolders(
            Jobs,
            Settings);
        var mergeMode = Settings.MergeVideosEnabled;
        var runMergePhase = false;
        IReadOnlyList<VideoJob> longEditJobs = [];
        IReadOnlyList<VideoJob> mergeSourceJobs = [];
        if (mergeMode)
        {
            if (Settings.AutoShuffleBatchEnabled && Settings.AutoShuffleStreamCopyEnabled)
            {
                IsBusy = true;
                RaiseCommands();
                try
                {
                    if (!await EnsureStreamCopyCompatibilityAsync(_lifetime.Token))
                        return;
                }
                finally
                {
                    IsBusy = false;
                    RaiseCommands();
                }
            }

            var resumedAutoShuffle = false;
            if (Settings.AutoShuffleBatchEnabled &&
                AutoShuffleResumeStore.TryRestoreSeed(
                    Jobs.ToArray(),
                    Settings,
                    out var resumeSeed))
            {
                Settings.MergeRandomSeed = resumeSeed;
                resumedAutoShuffle = true;
                StatusText = "Đang khôi phục kế hoạch tự xào dở…";
                _logger.Info(
                    $"Tiếp tục kế hoạch tự xào cũ • seed {unchecked((uint)resumeSeed):X8}. " +
                    "Các output đã hoàn thành sẽ được bỏ qua.");
            }
            else
            {
                // Ghép thường luôn phối lại. Tự xào chỉ tạo seed mới khi không có
                // phiên dở hợp lệ để tiếp tục.
                Settings.MergeRandomSeed = Random.Shared.Next();
            }
            CancelMergePlanBuild();
            if (!resumedAutoShuffle)
                StatusText = "Đang lập kế hoạch ghép ở nền…";
            var plans = await BuildMergePlansAsync(
                Jobs.ToArray(),
                CreateMergePlanningSettings(),
                _lifetime.Token);
            var countMode = Settings.MergeVideosPerOutput > 0;
            if (Settings.AutoShuffleBatchEnabled &&
                plans.Count != Settings.AutoShuffleTargetCount)
            {
                StatusText = plans.Count == 0
                    ? "Không tạo được nhóm ghép hợp lệ để tự xào. Hãy kiểm tra số video hoặc Min/Max."
                    : $"Kế hoạch tự xào chỉ tạo được {plans.Count:N0}/{Settings.AutoShuffleTargetCount:N0} video; đã dừng để không render thiếu.";
                _logger.Warning(StatusText);
                _cachedMergePlans = plans;
                OnPropertyChanged(nameof(QueueSummary));
                OnPropertyChanged(nameof(MergePlanSummary));
                OnPropertyChanged(nameof(MergePlanDetailsText));
                return;
            }
            if (Settings.AutoShuffleBatchEnabled && !resumedAutoShuffle)
            {
                if (AutoShuffleResumeStore.Save(Jobs.ToArray(), Settings))
                    _logger.Info(
                        $"Đã lưu kế hoạch tự xào để có thể tiếp tục nếu dừng giữa chừng • seed {unchecked((uint)Settings.MergeRandomSeed):X8}.");
                else
                    _logger.Warning(
                        "Không lưu được trạng thái tiếp tục tự xào; lượt hiện tại vẫn chạy nhưng nếu đóng app có thể phải lập kế hoạch mới.");
            }

            if (!countMode && !Settings.AutoShuffleBatchEnabled)
            {
                var minimum = Math.Min(Settings.MergeMinSeconds, Settings.MergeMaxSeconds);
                var maximum = Math.Max(Settings.MergeMinSeconds, Settings.MergeMaxSeconds);
                var speed = Math.Clamp(Settings.VideoSpeed, 0.1, 10);
                longEditJobs = Jobs
                    .Where(job =>
                        job.Media.DurationSeconds / speed >
                        maximum + 0.05)
                    .ToArray();
                plans = plans
                    .Where(plan =>
                        plan.DurationSeconds >= minimum - 0.05 &&
                        plan.DurationSeconds <= maximum + 0.05)
                    .ToArray();
            }
            _cachedMergePlans = plans;
            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(MergePlanSummary));
            OnPropertyChanged(nameof(MergePlanDetailsText));
            runMergePhase = plans.Count > 0;
            mergeSourceJobs = plans
                .SelectMany(plan => plan.Segments)
                .Select(segment => segment.Job)
                .DistinctBy(job => job.Id)
                .ToArray();
            if (!runMergePhase && longEditJobs.Count == 0)
            {
                StatusText = countMode
                    ? $"Không đủ video để tạo nhóm {Settings.MergeVideosPerOutput} clip."
                    : "Không có đủ clip phù hợp với khoảng thời lượng đã chọn.";
                _logger.Warning(StatusText);
                return;
            }
        }
        else
        {
            foreach (var job in Jobs)
                _split.NormalizeEdited(job.Parts, job.Media.DurationSeconds);
        }
        StatusText = mergeMode && Settings.AutoShuffleBatchEnabled
            ? Settings.AutoShuffleStreamCopyEnabled
                ? $"⚡ Đang xào siêu nhanh {Settings.AutoShuffleTargetCount:N0} video bằng stream copy…"
                : $"Đang tự xào và render {Settings.AutoShuffleTargetCount:N0} video…"
            : "Đang render…";
        RaiseCommands();
        var runCompletedSuccessfully = false;
        var queueSettings = mergeMode && Settings.AutoShuffleBatchEnabled
            ? CreateAutoShuffleRenderSettings()
            : Settings;
        try
        {
            if (!mergeMode)
            {
                var editJobs = Jobs.ToList();
                await _renderQueue.StartAsync(
                    editJobs,
                    Settings,
                    _lifetime.Token,
                    "Đang edit");
                runCompletedSuccessfully = AllJobsSucceeded(editJobs);
            }
            else
            {
                var mergePhaseSucceeded = !runMergePhase;
                var longEditPhaseSucceeded = longEditJobs.Count == 0;
                if (runMergePhase)
                {
                    await _renderQueue.StartAsync(
                        Jobs.ToList(),
                        queueSettings,
                        _lifetime.Token,
                        Settings.AutoShuffleBatchEnabled ? "Đang tự xào" : "Đang ghép");
                    mergePhaseSucceeded = AllJobsSucceeded(mergeSourceJobs);
                }

                var mergeWasCancelled = mergeSourceJobs.Any(job =>
                    job.Status == JobStatus.Cancelled);
                if (longEditJobs.Count > 0 && !mergeWasCancelled)
                {
                    StatusText =
                        $"{longEditJobs.Count:N0} clip dài hơn mức tối đa " +
                        $"{FormatDuration(Math.Max(
                            Settings.MergeMinSeconds,
                            Settings.MergeMaxSeconds))}; " +
                        "đang xuất riêng vào thư mục Edit…";
                    _logger.Info(StatusText);
                    var editSettings = CreateLongClipEditSettings();
                    foreach (var job in longEditJobs)
                    {
                        job.Parts.Clear();
                        foreach (var part in _split.Build(
                                     job.Media.DurationSeconds,
                                     editSettings,
                                     job.Id.GetHashCode()))
                            job.Parts.Add(part);
                        job.Status = JobStatus.Ready;
                        job.Progress = 0;
                        job.Error = "";
                        job.ProcessingStage = "Chờ edit riêng";
                    }
                    await _renderQueue.StartAsync(
                        longEditJobs.ToList(),
                        editSettings,
                        _lifetime.Token,
                        "Đang edit clip dài");
                    longEditPhaseSucceeded = AllJobsSucceeded(longEditJobs);
                }
                else if (longEditJobs.Count > 0)
                    longEditPhaseSucceeded = false;

                runCompletedSuccessfully =
                    mergePhaseSucceeded &&
                    longEditPhaseSucceeded;
            }
            await AutoSaveAsync();
            if (mergeMode &&
                Settings.AutoShuffleBatchEnabled &&
                Settings.DeleteSourceAfterSuccess &&
                _renderQueue.LastAutoShuffleTargetComplete)
            {
                StatusText = "Đã đủ mục tiêu; đang đưa cả thư mục nguồn vào Thùng rác…";
                await RecycleCompletedSourceFoldersAsync(
                    sourceFoldersToCleanup,
                    Settings.OutputRoot,
                    _lifetime.Token);
            }

            if (runCompletedSuccessfully &&
                !(mergeMode && Settings.AutoShuffleBatchEnabled) &&
                Settings.DeleteSourceAfterSuccess &&
                Settings.EmptyRecycleBinAfterSuccessfulRun)
            {
                StatusText =
                    "Đã edit xong; đang xóa cây thư mục nguồn trống…";
                await _recycleBin.DeleteEmptyDirectoryTreesAsync(
                    sourceFoldersToCleanup,
                    "toàn bộ danh sách edit đã hoàn tất thành công",
                    _lifetime.Token);
                StatusText = "Đã dọn thư mục nguồn trống; đang dọn sạch Thùng rác…";
                await _recycleBin.EmptyAsync(
                    "toàn bộ danh sách edit đã hoàn tất thành công",
                    _lifetime.Token);
            }

            StatusText = runCompletedSuccessfully
                ? "Danh sách render đã hoàn tất"
                : "Danh sách render đã kết thúc nhưng còn tác vụ lỗi hoặc bị hủy";
        }
        finally
        {
            InvalidateQueueSummary();
            RefreshSubtitleCacheSize();
            RefreshDiskSpaceStatus();
            RaiseCommands();
        }
    }

    private async Task<bool> EnsureDiskSpaceBeforeRenderAsync()
    {
        DiskSpaceSnapshot snapshot;
        try
        {
            // Chỉ dùng dung lượng thực tế của ổ output ở bước preflight.
            // Không lấy ước tính cả batch để kích hoạt dọn Thùng rác sớm.
            snapshot = _diskSpace.Inspect(
                Settings.OutputRoot,
                0,
                Settings.MinimumFreeSpaceGb);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Không kiểm tra được ổ đĩa xuất.\n\n{ex.Message}",
                "Ekko Tools - Dung lượng ổ đĩa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        UpdateDiskSpaceStatus(snapshot);
        var thresholdBytes = (long)(Math.Max(0.5, Settings.MinimumFreeSpaceGb) * 1024 * 1024 * 1024);

        if (snapshot.FreeBytes > thresholdBytes)
            return true;

        if (Settings.EmptyRecycleBinWhenDiskFull)
        {
            StatusText =
                $"Ổ {snapshot.DriveName} còn {DiskSpaceService.FormatBytes(snapshot.FreeBytes)}; " +
                "đã chạm ngưỡng, đang dọn file tạm và Thùng rác…";
            if (Settings.AutoCleanPartialFiles)
                await Task.Run(() => _diskSpace.CleanupPartialFiles(Settings.OutputRoot));
            await _recycleBin.EmptyAsync(
                $"dung lượng còn dưới ngưỡng {Settings.MinimumFreeSpaceGb:0.##} GB trước khi render",
                _lifetime.Token);

            snapshot = _diskSpace.Inspect(
                Settings.OutputRoot,
                0,
                Settings.MinimumFreeSpaceGb);
            UpdateDiskSpaceStatus(snapshot);
            if (snapshot.FreeBytes > thresholdBytes)
                return true;
        }

        var answer = MessageBox.Show(
            $"Ổ {snapshot.DriveName} hiện còn {DiskSpaceService.FormatBytes(snapshot.FreeBytes)} " +
            $"(ngưỡng tự dọn là {Settings.MinimumFreeSpaceGb:0.##} GB).\n\n" +
            "Thùng rác/file tạm đã được xử lý nhưng dung lượng vẫn thấp. " +
            "Bạn có muốn tiếp tục render và tự chịu rủi ro hết dung lượng không?",
            "Ekko Tools - Ổ đĩa có thể không đủ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    private void RefreshDiskSpaceStatus()
    {
        try
        {
            UpdateDiskSpaceStatus(_diskSpace.Inspect(
                Settings.OutputRoot,
                0,
                Settings.MinimumFreeSpaceGb));
        }
        catch (Exception ex)
        {
            DiskSpaceStatusText = "Không đọc được dung lượng: " + ex.Message;
        }
    }

    private void UpdateDiskSpaceStatus(DiskSpaceSnapshot snapshot)
    {
        DiskSpaceStatusText =
            $"Ổ {snapshot.DriveName}: còn {DiskSpaceService.FormatBytes(snapshot.FreeBytes)} / " +
            $"{DiskSpaceService.FormatBytes(snapshot.TotalBytes)}";
    }

    private static bool AllJobsSucceeded(IEnumerable<VideoJob> jobs)
    {
        var snapshot = jobs.ToArray();
        return snapshot.Length > 0 &&
               snapshot.All(job => job.Status == JobStatus.Succeeded);
    }

    private static IReadOnlyList<string> CaptureRunSourceFolders(
        IEnumerable<VideoJob> jobs,
        PresetSettings settings)
    {
        var sourcePaths = jobs
            .Select(job => TryGetFullPath(job.InputPath))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToArray();
        if (sourcePaths.Length == 0) return [];

        return settings.InputFolders
            .Append(settings.InputFolder)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryGetFullPath)
            .Where(path => path is not null)
            .Select(path => Path.TrimEndingDirectorySeparator(path!))
            .Where(folder => sourcePaths.Any(source =>
                IsPathInsideDirectory(source, folder)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RecycleCompletedSourceFoldersAsync(
        IReadOnlyList<string> sourceFolders,
        string outputRoot,
        CancellationToken token)
    {
        var output = TryGetFullPath(outputRoot);
        var app = TryGetFullPath(AppContext.BaseDirectory);
        var protectedExact = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(TryGetFullPath)
        .Where(path => path is not null)
        .Select(path => Path.TrimEndingDirectorySeparator(path!))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var folders = sourceFolders
            .Select(TryGetFullPath)
            .Where(path => path is not null)
            .Select(path => Path.TrimEndingDirectorySeparator(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToArray();

        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) continue;

            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(folder) ?? "");
            if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) ||
                protectedExact.Contains(folder) ||
                protectedExact.Any(protectedPath =>
                    IsPathInsideDirectory(protectedPath, folder)) ||
                (output is not null &&
                 (string.Equals(folder, Path.TrimEndingDirectorySeparator(output), StringComparison.OrdinalIgnoreCase) ||
                  IsPathInsideDirectory(output, folder))) ||
                (app is not null &&
                 (string.Equals(folder, Path.TrimEndingDirectorySeparator(app), StringComparison.OrdinalIgnoreCase) ||
                  IsPathInsideDirectory(app, folder))))
            {
                _logger.Warning(
                    $"Không xóa cả thư mục nguồn vì vướng bảo vệ an toàn: {folder}");
                continue;
            }

            try
            {
                await Task.Run(() =>
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        folder,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                        Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException), token);
                _logger.Success($"Đã đưa cả thư mục nguồn vào Thùng rác: {folder}");
            }
            catch (DirectoryNotFoundException) { }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(
                    $"Không thể đưa cả thư mục nguồn vào Thùng rác ({folder}): {ex.Message}");
            }
        }
    }


    private static bool IsUnsafeListScanRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var driveRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "");
            if (string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            var appFolder = TryGetFullPath(AppContext.BaseDirectory);
            if (!string.IsNullOrWhiteSpace(appFolder))
            {
                appFolder = Path.TrimEndingDirectorySeparator(appFolder);
                if (string.Equals(full, appFolder, StringComparison.OrdinalIgnoreCase) ||
                    IsPathInsideDirectory(appFolder, full))
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        try
        {
            var relative = Path.GetRelativePath(directory, path);
            return !Path.IsPathRooted(relative) &&
                   !string.Equals(relative, "..", StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.AltDirectorySeparatorChar,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private void PauseResume()
    {
        if (_renderQueue.IsPaused) _renderQueue.Resume(); else _renderQueue.Pause();
    }

    private async Task RetryAsync()
    {
        foreach (var job in Jobs.Where(x => x.Status == JobStatus.Failed))
        {
            foreach (var part in job.Parts.Where(x => x.Status == PartStatus.Failed))
            {
                part.Status = PartStatus.Pending;
                part.Error = "";
            }
            job.Status = JobStatus.Ready;
            job.Error = "";
        }
        await RenderAsync();
    }

    private Task PreviewAsync() => PreviewAtAsync(0);

    private async Task SafePreviewAtAsync(double positionPercent)
    {
        try
        {
            await PreviewAtAsync(positionPercent);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã hủy tạo preview.";
        }
        catch (Exception ex)
        {
            _logger.Error("Preview: " + ex.Message);
            StatusText = "Không tạo được preview: " + ex.Message;
        }
    }

    private async Task PreviewAtAsync(double positionPercent)
    {
        if (SelectedJob is null || SelectedPart is null) return;
        _lastPreviewPositionPercent = Math.Clamp(positionPercent, 0, 100);
        IsBusy = true;
        StatusText = "Đang tạo bản xem thử giống bản render…";
        try
        {
            if (IsAutoLandscapePreviewActive && !IsMouseCropEditMode)
            {
                var sourceResult = await _preview.GenerateSourceAspectClipAsync(
                    SelectedJob,
                    SelectedPart,
                    Settings,
                    positionPercent,
                    _lifetime.Token);
                PreviewUri = new Uri(sourceResult.VideoPath);
                PreviewOverlayUri = null;
                RaiseMouseCropPreviewProperties();
                RaiseExistingSubtitleBlurPreviewProperties();
                StatusText = "Đang xem nguồn ngang theo tỷ lệ gốc • bấm Crop bằng chuột để chọn vùng giữ.";
                return;
            }

            var result = await _preview.GenerateClipAsync(
                SelectedJob,
                SelectedPart,
                Jobs.IndexOf(SelectedJob),
                Settings,
                positionPercent,
                _lifetime.Token);
            PreviewUri = new Uri(result.VideoPath);
            PreviewOverlayUri = Settings.TextOverlayEnabled ? new Uri(result.OverlayPath) : null;
            RaiseExistingSubtitleBlurPreviewProperties();
            StatusText = Settings.IsFaceCropMainVideo
                ? SelectedJob.FaceDetectionStatus
                : "Bản xem thử đã sẵn sàng";
        }
        finally { IsBusy = false; RefreshSubtitleCacheSize(); }
    }

    public async Task RefreshPreviewTitleOverlayAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        var title = await _titleSource.GetTitleAsync(
            SelectedJob, Jobs.IndexOf(SelectedJob), Settings, _lifetime.Token);
        SelectedJob.CleanTitle = title;
        var overlay = await _typography.RenderAsync(
            title,
            SelectedPart.Index,
            SelectedPart.IsSplitPart,
            Settings,
            _lifetime.Token);
        PreviewOverlayUri = new Uri(overlay);
        StatusText = $"Vị trí chữ: ngang {Settings.TitleHorizontalOffset:0}, dọc {Settings.TitleTop:0}";
    }

    private async Task RefreshPreviewTitleOverlaySafeAsync()
    {
        try { await RefreshPreviewTitleOverlayAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Không cập nhật được chữ trên preview: " + ex.Message;
        }
    }

    private async Task RefreshPreviewAfterFlipSafeAsync()
    {
        var state = Settings.FlipMainVideo ? "BẬT" : "TẮT";
        if (!HasPreview || IsBusy)
        {
            StatusText = $"Lật video chính: {state} • bấm ▶ 6s để xem lại.";
            return;
        }

        try
        {
            await PreviewAsync();
            StatusText = $"Lật video chính: {state} • preview đã cập nhật.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Không cập nhật được preview lật hình: " + ex.Message;
        }
    }

    private async Task RefreshPreviewAfterMainVideoFitSafeAsync()
    {
        if (!HasPreview || IsBusy || _renderQueue.IsRunning)
        {
            StatusText = Settings.IsMouseCropCutOutside
                ? "Đã chọn Cắt sạch ngoài khung • bấm ▶ 6s để xem lại. Phần tối quanh preview chỉ là nền xem, không nằm trong file xuất."
                : Settings.IsFaceCropMainVideo
                    ? "Đã chọn Crop theo khuôn mặt • bấm ▶ 6s để dò mặt và xem kết quả."
                    : "Cách lấy vùng video đã đổi • bấm ▶ 6s để xem lại.";
            return;
        }

        try
        {
            await PreviewAsync();
            if (Settings.IsMouseCropCutOutside)
                StatusText = "Cắt sạch ngoài khung • phần tối quanh preview chỉ là nền xem, file xuất chỉ còn đúng khung crop.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Không cập nhật được cách lấp đầy trên preview: " + ex.Message;
        }
    }

    public async Task RefreshPreviewAfterSubtitleMoveAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        IsBusy = true;
        StatusText = "Đang cập nhật vị trí phụ đề trên preview…";
        try
        {
            var result = await _preview.GenerateClipAsync(
                SelectedJob, SelectedPart, Jobs.IndexOf(SelectedJob), Settings, _lifetime.Token);
            PreviewUri = new Uri(result.VideoPath);
            PreviewOverlayUri = Settings.TextOverlayEnabled ? new Uri(result.OverlayPath) : null;
            StatusText = $"Vị trí phụ đề: ngang {Settings.SubtitleHorizontalOffset:0}, cách đáy {Settings.SubtitleBottomMargin:0}";
        }
        finally
        {
            IsBusy = false;
            RefreshSubtitleCacheSize();
        }
    }

    public void UpdateCustomRatioFromPreview(double ratio)
    {
        Settings.CustomMainRatio = ratio;
    }

    public void UpdateCropFocusFromPreview(double xPercent, double yPercent)
    {
        Settings.CropFocusXPercent = xPercent;
        Settings.CropFocusYPercent = yPercent;
    }

    public async Task RefreshPreviewAfterCustomLayoutAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        await PreviewAsync();
        StatusText =
            $"Vùng video: {Settings.CustomMainPercent:0.#}% • {Settings.CustomMainHeightPixels} px • " +
            $"tâm crop X {Settings.CropFocusXPercent:0} / Y {Settings.CropFocusYPercent:0}";
    }

    private async Task ToggleMouseCropEditAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        if (IsMouseCropEditMode)
        {
            IsMouseCropEditMode = false;
            await PreviewAtAsync(_lastPreviewPositionPercent);
            StatusText = Settings.MouseCropEnabled
                ? "Đã thoát chọn crop chuột; vùng crop hiện tại vẫn được áp dụng."
                : "Đã thoát chọn crop chuột.";
            return;
        }

        IsSubtitlePositionEditMode = false;
        IsCustomCropEditMode = false;
        IsExistingSubtitleBlurEditMode = false;
        IsWatermarkPositionEditMode = false;

        // Crop luôn đi theo workflow ổn định riêng, giống chế độ preview cũ:
        // bật lớp crop trước rồi tạo preview nguồn không overlay. Không tái sử dụng
        // trạng thái auto-preview ngang để tránh xung đột hit-test.
        IsMouseCropEditMode = true;
        PreviewOverlayUri = null;
        try
        {
            await ShowUncroppedPreviewForMouseCropAsync();
        }
        catch (OperationCanceledException)
        {
            IsMouseCropEditMode = false;
            StatusText = "\u0110\u00e3 h\u1ee7y m\u1edf khung crop.";
            return;
        }
        catch (Exception ex)
        {
            IsMouseCropEditMode = false;
            _logger.Error("Crop preview: " + ex.Message);
            StatusText = "Kh\u00f4ng m\u1edf \u0111\u01b0\u1ee3c khung crop: " + ex.Message;
            return;
        }
        StatusText =
            "Crop bằng chuột: kéo khung tự do quanh vùng muốn GIỮ. App giữ nguyên đúng tỷ lệ ngang/vuông/dọc bạn chọn; có thể kéo lại nhiều lần. Bấm Xong crop để xem kết quả.";
    }

    private async Task ShowUncroppedPreviewForMouseCropAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        IsBusy = true;
        StatusText = "Đang mở khung gốc để chọn crop bằng chuột…";
        try
        {
            var result = await _preview.GenerateSourceAspectClipAsync(
                SelectedJob,
                SelectedPart,
                Settings,
                _lastPreviewPositionPercent,
                _lifetime.Token);
            PreviewUri = new Uri(result.VideoPath);
            // Khi khoanh crop chỉ hiển thị VIDEO. Tiêu đề là lớp độc lập và
            // được hiện lại sau khi áp dụng, vì vậy crop không bao giờ cắt chữ.
            PreviewOverlayUri = null;
            RaiseExistingSubtitleBlurPreviewProperties();
        }
        finally
        {
            IsBusy = false;
            RefreshSubtitleCacheSize();
        }
    }

    public void UpdateMouseCropFromPreview(
        double xPercent,
        double yPercent,
        double widthPercent,
        double heightPercent)
    {
        var x = Math.Clamp(xPercent, 0, 98);
        var y = Math.Clamp(yPercent, 0, 98);
        if (SelectedJob?.Media is { Width: > 0, Height: > 0 } media)
            Settings.MouseCropSourceAspect = media.Width / (double)media.Height;
        var width = Math.Clamp(widthPercent, 2, 100 - x);
        var height = Math.Clamp(heightPercent, 2, 100 - y);
        Settings.MouseCropXPercent = x;
        Settings.MouseCropYPercent = y;
        Settings.MouseCropWidthPercent = width;
        Settings.MouseCropHeightPercent = height;
        Settings.MouseCropEnabled = true;
        RaiseMouseCropPreviewProperties();
    }

    public Task RefreshPreviewAfterMouseCropAsync()
    {
        if (SelectedJob is null || SelectedPart is null)
            return Task.CompletedTask;

        // Giữ nguyên chế độ crop sau khi thả chuột để người dùng có thể kéo lại
        // ngay lập tức. Preview nền vẫn là bản chưa crop, title vẫn ẩn; khung cyan
        // chính là vùng sẽ được lấy. Chỉ khi bấm "Xong crop / xem kết quả"
        // mới thoát chế độ và render preview cuối có title.
        IsMouseCropEditMode = true;
        StatusText = MouseCropSummary +
            " • đã ghi vùng crop. Có thể kéo lại ngay; bấm Xong crop để xem kết quả.";
        return Task.CompletedTask;
    }

    private async Task AutoMouseCropAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;

        IsSubtitlePositionEditMode = false;
        IsCustomCropEditMode = false;
        IsMouseCropEditMode = false;
        IsExistingSubtitleBlurEditMode = false;
        IsWatermarkPositionEditMode = false;
        IsBusy = true;
        StatusText = "Đang tự chọn vùng crop…";
        try
        {
            var focus = await _preview.DetectFaceForMouseCropAsync(
                SelectedJob, Settings, _lifetime.Token);

            // Cùng một phần trăm W/H luôn giữ đúng tỷ lệ của khung đích.
            // 72% tạo mức zoom vừa phải; nếu có mặt thì đặt mặt gần tâm.
            const double size = 72;
            if (focus is not null)
            {
                // Đặt crop nền hiện có về đúng tâm mặt trước; sau đó vùng
                // mouse-crop có thể lấy quanh giữa khung, ổn định hơn với
                // video ngang hoặc video có nhiều phần thừa hai bên.
                Settings.CropFocusXPercent = focus.XPercent;
                Settings.CropFocusYPercent = focus.YPercent;
            }
            const double centerX = 50;
            const double centerY = 50;
            var x = Math.Clamp(centerX - size / 2, 0, 100 - size);
            var y = Math.Clamp(centerY - size / 2, 0, 100 - size);
            UpdateMouseCropFromPreview(x, y, size, size);
        }
        finally
        {
            IsBusy = false;
        }

        await PreviewAtAsync(_lastPreviewPositionPercent);
        StatusText = SelectedJob.DetectedFaceFocus is not null
            ? $"Đã tự chọn crop theo khuôn mặt • {MouseCropAspectText} • tiêu đề giữ nguyên."
            : $"Đã tự chọn crop ở giữa • {MouseCropAspectText} • tiêu đề giữ nguyên.";
    }

    private async Task ResetMouseCropAsync()
    {
        Settings.MouseCropEnabled = false;
        Settings.MouseCropXPercent = 0;
        Settings.MouseCropYPercent = 0;
        Settings.MouseCropWidthPercent = 100;
        Settings.MouseCropHeightPercent = 100;
        Settings.MouseCropSourceAspect = SelectedJob?.Media is { Width: > 0, Height: > 0 } media
            ? media.Width / (double)media.Height
            : Settings.DesignWidth / (double)Settings.DesignHeight;
        IsMouseCropEditMode = false;
        RaiseMouseCropPreviewProperties();
        if (HasPreview)
            await PreviewAtAsync(_lastPreviewPositionPercent);
        StatusText = "Đã bỏ crop bằng chuột; video trở về vùng hiển thị trước crop chuột.";
    }

    public async Task RefreshPreviewAfterExistingSubtitleBlurMoveAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        await PreviewAsync();
        StatusText =
            $"Vùng mờ sub cũ: X {Settings.ExistingSubtitleBlurX}, Y {Settings.ExistingSubtitleBlurY}, " +
            $"{Settings.ExistingSubtitleBlurWidth} × {Settings.ExistingSubtitleBlurHeight} px";
    }

    private async Task DetectExistingSubtitleBlurAsync()
    {
        if (SelectedJob is null || SelectedPart is null) return;
        Settings.ExistingSubtitleBlurEnabled = true;
        Settings.AutoDetectExistingSubtitleRegion = true;
        IsExistingSubtitleBlurEditMode = false;
        IsBusy = true;
        StatusText = "Đang lấy mẫu và tự nhận diện vùng chữ/sub cũ…";
        try
        {
            var region = await _preview.EnsureExistingSubtitleRegionAsync(
                SelectedJob,
                Settings,
                true,
                _lifetime.Token);
            RaiseExistingSubtitleBlurPreviewProperties();
            if (region is null)
            {
                StatusText =
                    "Không tìm thấy vùng chữ ổn định • đang dùng vùng mờ thủ công.";
                return;
            }

            var result = await _preview.GenerateClipAsync(
                SelectedJob,
                SelectedPart,
                Jobs.IndexOf(SelectedJob),
                Settings,
                _lifetime.Token);
            PreviewUri = new Uri(result.VideoPath);
            PreviewOverlayUri = Settings.TextOverlayEnabled
                ? new Uri(result.OverlayPath)
                : null;
            StatusText =
                $"Đã tự nhận diện vùng sub: X {region.X}, Y {region.Y}, " +
                $"{region.Width} × {region.Height} px • tin cậy {region.Confidence:P0}.";
        }
        finally
        {
            IsBusy = false;
            RaiseExistingSubtitleBlurPreviewProperties();
        }
    }

    private async Task GenerateThumbnailsAsync()
    {
        if (SelectedJob is null) return;
        IsBusy = true;
        try
        {
            var queueIndex = Jobs.IndexOf(SelectedJob);
            foreach (var part in SelectedJob.Parts)
                await _preview.GenerateThumbnailAsync(SelectedJob, part, queueIndex, Settings, _lifetime.Token);
            RaiseExistingSubtitleBlurPreviewProperties();
        }
        finally { IsBusy = false; RefreshSubtitleCacheSize(); }
    }

    private void ChooseOutput()
    {
        var folder = _dialogs.PickFolder("Chọn thư mục xuất video", Settings.OutputRoot);
        if (folder is not null)
        {
            Settings.OutputRoot = folder;
            RefreshDiskSpaceStatus();
        }
    }

    private void ApplyPlatformPreset()
    {
        Settings.Layout = LayoutKind.Original;
        Settings.MainVideoFit = MainVideoFitMode.Crop;
        Settings.VideoSpeed = 1;
        Settings.AudioEffectsEnabled = true;
        Settings.AudioNormalizeEnabled = true;
        Settings.AudioCompressorEnabled = true;
        Settings.Quality = Settings.OutputPlatform switch
        {
            OutputPlatformPreset.YouTubeShorts => 25,
            OutputPlatformPreset.FacebookReels => 27,
            OutputPlatformPreset.TikTok => 28,
            _ => Settings.Quality
        };
        StatusText = Settings.OutputPlatform == OutputPlatformPreset.Custom
            ? $"Đang dùng cấu hình {Settings.OutputWidth} × {Settings.OutputHeight} tùy chỉnh."
            : $"Đã áp dụng preset {OutputPlatformOptions.First(
                option => option.Value == Settings.OutputPlatform).Name}.";
    }

    private void RegenerateMergePlan()
    {
        if (Settings.AutoShuffleBatchEnabled)
            AutoShuffleResumeStore.Clear(Settings);
        Settings.MergeRandomSeed = Random.Shared.Next();
        InvalidateQueueSummary();
        StatusText = "Đang phối lại kế hoạch ghép…";
    }

    private async Task SavePresetAsync()
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(Settings.Name)
                ? "Preset mới"
                : Settings.Name.Trim();
            await _presets.SaveAsync(Settings, name, _lifetime.Token);
            RefreshSavedPresets();
            SelectedSavedPresetName = name;
            StatusText = $"Đã lưu preset: {name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Không lưu được preset",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task LoadPresetAsync()
    {
        var name = SelectedSavedPresetName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var loaded = await _presets.LoadAsync(name, _lifetime.Token);
        if (loaded is null)
        {
            MessageBox.Show(
                "Preset không còn tồn tại hoặc file bị lỗi.",
                "Ekko Tools",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshSavedPresets();
            return;
        }

        // Preset chỉ thay thiết lập edit/render, không đổi nguồn video,
        // nơi xuất và đường dẫn FFmpeg đang dùng trong dự án hiện tại.
        loaded.InputFolder = Settings.InputFolder;
        loaded.InputFolders = Settings.InputFolders.ToList();
        loaded.OutputRoot = Settings.OutputRoot;
        loaded.FfmpegPath = Settings.FfmpegPath;
        loaded.FfprobePath = Settings.FfprobePath;
        loaded.WatchFolderEnabled = Settings.WatchFolderEnabled;
        loaded.Name = name;
        Settings = loaded;
        StatusText = $"Đã nạp preset: {name}";
        await AutoSaveAsync();
    }

    private void DeletePreset()
    {
        var name = SelectedSavedPresetName;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (MessageBox.Show(
                $"Xóa preset “{name}”?",
                "Ekko Tools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            _presets.Delete(name);
            RefreshSavedPresets();
            StatusText = $"Đã xóa preset: {name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Không xóa được preset",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RefreshSavedPresets()
    {
        var selected = SelectedSavedPresetName;
        SavedPresetNames.Clear();
        foreach (var name in _presets.List())
            SavedPresetNames.Add(name);
        SelectedSavedPresetName = selected is not null &&
                                  SavedPresetNames.Contains(selected)
            ? selected
            : SavedPresetNames.FirstOrDefault();
    }

    private void OpenOutputFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.OutputRoot))
            {
                ChooseOutput();
                return;
            }
            Directory.CreateDirectory(Settings.OutputRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = Settings.OutputRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"Không thể mở thư mục video đã edit: {ex.Message}");
            MessageBox.Show($"Không thể mở thư mục video đã edit.\n\n{ex.Message}",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLatestOutput()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.OutputRoot) ||
                !Directory.Exists(Settings.OutputRoot))
            {
                MessageBox.Show("Thư mục Output chưa tồn tại.",
                    "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var latest = Directory
                .EnumerateFiles(Settings.OutputRoot, "*.mp4", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                MessageBox.Show("Chưa có video MP4 nào trong thư mục Output.",
                    "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = latest,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"Không thể mở video mới nhất: {ex.Message}");
            MessageBox.Show($"Không thể mở video mới nhất.\n\n{ex.Message}",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChooseBackgroundImage()
    {
        var path = _dialogs.PickBackgroundImage();
        if (path is null) return;
        Settings.BackgroundImagePath = path;
        Settings.UseBackgroundImage = true;
    }

    private void ChooseWatermarkImage()
    {
        var path = _dialogs.PickWatermarkImage();
        if (path is null) return;
        Settings.WatermarkPath = path;
        Settings.WatermarkEnabled = true;
        RefreshWatermarkPreview();
        StatusText = _watermarkBackgroundRemoved
            ? $"Đã chọn watermark và tự tách nền: {Path.GetFileName(path)}"
            : $"Đã chọn watermark: {Path.GetFileName(path)}";
    }

    private void ClearWatermarkImage()
    {
        IsWatermarkPositionEditMode = false;
        Settings.WatermarkEnabled = false;
        Settings.WatermarkPath = "";
        WatermarkPreviewSource = null;
        StatusText = "Đã bỏ logo / watermark khỏi video.";
    }

    private void RefreshWatermarkPreview()
    {
        var preview = _watermarks.CreatePreview(Settings);
        _watermarkPreviewAspectRatio = preview?.AspectRatio ?? 1;
        _watermarkBackgroundRemoved = preview?.BackgroundRemoved == true;
        WatermarkPreviewSource = preview?.Source;
        if (preview is null) IsWatermarkPositionEditMode = false;
        RaiseWatermarkPreviewProperties();
    }

    public void UpdateWatermarkPositionFromPreview(double left, double top)
    {
        var availableX = Math.Max(0, Settings.DesignWidth - WatermarkPreviewWidth);
        var availableY = Math.Max(0, Settings.DesignHeight - WatermarkPreviewHeight);
        var xPercent = availableX <= 0
            ? 0
            : Math.Clamp(left, 0, availableX) / availableX * 100;
        var yPercent = availableY <= 0
            ? 0
            : Math.Clamp(top, 0, availableY) / availableY * 100;
        Settings.WatermarkXPercent = xPercent;
        Settings.WatermarkYPercent = yPercent;
        Settings.WatermarkPosition = WatermarkPosition.Custom;
    }

    public void FinishWatermarkPositionMove()
    {
        StatusText =
            $"Vị trí logo: X {Settings.WatermarkXPercent:0.#}% • " +
            $"Y {Settings.WatermarkYPercent:0.#}%";
    }

    private void ChooseIntroImage()
    {
        var path = _dialogs.PickCardImage("Chọn ảnh mở đầu");
        if (path is null) return;
        Settings.IntroImagePath = path;
        Settings.IntroEnabled = true;
        StatusText = $"Đã chọn ảnh mở đầu: {Path.GetFileName(path)}";
    }

    private void ChooseOutroImage()
    {
        var path = _dialogs.PickCardImage("Chọn ảnh kết thúc");
        if (path is null) return;
        Settings.OutroImagePath = path;
        Settings.OutroEnabled = true;
        StatusText = $"Đã chọn ảnh kết thúc: {Path.GetFileName(path)}";
    }

    private void ChooseManualSubtitle()
    {
        if (SelectedJob is null) return;
        var path = _dialogs.PickSubtitle();
        if (path is null) return;
        SelectedJob.ManualSubtitlePath = path;
        StatusText = $"Đã gắn phụ đề SRT: {Path.GetFileName(path)}";
        _logger.Info(
            $"Đã gắn phụ đề thủ công cho {SelectedJob.FileName}: {path}");
        RaiseCommands();
        _ = AutoSaveAsync();
    }

    private void EditManualSubtitle()
    {
        if (SelectedJob?.HasManualSubtitle != true) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedJob.ManualSubtitlePath,
                UseShellExecute = true
            });
            StatusText = "Đã mở file SRT; lưu lại rồi bấm Đầu/Giữa/Cuối để xem.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Không mở được phụ đề",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ClearManualSubtitle()
    {
        if (SelectedJob is null) return;
        SelectedJob.ManualSubtitlePath = "";
        StatusText = "Đã bỏ phụ đề SRT thủ công khỏi video đang chọn.";
        RaiseCommands();
        _ = AutoSaveAsync();
    }

    private async Task SaveProjectAsync()
    {
        var path = _dialogs.SaveProject(ProjectName);
        if (path is null) return;
        await _projects.SaveAsync(Snapshot(), path, _lifetime.Token);
        _logger.Success($"Đã lưu dự án: {Path.GetFileName(path)}");
    }

    private async Task OpenProjectAsync()
    {
        var path = _dialogs.OpenProject();
        if (path is null) return;
        StatusText = "Đang mở và chuẩn bị Project…";
        var state = await _projects.LoadAsync(path, _lifetime.Token);
        if (state is not null)
        {
            StatusText =
                $"Đang chuẩn bị {state.Jobs.Count:N0} video và kế hoạch ghép…";
            await LoadStateAsync(state, _lifetime.Token);
            if (!IsMetadataAnalysisRunning)
                StatusText = $"Đã mở Project • {Jobs.Count:N0} video";
        }
    }

    private async Task InstallSubtitleAiAsync()
    {
        if (!File.Exists(_subtitles.InstallerPath))
        {
            MessageBox.Show("Không tìm thấy bộ cài phụ đề AI trong thư mục ứng dụng.",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            SubtitleAiStatusText = "Đang cài bộ AI trong cửa sổ riêng…";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = _subtitles.InstallerPath,
                WorkingDirectory = Path.GetDirectoryName(_subtitles.InstallerPath)!,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is not null) await process.WaitForExitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error($"Không mở được bộ cài phụ đề AI: {ex.Message}");
        }
        finally { RefreshSubtitleAiStatus(); }
    }

    private void RefreshSubtitleAiStatus()
    {
        RefreshSubtitleAiStatusText();
        RefreshSubtitleCacheSize();
    }

    private void RefreshSubtitleAiStatusText() =>
        SubtitleAiStatusText = _subtitles.GetStatusText();

    private void RefreshSubtitleCacheSize()
    {
        RaiseCommands();
        Interlocked.Exchange(ref _subtitleCacheRefreshRequested, 1);
        if (Interlocked.CompareExchange(
                ref _subtitleCacheRefreshRunning, 1, 0) == 0)
            _ = RefreshSubtitleCacheLoopAsync();
    }

    private async Task RefreshSubtitleCacheLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested &&
                   Interlocked.Exchange(
                       ref _subtitleCacheRefreshRequested, 0) == 1)
            {
                var cacheSize = await Task.Run(
                    _subtitles.GetCacheSizeText,
                    _lifetime.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    SubtitleCacheSizeText = cacheSize;
                    RaiseCommands();
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App đang đóng.
        }
        catch (Exception ex)
        {
            _logger.Warning("Không tính được cache phụ đề: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _subtitleCacheRefreshRunning, 0);
            if (!_lifetime.IsCancellationRequested &&
                Volatile.Read(ref _subtitleCacheRefreshRequested) == 1 &&
                Interlocked.CompareExchange(
                    ref _subtitleCacheRefreshRunning, 1, 0) == 0)
                _ = RefreshSubtitleCacheLoopAsync();
        }
    }

    private void ClearSelectedSubtitleCache()
    {
        if (SelectedJob is null) return;
        if (MessageBox.Show($"Xóa cache nhận giọng và bản dịch của video này?\n\n{SelectedJob.FileName}",
                "Ekko Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var count = _subtitles.ClearVideoCache(SelectedJob);
            _logger.Info(count > 0
                ? $"Đã xóa cache phụ đề của {SelectedJob.FileName}."
                : $"Video {SelectedJob.FileName} chưa có cache phụ đề.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không thể xóa cache", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshSubtitleCacheSize();
    }

    private void ClearAllSubtitleCache()
    {
        if (MessageBox.Show("Xóa toàn bộ cache nhận giọng, bản dịch và phụ đề đã tạo?\n\n" +
                            "Lần render sau sẽ phải phân tích lại video.",
                "Ekko Tools", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _subtitles.ClearAllCache();
            _logger.Info("Đã xóa toàn bộ cache phụ đề AI.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không thể xóa cache", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshSubtitleCacheSize();
    }

    private void RemoveSelected()
    {
        if (SelectedJob is null) return;
        var index = Jobs.IndexOf(SelectedJob);
        Jobs.Remove(SelectedJob);
        SelectedJob = Jobs.Count == 0 ? null : Jobs[Math.Clamp(index, 0, Jobs.Count - 1)];
        InvalidateQueueSummary();
        RaiseCommands();
        _ = AutoSaveAsync();
    }

    private void PermanentlyDeleteSelected()
    {
        if (SelectedJob is null) return;
        var job = SelectedJob;
        var path = job.InputPath;
        var answer = MessageBox.Show(
            $"Xóa vĩnh viễn video nguồn này?\n\n{path}\n\n" +
            "File sẽ KHÔNG được đưa vào Thùng rác và không thể khôi phục bằng ứng dụng.",
            "Ekko Tools - Xác nhận xóa vĩnh viễn",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path))
                throw new IOException("Windows vẫn báo file còn tồn tại sau khi xóa.");

            var index = Jobs.IndexOf(job);
            Jobs.Remove(job);
            SelectedJob = Jobs.Count == 0
                ? null
                : Jobs[Math.Clamp(index, 0, Jobs.Count - 1)];
            InvalidateQueueSummary();
            RaiseCommands();
            _logger.Info($"Đã xóa vĩnh viễn video nguồn: {path}");
            StatusText = $"Đã xóa vĩnh viễn: {Path.GetFileName(path)}";
            _ = AutoSaveAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Không thể xóa vĩnh viễn {path}: {ex.Message}");
            MessageBox.Show(
                $"Không thể xóa file.\n\n{ex.Message}",
                "Ekko Tools - Lỗi xóa file",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Clear()
    {
        Jobs.Clear();
        SelectedJob = null;
        SelectedPart = null;
        PreviewUri = null;
        PreviewOverlayUri = null;
        Settings.InputFolder = "";
        Settings.InputFolders = [];
        _pendingWatchedPaths.Clear();
        _watchFolder.Stop();
        CurrentFolderName = "Chưa chọn thư mục";
        StatusText = "Đã xóa danh sách và bỏ thư mục nguồn cũ.";
        _cachedMergePlans = null;
        CancelMergePlanBuild();
        InvalidateQueueSummary();
        RaiseCommands();
        _ = AutoSaveAsync();
    }

    private async Task LoadStateAsync(
        ProjectState state,
        CancellationToken token)
    {
        CancelMergePlanBuild();
        var preparedPlans = await Task.Run(() =>
        {
            foreach (var job in state.Jobs)
            {
                token.ThrowIfCancellationRequested();
                if (!HasUsableMetadata(job))
                {
                    job.Parts.Clear();
                    if (job.Status != JobStatus.Failed)
                    {
                        job.Status = JobStatus.Analyzing;
                        job.ProcessingStage = "Chờ đọc tiếp metadata";
                    }
                    else
                    {
                        job.ProcessingStage = "Lỗi đọc thông tin";
                    }
                    continue;
                }

                if (job.Status is JobStatus.Rendering or
                    JobStatus.Paused or
                    JobStatus.Analyzing)
                    job.Status = JobStatus.Ready;
                job.ProcessingStage =
                    job.Status == JobStatus.Succeeded
                        ? "Hoàn thành"
                        : "Sẵn sàng";
                for (var i = 0; i < job.Parts.Count; i++)
                {
                    job.Parts[i].Index = i + 1;
                    job.Parts[i].IsSplitPart =
                        state.Settings.SplitEnabled && job.Parts.Count > 1;
                    if (job.Parts[i].Status == PartStatus.Rendering)
                        job.Parts[i].Status = PartStatus.Pending;
                }
            }

            return state.Settings.MergeVideosEnabled &&
                   !state.Jobs.Any(job => job.Status == JobStatus.Analyzing)
                ? _mergePlanner.Build(state.Jobs, state.Settings)
                : null;
        }, token);

        ProjectName = state.Name;
        Settings = state.Settings;
        // Settings mới có thể phát sự kiện lập kế hoạch cho danh sách cũ.
        // Hủy tác vụ đó trước khi thay toàn bộ Jobs.
        CancelMergePlanBuild();
        Jobs.Clear();
        Jobs.AddRange(state.Jobs);
        SelectedJob = Jobs.FirstOrDefault();
        _cachedMergePlans = preparedPlans;
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(MergePlanSummary));
        OnPropertyChanged(nameof(MergePlanDetailsText));
        RaiseCommands();
        await Dispatcher.Yield(DispatcherPriority.Background);

        var pendingMetadata = Jobs
            .Where(job => job.Status == JobStatus.Analyzing)
            .ToArray();
        if (pendingMetadata.Length > 0)
        {
            StartMetadataAnalysis(
                pendingMetadata,
                "Đang đọc tiếp metadata",
                $"Đã khôi phục {Jobs.Count:N0} video");
        }
    }

    private ProjectState Snapshot() => new() { Name = ProjectName, Settings = Settings, Jobs = Jobs.ToList() };
    private Task AutoSaveAsync() => _projects.SaveAutoAsync(Snapshot(), _lifetime.Token);

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PresetSettings.WorkerCount) &&
            _renderQueue.IsRunning)
            _renderQueue.UpdateWorkerCount(Settings.WorkerCount);
        ScheduleSettingsAutoSave();
        if (e.PropertyName == nameof(PresetSettings.Layout) &&
            Settings.Layout == LayoutKind.Custom)
        {
            Settings.CustomMainRatio = 1;
            Settings.CustomMainCenterYPercent = 50;
            // Mục Custom trên UI nay chính là Crop bằng chuột. Dùng Crop làm
            // cách lấp đầy mặc định để khung chọn không dính viền đen của Fit
            // và không kéo méo hình như Stretch. Người dùng vẫn có thể đổi sau.
            Settings.MainVideoFit = MainVideoFitMode.Crop;
            IsCustomCropEditMode = false;
        }
        if (e.PropertyName is nameof(PresetSettings.Layout) or
            nameof(PresetSettings.CustomMainRatio) or
            nameof(PresetSettings.CustomMainCenterYPercent) or
            nameof(PresetSettings.MainVideoFit) or
            nameof(PresetSettings.CropFocusXPercent) or
            nameof(PresetSettings.CropFocusYPercent) or
            nameof(PresetSettings.MouseCropEnabled) or
            nameof(PresetSettings.MouseCropXPercent) or
            nameof(PresetSettings.MouseCropYPercent) or
            nameof(PresetSettings.MouseCropWidthPercent) or
            nameof(PresetSettings.MouseCropHeightPercent) or
            nameof(PresetSettings.MouseCropSourceAspect) or
            nameof(PresetSettings.MouseCropOutside) or
            nameof(PresetSettings.AutoFaceCropEnabled) or
            nameof(PresetSettings.FaceDetectionSampleCount) or
            nameof(PresetSettings.FaceCropZoomPercent) or
            nameof(PresetSettings.FlipMainVideo) or
            nameof(PresetSettings.VideoEffectsEnabled) or
            nameof(PresetSettings.VideoShakeEnabled) or
            nameof(PresetSettings.VideoOffsetEnabled) or
            nameof(PresetSettings.VideoColorEnabled) or
            nameof(PresetSettings.VideoNoiseEnabled) or
            nameof(PresetSettings.VideoShakeStrength) or
            nameof(PresetSettings.VideoShakeSpeed) or
            nameof(PresetSettings.VideoShakePeriodic) or
            nameof(PresetSettings.VideoShakeIntervalSeconds) or
            nameof(PresetSettings.VideoShakeDurationSeconds) or
            nameof(PresetSettings.VideoOffsetX) or
            nameof(PresetSettings.VideoOffsetY) or
            nameof(PresetSettings.VideoBrightness) or
            nameof(PresetSettings.VideoContrast) or
            nameof(PresetSettings.VideoSaturation) or
            nameof(PresetSettings.VideoNoiseStrength) or
            nameof(PresetSettings.ExistingSubtitleDetectionSampleCount) or
            nameof(PresetSettings.ExistingSubtitleDetectionSearchBottomPercent) or
            nameof(PresetSettings.ExistingSubtitleDetectionSensitivity) or
            nameof(PresetSettings.ExistingSubtitleDetectionMargin))
            InvalidateExistingSubtitleDetections();
        if (e.PropertyName is nameof(PresetSettings.WatchFolderEnabled) or
            nameof(PresetSettings.InputFolder) or
            nameof(PresetSettings.InputFolders) or
            nameof(PresetSettings.IncludeSubfolders))
            ApplyWatchFolder();
        if (e.PropertyName is nameof(PresetSettings.OutputRoot) or
            nameof(PresetSettings.MinimumFreeSpaceGb))
            RefreshDiskSpaceStatus();
        if (e.PropertyName == nameof(PresetSettings.SplitEnabled))
        {
            if (Settings.SplitEnabled && Settings.MergeVideosEnabled)
                Settings.MergeVideosEnabled = false;
        }
        if (e.PropertyName is nameof(PresetSettings.SplitEnabled) or
            nameof(PresetSettings.SplitMode) or
            nameof(PresetSettings.PartCount) or
            nameof(PresetSettings.PartDurationSeconds) or
            nameof(PresetSettings.RandomMinSeconds) or
            nameof(PresetSettings.RandomMaxSeconds) or
            nameof(PresetSettings.SmartRemainderSeconds) or
            nameof(PresetSettings.SkipSplitBelowSeconds))
        {
            if (Jobs.Count > 0 &&
                !IsMetadataAnalysisRunning &&
                !_renderQueue.IsRunning &&
                Jobs.All(job => job.Status != JobStatus.Analyzing))
                _ = RebuildPartsAsync();
        }
        if (e.PropertyName == nameof(PresetSettings.MergeVideosEnabled) &&
            Settings.MergeVideosEnabled)
        {
            // Ghép video vẫn dùng được mọi kiểu bố cục, kể cả Crop bằng chuột.
            Settings.SplitEnabled = false;
        }
        if (e.PropertyName is nameof(PresetSettings.MergeVideosEnabled) or
            nameof(PresetSettings.MergeMinSeconds) or
            nameof(PresetSettings.MergeMaxSeconds) or
            nameof(PresetSettings.MergeVideosPerOutput) or
            nameof(PresetSettings.MergeRandomSeed) or
            nameof(PresetSettings.MergeOrder) or
            nameof(PresetSettings.AutoShuffleBatchEnabled) or
            nameof(PresetSettings.AutoShuffleStreamCopyEnabled) or
            nameof(PresetSettings.AutoShuffleTargetCount) or
            nameof(PresetSettings.VideoSpeed))
        {
            InvalidateQueueSummary();
        }
        if (e.PropertyName == nameof(PresetSettings.SubtitleOutput) &&
            Settings.SubtitleOutput == SubtitleOutputMode.BilingualBurnInAndSidecar &&
            Settings.SubtitleTargetLanguage == "original")
            Settings.SubtitleTargetLanguage = "auto-translate";
        if (Settings.MergeVideosEnabled &&
            Settings.AutoSubtitlesEnabled &&
            Settings.SubtitleOutput == SubtitleOutputMode.SidecarOnly)
            Settings.SubtitleOutput = SubtitleOutputMode.BurnIn;
        if (e.PropertyName is nameof(PresetSettings.AutoSubtitlesEnabled) or
            nameof(PresetSettings.SubtitleSourceLanguage) or
            nameof(PresetSettings.SubtitleTargetLanguage) or
            nameof(PresetSettings.SubtitleOutput) or
            nameof(PresetSettings.MergeVideosEnabled))
            OnPropertyChanged(nameof(SubtitleModeSummary));
        if (e.PropertyName is nameof(PresetSettings.Layout) or
            nameof(PresetSettings.CustomMainRatio) or
            nameof(PresetSettings.CustomMainCenterYPercent) or
            nameof(PresetSettings.MainVideoFit) or
            nameof(PresetSettings.CropFocusXPercent) or
            nameof(PresetSettings.CropFocusYPercent) or
            nameof(PresetSettings.MouseCropEnabled) or
            nameof(PresetSettings.MouseCropXPercent) or
            nameof(PresetSettings.MouseCropYPercent) or
            nameof(PresetSettings.MouseCropWidthPercent) or
            nameof(PresetSettings.MouseCropHeightPercent) or
            nameof(PresetSettings.MouseCropSourceAspect) or
            nameof(PresetSettings.MouseCropOutside) or
            nameof(PresetSettings.AutoFaceCropEnabled) or
            nameof(PresetSettings.FaceDetectionSampleCount) or
            nameof(PresetSettings.FaceCropZoomPercent))
        {
            OnPropertyChanged(nameof(CustomVideoTop));
            OnPropertyChanged(nameof(CustomVideoHeight));
            OnPropertyChanged(nameof(CustomVideoBottomHandleTop));
            OnPropertyChanged(nameof(CustomVideoRegionText));
            RaiseMouseCropPreviewProperties();
            if (!Settings.IsCustomLayout) IsCustomCropEditMode = false;
            if (e.PropertyName == nameof(PresetSettings.Layout))
                IsMouseCropEditMode = false;
            if (PreviewUri is not null)
                StatusText = "Bố cục đã đổi • bấm Xem thử 6 giây hoặc chỉnh trực tiếp trên màn hình.";
            RaiseCommands();
        }
        if (e.PropertyName == nameof(PresetSettings.OutputResolution))
        {
            OnPropertyChanged(nameof(CustomVideoTop));
            OnPropertyChanged(nameof(CustomVideoHeight));
            OnPropertyChanged(nameof(CustomVideoBottomHandleTop));
            OnPropertyChanged(nameof(SubtitlePreviewLeft));
            OnPropertyChanged(nameof(SubtitlePreviewTop));
            RaiseMouseCropPreviewProperties();
            RaiseWatermarkPreviewProperties();
            StatusText = $"Độ phân giải đầu ra: {Settings.OutputWidth} × {Settings.OutputHeight} • bấm Xem thử 6 giây để cập nhật preview.";
            if (HasPreview)
            {
                PreviewUri = null;
                PreviewOverlayUri = null;
            }
            RaiseCommands();
        }
        if (e.PropertyName == nameof(PresetSettings.FlipMainVideo))
            _ = RefreshPreviewAfterFlipSafeAsync();
        if (e.PropertyName == nameof(PresetSettings.MainVideoFit) ||
            e.PropertyName == nameof(PresetSettings.MouseCropOutside))
            _ = RefreshPreviewAfterMainVideoFitSafeAsync();
        if (e.PropertyName is nameof(PresetSettings.SubtitleHorizontalOffset) or nameof(PresetSettings.SubtitleBottomMargin))
        {
            OnPropertyChanged(nameof(SubtitlePreviewLeft));
            OnPropertyChanged(nameof(SubtitlePreviewTop));
        }
        if (e.PropertyName == nameof(PresetSettings.TextOverlayEnabled))
        {
            if (!Settings.TextOverlayEnabled)
                PreviewOverlayUri = null;
            else if (HasPreview)
                _ = RefreshPreviewTitleOverlaySafeAsync();
        }
        if (e.PropertyName == nameof(PresetSettings.PartTextAboveTitle) && HasPreview)
            _ = RefreshPreviewTitleOverlaySafeAsync();
        if (e.PropertyName is nameof(PresetSettings.VideoEffectsEnabled) or
            nameof(PresetSettings.VideoShakeEnabled) or
            nameof(PresetSettings.VideoOffsetEnabled) or
            nameof(PresetSettings.VideoColorEnabled) or
            nameof(PresetSettings.VideoNoiseEnabled) or
            nameof(PresetSettings.VideoShakeStrength) or
            nameof(PresetSettings.VideoShakeSpeed) or
            nameof(PresetSettings.VideoShakePeriodic) or
            nameof(PresetSettings.VideoShakeIntervalSeconds) or
            nameof(PresetSettings.VideoShakeDurationSeconds) or
            nameof(PresetSettings.VideoOffsetX) or
            nameof(PresetSettings.VideoOffsetY) or
            nameof(PresetSettings.VideoBrightness) or
            nameof(PresetSettings.VideoContrast) or
            nameof(PresetSettings.VideoSaturation) or
            nameof(PresetSettings.VideoNoiseStrength))
        {
            if (PreviewUri is not null)
                StatusText = "Hiệu ứng đã đổi • bấm Xem thử 6 giây để kiểm tra.";
        }
        if (e.PropertyName is nameof(PresetSettings.BlurRadius) or
            nameof(PresetSettings.BlurZoom) or
            nameof(PresetSettings.BlurBrightness) or
            nameof(PresetSettings.BlurDarkness))
        {
            if (PreviewUri is not null)
                StatusText = Settings.BlurRadius <= 0.01
                    ? "Vùng nền đang NÉT (mờ 0) • bấm Xem thử 6 giây để cập nhật preview."
                    : $"Chỉ vùng nền đang mờ mức {Settings.BlurRadius:0} • video chính vẫn nét • bấm Xem thử 6 giây.";
        }
        if (e.PropertyName is nameof(PresetSettings.AudioEffectsEnabled) or
            nameof(PresetSettings.AudioVolumeEnabled) or
            nameof(PresetSettings.AudioPitchEnabled) or
            nameof(PresetSettings.AudioEqEnabled) or
            nameof(PresetSettings.AudioEchoEnabled) or
            nameof(PresetSettings.AudioNoiseReductionEnabled) or
            nameof(PresetSettings.AudioCompressorEnabled) or
            nameof(PresetSettings.AudioNormalizeEnabled) or
            nameof(PresetSettings.AudioVolume) or
            nameof(PresetSettings.AudioPitchSemitones) or
            nameof(PresetSettings.AudioBassGain) or
            nameof(PresetSettings.AudioTrebleGain) or
            nameof(PresetSettings.AudioEchoDelayMs) or
            nameof(PresetSettings.AudioEchoStrength))
        {
            if (PreviewUri is not null)
                StatusText = "Âm thanh đã đổi • bấm Xem thử 6 giây để nghe trước.";
        }
        if (e.PropertyName is nameof(PresetSettings.WatermarkPath) or
            nameof(PresetSettings.WatermarkEnabled) or
            nameof(PresetSettings.WatermarkAutoRemoveBackground) or
            nameof(PresetSettings.WatermarkBackgroundTolerance))
        {
            RefreshWatermarkPreview();
            if (!Settings.WatermarkEnabled || WatermarkPreviewSource is null)
                IsWatermarkPositionEditMode = false;
            RaiseCommands();
        }
        if (e.PropertyName is nameof(PresetSettings.WatermarkPosition) or
            nameof(PresetSettings.WatermarkWidthPercent) or
            nameof(PresetSettings.WatermarkMargin) or
            nameof(PresetSettings.WatermarkXPercent) or
            nameof(PresetSettings.WatermarkYPercent))
            RaiseWatermarkPreviewProperties();
        if (e.PropertyName is nameof(PresetSettings.ExistingSubtitleBlurEnabled) or
            nameof(PresetSettings.AutoDetectExistingSubtitleRegion) or
            nameof(PresetSettings.ExistingSubtitleBlurX) or
            nameof(PresetSettings.ExistingSubtitleBlurY) or
            nameof(PresetSettings.ExistingSubtitleBlurWidth) or
            nameof(PresetSettings.ExistingSubtitleBlurHeight) or
            nameof(PresetSettings.ExistingSubtitleBlurStrength) or
            nameof(PresetSettings.ExistingSubtitleDetectionSampleCount) or
            nameof(PresetSettings.ExistingSubtitleDetectionSearchBottomPercent) or
            nameof(PresetSettings.ExistingSubtitleDetectionSensitivity) or
            nameof(PresetSettings.ExistingSubtitleDetectionMargin))
        {
            if (Settings.AutoDetectExistingSubtitleRegion)
                IsExistingSubtitleBlurEditMode = false;
            RaiseExistingSubtitleBlurPreviewProperties();
            if (PreviewUri is not null)
                StatusText = Settings.AutoDetectExistingSubtitleRegion
                    ? "Nhận diện vùng sub cũ đã đổi • bấm Tự dò lại hoặc Xem thử 6 giây."
                    : "Vùng làm mờ sub cũ đã đổi • bấm Xem thử 6 giây để kiểm tra.";
        }
    }

    private void InvalidateExistingSubtitleDetections()
    {
        // Chỉ xóa ngay cache của dòng đang xem để khung preview không cũ.
        // Các dòng còn lại tự so khóa thiết lập khi đến lượt xử lý; tránh quét
        // hàng chục nghìn VideoJob mỗi lần người dùng kéo một slider.
        if (SelectedJob is { } job)
        {
            job.ExistingSubtitleDetectionCompleted = false;
            job.ExistingSubtitleDetectionKey = "";
            job.DetectedExistingSubtitleBlurRegion = null;
        }
        RaiseExistingSubtitleBlurPreviewProperties();
    }

    private void RaiseMouseCropPreviewProperties()
    {
        OnPropertyChanged(nameof(MouseCropSummary));
        OnPropertyChanged(nameof(MouseCropAspectText));
        OnPropertyChanged(nameof(MouseCropOutsideText));
        OnPropertyChanged(nameof(IsAutoLandscapePreviewActive));
        OnPropertyChanged(nameof(ShowPreviewEditOverlayCanvas));
        OnPropertyChanged(nameof(PreviewCanvasWidth));
        OnPropertyChanged(nameof(PreviewCanvasHeight));
        OnPropertyChanged(nameof(PreviewBannerLeft));
        OnPropertyChanged(nameof(PreviewAspectText));
        OnPropertyChanged(nameof(MainVideoPreviewTop));
        OnPropertyChanged(nameof(MainVideoPreviewWidth));
        OnPropertyChanged(nameof(MainVideoPreviewHeight));
        OnPropertyChanged(nameof(MouseCropPreviewLeft));
        OnPropertyChanged(nameof(MouseCropPreviewTop));
        OnPropertyChanged(nameof(MouseCropPreviewWidth));
        OnPropertyChanged(nameof(MouseCropPreviewHeight));
    }

    private void RaiseExistingSubtitleBlurPreviewProperties()
    {
        OnPropertyChanged(nameof(ExistingSubtitleBlurPreviewX));
        OnPropertyChanged(nameof(ExistingSubtitleBlurPreviewY));
        OnPropertyChanged(nameof(ExistingSubtitleBlurPreviewWidth));
        OnPropertyChanged(nameof(ExistingSubtitleBlurPreviewHeight));
        OnPropertyChanged(nameof(ExistingSubtitleBlurRegionLabel));
        OnPropertyChanged(nameof(ExistingSubtitleDetectionStatusText));
    }

    private void RaiseWatermarkPreviewProperties()
    {
        OnPropertyChanged(nameof(HasWatermarkPreview));
        OnPropertyChanged(nameof(WatermarkPreviewWidth));
        OnPropertyChanged(nameof(WatermarkPreviewHeight));
        OnPropertyChanged(nameof(WatermarkPreviewLeft));
        OnPropertyChanged(nameof(WatermarkPreviewTop));
    }

    private void ApplyWatchFolder()
    {
        var folders = Settings.InputFolders
            .Append(Settings.InputFolder)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (Settings.WatchFolderEnabled && folders.Length > 0)
            _watchFolder.Start(folders, Settings.IncludeSubfolders);
        else _watchFolder.Stop();
    }

    private Task WatchFolderOnVideoReady(string path)
    {
        _pendingWatchedPaths[path] = 0;
        StartWatchFolderDrain();
        return Task.CompletedTask;
    }

    private void StartWatchFolderDrain()
    {
        if (Interlocked.CompareExchange(ref _watchDrainActive, 1, 0) == 0)
            _ = DrainWatchFolderQueueAsync();
    }

    private async Task DrainWatchFolderQueueAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                while ((IsBusy ||
                        IsMetadataAnalysisRunning ||
                        _renderQueue.IsRunning) &&
                       !_lifetime.IsCancellationRequested)
                    await Task.Delay(300, _lifetime.Token);

                var batch = _pendingWatchedPaths.Keys.Take(100).ToArray();
                if (batch.Length == 0) break;
                foreach (var path in batch)
                    _pendingWatchedPaths.TryRemove(path, out _);

                await Application.Current.Dispatcher
                    .InvokeAsync(() => AddPathsAsync(batch))
                    .Task.Unwrap();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App đang đóng.
        }
        catch (Exception ex)
        {
            _logger.Warning("Watch Folder: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _watchDrainActive, 0);
            if (!_pendingWatchedPaths.IsEmpty &&
                !_lifetime.IsCancellationRequested)
                StartWatchFolderDrain();
        }
    }

    private void LoggerOnEntryAdded(object? sender, LogEntry entry)
    {
        void Add()
        {
            Logs.Add(entry);
            while (Logs.Count > 1000) Logs.RemoveAt(0);
            RaiseCommands();
        }
        if (Application.Current.Dispatcher.CheckAccess()) Add(); else Application.Current.Dispatcher.BeginInvoke(Add);
    }

    private void CopySelectedLog()
    {
        if (SelectedLog is null) return;
        try
        {
            Clipboard.SetText(SelectedLog.CopyText);
            StatusText = SelectedLog.Severity == LogSeverity.Error
                ? "Đã sao chép lỗi được chọn."
                : "Đã sao chép dòng nhật ký được chọn.";
        }
        catch (Exception ex)
        {
            StatusText = "Không sao chép được nhật ký: " + ex.Message;
        }
    }

    private void CopyAllErrors()
    {
        var errors = Logs
            .Where(entry => entry.Severity == LogSeverity.Error)
            .Select(entry => entry.CopyText)
            .ToArray();
        if (errors.Length == 0) return;
        try
        {
            Clipboard.SetText(
                string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    new string('-', 72) +
                    $"{Environment.NewLine}{Environment.NewLine}",
                    errors));
            StatusText = $"Đã sao chép {errors.Length} lỗi.";
        }
        catch (Exception ex)
        {
            StatusText = "Không sao chép được danh sách lỗi: " + ex.Message;
        }
    }

    private void QueueOnStateChanged()
    {
        if (_renderQueue.IsRunning)
            _queueProgressStartedAt ??= DateTimeOffset.UtcNow;
        else
        {
            _currentQueueProgress = null;
            _queueProgressStartedAt = null;
        }
        OnPropertyChanged(nameof(IsRendering));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(QueueSummary));
        RaiseOverallProgressProperties();
        RaiseCommands();
    }

    private void QueueOnProgressChanged(RenderQueueProgress progress)
    {
        void Update()
        {
            if (!_renderQueue.IsRunning) return;
            _currentQueueProgress = progress;
            OnPropertyChanged(nameof(QueueSummary));
            RaiseOverallProgressProperties();
            var succeeded = Math.Max(0, progress.Completed - progress.Failed);
            var isSubtitleStage = progress.Stage.StartsWith(
                "Phụ đề AI",
                StringComparison.OrdinalIgnoreCase);
            StatusText = isSubtitleStage
                ? $"{progress.Stage} • đã xử lý {succeeded:N0}/" +
                  $"{progress.Total:N0} video • tổng {progress.Percent:0.0}%"
                : $"{progress.Stage}: {succeeded:N0}/{progress.Total:N0} video xong" +
                  (progress.Active > 0
                      ? $" • đang xử lý {progress.Active:N0}"
                      : "") +
                  (progress.Failed > 0
                      ? $" • lỗi {progress.Failed:N0}"
                      : "");
        }

        if (Application.Current.Dispatcher.CheckAccess()) Update();
        else Application.Current.Dispatcher.BeginInvoke(Update);
    }

    private void RaiseOverallProgressProperties()
    {
        OnPropertyChanged(nameof(OverallProgressPercent));
        OnPropertyChanged(nameof(OverallProgressPercentText));
        OnPropertyChanged(nameof(OverallProgressStageText));
        OnPropertyChanged(nameof(OverallProgressDetailsText));
        OnPropertyChanged(nameof(OverallProgressEtaText));
    }

    private static string FormatQueueDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359_999));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void QueueOnSourcesDeleted(IReadOnlyList<VideoJob> deletedJobs)
    {
        void Update()
        {
            if (deletedJobs.Count == 0) return;
            var deletedIds = deletedJobs
                .Select(job => job.Id)
                .ToHashSet();
            var deletedPaths = deletedJobs
                .Select(job => job.InputPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedWasDeleted = SelectedJob is not null &&
                                     (deletedIds.Contains(SelectedJob.Id) ||
                                      deletedPaths.Contains(SelectedJob.InputPath));
            var selectedIndex = SelectedJob is null
                ? 0
                : Math.Max(0, Jobs.IndexOf(SelectedJob));

            var removedJobs = Jobs
                .Where(job =>
                    deletedIds.Contains(job.Id) ||
                    deletedPaths.Contains(job.InputPath))
                .ToArray();
            Jobs.RemoveRange(removedJobs);
            foreach (var path in deletedPaths)
                _pendingWatchedPaths.TryRemove(path, out _);

            if (selectedWasDeleted)
                SelectedJob = Jobs.Count == 0
                    ? null
                    : Jobs[Math.Clamp(selectedIndex, 0, Jobs.Count - 1)];

            if (_cachedMergePlans is not null)
                _cachedMergePlans = _cachedMergePlans
                    .Where(plan => plan.Segments.All(segment =>
                        !deletedIds.Contains(segment.Job.Id) &&
                        !deletedPaths.Contains(segment.Job.InputPath)))
                    .ToArray();

            var existingFolders = Settings.InputFolders
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existingFolders.Count != Settings.InputFolders.Count)
                Settings.InputFolders = existingFolders;
            if (!Directory.Exists(Settings.InputFolder))
                Settings.InputFolder =
                    existingFolders.FirstOrDefault() ?? "";

            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(MergePlanSummary));
            OnPropertyChanged(nameof(Jobs));
            StatusText =
                $"Đã xóa {removedJobs.Length:N0} video nguồn và cập nhật danh sách còn {Jobs.Count:N0} video.";
            _logger.Success(
                $"Danh sách video đã cập nhật: gỡ {removedJobs.Length:N0} nguồn đã xóa; " +
                $"còn {Jobs.Count:N0} video.");
            RaiseCommands();
            ScheduleSourceDeletionSave();
        }

        if (Application.Current.Dispatcher.CheckAccess()) Update();
        else Application.Current.Dispatcher.BeginInvoke(Update);
    }

    private void ScheduleSettingsAutoSave()
    {
        if (_lifetime.IsCancellationRequested) return;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _settingsAutoSaveSource;
        _settingsAutoSaveSource = source;
        previous?.Cancel();
        previous?.Dispose();
        _ = SaveSettingsAfterDelayAsync(source);
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(900, source.Token);
            await _projects.SaveAutoAsync(Snapshot(), source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Người dùng tiếp tục chỉnh setting hoặc app đang đóng.
        }
        catch (Exception ex)
        {
            _logger.Warning($"Tự lưu cài đặt: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_settingsAutoSaveSource, source))
                _settingsAutoSaveSource = null;
            source.Dispose();
        }
    }

    private void ScheduleSourceDeletionSave()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        var previous = _sourceDeletionSaveSource;
        _sourceDeletionSaveSource = source;
        previous?.Cancel();
        _ = SaveAfterSourceDeletionAsync(source);
    }

    private async Task SaveAfterSourceDeletionAsync(
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(500, source.Token);
            await AutoSaveAsync();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Gộp nhiều lần xóa liên tiếp thành một lần tự lưu.
        }
        catch (Exception ex)
        {
            _logger.Warning($"Tự lưu sau khi xóa nguồn: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_sourceDeletionSaveSource, source))
                _sourceDeletionSaveSource = null;
            source.Dispose();
        }
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { PickFolderCommand, AddVideosCommand,
            RenderCommand, PauseResumeCommand, CancelCommand, RetryCommand, PreviewCommand,
                     PreviewStartCommand, PreviewMiddleCommand, PreviewEndCommand,
                     ThumbnailsCommand, RemoveSelectedCommand, PermanentlyDeleteSelectedCommand,
                     ClearCommand, RebuildPartsCommand, SmartTrimSelectedCommand,
                     RefreshListCommand, RefreshSubtitleCacheCommand,
                     CancelMetadataAnalysisCommand,
                     ClearSelectedSubtitleCacheCommand, ClearAllSubtitleCacheCommand,
                     CopySelectedLogCommand, CopyAllErrorsCommand,
                     ToggleSubtitlePositionEditCommand, ToggleCustomCropEditCommand,
                     ToggleMouseCropEditCommand, AutoMouseCropCommand, ResetMouseCropCommand,
                     ToggleExistingSubtitleBlurEditCommand,
                     ToggleWatermarkPositionEditCommand,
                     DetectExistingSubtitleBlurCommand,
                     ChooseManualSubtitleCommand, EditManualSubtitleCommand,
                     ClearManualSubtitleCommand,
                     ClearWatermarkImageCommand,
                     ApplyPlatformPresetCommand, SavePresetCommand,
                     LoadPresetCommand, DeletePresetCommand,
                     RegenerateMergePlanCommand,
                     SaveProjectCommand, OpenProjectCommand })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            else if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
    }

    private void InvalidateQueueSummary()
    {
        CancelMergePlanBuild();
        _cachedMergePlans = null;
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(MergePlanSummary));
        OnPropertyChanged(nameof(MergePlanDetailsText));
        ScheduleMergePlanBuild();
    }

    private void ScheduleMergePlanBuild()
    {
        if (!Settings.MergeVideosEnabled ||
            Jobs.Count == 0 ||
            IsBusy ||
            IsMetadataAnalysisRunning ||
            Jobs.Any(job => job.Status == JobStatus.Analyzing) ||
            _renderQueue.IsRunning ||
            _lifetime.IsCancellationRequested)
            return;

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _mergePlanBuildSource = source;
        var jobs = Jobs.ToArray();
        var settings = CreateMergePlanningSettings();
        _ = BuildAndPublishMergePlansAsync(jobs, settings, source);
    }

    private async Task BuildAndPublishMergePlansAsync(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings,
        CancellationTokenSource source)
    {
        try
        {
            var plans = await BuildMergePlansAsync(jobs, settings, source.Token);
            if (source.IsCancellationRequested ||
                !ReferenceEquals(_mergePlanBuildSource, source))
                return;
            _cachedMergePlans = plans;
            OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(MergePlanSummary));
            OnPropertyChanged(nameof(MergePlanDetailsText));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Một thay đổi mới đã thay thế kế hoạch cũ.
        }
        catch (Exception ex)
        {
            if (!source.IsCancellationRequested)
            {
                _cachedMergePlans = [];
                _logger.Warning("Lập kế hoạch ghép: " + ex.Message);
                OnPropertyChanged(nameof(QueueSummary));
                OnPropertyChanged(nameof(MergePlanSummary));
                OnPropertyChanged(nameof(MergePlanDetailsText));
            }
        }
        finally
        {
            if (ReferenceEquals(_mergePlanBuildSource, source))
                _mergePlanBuildSource = null;
            source.Dispose();
        }
    }

    private Task<IReadOnlyList<MergeOutputPlan>> BuildMergePlansAsync(
        IReadOnlyList<VideoJob> jobs,
        PresetSettings settings,
        CancellationToken token) =>
        Task.Run<IReadOnlyList<MergeOutputPlan>>(
            () => _mergePlanner.Build(jobs, settings),
            token);

    private PresetSettings CreateMergePlanningSettings() => new()
    {
        MergeMinSeconds = Settings.MergeMinSeconds,
        MergeMaxSeconds = Settings.MergeMaxSeconds,
        MergeVideosPerOutput = Settings.MergeVideosPerOutput,
        MergeRandomSeed = Settings.MergeRandomSeed,
        MergeOrder = Settings.MergeOrder,
        AutoShuffleBatchEnabled = Settings.AutoShuffleBatchEnabled,
        AutoShuffleStreamCopyEnabled = Settings.AutoShuffleStreamCopyEnabled,
        AutoShuffleTargetCount = Settings.AutoShuffleTargetCount,
        VideoSpeed = Settings.AutoShuffleBatchEnabled && Settings.AutoShuffleStreamCopyEnabled
            ? 1
            : Settings.VideoSpeed
    };

    private PresetSettings CreateAutoShuffleRenderSettings()
    {
        var options = ProjectService.CreateOptions();
        var json = JsonSerializer.Serialize(Settings, options);
        var renderSettings =
            JsonSerializer.Deserialize<PresetSettings>(json, options) ??
            new PresetSettings();

        // Tự xào tái sử dụng một nguồn ở nhiều output. RenderQueue sẽ chỉ xóa
        // nguồn SAU KHI đủ toàn bộ mục tiêu và mọi output đều thành công.
        // Giữ nguyên DeleteSourceAfterSuccess của người dùng để checkbox hiện có
        // quyết định có xóa nguồn sau khi đạt đủ mục tiêu hay không.
        renderSettings.DeleteFailedSourceAfterRetries = false;
        renderSettings.EmptyRecycleBinAfterSuccessfulRun = false;
        // Bắt buộc bỏ qua output đã hoàn thành để một phiên dở tiếp tục đúng chỗ.
        renderSettings.SkipExisting = true;
        if (renderSettings.AutoShuffleStreamCopyEnabled)
        {
            // Stream copy chỉ nối nguyên luồng. Mọi chức năng cần decode/filter/encode
            // phải bị bỏ qua trong đúng chế độ xào siêu nhanh này.
            renderSettings.VideoSpeed = 1;
            renderSettings.TitleTextEnabled = false;
            renderSettings.AutoSubtitlesEnabled = false;
            renderSettings.ExistingSubtitleBlurEnabled = false;
            renderSettings.WatermarkEnabled = false;
            renderSettings.IntroEnabled = false;
            renderSettings.OutroEnabled = false;
            renderSettings.VideoEffectsEnabled = false;
        }
        return renderSettings;
    }

    private PresetSettings CreateLongClipEditSettings()
    {
        var options = ProjectService.CreateOptions();
        var json = JsonSerializer.Serialize(Settings, options);
        var editSettings =
            JsonSerializer.Deserialize<PresetSettings>(json, options) ??
            new PresetSettings();
        editSettings.MergeVideosEnabled = false;
        editSettings.SplitEnabled = false;
        editSettings.OutputRoot = Path.Combine(Settings.OutputRoot, "Edit");
        return editSettings;
    }

    private void CancelMergePlanBuild()
    {
        try { _mergePlanBuildSource?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancelMergePlanBuild();
        _sourceDeletionSaveSource?.Cancel();
        _settingsAutoSaveSource?.Cancel();
        _metadataAnalysisSource?.Cancel();
        _lifetime.Cancel();
        _renderQueue.StateChanged -= QueueOnStateChanged;
        _renderQueue.ProgressChanged -= QueueOnProgressChanged;
        _renderQueue.SourcesDeleted -= QueueOnSourcesDeleted;
        _renderQueue.Dispose();
        _watchFolder.Dispose();
        _lifetime.Dispose();
    }

    private static IReadOnlyList<string> LoadFontOptions()
    {
        var installed = Fonts.SystemFontFamilies
            .Select(x => x.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var installedSet = installed.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        string[] preferred =
        [
            "Segoe UI", "Arial", "Tahoma", "Verdana", "Calibri", "Cambria", "Times New Roman",
            "Noto Sans", "Noto Sans CJK JP", "Yu Gothic UI", "Microsoft YaHei UI", "Malgun Gothic"
        ];
        return preferred.Where(installedSet.Contains)
            .Concat(installed)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void NormalizeSettingChoices(PresetSettings settings)
    {
        if (settings.MergeVideosEnabled)
        {
            settings.Layout = LayoutKind.Original;
            settings.SplitEnabled = false;
        }
        // Chuyển tùy chọn cũ “bỏ giữ chữ số” sang tên rõ nghĩa hơn.
        if (!settings.KeepDigits)
        {
            settings.RemoveAllDigits = true;
            settings.KeepDigits = true;
        }
        // Bản trước từng dùng tùy chọn xóa mọi chữ số. Chuyển sang chỉ xóa ID dài
        // để giữ lại năm 2026 và những số ngắn có nghĩa.
        if (settings.RemoveAllDigits)
        {
            settings.RemoveNumericIds = true;
            settings.RemoveAllDigits = false;
        }
        // Chuyển vị trí mặc định cũ xuống vùng 35% dưới của bố cục 35–30–35.
        if (settings.Layout == LayoutKind.Layout353035 && Math.Abs(settings.TitleTop - 1260) < 0.01)
            settings.TitleTop = 1340;
        // Bản cũ tự làm tối nền ở mức 0,25. Chuyển giá trị mặc định cũ về trung tính.
        if (Math.Abs(settings.BlurDarkness - 0.25) < 0.0001)
            settings.BlurDarkness = 0;
        if (!SubtitleSourceOptions.Any(x => x.Value == settings.SubtitleSourceLanguage))
            settings.SubtitleSourceLanguage = "auto";
        if (!SubtitleTargetOptions.Any(x => x.Value == settings.SubtitleTargetLanguage))
            settings.SubtitleTargetLanguage = "original";
        if (!Enum.IsDefined(typeof(SubtitleOutputMode), settings.SubtitleOutput))
            settings.SubtitleOutput = SubtitleOutputMode.BurnIn;
        if (settings.SubtitleOutput == SubtitleOutputMode.BilingualBurnInAndSidecar &&
            settings.SubtitleTargetLanguage == "original")
            settings.SubtitleTargetLanguage = "auto-translate";
        if (settings.MergeVideosEnabled && settings.AutoSubtitlesEnabled &&
            settings.SubtitleOutput == SubtitleOutputMode.SidecarOnly)
            settings.SubtitleOutput = SubtitleOutputMode.BurnIn;
        if (!Enum.IsDefined(typeof(SubtitleDisplayMode), settings.SubtitleDisplay))
            settings.SubtitleDisplay = SubtitleDisplayMode.Sentence;
        if (!Enum.IsDefined(typeof(MainVideoFitMode), settings.MainVideoFit))
            settings.MainVideoFit = MainVideoFitMode.Fit;
        if (!Enum.IsDefined(typeof(MouseCropOutsideMode), settings.MouseCropOutside))
            settings.MouseCropOutside = MouseCropOutsideMode.BlurOutside;
        if (settings.Layout == LayoutKind.Custom)
        {
            settings.CustomMainRatio = 1;
            settings.CustomMainCenterYPercent = 50;
        }
        settings.MouseCropXPercent = Math.Clamp(settings.MouseCropXPercent, 0, 98);
        settings.MouseCropYPercent = Math.Clamp(settings.MouseCropYPercent, 0, 98);
        settings.MouseCropWidthPercent = Math.Clamp(
            settings.MouseCropWidthPercent, 2, 100 - settings.MouseCropXPercent);
        settings.MouseCropHeightPercent = Math.Clamp(
            settings.MouseCropHeightPercent, 2, 100 - settings.MouseCropYPercent);
        // Chuyển checkbox "tự lấy tâm crop theo mặt" của bản cũ thành một
        // lựa chọn chính thức trong danh sách Cách lấp đầy vùng video.
        if (settings.AutoFaceCropEnabled)
        {
            if (settings.MainVideoFit == MainVideoFitMode.Crop)
                settings.MainVideoFit = MainVideoFitMode.FaceCrop;
            settings.AutoFaceCropEnabled = false;
        }
        if (!Enum.IsDefined(typeof(OutputPlatformPreset), settings.OutputPlatform))
            settings.OutputPlatform = OutputPlatformPreset.Custom;
        if (!Enum.IsDefined(typeof(OutputResolutionPreset), settings.OutputResolution))
            settings.OutputResolution = OutputResolutionPreset.Hd720x1280;
        if (!Enum.IsDefined(typeof(WatermarkPosition), settings.WatermarkPosition))
            settings.WatermarkPosition = WatermarkPosition.BottomRight;
        if (!Enum.IsDefined(typeof(MergeOrderMode), settings.MergeOrder))
            settings.MergeOrder = MergeOrderMode.Random;
        if (!FontOptions.Contains(settings.SubtitleFontFamily, StringComparer.CurrentCultureIgnoreCase))
            settings.SubtitleFontFamily = FontOptions.FirstOrDefault(x => x.Equals("Arial", StringComparison.OrdinalIgnoreCase))
                                            ?? FontOptions.FirstOrDefault()
                                            ?? "Arial";
        settings.TitleFrameColor = NormalizeOpaqueColor(settings.TitleFrameColor);
        if (FontOptions.Contains(settings.FontFamily, StringComparer.CurrentCultureIgnoreCase)) return;
        if (settings.FontFamily.Contains("SemiBold", StringComparison.OrdinalIgnoreCase))
            settings.FontWeightName = "SemiBold";
        else if (settings.FontFamily.Contains("Bold", StringComparison.OrdinalIgnoreCase))
            settings.FontWeightName = "Bold";
        settings.FontFamily = FontOptions.FirstOrDefault(x => x.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase))
                              ?? FontOptions.FirstOrDefault()
                              ?? "Segoe UI";
    }

    private static string NormalizeOpaqueColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#FF000000";
        var color = value.Trim();
        return color.Length == 9 && color[0] == '#'
            ? "#FF" + color[3..]
            : color;
    }

    private static bool HasUsableMetadata(VideoJob job) =>
        job.Media.HasAudio &&
        job.Media.DurationSeconds > 0 &&
        job.Media.Width > 0 &&
        job.Media.Height > 0 &&
        job.SourceLength > 0;

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"hh\:mm\:ss")
            : span.ToString(@"mm\:ss");
    }

    private static string FormatRemainingTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return "đang tính";

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} ngày {span.Hours} giờ";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} giờ {span.Minutes:00} phút";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} phút {span.Seconds:00} giây";
        return $"{Math.Max(1, span.Seconds)} giây";
    }
}
