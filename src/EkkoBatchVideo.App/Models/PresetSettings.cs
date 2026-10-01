using EkkoBatchVideo.Infrastructure;
using System.Text.Json.Serialization;

namespace EkkoBatchVideo.Models;

public sealed class PresetSettings : ObservableObject
{
    private string _name = "Fast 1080P";
    private string _inputFolder = "";
    private List<string> _inputFolders = [];
    private string _outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ekko Output");
    private string _ffmpegPath = "";
    private string _ffprobePath = "";
    private bool _watchFolderEnabled;
    private MergeTitleMode _mergeTitleMode = MergeTitleMode.AllVideoTitles;
    private bool _includeSubfolders;
    private bool _autoLandscapePreviewEnabled = true;
    private LayoutKind _layout = LayoutKind.Original;
    private double _customMainRatio = 0.5;
    private double _customMainCenterYPercent = 50;
    private MainVideoFitMode _mainVideoFit = MainVideoFitMode.Fit;
    private double _cropFocusXPercent = 50;
    private double _cropFocusYPercent = 50;
    private bool _mouseCropEnabled;
    private double _mouseCropXPercent;
    private double _mouseCropYPercent;
    private double _mouseCropWidthPercent = 100;
    private double _mouseCropHeightPercent = 100;
    private double _mouseCropSourceAspect = 9.0 / 16.0;
    private MouseCropOutsideMode _mouseCropOutsideMode = MouseCropOutsideMode.BlurOutside;
    private bool _autoFaceCropEnabled;
    private int _faceDetectionSampleCount = 5;
    private double _faceCropZoomPercent = 135;
    private bool _videoEffectsEnabled;
    private bool _videoShakeEnabled;
    private bool _videoOffsetEnabled;
    private bool _videoColorEnabled;
    private bool _videoNoiseEnabled;
    private double _videoShakeStrength;
    private double _videoShakeSpeed = 3;
    private bool _videoShakePeriodic;
    private double _videoShakeIntervalSeconds = 5;
    private double _videoShakeDurationSeconds = 0.35;
    private double _videoOffsetX;
    private double _videoOffsetY;
    private double _videoBrightness;
    private double _videoContrast = 1;
    private double _videoSaturation = 1;
    private double _videoNoiseStrength;
    private bool _audioEffectsEnabled;
    private bool _audioVolumeEnabled;
    private bool _audioPitchEnabled;
    private bool _audioEqEnabled;
    private bool _audioEchoEnabled;
    private bool _audioNoiseReductionEnabled;
    private bool _audioCompressorEnabled;
    private bool _audioNormalizeEnabled;
    private double _audioVolume = 1;
    private double _audioPitchSemitones;
    private double _audioBassGain;
    private double _audioTrebleGain;
    private double _audioEchoDelayMs = 60;
    private double _audioEchoStrength;
    private double _videoSpeed = 1.0;
    private bool _trimOneSecondEdgesEnabled = true;
    private bool _flipMainVideo;
    private bool _flipBackgroundVideo;
    private bool _useBackgroundImage;
    private string _backgroundImagePath = "";
    private bool _watermarkEnabled;
    private string _watermarkPath = "";
    private WatermarkPosition _watermarkPosition = WatermarkPosition.BottomRight;
    private double _watermarkWidthPercent = 18;
    private double _watermarkOpacity = 0.85;
    private int _watermarkMargin = 32;
    private double _watermarkXPercent = 100;
    private double _watermarkYPercent = 100;
    private bool _watermarkAutoRemoveBackground;
    private double _watermarkBackgroundTolerance = 24;
    private bool _introEnabled;
    private string _introImagePath = "";
    private double _introDurationSeconds = 2;
    private bool _outroEnabled;
    private string _outroImagePath = "";
    private double _outroDurationSeconds = 2;
    private bool _splitEnabled;
    private SplitMode _splitMode = SplitMode.RandomRange;
    private int _partCount = 6;
    private double _partDurationSeconds = 150;
    private double _randomMinSeconds = 120;
    private double _randomMaxSeconds = 160;
    private double _smartRemainderSeconds = 30;
    private double _skipSplitBelowSeconds;
    private bool _mergeVideosEnabled;
    private double _mergeMinSeconds = 65;
    private double _mergeMaxSeconds = 190;
    private int _mergeVideosPerOutput = 3;
    private int _lastMergeVideosPerOutput = 3;
    private int _mergeRandomSeed = Environment.TickCount;
    private MergeOrderMode _mergeOrder = MergeOrderMode.Random;
    private bool _autoShuffleBatchEnabled;
    private bool _autoShuffleStreamCopyEnabled = true;
    private int _autoShuffleTargetCount = 1650;
    private double _blurRadius;
    private double _blurDarkness;
    private double _blurZoom = 1;
    private bool _removeLeadingNumbers;
    private bool _keepDigits = true;
    private bool _removeAllDigits;
    private bool _removeNumericIds;
    private bool _removeHashtags;
    private string _blacklist = "shorts;viral;fyp";
    private string _whitelist = "";
    private bool _titleTextEnabled;
    private bool _partTextEnabled;
    private bool _partTextAboveTitle;
    private string _fontFamily = "Segoe UI";
    private string _fontWeightName = "SemiBold";
    private double _fontSize = 76;
    private double _minimumFontSize = 30;
    private double _titleMaxWidth = 940;
    private int _titleMaxLines = 5;
    private double _titleTop = 1340;
    private double _titleHorizontalOffset;
    private string _textColor = "#FFFFFFFF";
    private string _strokeColor = "#FF000000";
    private double _strokeWidth = 4;
    private bool _shadowEnabled;
    private double _shadowOpacity = 0.75;
    private double _partScale = 0.90;
    private bool _titleFrameEnabled;
    private string _titleFrameColor = "#FF000000";
    private double _titleFrameOpacityPercent = 75;
    private string _titleFrameBorderColor = "#FFFFFFFF";
    private double _titleFrameBorderWidth = 2;
    private double _titleFramePadding = 28;
    private double _titleFrameCornerRadius = 22;
    private bool _autoSubtitlesEnabled;
    private SubtitleOutputMode _subtitleOutput = SubtitleOutputMode.BurnIn;
    private SubtitleDisplayMode _subtitleDisplay = SubtitleDisplayMode.Sentence;
    private int _subtitleWordsPerCue = 1;
    private string _subtitleSourceLanguage = "auto";
    private string _subtitleTargetLanguage = "original";
    private string _subtitleFontFamily = "Arial";
    private double _subtitleFontSize = 54;
    private string _subtitleTextColor = "#FFFFFFFF";
    private string _subtitleOutlineColor = "#FF000000";
    private double _subtitleOutlineWidth = 4;
    private double _subtitleBottomMargin = 105;
    private double _subtitleHorizontalOffset;
    private int _subtitleMaxCharacters = 38;
    private bool _subtitleCleanSpecialCharacters;
    private bool _subtitleRemoveDuplicates;
    private bool _subtitleContextTranslation;
    private bool _existingSubtitleBlurEnabled;
    private int _existingSubtitleBlurX = 60;
    private int _existingSubtitleBlurY = 1580;
    private int _existingSubtitleBlurWidth = 960;
    private int _existingSubtitleBlurHeight = 270;
    private int _existingSubtitleBlurStrength = 22;
    private bool _autoDetectExistingSubtitleRegion = true;
    private int _existingSubtitleDetectionSampleCount = 5;
    private int _existingSubtitleDetectionSearchBottomPercent = 60;
    private int _existingSubtitleDetectionSensitivity = 55;
    private int _existingSubtitleDetectionMargin = 24;
    private GpuEncoderKind _gpuEncoder = GpuEncoderKind.Auto;
    private int _workerCount = 8;
    private int _maxRetries = 2;
    private int _quality = 30;
    private bool _fastestRenderMode = true;
    private bool _skipExisting;
    private bool _autoResumeProject = true;
    private bool _deleteSourceAfterSuccess;
    private bool _deleteFailedSourceAfterRetries;
    private bool _emptyRecycleBinAfterSuccessfulRun = true;
    private bool _emptyRecycleBinWhenDiskFull;
    private bool _verifyOutputAfterRender = true;
    private bool _deepOutputVerification;
    private bool _autoCleanPartialFiles = true;
    private double _minimumFreeSpaceGb = 10;
    private OutputPlatformPreset _outputPlatform = OutputPlatformPreset.Custom;
    private OutputResolutionPreset _outputResolution = OutputResolutionPreset.Hd720x1280;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string InputFolder { get => _inputFolder; set => SetProperty(ref _inputFolder, value); }
    public List<string> InputFolders { get => _inputFolders; set => SetProperty(ref _inputFolders, value ?? []); }
    private bool _randomOutputFileName;
    public bool RandomOutputFileName { get => _randomOutputFileName; set => SetProperty(ref _randomOutputFileName, value); }

