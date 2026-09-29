using System.Windows;
using System.Windows.Threading;
using EkkoBatchVideo.Services;
using EkkoBatchVideo.ViewModels;
using EkkoBatchVideo.Views;

namespace EkkoBatchVideo;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private AuthService? _auth;
    private DispatcherTimer? _authTimer;
    private AppLogger? _logger;

    public App() => WriteStartupTrace("App constructor");

    protected override async void OnStartup(StartupEventArgs e)
    {
        WriteStartupTrace("OnStartup start");
        base.OnStartup(e);

        var logger = new AppLogger();
        _logger = logger;
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("Lỗi giao diện", args.Exception);
            logger.Error($"Lỗi giao diện đã được chặn: {args.Exception.Message}");
            if (MainWindow is null)
                Shutdown();
            MessageBox.Show(
                $"Ekko Tools vừa gặp lỗi nhưng ứng dụng vẫn được giữ mở.\n\n{args.Exception.Message}\n\nChi tiết đã được lưu trong thư mục Logs.",
                "Ekko Tools - Thông báo lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Lỗi tác vụ nền", args.Exception);
            logger.Error($"Lỗi tác vụ nền: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };
        WriteStartupTrace("Creating AuthService");
        _auth = new AuthService();

        AuthStatus? authStatus = null;
        if (!string.IsNullOrWhiteSpace(_auth.Token))
        {
            WriteStartupTrace("Checking saved token");
            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                authStatus = await _auth.CheckAsync(startupCts.Token);
            }
            catch
            {
                authStatus = null;
            }
        }

        if (authStatus is null)
        {
            WriteStartupTrace("Creating LoginWindow");
            var login = new LoginWindow(_auth);
            WriteStartupTrace("Showing LoginWindow");
            if (login.ShowDialog() != true)
            {
                WriteStartupTrace("LoginWindow closed without success");
                Shutdown();
                return;
            }
            WriteStartupTrace("LoginWindow returned success");
            authStatus = login.AuthStatus;
        }

        if (authStatus is null) { WriteStartupTrace("AuthStatus was null"); Shutdown(); return; }
        if (!authStatus.Valid)
        {
            WriteStartupTrace("Showing RenewalWindow");
            var renewal = new RenewalWindow(_auth);
            if (renewal.ShowDialog() != true) { Shutdown(); return; }
        }
        MainWindow window;
        try
        {
            WriteStartupTrace("Creating MainWindow");
            window = new MainWindow();
        }
        catch (Exception ex)
        {
            WriteCrashLog("Không mở được cửa sổ chính", ex);
            MessageBox.Show(
                $"Không thể mở giao diện Ekko Tools.\n\n{ex.Message}\n\nChi tiết đã lưu trong Logs.",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        MainWindow = window;
        window.ContentRendered += MainWindowOnContentRendered;
        window.Loaded += (_, _) =>
        {
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
        };
        WriteStartupTrace("Showing MainWindow");
        window.Show();
        WriteStartupTrace("MainWindow shown");
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _authTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _authTimer.Tick += AuthTimerOnTick;
        _authTimer.Start();
    }

    private async Task BuildMainViewModelAsync()
    {
        if (_auth is null || _logger is null || MainWindow is null) return;

        try
        {
            WriteStartupTrace("BuildMainViewModel start");
            var logger = _logger;
            logger.Info("Đang nạp Ekko Tools...");

            var locator = new FfmpegLocator(logger);
            var analyzer = new AnalyzeEngine(locator, logger);
            var streamCopyCompatibility = new StreamCopyCompatibilityService(locator, logger);
            var splitEngine = new SplitEngine();
            var mergePlanner = new MergePlanner();
            var titleCleaner = new TitleCleanEngine();
            var titleSource = new TitleSourceService(titleCleaner, logger);
            var typography = new TypographyEngine(logger);
            var layout = new LayoutEngine();
            var filterBuilder = new FilterBuilder(layout);
            var gpuDetector = new GpuDetector(locator, logger);
            var subtitles = new SubtitleAiService(locator, logger);
            var faceFocus = new FaceFocusDetectionService(logger);
            var manualSubtitles = new ManualSubtitleService(logger);
            var existingSubtitleDetector =
                new ExistingSubtitleDetectionEngine(locator, logger);
            var outputVerifier = new OutputVerificationService(analyzer, locator, logger);
            var ffmpeg = new FFmpegEngine(
                locator,
                gpuDetector,
                filterBuilder,
                outputVerifier,
                logger);
            var input = new InputEngine(analyzer, logger);
            var watchFolder = new WatchFolderService(logger);
            var projectService = new ProjectService(logger);
            var presetService = new PresetService(logger);
            var recycleBin = new RecycleBinService(logger);
            var diskSpace = new DiskSpaceService(logger);
            var smartTrim = new SmartTrimService(locator, logger);
            var watermarks = new WatermarkImageService(logger);
            var dialogs = new FileDialogService();
            var preview = new PreviewService(
                locator,
                filterBuilder,
                faceFocus,
                existingSubtitleDetector,
                typography,
                titleSource,
                subtitles,
                manualSubtitles,
                logger);
            var renderQueue = new RenderQueue(
                ffmpeg,
                faceFocus,
                existingSubtitleDetector,
                typography,
                titleSource,
                subtitles,
                manualSubtitles,
                mergePlanner,
                projectService,
                recycleBin,
                diskSpace,
                logger);

            _viewModel = new MainViewModel(
                input, streamCopyCompatibility, splitEngine, mergePlanner, titleSource, typography, subtitles, renderQueue,
                preview, watchFolder, projectService, presetService, recycleBin, diskSpace,
                smartTrim, watermarks, dialogs, logger, _auth);

            MainWindow.DataContext = _viewModel;
            WriteStartupTrace("MainViewModel set");
            await _viewModel.InitializeAsync();
            WriteStartupTrace("MainViewModel initialized");
        }
        catch (Exception ex)
        {
            WriteCrashLog("Không nạp được Ekko Tools", ex);
            MessageBox.Show(
                $"Không nạp được Ekko Tools.\n\n{ex.Message}\n\nChi tiết đã lưu trong Logs.",
                "Ekko Tools", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async void AuthTimerOnTick(object? sender, EventArgs e)
    {
        if (_auth is null || MainWindow is null) return;
        var status = await _auth.CheckAsync();
        if (status is null) return;
        if (status.Valid) return;
        _authTimer?.Stop();
        MainWindow.IsEnabled = false;
        var renewal = new RenewalWindow(_auth) { Owner = MainWindow };
        if (renewal.ShowDialog() == true) MainWindow.IsEnabled = true;
        else Shutdown();
        _authTimer?.Start();
    }

    private async void MainWindowOnContentRendered(object? sender, EventArgs e)
    {
        WriteStartupTrace("MainWindow ContentRendered");
        if (sender is Window window)
            window.ContentRendered -= MainWindowOnContentRendered;

        // OnStartup phải kết thúc hoàn toàn để WPF bắt đầu bơm message.
        // Chỉ khởi tạo sau khi khung hình đầu tiên đã render và UI ở trạng thái rảnh.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await BuildMainViewModelAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _authTimer?.Stop();
        _viewModel?.Dispose();
        _auth?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrashLog(string category, Exception exception)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EkkoBatchVideo", "Logs");
            Directory.CreateDirectory(logFolder);
            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logFolder, "errors.log"), message);
        }
        catch
        {
            // Không để lỗi ghi nhật ký che mất lỗi gốc.
        }
    }

    private static void WriteStartupTrace(string message)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EkkoBatchVideo", "Logs");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "startup-trace.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Startup trace is best-effort only.
        }
    }
}