    public string OutputRoot { get => _outputRoot; set => SetProperty(ref _outputRoot, value); }
    public string FfmpegPath { get => _ffmpegPath; set => SetProperty(ref _ffmpegPath, value); }
    public string FfprobePath { get => _ffprobePath; set => SetProperty(ref _ffprobePath, value); }
    public bool WatchFolderEnabled { get => _watchFolderEnabled; set => SetProperty(ref _watchFolderEnabled, value); }
    public bool IncludeSubfolders { get => _includeSubfolders; set => SetProperty(ref _includeSubfolders, value); }
    public bool AutoLandscapePreviewEnabled { get => _autoLandscapePreviewEnabled; set => SetProperty(ref _autoLandscapePreviewEnabled, value); }
    public LayoutKind Layout
    {
        get => _layout;
        set
        {
            if (!SetProperty(ref _layout, value)) return;
            OnPropertyChanged(nameof(IsCustomLayout));
            OnPropertyChanged(nameof(IsNotCustomLayout));
            OnPropertyChanged(nameof(IsStandardCropMainVideo));
            OnPropertyChanged(nameof(IsMouseCropCutOutside));
        }
    }
    [JsonIgnore] public bool IsCustomLayout => Layout == LayoutKind.Custom;
    [JsonIgnore] public bool IsNotCustomLayout => Layout != LayoutKind.Custom;
    public double CustomMainRatio
    {
        get => _customMainRatio;
        set
        {
            if (!SetProperty(ref _customMainRatio, Math.Clamp(value, 0.1, 1))) return;
            OnPropertyChanged(nameof(CustomMainPercent));
            OnPropertyChanged(nameof(CustomMainHeightPixels));
            OnPropertyChanged(nameof(CustomMainTopPixels));
            // Re-clamp các tọa độ pixel khi đổi dọc <-> ngang.
            TitleTop = _titleTop;
            TitleHorizontalOffset = _titleHorizontalOffset;
            SubtitleBottomMargin = _subtitleBottomMargin;
            SubtitleHorizontalOffset = _subtitleHorizontalOffset;
            ExistingSubtitleBlurX = _existingSubtitleBlurX;
            ExistingSubtitleBlurY = _existingSubtitleBlurY;
            ExistingSubtitleBlurWidth = _existingSubtitleBlurWidth;
            ExistingSubtitleBlurHeight = _existingSubtitleBlurHeight;
        }
    }
    [JsonIgnore]
    public double CustomMainPercent
    {
        get => CustomMainRatio * 100;
        set => CustomMainRatio = value / 100;
    }
    [JsonIgnore]
    public int CustomMainHeightPixels
    {
        get
        {
            var minHeight = Math.Max(2, (int)Math.Round(DesignHeight * 0.10 / 2) * 2);
            return Math.Clamp(
                (int)Math.Round(DesignHeight * CustomMainRatio / 2) * 2,
                minHeight,
                DesignHeight);
        }
    }
    public double CustomMainCenterYPercent
    {
        get => _customMainCenterYPercent;
        set
        {
            if (!SetProperty(ref _customMainCenterYPercent, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(CustomMainTopPixels));
        }
    }
    [JsonIgnore]
    public int CustomMainTopPixels
    {
        get
        {
            var maxTop = Math.Max(0, DesignHeight - CustomMainHeightPixels);
            var centerY = DesignHeight * CustomMainCenterYPercent / 100.0;
            var top = (int)Math.Round(centerY - CustomMainHeightPixels / 2.0);
            return Math.Clamp(top, 0, maxTop);
        }
    }
    public MainVideoFitMode MainVideoFit
    {
        get => _mainVideoFit;
        set
        {
            if (!SetProperty(ref _mainVideoFit, value)) return;
            OnPropertyChanged(nameof(IsCropMainVideo));
            OnPropertyChanged(nameof(IsFaceCropMainVideo));
            OnPropertyChanged(nameof(IsStandardCropMainVideo));
        }
    }
    [JsonIgnore] public bool IsCropMainVideo =>
        MainVideoFit is MainVideoFitMode.Crop or MainVideoFitMode.FaceCrop;
    [JsonIgnore] public bool IsFaceCropMainVideo =>
        MainVideoFit == MainVideoFitMode.FaceCrop;
    [JsonIgnore] public bool IsStandardCropMainVideo =>
        IsNotCustomLayout && IsCropMainVideo;
    public double CropFocusXPercent { get => _cropFocusXPercent; set => SetProperty(ref _cropFocusXPercent, Math.Clamp(value, 0, 100)); }
    public double CropFocusYPercent { get => _cropFocusYPercent; set => SetProperty(ref _cropFocusYPercent, Math.Clamp(value, 0, 100)); }
    public bool MouseCropEnabled
    {
        get => _mouseCropEnabled;
        set
        {
            if (!SetProperty(ref _mouseCropEnabled, value)) return;
            OnPropertyChanged(nameof(IsMouseCropCutOutside));
        }
    }
    public double MouseCropXPercent { get => _mouseCropXPercent; set => SetProperty(ref _mouseCropXPercent, Math.Clamp(value, 0, 100)); }
    public double MouseCropYPercent { get => _mouseCropYPercent; set => SetProperty(ref _mouseCropYPercent, Math.Clamp(value, 0, 100)); }
    public double MouseCropWidthPercent { get => _mouseCropWidthPercent; set => SetProperty(ref _mouseCropWidthPercent, Math.Clamp(value, 2, 100)); }
    public double MouseCropHeightPercent { get => _mouseCropHeightPercent; set => SetProperty(ref _mouseCropHeightPercent, Math.Clamp(value, 2, 100)); }
    public double MouseCropSourceAspect { get => _mouseCropSourceAspect; set => SetProperty(ref _mouseCropSourceAspect, Math.Clamp(value, 0.05, 20)); }
    public MouseCropOutsideMode MouseCropOutside
    {
        get => _mouseCropOutsideMode;
        set
        {
            if (!SetProperty(ref _mouseCropOutsideMode, value)) return;
            OnPropertyChanged(nameof(IsMouseCropCutOutside));
        }
    }
    [JsonIgnore] public bool IsMouseCropCutOutside =>
        IsCustomLayout && MouseCropEnabled && MouseCropOutside == MouseCropOutsideMode.CutOutside;
    public bool AutoFaceCropEnabled { get => _autoFaceCropEnabled; set => SetProperty(ref _autoFaceCropEnabled, value); }
    public int FaceDetectionSampleCount { get => _faceDetectionSampleCount; set => SetProperty(ref _faceDetectionSampleCount, Math.Clamp(value, 3, 9)); }
    public double FaceCropZoomPercent { get => _faceCropZoomPercent; set => SetProperty(ref _faceCropZoomPercent, Math.Clamp(value, 100, 200)); }
    public bool VideoEffectsEnabled
    {
        get => _videoEffectsEnabled;
        set
        {
            if (SetProperty(ref _videoEffectsEnabled, value))
                OnPropertyChanged(nameof(AreVideoShakeControlsEnabled));
        }
    }
    public bool VideoShakeEnabled
    {
        get => _videoShakeEnabled;
        set
        {
            if (SetProperty(ref _videoShakeEnabled, value))
                OnPropertyChanged(nameof(AreVideoShakeControlsEnabled));
        }
    }
    [JsonIgnore] public bool AreVideoShakeControlsEnabled => VideoEffectsEnabled && VideoShakeEnabled;
    public bool VideoOffsetEnabled { get => _videoOffsetEnabled; set => SetProperty(ref _videoOffsetEnabled, value); }
    public bool VideoColorEnabled { get => _videoColorEnabled; set => SetProperty(ref _videoColorEnabled, value); }
    public bool VideoNoiseEnabled { get => _videoNoiseEnabled; set => SetProperty(ref _videoNoiseEnabled, value); }
    public double VideoShakeStrength { get => _videoShakeStrength; set => SetProperty(ref _videoShakeStrength, Math.Clamp(value, 0, 40)); }
    public double VideoShakeSpeed { get => _videoShakeSpeed; set => SetProperty(ref _videoShakeSpeed, Math.Clamp(value, 0.2, 12)); }
    public bool VideoShakePeriodic { get => _videoShakePeriodic; set => SetProperty(ref _videoShakePeriodic, value); }
    public double VideoShakeIntervalSeconds { get => _videoShakeIntervalSeconds; set => SetProperty(ref _videoShakeIntervalSeconds, Math.Clamp(value, 0.2, 86400)); }
    public double VideoShakeDurationSeconds { get => _videoShakeDurationSeconds; set => SetProperty(ref _videoShakeDurationSeconds, Math.Clamp(value, 0.1, 86400)); }
    public double VideoOffsetX { get => _videoOffsetX; set => SetProperty(ref _videoOffsetX, Math.Clamp(value, -100, 100)); }
    public double VideoOffsetY { get => _videoOffsetY; set => SetProperty(ref _videoOffsetY, Math.Clamp(value, -100, 100)); }
    public double VideoBrightness { get => _videoBrightness; set => SetProperty(ref _videoBrightness, Math.Clamp(value, -1, 1)); }
    public double VideoContrast { get => _videoContrast; set => SetProperty(ref _videoContrast, Math.Clamp(value, 0.5, 2)); }
    public double VideoSaturation { get => _videoSaturation; set => SetProperty(ref _videoSaturation, Math.Clamp(value, 0, 3)); }
    public double VideoNoiseStrength { get => _videoNoiseStrength; set => SetProperty(ref _videoNoiseStrength, Math.Clamp(value, 0, 50)); }
    public bool AudioEffectsEnabled { get => _audioEffectsEnabled; set => SetProperty(ref _audioEffectsEnabled, value); }
    public bool AudioVolumeEnabled { get => _audioVolumeEnabled; set => SetProperty(ref _audioVolumeEnabled, value); }
    public bool AudioPitchEnabled { get => _audioPitchEnabled; set => SetProperty(ref _audioPitchEnabled, value); }
    public bool AudioEqEnabled { get => _audioEqEnabled; set => SetProperty(ref _audioEqEnabled, value); }
    public bool AudioEchoEnabled { get => _audioEchoEnabled; set => SetProperty(ref _audioEchoEnabled, value); }
    public bool AudioNoiseReductionEnabled { get => _audioNoiseReductionEnabled; set => SetProperty(ref _audioNoiseReductionEnabled, value); }
    public bool AudioCompressorEnabled { get => _audioCompressorEnabled; set => SetProperty(ref _audioCompressorEnabled, value); }
    public bool AudioNormalizeEnabled { get => _audioNormalizeEnabled; set => SetProperty(ref _audioNormalizeEnabled, value); }
    public double AudioVolume { get => _audioVolume; set => SetProperty(ref _audioVolume, Math.Clamp(value, 0, 2)); }
    public double AudioPitchSemitones { get => _audioPitchSemitones; set => SetProperty(ref _audioPitchSemitones, Math.Clamp(value, -4, 4)); }
    public double AudioBassGain { get => _audioBassGain; set => SetProperty(ref _audioBassGain, Math.Clamp(value, -10, 10)); }
    public double AudioTrebleGain { get => _audioTrebleGain; set => SetProperty(ref _audioTrebleGain, Math.Clamp(value, -10, 10)); }
    public double AudioEchoDelayMs { get => _audioEchoDelayMs; set => SetProperty(ref _audioEchoDelayMs, Math.Clamp(value, 20, 300)); }
    public double AudioEchoStrength { get => _audioEchoStrength; set => SetProperty(ref _audioEchoStrength, Math.Clamp(value, 0, 0.6)); }
    public double VideoSpeed { get => _videoSpeed; set => SetProperty(ref _videoSpeed, Math.Clamp(value, 0.1, 10)); }
    public bool TrimOneSecondEdgesEnabled { get => _trimOneSecondEdgesEnabled; set => SetProperty(ref _trimOneSecondEdgesEnabled, value); }
    public bool FlipMainVideo { get => _flipMainVideo; set => SetProperty(ref _flipMainVideo, value); }
    public bool FlipBackgroundVideo { get => _flipBackgroundVideo; set => SetProperty(ref _flipBackgroundVideo, value); }
    public bool UseBackgroundImage { get => _useBackgroundImage; set => SetProperty(ref _useBackgroundImage, value); }
    public string BackgroundImagePath { get => _backgroundImagePath; set => SetProperty(ref _backgroundImagePath, value); }
    public bool WatermarkEnabled { get => _watermarkEnabled; set => SetProperty(ref _watermarkEnabled, value); }
    public string WatermarkPath { get => _watermarkPath; set => SetProperty(ref _watermarkPath, value); }
    public WatermarkPosition WatermarkPosition { get => _watermarkPosition; set => SetProperty(ref _watermarkPosition, value); }
    public double WatermarkWidthPercent { get => _watermarkWidthPercent; set => SetProperty(ref _watermarkWidthPercent, Math.Clamp(value, 3, 80)); }
    public double WatermarkOpacity { get => _watermarkOpacity; set => SetProperty(ref _watermarkOpacity, Math.Clamp(value, 0.05, 1)); }
    public int WatermarkMargin { get => _watermarkMargin; set => SetProperty(ref _watermarkMargin, Math.Clamp(value, 0, 400)); }
    public double WatermarkXPercent { get => _watermarkXPercent; set => SetProperty(ref _watermarkXPercent, Math.Clamp(value, 0, 100)); }
    public double WatermarkYPercent { get => _watermarkYPercent; set => SetProperty(ref _watermarkYPercent, Math.Clamp(value, 0, 100)); }
    public bool WatermarkAutoRemoveBackground { get => _watermarkAutoRemoveBackground; set => SetProperty(ref _watermarkAutoRemoveBackground, value); }
    public double WatermarkBackgroundTolerance { get => _watermarkBackgroundTolerance; set => SetProperty(ref _watermarkBackgroundTolerance, Math.Clamp(value, 0, 128)); }
    [JsonIgnore] public string WatermarkRenderPath { get; set; } = "";
    [JsonIgnore] public string EffectiveWatermarkPath =>
        !string.IsNullOrWhiteSpace(WatermarkRenderPath) && File.Exists(WatermarkRenderPath)
            ? WatermarkRenderPath
            : WatermarkPath;
    public bool IntroEnabled { get => _introEnabled; set => SetProperty(ref _introEnabled, value); }
    public string IntroImagePath { get => _introImagePath; set => SetProperty(ref _introImagePath, value); }
    public double IntroDurationSeconds { get => _introDurationSeconds; set => SetProperty(ref _introDurationSeconds, Math.Clamp(value, 0.5, 30)); }
    public bool OutroEnabled { get => _outroEnabled; set => SetProperty(ref _outroEnabled, value); }
    public string OutroImagePath { get => _outroImagePath; set => SetProperty(ref _outroImagePath, value); }
    public double OutroDurationSeconds { get => _outroDurationSeconds; set => SetProperty(ref _outroDurationSeconds, Math.Clamp(value, 0.5, 30)); }
    public bool SplitEnabled
    {
        get => _splitEnabled;
        set
        {
            if (!SetProperty(ref _splitEnabled, value)) return;
            OnPropertyChanged(nameof(TextOverlayEnabled));
        }
    }
    public SplitMode SplitMode
    {
        get => _splitMode;
        set
        {
            if (!SetProperty(ref _splitMode, value)) return;
            OnPropertyChanged(nameof(IsPartCountSplit));
            OnPropertyChanged(nameof(IsDurationSplit));
            OnPropertyChanged(nameof(IsRandomRangeSplit));
        }
    }
    [JsonIgnore] public bool IsPartCountSplit => SplitMode == global::EkkoBatchVideo.Models.SplitMode.PartCount;
    [JsonIgnore] public bool IsDurationSplit => SplitMode == global::EkkoBatchVideo.Models.SplitMode.Duration;
    [JsonIgnore] public bool IsRandomRangeSplit => SplitMode == global::EkkoBatchVideo.Models.SplitMode.RandomRange;
    public int PartCount { get => _partCount; set => SetProperty(ref _partCount, Math.Clamp(value, 1, 500)); }
    public double PartDurationSeconds { get => _partDurationSeconds; set => SetProperty(ref _partDurationSeconds, Math.Max(1, value)); }
    public double RandomMinSeconds { get => _randomMinSeconds; set => SetProperty(ref _randomMinSeconds, Math.Max(1, value)); }
    public double RandomMaxSeconds { get => _randomMaxSeconds; set => SetProperty(ref _randomMaxSeconds, Math.Max(1, value)); }
    public double SmartRemainderSeconds { get => _smartRemainderSeconds; set => SetProperty(ref _smartRemainderSeconds, Math.Max(0, value)); }
    public double SkipSplitBelowSeconds { get => _skipSplitBelowSeconds; set => SetProperty(ref _skipSplitBelowSeconds, Math.Max(0, value)); }
    public bool MergeVideosEnabled { get => _mergeVideosEnabled; set => SetProperty(ref _mergeVideosEnabled, value); }
    public double MergeMinSeconds { get => _mergeMinSeconds; set => SetProperty(ref _mergeMinSeconds, Math.Max(1, value)); }
    public double MergeMaxSeconds { get => _mergeMaxSeconds; set => SetProperty(ref _mergeMaxSeconds, Math.Max(1, value)); }
    public int MergeVideosPerOutput
    {
        get => _mergeVideosPerOutput;
        set
        {
            // 0 = ghép theo thời gian. Khi đã chọn ghép theo số video thì
            // phải có ít nhất 2 clip; không cho trạng thái "ghép 1 video".
            var normalized = value <= 0 ? 0 : Math.Clamp(value, 2, 1000);
            if (normalized > 0) _lastMergeVideosPerOutput = normalized;
            if (!SetProperty(ref _mergeVideosPerOutput, normalized)) return;
            OnPropertyChanged(nameof(IsMergeByDuration));
            OnPropertyChanged(nameof(IsMergeByVideoCount));
        }
    }
    [JsonIgnore]
    public bool IsMergeByDuration
    {
        get => MergeVideosPerOutput <= 0;
        set
        {
            if (value && MergeVideosPerOutput != 0) MergeVideosPerOutput = 0;
        }
    }
    [JsonIgnore]
    public bool IsMergeByVideoCount
    {
        get => MergeVideosPerOutput > 0;
        set
        {
            if (value && MergeVideosPerOutput <= 0)
                MergeVideosPerOutput = Math.Max(2, _lastMergeVideosPerOutput);
        }
    }
    public int MergeRandomSeed { get => _mergeRandomSeed; set => SetProperty(ref _mergeRandomSeed, value); }
    public MergeOrderMode MergeOrder { get => _mergeOrder; set => SetProperty(ref _mergeOrder, value); }
    public MergeTitleMode MergeTitleMode { get => _mergeTitleMode; set => SetProperty(ref _mergeTitleMode, value); }
    public bool AutoShuffleBatchEnabled { get => _autoShuffleBatchEnabled; set => SetProperty(ref _autoShuffleBatchEnabled, value); }
    public bool AutoShuffleStreamCopyEnabled
    {
        get => _autoShuffleStreamCopyEnabled;
        set => SetProperty(ref _autoShuffleStreamCopyEnabled, value);
    }
    [JsonIgnore] public bool IsAutoShuffleStreamCopyActive =>
        MergeVideosEnabled && AutoShuffleBatchEnabled && AutoShuffleStreamCopyEnabled;
    public int AutoShuffleTargetCount
    {
        get => _autoShuffleTargetCount;
        set => SetProperty(ref _autoShuffleTargetCount, Math.Clamp(value, 1, 100000));
    }
    public double BlurRadius { get => _blurRadius; set => SetProperty(ref _blurRadius, Math.Clamp(value, 0, 100)); }
    public double BlurDarkness
    {
        get => _blurDarkness;
        set
        {
            if (!SetProperty(ref _blurDarkness, Math.Clamp(value, -0.95, 0.95))) return;
            OnPropertyChanged(nameof(BlurBrightness));
        }
    }
    [JsonIgnore]
    public double BlurBrightness
    {
        get => -BlurDarkness;
        set => BlurDarkness = -Math.Clamp(value, -0.95, 0.95);
    }
    public double BlurZoom { get => _blurZoom; set => SetProperty(ref _blurZoom, Math.Clamp(value, 1, 3)); }
    public bool RemoveLeadingNumbers { get => _removeLeadingNumbers; set => SetProperty(ref _removeLeadingNumbers, value); }
    public bool KeepDigits { get => _keepDigits; set => SetProperty(ref _keepDigits, value); }
    public bool RemoveAllDigits { get => _removeAllDigits; set => SetProperty(ref _removeAllDigits, value); }
    public bool RemoveNumericIds { get => _removeNumericIds; set => SetProperty(ref _removeNumericIds, value); }
    public bool RemoveHashtags { get => _removeHashtags; set => SetProperty(ref _removeHashtags, value); }
    public string Blacklist { get => _blacklist; set => SetProperty(ref _blacklist, value); }
    public string Whitelist { get => _whitelist; set => SetProperty(ref _whitelist, value); }
    public bool TitleTextEnabled
    {
        get => _titleTextEnabled;
        set
        {
            if (!SetProperty(ref _titleTextEnabled, value)) return;
            OnPropertyChanged(nameof(TextOverlayEnabled));
        }
    }
    public bool PartTextEnabled
    {
        get => _partTextEnabled;
        set
        {
            if (!SetProperty(ref _partTextEnabled, value)) return;
            OnPropertyChanged(nameof(TextOverlayEnabled));
        }
    }
    public bool PartTextAboveTitle { get => _partTextAboveTitle; set => SetProperty(ref _partTextAboveTitle, value); }
    [JsonIgnore] public bool TextOverlayEnabled =>
        TitleTextEnabled || (PartTextEnabled && SplitEnabled);
    public string FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }
    public string FontWeightName { get => _fontWeightName; set => SetProperty(ref _fontWeightName, value); }
    public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, Math.Clamp(value, 12, 220)); }
    public double MinimumFontSize { get => _minimumFontSize; set => SetProperty(ref _minimumFontSize, Math.Clamp(value, 10, 200)); }
    public double TitleMaxWidth { get => _titleMaxWidth; set => SetProperty(ref _titleMaxWidth, Math.Clamp(value, 100, 1060)); }
    public int TitleMaxLines { get => _titleMaxLines; set => SetProperty(ref _titleMaxLines, Math.Clamp(value, 1, 10)); }
    public double TitleTop { get => _titleTop; set => SetProperty(ref _titleTop, Math.Clamp(value, 0, Math.Max(0, DesignHeight - 70))); }
    public double TitleHorizontalOffset { get => _titleHorizontalOffset; set => SetProperty(ref _titleHorizontalOffset, Math.Clamp(value, -DesignWidth / 2.0, DesignWidth / 2.0)); }
    public string TextColor { get => _textColor; set => SetProperty(ref _textColor, value); }
    public string StrokeColor { get => _strokeColor; set => SetProperty(ref _strokeColor, value); }
    public double StrokeWidth { get => _strokeWidth; set => SetProperty(ref _strokeWidth, Math.Clamp(value, 0, 20)); }
    public bool ShadowEnabled { get => _shadowEnabled; set => SetProperty(ref _shadowEnabled, value); }
    public double ShadowOpacity { get => _shadowOpacity; set => SetProperty(ref _shadowOpacity, Math.Clamp(value, 0, 1)); }
    public double PartScale { get => _partScale; set => SetProperty(ref _partScale, Math.Clamp(value, 0.5, 0.95)); }
    public bool TitleFrameEnabled { get => _titleFrameEnabled; set => SetProperty(ref _titleFrameEnabled, value); }
    public string TitleFrameColor { get => _titleFrameColor; set => SetProperty(ref _titleFrameColor, value); }
    public double TitleFrameOpacityPercent { get => _titleFrameOpacityPercent; set => SetProperty(ref _titleFrameOpacityPercent, Math.Clamp(value, 0, 100)); }
    public string TitleFrameBorderColor { get => _titleFrameBorderColor; set => SetProperty(ref _titleFrameBorderColor, value); }
    public double TitleFrameBorderWidth { get => _titleFrameBorderWidth; set => SetProperty(ref _titleFrameBorderWidth, Math.Clamp(value, 0, 20)); }
    public double TitleFramePadding { get => _titleFramePadding; set => SetProperty(ref _titleFramePadding, Math.Clamp(value, 0, 100)); }
    public double TitleFrameCornerRadius { get => _titleFrameCornerRadius; set => SetProperty(ref _titleFrameCornerRadius, Math.Clamp(value, 0, 100)); }
    public bool AutoSubtitlesEnabled { get => _autoSubtitlesEnabled; set => SetProperty(ref _autoSubtitlesEnabled, value); }
    public SubtitleOutputMode SubtitleOutput { get => _subtitleOutput; set => SetProperty(ref _subtitleOutput, value); }
    public SubtitleDisplayMode SubtitleDisplay
    {
        get => _subtitleDisplay;
        set
        {
            if (!SetProperty(ref _subtitleDisplay, value)) return;
            OnPropertyChanged(nameof(IsSubtitleWordGroups));
        }
    }
    [JsonIgnore] public bool IsSubtitleWordGroups => SubtitleDisplay == SubtitleDisplayMode.WordGroups;
    public int SubtitleWordsPerCue { get => _subtitleWordsPerCue; set => SetProperty(ref _subtitleWordsPerCue, Math.Clamp(value, 1, 30)); }
    public string SubtitleSourceLanguage { get => _subtitleSourceLanguage; set => SetProperty(ref _subtitleSourceLanguage, value); }
    public string SubtitleTargetLanguage { get => _subtitleTargetLanguage; set => SetProperty(ref _subtitleTargetLanguage, value); }
    public string SubtitleFontFamily { get => _subtitleFontFamily; set => SetProperty(ref _subtitleFontFamily, value); }
    public double SubtitleFontSize { get => _subtitleFontSize; set => SetProperty(ref _subtitleFontSize, Math.Clamp(value, 20, 120)); }
    public string SubtitleTextColor { get => _subtitleTextColor; set => SetProperty(ref _subtitleTextColor, value); }
    public string SubtitleOutlineColor { get => _subtitleOutlineColor; set => SetProperty(ref _subtitleOutlineColor, value); }
    public double SubtitleOutlineWidth { get => _subtitleOutlineWidth; set => SetProperty(ref _subtitleOutlineWidth, Math.Clamp(value, 0, 12)); }
    public double SubtitleBottomMargin { get => _subtitleBottomMargin; set => SetProperty(ref _subtitleBottomMargin, Math.Clamp(value, 20, Math.Max(20, DesignHeight - 120))); }
    public double SubtitleHorizontalOffset { get => _subtitleHorizontalOffset; set => SetProperty(ref _subtitleHorizontalOffset, Math.Clamp(value, -DesignWidth / 2.0, DesignWidth / 2.0)); }
    public int SubtitleMaxCharacters { get => _subtitleMaxCharacters; set => SetProperty(ref _subtitleMaxCharacters, Math.Clamp(value, 12, 80)); }
    public bool SubtitleCleanSpecialCharacters { get => _subtitleCleanSpecialCharacters; set => SetProperty(ref _subtitleCleanSpecialCharacters, value); }
    public bool SubtitleRemoveDuplicates { get => _subtitleRemoveDuplicates; set => SetProperty(ref _subtitleRemoveDuplicates, value); }
    public bool SubtitleContextTranslation { get => _subtitleContextTranslation; set => SetProperty(ref _subtitleContextTranslation, value); }
    public bool ExistingSubtitleBlurEnabled { get => _existingSubtitleBlurEnabled; set => SetProperty(ref _existingSubtitleBlurEnabled, value); }
    public int ExistingSubtitleBlurX { get => _existingSubtitleBlurX; set => SetProperty(ref _existingSubtitleBlurX, Math.Clamp(value, 0, Math.Max(0, DesignWidth - 8))); }
    public int ExistingSubtitleBlurY { get => _existingSubtitleBlurY; set => SetProperty(ref _existingSubtitleBlurY, Math.Clamp(value, 0, Math.Max(0, DesignHeight - 8))); }
    public int ExistingSubtitleBlurWidth { get => _existingSubtitleBlurWidth; set => SetProperty(ref _existingSubtitleBlurWidth, Math.Clamp(value, 8, DesignWidth)); }
    public int ExistingSubtitleBlurHeight { get => _existingSubtitleBlurHeight; set => SetProperty(ref _existingSubtitleBlurHeight, Math.Clamp(value, 8, DesignHeight)); }
    public int ExistingSubtitleBlurStrength { get => _existingSubtitleBlurStrength; set => SetProperty(ref _existingSubtitleBlurStrength, Math.Clamp(value, 1, 80)); }
    public bool AutoDetectExistingSubtitleRegion { get => _autoDetectExistingSubtitleRegion; set => SetProperty(ref _autoDetectExistingSubtitleRegion, value); }
    public int ExistingSubtitleDetectionSampleCount { get => _existingSubtitleDetectionSampleCount; set => SetProperty(ref _existingSubtitleDetectionSampleCount, Math.Clamp(value, 3, 8)); }
    public int ExistingSubtitleDetectionSearchBottomPercent { get => _existingSubtitleDetectionSearchBottomPercent; set => SetProperty(ref _existingSubtitleDetectionSearchBottomPercent, Math.Clamp(value, 30, 90)); }
    public int ExistingSubtitleDetectionSensitivity { get => _existingSubtitleDetectionSensitivity; set => SetProperty(ref _existingSubtitleDetectionSensitivity, Math.Clamp(value, 10, 100)); }
    public int ExistingSubtitleDetectionMargin { get => _existingSubtitleDetectionMargin; set => SetProperty(ref _existingSubtitleDetectionMargin, Math.Clamp(value, 0, 120)); }
    public GpuEncoderKind GpuEncoder { get => _gpuEncoder; set => SetProperty(ref _gpuEncoder, value); }
    public int WorkerCount { get => _workerCount; set => SetProperty(ref _workerCount, Math.Clamp(value, 1, 8)); }
    public int MaxRetries { get => _maxRetries; set => SetProperty(ref _maxRetries, Math.Clamp(value, 0, 10)); }
    public int Quality { get => _quality; set => SetProperty(ref _quality, Math.Clamp(value, 16, 45)); }
    public bool FastestRenderMode { get => _fastestRenderMode; set => SetProperty(ref _fastestRenderMode, value); }
    public bool SkipExisting { get => _skipExisting; set => SetProperty(ref _skipExisting, value); }
    public bool AutoResumeProject { get => _autoResumeProject; set => SetProperty(ref _autoResumeProject, value); }
    public bool DeleteSourceAfterSuccess { get => _deleteSourceAfterSuccess; set => SetProperty(ref _deleteSourceAfterSuccess, value); }
    public bool DeleteFailedSourceAfterRetries { get => _deleteFailedSourceAfterRetries; set => SetProperty(ref _deleteFailedSourceAfterRetries, value); }
    public bool EmptyRecycleBinAfterSuccessfulRun { get => _emptyRecycleBinAfterSuccessfulRun; set => SetProperty(ref _emptyRecycleBinAfterSuccessfulRun, value); }
    public bool EmptyRecycleBinWhenDiskFull { get => _emptyRecycleBinWhenDiskFull; set => SetProperty(ref _emptyRecycleBinWhenDiskFull, value); }
    public bool VerifyOutputAfterRender { get => _verifyOutputAfterRender; set => SetProperty(ref _verifyOutputAfterRender, value); }
    public bool DeepOutputVerification { get => _deepOutputVerification; set => SetProperty(ref _deepOutputVerification, value); }
    public bool AutoCleanPartialFiles { get => _autoCleanPartialFiles; set => SetProperty(ref _autoCleanPartialFiles, value); }
    public double MinimumFreeSpaceGb { get => _minimumFreeSpaceGb; set => SetProperty(ref _minimumFreeSpaceGb, Math.Clamp(value, 0.5, 1024)); }
    public OutputPlatformPreset OutputPlatform { get => _outputPlatform; set => SetProperty(ref _outputPlatform, value); }
    public OutputResolutionPreset OutputResolution
    {
        get => _outputResolution;
        set
        {
            if (!SetProperty(ref _outputResolution, value)) return;
            OnPropertyChanged(nameof(IsLandscapeOutput));
            OnPropertyChanged(nameof(DesignWidth));
            OnPropertyChanged(nameof(DesignHeight));
            OnPropertyChanged(nameof(OutputWidth));
            OnPropertyChanged(nameof(OutputHeight));
            OnPropertyChanged(nameof(OutputResolutionText));
            OnPropertyChanged(nameof(CustomMainHeightPixels));
            OnPropertyChanged(nameof(CustomMainTopPixels));
        }
    }
    [JsonIgnore] public bool IsLandscapeOutput =>
        OutputResolution is OutputResolutionPreset.FullHd1920x1080 or OutputResolutionPreset.Hd1280x720;
    [JsonIgnore] public int DesignWidth => IsLandscapeOutput ? 1920 : 1080;
    [JsonIgnore] public int DesignHeight => IsLandscapeOutput ? 1080 : 1920;
    [JsonIgnore] public int OutputWidth => OutputResolution switch
    {
        OutputResolutionPreset.Hd720x1280 => 720,
        OutputResolutionPreset.FullHd1920x1080 => 1920,
        OutputResolutionPreset.Hd1280x720 => 1280,
        _ => 1080
    };
    [JsonIgnore] public int OutputHeight => OutputResolution switch
    {
        OutputResolutionPreset.Hd720x1280 => 1280,
        OutputResolutionPreset.FullHd1920x1080 => 1080,
        OutputResolutionPreset.Hd1280x720 => 720,
        _ => 1920
    };
    [JsonIgnore] public string OutputResolutionText =>
        $"Video {(IsLandscapeOutput ? "ngang 16:9" : "dọc 9:16")} {OutputWidth} × {OutputHeight}";
}
