using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChGhost.Core.Configuration;
using ChGhost.Core.Contracts;
using ChGhost.Core.Diagnostics;
using ChGhost.Core.Engine;
using ChGhost.Core.Models;
using ChGhost.Core.Native;
using ChGhost.Core.Tracking;
using ChGhost.Core.Vision;
using Microsoft.ML.OnnxRuntime;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace ChGhost
{
    public partial class MainWindow : Window
    {
        private const int PROFILE_COUNT = 3;

        private readonly IAppLogger _logger;
        private readonly SettingsStore _settingsStore;
        private readonly AppSettings _settings;
        private readonly string _enginePath;
        private readonly IChessEngine _engine;
        private readonly CancellationTokenSource _appLifetimeCts = new();
        private readonly Random _rng = new();
        private int _activeProfile = 1;
        private readonly IBoardRecognizer _recognizer;
        private readonly IBoardDetector _boardDetector = new ManualBoardDetector();
        private readonly AutoBoardDetector _autoBoardDetector;
        private readonly GameStateTracker _gameStateTracker = new();

        private readonly (char fen, string name)[] _manualFixOrder =
        {
            ('P', "Белая пешка"), ('N', "Белый конь"), ('B', "Белый слон"),
            ('R', "Белая ладья"), ('Q', "Белый ферзь"), ('K', "Белый король"),
            ('p', "Чёрная пешка"), ('n', "Чёрный конь"), ('b', "Чёрный слон"),
            ('r', "Чёрная ладья"), ('q', "Чёрный ферзь"), ('k', "Чёрный король")
        };
        private int _fixIndex = 0;

        private Point _topLeft
        {
            get => _boardDetector.TopLeft ?? Point.Empty;
            set => _boardDetector.SetTopLeft(value.X, value.Y);
        }

        private Point _bottomRight
        {
            get => _boardDetector.BottomRight ?? Point.Empty;
            set => _boardDetector.SetBottomRight(value.X, value.Y);
        }

        private bool _isRunning = false;
        private bool _playAsBlack = false;
        private bool _isHidden = false;
        private bool _showDebugOverlay = false;
        private bool _isBlitzTurboMode = true;
        private string _startupWarning = "";
        private readonly HashSet<int> _registeredHotkeys = new();
        private long _stateVersion;
        private bool _shutdownStarted;
        private bool _readyToClose;
        private Task? _activeScanTask;
        private CancellationTokenSource? _scanCts;
        private int _rescanRequested;
        private int _rescanForce;
        private System.Drawing.Rectangle _overlayMonitorBounds = System.Drawing.Rectangle.Empty;
        private bool _isAutoDetecting;
        private bool _isCalibrating;

        private readonly DispatcherTimer _scanTimer = new();
        private string _lastBestMove = "";
        private string _lastMoveRussianText = "";
        private bool _lastMoveIsCastling = false;
        private string _debugInfo = "";
        private bool _lastBoardVisible = true;
        private string _lastIllegalFen = "";
        private string? _activeFen = null;
        private string? _lastStableFullFen = null;
        private string? _lastRenderedStateKey = null;

        private char[,]? _currentGrid = null;
        private bool _isScanning = false;
        private Bitmap? _captureBitmap;
        private CancellationTokenSource? _humanDelayCts;
        private readonly List<string> _advisorMoves = new();

        public MainWindow()
        {
            _logger = new FileAppLogger();
            _autoBoardDetector = new AutoBoardDetector(_logger, CaptureVirtualScreen);
            _settingsStore = new SettingsStore(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"), _logger);
            _settings = _settingsStore.Load();
            _recognizer = CreateRecognizer();
            _enginePath = ResolveEnginePath(_settings.EngineExecutablePath);
            _engine = new StockfishEngine(_enginePath, _settings, _logger);
            InitializeComponent();
            LoadConfig();
            UpdateUI();

            ApplySpeedPreset();
            _scanTimer.Tick += (_, _) => _ = RunScanAndSuggestAsync();
            Loaded += (_, _) =>
            {
                ApplyOverlayMonitorForBoard();
                RedrawOverlay();
            };
        }

        private IBoardRecognizer CreateRecognizer()
        {
            if (_settings.RecognizerMode == "Template")
                return new TemplateRecognizer();

            try
            {
                string modelPath = _settings.OnnxModelPath;
                if (!System.IO.Path.IsPathRooted(modelPath))
                    modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, modelPath);
                if (!File.Exists(modelPath))
                {
                    string fallbackPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "pieces_64x64.onnx");
                    if (File.Exists(fallbackPath) && !string.Equals(modelPath, fallbackPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn($"ONNX model not found: {modelPath}. Falling back to {fallbackPath}.");
                        modelPath = fallbackPath;
                    }
                }
                return new OnnxBoardRecognizer(modelPath, _logger);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or OnnxRuntimeException)
            {
                _logger.Warn($"ONNX recognizer could not be initialized: {ex.Message}. Falling back to TemplateRecognizer.");
                return new TemplateRecognizer();
            }
        }

        private ICalibratableRecognizer? CalibratableRecognizer => _recognizer as ICalibratableRecognizer;

        private void InvalidateOnnxCache()
        {
            if (_recognizer is OnnxBoardRecognizer onnxRecognizer)
                onnxRecognizer.InvalidateCache();
        }

        private string GetActiveDbFileName() => $"board_auto_v13_p{_activeProfile}.json";

        private string ResolveEnginePath(string configured)
        {
            if (System.IO.Path.IsPathRooted(configured))
                return configured;
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
        }

        private void ApplySpeedPreset()
        {
            _scanTimer.Interval = _isBlitzTurboMode
                ? TimeSpan.FromMilliseconds(_settings.ScanIntervalBlitzMs)
                : TimeSpan.FromMilliseconds(_settings.ScanIntervalRapidMs);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GwlExStyle);
            if (!Win32Interop.TrySetWindowLong(
                    hwnd,
                    Win32Interop.GwlExStyle,
                    exStyle | Win32Interop.WsExLayered | Win32Interop.WsExTransparent,
                    out int styleError))
                _logger.Warn($"SetWindowLong failed with Win32 error {styleError}.");
            Win32Interop.ApplyNoActivateToolStyle(hwnd);

            if (_settings.ExcludeFromCapture)
            {
                if (!Win32Interop.TrySetExcludeFromCapture(hwnd, true))
                    _logger.Warn("SetWindowDisplayAffinity exclude-from-capture failed.");
            }
            else
            {
                _logger.Warn("ExcludeFromCapture disabled in settings; overlay may appear in screenshots.");
                Win32Interop.TrySetExcludeFromCapture(hwnd, false);
            }

            RegisterHotKeys(hwnd);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ApplyOverlayMonitorForBoard();
            UpdateUI();
        }

        private void RegisterHotKeys(IntPtr hwnd)
        {
            (int Id, uint Key, string Name)[] hotkeys =
            {
                (1, 0x31, "Alt+1"), (2, 0x32, "Alt+2"), (3, 0x33, "Alt+3"),
                (4, 0x34, "Alt+4"), (5, 0x35, "Alt+5"), (6, 0x53, "Alt+S"),
                (7, 0x43, "Alt+C"), (8, 0x48, "Alt+H"), (9, 0x51, "Alt+Q"),
                (10, 0x52, "Alt+R"), (11, 0x44, "Alt+D"), (12, 0x46, "Alt+F"),
                (13, 0x47, "Alt+G"), (14, 0x42, "Alt+B"), (15, 0x45, "Alt+E"),
                (16, 0x50, "Alt+P"), (17, 0x54, "Alt+T")
            };
            var failed = new List<string>();
            foreach (var hotkey in hotkeys)
            {
                if (Win32Interop.TryRegisterHotKey(
                        hwnd,
                        hotkey.Id,
                        Win32Interop.ModAlt | Win32Interop.ModNoRepeat,
                        hotkey.Key,
                        out int errorCode))
                {
                    _registeredHotkeys.Add(hotkey.Id);
                    continue;
                }

                failed.Add(hotkey.Name);
                _logger.Warn($"RegisterHotKey failed for {hotkey.Name}; Win32 error {errorCode}.");
            }

            if (failed.Count > 0)
                _startupWarning = $"Не зарегистрированы горячие клавиши: {string.Join(", ", failed)}.";
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32Interop.WmDpiChanged)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyOverlayMonitorForBoard();
                    RedrawOverlay();
                });
                return IntPtr.Zero;
            }

            if (msg == Win32Interop.WmHotkey)
            {
                int id = wParam.ToInt32();
                Point cursor;
                try
                {
                    cursor = Win32Interop.GetCursorPosition();
                }
                catch (Exception ex)
                {
                    _logger.Error("Unable to read cursor position for hotkey.", ex);
                    UpdateUI("Не удалось получить позицию курсора.");
                    handled = true;
                    return IntPtr.Zero;
                }

                switch (id)
                {
                    case 1:
                        _topLeft = cursor;
                        SaveConfig();
                        ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(cursor.X, cursor.Y, 1, 1));
                        DrawCalibrationGrid();
                        InvalidateOnnxCache();
                        _logger.Info($"[П{_activeProfile}] Угол A8 задан ({_topLeft.X}, {_topLeft.Y})");
                        UpdateUI("Верхний угол задан. Наведите на нижний угол и нажмите Alt+2.");
                        InvalidateScanState(requestRescan: _isRunning);
                        break;

                    case 2:
                        _bottomRight = cursor;
                        SaveConfig();
                        BoardRect? bounds = _boardDetector.Detect();
                        if (bounds.HasValue)
                            ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height));
                        DrawCalibrationGrid();
                        InvalidateOnnxCache();
                        _logger.Info($"[П{_activeProfile}] Углы доски заданы.");
                        UpdateUI("Углы доски заданы. Нажмите Alt+3 для анализа.");
                        InvalidateScanState(requestRescan: _isRunning);
                        break;

                    case 3:
                        _ = SmartAlt3ActionAsync();
                        break;

                    case 4:
                        if (CalibratableRecognizer == null)
                        {
                            UpdateUI("Калибровка фигур не требуется. Автопоиск доски: Alt+F.");
                        }
                        else
                        {
                            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;
                            UpdateUI($"Правка эталона ({_fixIndex + 1}/12): {_manualFixOrder[_fixIndex].name}. Наведите и нажмите Alt+F.");
                        }
                        break;

                    case 5:
                        ResetAllCalibration();
                        break;

                    case 6:
                        ToggleEngine();
                        break;

                    case 7:
                        _playAsBlack = !_playAsBlack;
                        InvalidateOnnxCache();
                        ResetBoardTracking();
                        RedrawOverlay();
                        UpdateUI();
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;

                    case 8:
                        _settings.StealthHumanize = !_settings.StealthHumanize;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                            _logger.Error("Unable to save settings after changing advisor mode.", ex);
                            _startupWarning = "Не удалось сохранить настройки.";
                        }
                        _logger.Info($"StealthHumanize={_settings.StealthHumanize}");
                        if (!_settings.StealthHumanize)
                        {
                            _advisorMoves.Clear();
                            RedrawOverlayIfNeeded(_lastStableFullFen);
                        }
                        UpdateUI($"Режим подсказок: {(_settings.StealthHumanize ? "вкл." : "выкл.")}");
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;

                    case 9:
                        Close();
                        break;

                    case 17:
                        _settings.HumanPacingEnabled = !_settings.HumanPacingEnabled;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                            _logger.Error("Unable to save settings after changing human pacing.", ex);
                            _startupWarning = "Не удалось сохранить настройки.";
                        }
                        _logger.Info($"HumanPacingEnabled={_settings.HumanPacingEnabled}");
                        UpdateUI($"Задержка перед подсказкой: {(_settings.HumanPacingEnabled ? "вкл." : "выкл.")}");
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;

                    case 10:
                        InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                        ResetBoardTracking();
                        RedrawOverlay();
                        _ = RunScanAndSuggestAsync(forceCalculate: true);
                        break;

                    case 11:
                        _showDebugOverlay = !_showDebugOverlay;
                        RedrawOverlay();
                        UpdateUI();
                        break;

                    case 12:
                        if (CalibratableRecognizer == null || !HasValidBoardBounds())
                            _ = TryAutoDetectBoardAsync();
                        else
                            ManualFixPieceAtCursor(cursor);
                        break;

                    case 13:
                        _isHidden = !_isHidden;
                        OverlayCanvas.Visibility = _isHidden ? Visibility.Hidden : Visibility.Visible;
                        break;

                    case 14:
                        _isBlitzTurboMode = !_isBlitzTurboMode;
                        ApplySpeedPreset();
                        UpdateUI();
                        break;

                    case 15:
                        _settings.FullStrength = !_settings.FullStrength;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.Error("Unable to save settings after changing engine strength.", ex);
                            _startupWarning = "Не удалось сохранить настройки движка.";
                        }
                        _ = RestartEngineAsync();
                        UpdateUI();
                        if (_isRunning) _ = RunScanAndSuggestAsync(forceCalculate: true);
                        break;

                    case 16:
                        SwitchProfile();
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SwitchProfile()
        {
            InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
            _activeProfile = (_activeProfile % PROFILE_COUNT) + 1;
            _boardDetector.Reset();
            CalibratableRecognizer?.ResetCalibration();
            ResetBoardTracking();
            ClearGraphics();
            LoadConfig();
            ApplyOverlayMonitorForBoard();

            if (HasValidBoardBounds() && HasValidSignatures())
            {
                UpdateUI($"Профиль {_activeProfile}: синхронизирую доску…");
                if (_isRunning)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            else if (HasValidBoardBounds())
            {
                _isRunning = false;
                _scanTimer.Stop();
                UpdateUI($"Профиль {_activeProfile}: углы заданы. Нажмите Alt+3 для анализа.");
            }
            else
            {
                _isRunning = false;
                _scanTimer.Stop();
                UpdateUI($"Профиль {_activeProfile}: задайте углы (Alt+1, Alt+2) или нажмите Alt+F.");
            }
        }

        private bool HasValidBoardBounds()
        {
            BoardRect? bounds = _boardDetector.Detect();
            return _topLeft != Point.Empty
                && _bottomRight != Point.Empty
                && bounds.HasValue
                && bounds.Value.IsValid;
        }

        private bool HasValidSignatures() =>
            CalibratableRecognizer?.IsReady ?? _recognizer.IsReady;

        private Task<bool> TryAutoDetectBoardAsync(bool triggerScanAfter = true)
        {
            if (_isCalibrating)
                return Task.FromResult(false);
            return TryAutoDetectBoardCoreAsync(triggerScanAfter);
        }

        private async Task<bool> TryAutoDetectBoardCoreAsync(bool triggerScanAfter)
        {
            if (_isAutoDetecting || _shutdownStarted)
                return false;
            _isAutoDetecting = true;
            try
            {
                long versionSnap = Interlocked.Read(ref _stateVersion);
                int profileSnap = _activeProfile;
                BoardDetectionDiagnostics diag = await Task.Run(() => _autoBoardDetector.DetectWithDiagnostics());
                if (versionSnap != Interlocked.Read(ref _stateVersion)
                    || profileSnap != _activeProfile
                    || _shutdownStarted)
                {
                    _logger.Info("Auto board detection result discarded: application state changed during scan.");
                    return false;
                }
                if (diag.Rect is not { } rect)
                {
                    _logger.Warn("Auto board detection did not find a chessboard.");
                    UpdateUI("Доска не найдена. Задайте углы вручную: Alt+1 и Alt+2.");
                    return false;
                }

                bool? detectedSide = null;
                if (_recognizer is OnnxBoardRecognizer onnxRec)
                {
                    bool playAsBlackSnap = _playAsBlack;
                    try
                    {
                        detectedSide = await Task.Run(() =>
                        {
                            onnxRec.InvalidateCache();
                            using Bitmap boardBmp = CaptureBoardSnapshot(rect);
                            BoardRecognitionResult recognition = onnxRec.Recognize(boardBmp, playAsBlackSnap);
                            return OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, playAsBlackSnap);
                        });
                    }
                    catch (ObjectDisposedException) when (_shutdownStarted)
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("ONNX side auto-detection after board autodetect failed.", ex);
                    }

                    if (versionSnap != Interlocked.Read(ref _stateVersion)
                        || profileSnap != _activeProfile
                        || _shutdownStarted)
                    {
                        _logger.Info("Auto board detection side check discarded: application state changed during scan.");
                        return false;
                    }
                }

                _topLeft = new Point(rect.Left, rect.Top);
                _bottomRight = new Point(rect.Right, rect.Bottom);
                if (detectedSide.HasValue)
                    _playAsBlack = detectedSide.Value;
                ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
                SaveConfig();

                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                ResetBoardTracking();
                DrawCalibrationGrid();
                _logger.Info($"[AUTODETECT] Board found: {rect.Left},{rect.Top} -> {rect.Right},{rect.Bottom} ({rect.Width}x{rect.Height}) | corners={diag.MatchedCorners}/49 | parity={diag.ParitySquares}/64 | ms={diag.ElapsedMs:F1}");
                UpdateUI($"Доска найдена ({rect.Width}x{rect.Height}).");
                if (triggerScanAfter)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
                return true;
            }
            catch (ObjectDisposedException) when (_shutdownStarted)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error("Auto board detection failed.", ex);
                if (!_shutdownStarted)
                    UpdateUI($"Автопоиск не удался: {ex.Message}");
                return false;
            }
            finally
            {
                _isAutoDetecting = false;
            }
        }

        private async Task SmartAlt3ActionAsync()
        {
            if (_shutdownStarted || _isCalibrating || _isAutoDetecting)
                return;

            _isCalibrating = true;
            try
            {
                bool boardOccluded = _recognizer is OnnxBoardRecognizer onnx && !onnx.LastBoardVisible;
                if (!HasValidBoardBounds() || boardOccluded)
                {
                    long autoVersionSnap = Interlocked.Read(ref _stateVersion);
                    int autoProfileSnap = _activeProfile;
                    if (await TryAutoDetectBoardCoreAsync(triggerScanAfter: true))
                        return;

                    if (autoVersionSnap != Interlocked.Read(ref _stateVersion)
                        || autoProfileSnap != _activeProfile
                        || _shutdownStarted)
                        return;

                    if (!HasValidBoardBounds())
                    {
                        UpdateUI("Сначала задайте углы доски: Alt+1 и Alt+2.");
                        return;
                    }
                }

                if (CalibratableRecognizer == null)
                {
                    InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                    long versionSnap = Interlocked.Read(ref _stateVersion);
                    int profileSnap = _activeProfile;
                    BoardRect? boundsSnap = _boardDetector.Detect();

                    if (_recognizer is OnnxBoardRecognizer onnxRecognizer && boundsSnap is { IsValid: true } rect)
                    {
                        UpdateUI("Пересчитываю позицию…");
                        bool playAsBlackSnap = _playAsBlack;
                        bool? detected = null;
                        try
                        {
                            detected = await Task.Run(() =>
                            {
                                onnxRecognizer.InvalidateCache();
                                using Bitmap boardBmp = CaptureBoardSnapshot(rect);
                                BoardRecognitionResult recognition = onnxRecognizer.Recognize(boardBmp, playAsBlackSnap);
                                return OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, playAsBlackSnap);
                            });
                        }
                        catch (ObjectDisposedException) when (_shutdownStarted)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("ONNX side auto-detection failed.", ex);
                        }

                        if (versionSnap != Interlocked.Read(ref _stateVersion)
                            || profileSnap != _activeProfile
                            || _shutdownStarted)
                        {
                            _logger.Info("Alt+3 ONNX side detection discarded: application state changed.");
                            return;
                        }

                        if (detected.HasValue && detected.Value != _playAsBlack)
                        {
                            _playAsBlack = detected.Value;
                            _logger.Info($"[AI] Авто-цвет: {(_playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ")}");
                        }
                    }

                    if (versionSnap != Interlocked.Read(ref _stateVersion)
                        || profileSnap != _activeProfile
                        || _shutdownStarted)
                        return;

                    ResetBoardTracking();
                    ClearGraphics();
                    if (!_isRunning)
                    {
                        _isRunning = true;
                        _scanTimer.Start();
                    }
                    UpdateUI("Пересчитываю позицию…");
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
                    return;
                }

                if (!HasValidSignatures())
                    LoadConfig();

                BoardRect? templateBounds = _boardDetector.Detect();
                if (templateBounds is not { IsValid: true } validBounds)
                {
                    UpdateUI("Сначала задайте углы доски: Alt+1 и Alt+2.");
                    return;
                }

                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                long templateVersionSnap = Interlocked.Read(ref _stateVersion);
                int templateProfileSnap = _activeProfile;
                bool templatePlayAsBlackSnap = _playAsBlack;
                ICalibratableRecognizer calibratable = CalibratableRecognizer
                    ?? throw new InvalidOperationException("Template calibratable recognizer is unavailable.");

                CalibrationOutcome calibration;
                try
                {
                    calibration = await Task.Run(() =>
                    {
                        using Bitmap boardBmp = CaptureBoardSnapshot(validBounds);
                        return calibratable.ComputeStartingPositionCalibration(boardBmp, templatePlayAsBlackSnap);
                    });
                }
                catch (ObjectDisposedException) when (_shutdownStarted)
                {
                    return;
                }

                if (templateVersionSnap != Interlocked.Read(ref _stateVersion)
                    || templateProfileSnap != _activeProfile
                    || _shutdownStarted)
                {
                    _logger.Info("Alt+3 template calibration discarded: application state changed.");
                    return;
                }

                if (calibration.IsStartingPosition)
                {
                    calibratable.ApplyCalibration(calibration);
                    _playAsBlack = calibration.PlayAsBlack;
                    SaveConfig();
                    InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                    ResetBoardTracking();
                    ClearGraphics();

                    if (!_isRunning)
                    {
                        _isRunning = true;
                        _scanTimer.Start();
                    }

                    string sideDetected = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
                    _logger.Info($"[П{_activeProfile}] Старт v13 снят ({sideDetected}, Б={calibration.WhiteLuma:F0}/Ч={calibration.BlackLuma:F0})");
                    UpdateUI("Начальная позиция распознана. Анализирую…");
                    _ = StartNewGameAndScanAsync();
                    return;
                }

                if (!HasValidSignatures())
                {
                    _logger.Info($"[П{_activeProfile}] Нет эталонов v13 (края={calibration.BackRanksOccupied}, центр={calibration.CenterOccupied}).");
                    UpdateUI("Нет эталонов. Откройте начальную позицию и нажмите Alt+3.");
                    return;
                }

                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                ResetBoardTracking();
                ClearGraphics();

                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                UpdateUI("Пересчитываю позицию…");
                _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (ObjectDisposedException) when (_shutdownStarted)
            {
            }
            catch (Exception ex)
            {
                _logger.Error("Alt+3 action failed.", ex);
                if (!_shutdownStarted)
                    UpdateUI($"Ошибка анализа: {ex.Message}");
            }
            finally
            {
                _isCalibrating = false;
            }
        }

        private void ManualFixPieceAtCursor(Point cursor)
        {
            if (_shutdownStarted || _isCalibrating || _isAutoDetecting)
            {
                UpdateUI("Дождитесь завершения калибровки.");
                return;
            }

            ICalibratableRecognizer? calibratable = CalibratableRecognizer;
            if (calibratable == null)
            {
                UpdateUI("Калибровка фигур не требуется в ONNX-режиме.");
                return;
            }

            if (!HasValidBoardBounds())
            {
                UpdateUI("Сначала задайте углы доски: Alt+1 и Alt+2.");
                return;
            }

            BoardRect bounds = _boardDetector.Detect()!.Value;
            double cellW = bounds.Width / 8.0;
            double cellH = bounds.Height / 8.0;

            int col = (int)((cursor.X - bounds.Left) / cellW);
            int row = (int)((cursor.Y - bounds.Top) / cellH);

            if (col < 0 || col > 7 || row < 0 || row > 7)
            {
                UpdateUI("Курсор вне доски. Наведите на фигуру и нажмите Alt+F.");
                return;
            }

            InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
            char targetFen = _manualFixOrder[_fixIndex].fen;
            string savedName = _manualFixOrder[_fixIndex].name;

            using Bitmap boardBmp = CaptureBoardSnapshot();
            calibratable.CalibratePiece(boardBmp, new Square(row, col), ToPieceClass(targetFen));
            SaveConfig();

            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;

            if (!HasValidSignatures())
            {
                UpdateUI($"Сохранено: {savedName} ({calibratable.CalibrationClassCount}/12). Далее: {_manualFixOrder[_fixIndex].name} (Alt+F).");
            }
            else
            {
                ResetBoardTracking();
                UpdateUI($"Обновлено: {savedName}. Далее: {_manualFixOrder[_fixIndex].name}.");
                if (_isRunning)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
        }

        private static PieceClass ToPieceClass(char piece) => piece switch
        {
            'P' => PieceClass.WhitePawn,
            'N' => PieceClass.WhiteKnight,
            'B' => PieceClass.WhiteBishop,
            'R' => PieceClass.WhiteRook,
            'Q' => PieceClass.WhiteQueen,
            'K' => PieceClass.WhiteKing,
            'p' => PieceClass.BlackPawn,
            'n' => PieceClass.BlackKnight,
            'b' => PieceClass.BlackBishop,
            'r' => PieceClass.BlackRook,
            'q' => PieceClass.BlackQueen,
            'k' => PieceClass.BlackKing,
            _ => PieceClass.Empty
        };

        private void ResetBoardTracking()
        {
            CancelHumanDelay();
            _gameStateTracker.Reset();
            _lastBestMove = "";
            _lastMoveRussianText = "";
            _lastMoveIsCastling = false;
            _debugInfo = "";
            _lastIllegalFen = "";
            _activeFen = null;
            _lastStableFullFen = null;
            _lastRenderedStateKey = null;
            _currentGrid = null;
            _advisorMoves.Clear();
        }

        private void InvalidateScanState(bool requestRescan = false, bool forceCalculate = false)
        {
            Interlocked.Increment(ref _stateVersion);
            try { _scanCts?.Cancel(); } catch { }

            if (requestRescan && !_shutdownStarted)
            {
                Interlocked.Exchange(ref _rescanRequested, 1);
                if (forceCalculate) Interlocked.Exchange(ref _rescanForce, 1);
            }
        }

        private void CancelHumanDelay()
        {
            try
            {
                _humanDelayCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task StartNewGameAndScanAsync()
        {
            try
            {
                await _engine.NewGameAsync(_appLifetimeCts.Token);
                await RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested)
            {
                _logger.Info("New-game request cancelled during application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.Error("Unable to start a new Stockfish game.", ex);
                UpdateUI($"Ошибка Stockfish: {ex.Message}");
            }
        }

        private async Task RestartEngineAsync()
        {
            try
            {
                await _engine.RestartAsync(_appLifetimeCts.Token);
                if (_isRunning)
                    await RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested)
            {
                _logger.Info("Engine restart cancelled during application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.Error("Unable to restart Stockfish.", ex);
                UpdateUI($"Ошибка перезапуска Stockfish: {ex.Message}");
            }
        }

        private static string GetRussianPieceShort(char fenPiece) => char.ToUpper(fenPiece) switch
        {
            'P' => "П",
            'N' => "К",
            'B' => "С",
            'R' => "Л",
            'Q' => "Ф",
            'K' => "Кр",
            _ => ""
        };

        private static string GetRussianPieceFull(char fenPiece) => char.ToUpper(fenPiece) switch
        {
            'P' => "Пешка",
            'N' => "Конь",
            'B' => "Слон",
            'R' => "Ладья",
            'Q' => "Ферзь",
            'K' => "Король",
            _ => "Фигура"
        };

        private void UpdateUI(string? customStatus = null)
        {
            string sideText = _playAsBlack ? "Чёрные" : "Белые";
            string stateText = _isRunning ? "Анализ" : "Пауза";
            string speedText = _isBlitzTurboMode ? "Блиц" : "Рапид";
            string strengthSuffix = _settings.FullStrength ? "" : $" · {_settings.TargetElo}";

            HeaderText.Text = $"ChGhost · {sideText} · {stateText} · {speedText}{strengthSuffix}";
            HeaderText.Foreground = _isRunning
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 60));

            if (customStatus != null)
            {
                StatusText.Text = customStatus;
                return;
            }

            if (!string.IsNullOrEmpty(_startupWarning))
            {
                StatusText.Text = _startupWarning;
                return;
            }

            if (!HasValidBoardBounds())
            {
                StatusText.Text = "Alt+F автопоиск доски · или Alt+1 (верх-лево) и Alt+2 (низ-право)";
                return;
            }

            if (!HasValidSignatures())
            {
                StatusText.Text = "Нет эталонов. Откройте начальную позицию и нажмите Alt+3.";
                return;
            }

            if (_isRunning)
            {
                if (!string.IsNullOrEmpty(_lastMoveRussianText))
                    StatusText.Text = $"Ваш ход: {_lastMoveRussianText}";
                else if (!string.IsNullOrEmpty(_debugInfo))
                    StatusText.Text = _debugInfo;
                else
                    StatusText.Text = "Ожидание хода соперника…";
            }
            else
            {
                StatusText.Text = "Пауза · Alt+3 для анализа";
            }
        }

        private void ResetAllCalibration()
        {
            InvalidateScanState();
            _isRunning = false;
            _scanTimer.Stop();
            CalibratableRecognizer?.ResetCalibration();
            _fixIndex = 0;
            _boardDetector.Reset();
            ResetBoardTracking();

            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            if (File.Exists(cfgPath)) File.Delete(cfgPath);
            ClearGraphics();
            _logger.Info($"[П{_activeProfile}] Калибровка сброшена.");
            UpdateUI("Калибровка сброшена. Задайте углы: Alt+1 и Alt+2.");
        }

        private void SaveConfig()
        {
            ICalibratableRecognizer? calibratable = CalibratableRecognizer;
            if (calibratable == null)
            {
                SaveBoardBounds();
                return;
            }
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            try
            {
                SavedConfig cfg = calibratable.ExportCalibration();
                cfg.TopLeftX = _topLeft.X;
                cfg.TopLeftY = _topLeft.Y;
                cfg.BottomRightX = _bottomRight.X;
                cfg.BottomRightY = _bottomRight.Y;
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Error($"Unable to save profile '{cfgPath}'.", ex);
                _startupWarning = "Не удалось сохранить профиль. Проверьте доступ к папке приложения.";
                UpdateUI();
            }
        }

        private void SaveBoardBounds()
        {
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            try
            {
                SavedConfig cfg = new SavedConfig
                {
                    TopLeftX = _topLeft.X,
                    TopLeftY = _topLeft.Y,
                    BottomRightX = _bottomRight.X,
                    BottomRightY = _bottomRight.Y
                };
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Error($"Unable to save board bounds '{cfgPath}'.", ex);
                _startupWarning = "Не удалось сохранить границы доски. Проверьте доступ к папке приложения.";
                UpdateUI();
            }
        }

        private void LoadConfig()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string curDir = Directory.GetCurrentDirectory();
            string dbFile = GetActiveDbFileName();

            string[] candidates =
            {
                System.IO.Path.Combine(baseDir, dbFile),
                System.IO.Path.Combine(curDir, dbFile),
                System.IO.Path.Combine(curDir, "Release_Build", dbFile)
            };

            string? path = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (CalibratableRecognizer is { } calibratable)
                            calibratable.ImportCalibration(cfg);
                        _topLeft = new Point(cfg.TopLeftX, cfg.TopLeftY);
                        _bottomRight = new Point(cfg.BottomRightX, cfg.BottomRightY);
                        if (HasValidSignatures())
                            return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    _logger.Error($"Unable to load profile '{path}'.", ex);
                    _startupWarning = "Файл профиля повреждён; используются настройки по умолчанию.";
                }
            }

            string[] legacyNames = _activeProfile switch
            {
                1 => new[] { "board_auto_v12.json", "board_auto_v11.json", "board_auto_v10.json", "board_auto_v9.json" },
                2 => new[] { "board_auto_v12_p2.json", "board_auto_v11_p2.json", "board_auto_v10_p2.json" },
                _ => Array.Empty<string>()
            };

            foreach (string legacyName in legacyNames)
            {
                string[] legacyPaths =
                {
                    System.IO.Path.Combine(baseDir, legacyName),
                    System.IO.Path.Combine(curDir, legacyName),
                    System.IO.Path.Combine(curDir, "Release_Build", legacyName)
                };

                string? legacyPath = legacyPaths.FirstOrDefault(File.Exists);
                if (string.IsNullOrEmpty(legacyPath)) continue;

                try
                {
                    var legacyCfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(legacyPath));
                    if (legacyCfg != null && _topLeft == Point.Empty && _bottomRight == Point.Empty)
                    {
                        _topLeft = new Point(legacyCfg.TopLeftX, legacyCfg.TopLeftY);
                        _bottomRight = new Point(legacyCfg.BottomRightX, legacyCfg.BottomRightY);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    _logger.Error($"Unable to load legacy profile '{legacyPath}'.", ex);
                    _startupWarning = "Файл конфигурации повреждён; используются настройки профиля по умолчанию.";
                }
            }
        }

        private void ToggleEngine()
        {
            if (!HasValidBoardBounds() || !HasValidSignatures())
            {
                UpdateUI("Сначала найдите доску (Alt+F) или задайте углы (Alt+1, Alt+2).");
                return;
            }

            _isRunning = !_isRunning;
            if (_isRunning)
            {
                InvalidateScanState();
                ResetBoardTracking();
                ClearGraphics();
                _scanTimer.Start();
                _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            else
            {
                InvalidateScanState();
                _scanTimer.Stop();
                CancelHumanDelay();
                ClearGraphics();
            }
            UpdateUI();
        }

        private void DrawCalibrationGrid()
        {
            ClearGraphics();
            if (!HasValidBoardBounds()) return;

            System.Windows.Rect board = GetBoardDipRect();
            double x = board.X;
            double y = board.Y;
            double w = board.Width;
            double h = board.Height;

            var gridBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 255, 136));

            var border = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Stroke = gridBrush,
                StrokeThickness = 2
            };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            OverlayCanvas.Children.Add(border);

            for (int i = 1; i < 8; i++)
            {
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = x + (w / 8.0) * i, Y1 = y,
                    X2 = x + (w / 8.0) * i, Y2 = y + h,
                    Stroke = gridBrush, StrokeThickness = 1
                });
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = y + (h / 8.0) * i,
                    X2 = x + w, Y2 = y + (h / 8.0) * i,
                    Stroke = gridBrush, StrokeThickness = 1
                });
            }
        }

        private System.Windows.Rect GetBoardDipRect()
        {
            System.Windows.Point topLeft = ScreenToOverlayDip(_topLeft);
            System.Windows.Point bottomRight = ScreenToOverlayDip(_bottomRight);
            return new System.Windows.Rect(topLeft, bottomRight);
        }

        private void ApplyOverlayMonitorForBoard()
        {
            if (!HasValidBoardBounds())
                return;
            BoardRect? bounds = _boardDetector.Detect();
            if (bounds.HasValue)
                ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height));
        }

        private void ApplyOverlayMonitorBounds(System.Drawing.Rectangle boardRect)
        {
            if (!Win32Interop.TryGetMonitorBoundsForRect(boardRect, out var monBounds, out bool fullyContained))
                return;
            if (!fullyContained)
                _logger.Warn($"Board rect crosses monitor bounds: board={boardRect}, monitor={monBounds}");
            _overlayMonitorBounds = monBounds;
            try
            {
                WindowState = WindowState.Normal;
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Win32Interop.SetOverlayBoundsNoActivate(hwnd, monBounds);
            }
            catch (Exception ex)
            {
                _logger.Warn($"SetOverlayBoundsNoActivate failed: {ex.Message}");
            }
        }

        private System.Windows.Point ScreenToOverlayDip(Point screenPoint)
        {
            PresentationSource? source = PresentationSource.FromVisual(OverlayCanvas);
            if (source?.CompositionTarget != null && !source.IsDisposed)
            {
                try
                {
                    return OverlayCanvas.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
                }
                catch (InvalidOperationException)
                {
                }
            }

            int offsetX = !_overlayMonitorBounds.IsEmpty ? _overlayMonitorBounds.Left : 0;
            int offsetY = !_overlayMonitorBounds.IsEmpty ? _overlayMonitorBounds.Top : 0;
            return new System.Windows.Point(screenPoint.X - offsetX, screenPoint.Y - offsetY);
        }

        private (Bitmap Screen, int OffsetX, int OffsetY) CaptureVirtualScreen()
        {
            System.Drawing.Rectangle bounds = Win32Interop.GetVirtualScreenBounds();
            if (bounds.IsEmpty)
                bounds = new System.Drawing.Rectangle(0, 0, 1, 1);

            var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            return (bmp, bounds.X, bounds.Y);
        }

        private Bitmap CaptureBoard()
        {
            BoardRect bounds = _boardDetector.Detect() ?? throw new InvalidOperationException("Board bounds are not configured.");
            int width = bounds.Width;
            int height = bounds.Height;
            if (_captureBitmap == null || _captureBitmap.Width != width || _captureBitmap.Height != height)
            {
                _captureBitmap?.Dispose();
                _captureBitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            using Graphics g = Graphics.FromImage(_captureBitmap);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
            return _captureBitmap;
        }

        private Bitmap CaptureBoardSnapshot()
        {
            BoardRect bounds = _boardDetector.Detect() ?? throw new InvalidOperationException("Board bounds are not configured.");
            return CaptureBoardSnapshot(bounds);
        }

        private static Bitmap CaptureBoardSnapshot(BoardRect bounds)
        {
            Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
            return bmp;
        }

        private async Task MaybeUnloadIdleEngineAsync()
        {
            if (_settings.EngineIdleUnloadSeconds <= 0)
                return;
            try
            {
                await _engine.UnloadIfIdleAsync(TimeSpan.FromSeconds(_settings.EngineIdleUnloadSeconds));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Idle engine unload failed: {ex.Message}");
            }
        }

        private async Task ScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_isScanning || !HasValidBoardBounds() || !HasValidSignatures()) return;

            if (!forceCalculate && Win32Interop.IsLeftMouseButtonDown())
                return;

            _isScanning = true;
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetimeCts.Token);
            CancellationToken scanToken = _scanCts.Token;
            long versionSnap = Interlocked.Read(ref _stateVersion);
            bool IsStale() => _shutdownStarted || versionSnap != Interlocked.Read(ref _stateVersion);
            bool playAsBlackSnap = _playAsBlack;
            _activeFen = null;
            double inferMs = 0;
            int dirtyCount = 0;
            try
            {
                BoardRecognitionResult recognition = await Task.Run(() =>
                {
                    Bitmap bmp = CaptureBoard();
                    return _recognizer.Recognize(bmp, playAsBlackSnap);
                });
                if (IsStale()) return;

                if (_recognizer is OnnxBoardRecognizer onnxRecognizer)
                {
                    inferMs = onnxRecognizer.LastInferenceMs;
                    dirtyCount = onnxRecognizer.LastDirtySquareCount;
                    if (!onnxRecognizer.LastBoardVisible)
                    {
                        if (_lastBoardVisible)
                        {
                            _logger.Info("Board is occluded; pausing board scans.");
                            _lastBoardVisible = false;
                        }
                        _lastBestMove = "";
                        _lastMoveRussianText = "";
                        _lastMoveIsCastling = false;
                        _advisorMoves.Clear();
                        ClearGraphics();
                        UpdateUI("Доска не видна — анализ приостановлен.");
                        await MaybeUnloadIdleEngineAsync();
                        return;
                    }
                    _lastBoardVisible = true;

                    bool? strictSide = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, _playAsBlack);
                    if (strictSide.HasValue && strictSide.Value != _playAsBlack)
                    {
                        _playAsBlack = strictSide.Value;
                        recognition = onnxRecognizer.ReprojectCachedResult(_playAsBlack);
                        playAsBlackSnap = _playAsBlack;
                        ResetBoardTracking();
                        SaveConfig();
                        UpdateUI();
                        _logger.Info($"[ORIENT] Auto-flipped playAsBlack={_playAsBlack} from live board.");
                    }
                }

                scanToken.ThrowIfCancellationRequested();
                if (IsStale()) return;
                TrackingUpdate tracking = _gameStateTracker.Update(recognition, forceCalculate, playAsBlackSnap);
                if (tracking.IsDebounced)
                {
                    await MaybeUnloadIdleEngineAsync();
                    return;
                }

                if (!tracking.IsValid)
                {
                    _logger.Warn($"Invalid king count: white={tracking.WhiteKings}, black={tracking.BlackKings}");
                    _debugInfo = "Не удалось распознать оба короля. Нажмите Alt+3 для пересчёта.";
                    UpdateUI();
                    return;
                }

                char[,] stableGrid = tracking.Grid;
                _currentGrid = stableGrid;
                _debugInfo = "";

                string fullFen = BuildFullFen(tracking, playAsBlackSnap);
                _lastStableFullFen = fullFen;

                if (tracking.IsUnchanged)
                {
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlayIfNeeded(fullFen);
                    return;
                }

                if (tracking.IsWaitingForOpponent)
                {
                    _lastBestMove = "";
                    _lastMoveRussianText = "";
                    _lastMoveIsCastling = false;
                    _advisorMoves.Clear();
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlayIfNeeded(fullFen);
                    UpdateUI("Ожидание хода соперника…");
                    return;
                }

                if (!tracking.ShouldCalculateMove)
                {
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlayIfNeeded(fullFen);
                    return;
                }

                _activeFen = fullFen;
                int moveTimeMs = _isBlitzTurboMode ? _settings.MoveTimeBlitzMs : _settings.MoveTimeRapidMs;

                var (isLegal, illegalReason) = FenLegalityValidator.Validate(stableGrid, whiteToMove: !playAsBlackSnap);
                if (!isLegal)
                {
                    if (fullFen != _lastIllegalFen)
                    {
                        _logger.Info($"[SKIP] Illegal FEN: {illegalReason} | fen={fullFen}");
                        _lastIllegalFen = fullFen;
                    }
                    _debugInfo = $"Нелегальная позиция: {illegalReason}";
                    await MaybeUnloadIdleEngineAsync();
                    UpdateUI(_debugInfo);
                    return;
                }

                _advisorMoves.Clear();
                bool isAdvisorMode = _settings.StealthHumanize && _settings.ShowAdvisorArrows;
                bool useMultiPv = (isAdvisorMode && _settings.MultiPvCount > 1)
                    || (!_settings.FullStrength && _settings.StealthHumanize && _settings.HumanPacingEnabled && _settings.MultiPvCount > 1);
                int requestedMultiPv = Math.Clamp(_settings.MultiPvCount, 1, 6);
                IReadOnlyList<CandidateMove> candidates = Array.Empty<CandidateMove>();
                string bestMove = "(none)";
                int appliedDelayMs = 0;
                int pvCount = 1;
                int pickIndex = 1;
                var sfStopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (useMultiPv)
                {
                    candidates = await _engine.GetTopMovesAsync(fullFen, moveTimeMs, requestedMultiPv, scanToken);
                    pvCount = candidates.Count;
                }
                else
                {
                    bestMove = await _engine.GetBestMoveAsync(fullFen, moveTimeMs, scanToken);
                }
                sfStopwatch.Stop();
                if (IsStale() || (!_isRunning && !forceCalculate) || scanToken.IsCancellationRequested)
                    return;

                if (useMultiPv)
                {
                    if (candidates.Count == 0)
                    {
                        bestMove = "(none)";
                    }
                    else
                    {
                        var humanizerOptions = new HumanizerOptions(
                            MaxCpLoss: _settings.HumanMaxCpLoss,
                            Temperature: _settings.HumanTemperature,
                            AdvisorMaxGapCp: _settings.AdvisorMaxGapCp,
                            PacingEnabled: _settings.HumanPacingEnabled,
                            MinDelayMs: _isBlitzTurboMode ? _settings.HumanDelayBlitzMinMs : _settings.HumanDelayRapidMinMs,
                            MaxDelayMs: _isBlitzTurboMode ? _settings.HumanDelayBlitzMaxMs : _settings.HumanDelayRapidMaxMs,
                            AdvisorMode: isAdvisorMode,
                            StrictBestMove: _settings.FullStrength);
                        MoveRecommendation rec = HumanMoveSelector.Select(candidates, stableGrid, humanizerOptions, _rng);
                        bestMove = rec.Primary.Uci;
                        pickIndex = rec.PickIndex;
                        if (_settings.ShowAdvisorArrows)
                        {
                            foreach (CandidateMove alt in rec.Alternatives.Take(2))
                                _advisorMoves.Add(alt.Uci);
                        }

                        if (_settings.HumanPacingEnabled && rec.RecommendedDelayMs > 0 && !forceCalculate)
                        {
                            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(scanToken);
                            _humanDelayCts = delayCts;
                            try
                            {
                                await Task.Delay(rec.RecommendedDelayMs, delayCts.Token);
                            }
                            catch (OperationCanceledException) when (!_appLifetimeCts.IsCancellationRequested)
                            {
                                return;
                            }
                            finally
                            {
                                if (ReferenceEquals(_humanDelayCts, delayCts))
                                    _humanDelayCts = null;
                                delayCts.Dispose();
                            }
                            if (IsStale() || _activeFen != fullFen || (!_isRunning && !forceCalculate) || scanToken.IsCancellationRequested)
                                return;
                            appliedDelayMs = rec.RecommendedDelayMs;
                        }
                    }
                }

                _logger.Info($"[MOVE] black={playAsBlackSnap} | inferMs={inferMs:F1} | dirty={dirtyCount} | sfMs={sfStopwatch.Elapsed.TotalMilliseconds:F1} | delayMs={appliedDelayMs} | pv={pvCount} | pick=#{pickIndex} | fen={fullFen} | best={bestMove}");

                if (IsStale() || (!_isRunning && !forceCalculate) || scanToken.IsCancellationRequested)
                    return;

                if (bestMove.Length >= 4 && bestMove != "(none)")
                {
                    _lastBestMove = bestMove;

                    int fromCol = bestMove[0] - 'a';
                    int fromRow = 8 - (bestMove[1] - '0');
                    char movingPiece = (fromRow >= 0 && fromRow < 8 && fromCol >= 0 && fromCol < 8)
                        ? stableGrid[fromRow, fromCol]
                        : 'P';

                    bool isKing = char.ToUpper(movingPiece) == 'K';
                    bool isKingSideCastle = isKing && (bestMove == "e1g1" || bestMove == "e8g8");
                    bool isQueenSideCastle = isKing && (bestMove == "e1c1" || bestMove == "e8c8");
                    _lastMoveIsCastling = isKingSideCastle || isQueenSideCastle;

                    if (isKingSideCastle)
                    {
                        _lastMoveRussianText = "Рокировка 0-0 (короткая)";
                    }
                    else if (isQueenSideCastle)
                    {
                        _lastMoveRussianText = "Рокировка 0-0-0 (длинная)";
                    }
                    else
                    {
                        string pieceRus = GetRussianPieceFull(movingPiece);
                        string fromSq = bestMove.Substring(0, 2);
                        string toSq = bestMove.Substring(2, 2);
                        string promoSuffix = "";
                        if (bestMove.Length >= 5)
                            promoSuffix = $"={GetRussianPieceShort(bestMove[4])}";
                        _lastMoveRussianText = $"{pieceRus} {fromSq} → {toSq}{promoSuffix}";
                    }

                    RedrawOverlay(fullFen);
                    UpdateUI();
                }
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested || (_scanCts?.IsCancellationRequested ?? false))
            {
                _logger.Info("Board scan cancelled.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Board scan or Stockfish operation failed. fen={_activeFen ?? "-"}", ex);
                _debugInfo = File.Exists(_enginePath)
                    ? $"Ошибка движка: {ex.Message}"
                    : $"Не найден {_settings.EngineExecutablePath} рядом с ChGhost.exe.";
                UpdateUI(_debugInfo);
            }
            finally
            {
                _isScanning = false;
                CancellationTokenSource? scanCtsLocal = _scanCts;
                _scanCts = null;
                try { scanCtsLocal?.Dispose(); } catch { }
                if (!_shutdownStarted && Interlocked.Exchange(ref _rescanRequested, 0) == 1)
                {
                    bool force = Interlocked.Exchange(ref _rescanForce, 0) == 1;
                    _ = Dispatcher.BeginInvoke(async delegate
                    {
                        await RunScanAndSuggestAsync(forceCalculate: force).ConfigureAwait(false);
                    });
                }
            }
        }
        private void ScheduleRescanSync(bool forceCalculate)
        {
            if (_shutdownStarted)
                return;
            _ = RunScanAndSuggestAsync(forceCalculate);
        }

        private Task RunScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_shutdownStarted)
                return Task.CompletedTask;
            if (_isScanning)
            {
                Interlocked.Exchange(ref _rescanRequested, 1);
                if (forceCalculate)
                    Interlocked.Exchange(ref _rescanForce, 1);
                return _activeScanTask ?? Task.CompletedTask;
            }

            Task task = ScanAndSuggestAsync(forceCalculate);
            if (!task.IsCompleted)
            {
                _activeScanTask = task;
                task.ContinueWith(
                    completed =>
                    {
                        if (ReferenceEquals(_activeScanTask, completed))
                            Interlocked.CompareExchange(ref _activeScanTask, null, completed);
                    },
                    TaskScheduler.Default);
            }
            return task;
        }

        private static string BuildFullFen(TrackingUpdate tracking, bool playAsBlack)
        {
            char turn = playAsBlack ? 'b' : 'w';
            string epTarget = tracking.EnPassantTarget;
            bool epMatchesTurn = epTarget != "-" && epTarget.Length == 2
                && ((turn == 'w' && epTarget[1] == '6') || (turn == 'b' && epTarget[1] == '3'));
            return $"{tracking.StableFen} {turn} {tracking.CastlingRights} {(epMatchesTurn ? epTarget : "-")} {tracking.HalfmoveClock} {tracking.FullmoveNumber}";
        }

        private string BuildRenderStateKey(string? fullFen)
        {
            BoardRect? bounds = _boardDetector.Detect();
            string boundsKey = bounds is { IsValid: true } b
                ? $"{b.Left},{b.Top},{b.Right},{b.Bottom}|{_overlayMonitorBounds.Left},{_overlayMonitorBounds.Top},{_overlayMonitorBounds.Width},{_overlayMonitorBounds.Height}"
                : "none";
            string advisorKey = _settings.ShowAdvisorArrows && _advisorMoves.Count > 0
                ? string.Join(",", _advisorMoves)
                : "-";
            return $"{_isRunning}|{_settings.StealthHumanize}|{_settings.HumanPacingEnabled}|{_settings.ShowAdvisorArrows}|{_playAsBlack}|{fullFen ?? "-"}|{_lastBestMove}|{_lastMoveIsCastling}|{advisorKey}|{_showDebugOverlay}|{boundsKey}";
        }

        private void ClearGraphics()
        {
            _lastRenderedStateKey = null;
            for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (OverlayCanvas.Children[i] != StatusBox)
                    OverlayCanvas.Children.RemoveAt(i);
            }
        }

        private void RedrawOverlayIfNeeded(string? fullFen)
        {
            string renderKey = BuildRenderStateKey(fullFen);
            if (string.Equals(renderKey, _lastRenderedStateKey, StringComparison.Ordinal))
                return;
            RedrawOverlay(fullFen);
        }

        private void RedrawOverlay(string? fullFen = null)
        {
            ClearGraphics();
            if (_showDebugOverlay && _currentGrid != null)
            {
                DrawXRayLabels(_currentGrid);
            }
            if (!string.IsNullOrEmpty(_lastBestMove) && _lastBestMove.Length >= 4)
            {
                if (_settings.ShowAdvisorArrows)
                {
                    double advisorOpacity = Math.Clamp(_settings.AdvisorArrowOpacity, 0.2, 1.0);
                    System.Windows.Media.Color advisorColor1 = ParseAdvisorColor(_settings.AdvisorArrowColor1, System.Windows.Media.Color.FromRgb(0, 229, 255));
                    System.Windows.Media.Color advisorColor2 = ParseAdvisorColor(_settings.AdvisorArrowColor2, System.Windows.Media.Color.FromRgb(224, 64, 251));
                    int advisorIndex = 0;
                    foreach (string altMove in _advisorMoves)
                    {
                        if (advisorIndex >= 2)
                            break;
                        DrawSingleArrow(
                            altMove,
                            advisorIndex == 0 ? advisorColor1 : advisorColor2,
                            thickness: 6.4,
                            opacity: advisorOpacity,
                            highContrastOutline: true);
                        advisorIndex++;
                    }
                }

                if (_lastMoveIsCastling)
                    DrawCastlingArrows(_lastBestMove);
                else
                    DrawSingleArrow(_lastBestMove, System.Windows.Media.Color.FromArgb(215, 0, 255, 136), thickness: 8);
            }
            _lastRenderedStateKey = BuildRenderStateKey(fullFen ?? _activeFen ?? _lastStableFullFen);
        }

        private void DrawXRayLabels(char[,] grid)
        {
            if (!HasValidBoardBounds()) return;

            System.Windows.Rect board = GetBoardDipRect();
            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;
            double startX = board.X;
            double startY = board.Y;

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int boardR = _playAsBlack ? 7 - r : r;
                    int boardC = _playAsBlack ? 7 - c : c;
                    char p = grid[boardR, boardC];
                    if (p == '1') continue;

                    bool isWhite = char.IsUpper(p);
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 15, 20, 25)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(3, 0, 3, 0),
                        Child = new TextBlock
                        {
                            Text = GetRussianPieceShort(p),
                            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                            FontWeight = FontWeights.Bold,
                            FontSize = 11,
                            Foreground = isWhite
                                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 50))
                        }
                    };
                    Canvas.SetLeft(badge, startX + c * cellW + 3);
                    Canvas.SetTop(badge, startY + r * cellH + 3);
                    OverlayCanvas.Children.Add(badge);
                }
            }
        }

        private void DrawCastlingArrows(string kingUciMove)
        {
            DrawSingleArrow(kingUciMove, System.Windows.Media.Color.FromArgb(225, 0, 255, 136), thickness: 8);

            string rookMove = kingUciMove switch
            {
                "e1g1" => "h1f1",
                "e1c1" => "a1d1",
                "e8g8" => "h8f8",
                "e8c8" => "a8d8",
                _ => ""
            };

            if (!string.IsNullOrEmpty(rookMove))
            {
                DrawSingleArrow(rookMove, System.Windows.Media.Color.FromArgb(205, 0, 200, 255), thickness: 5);
            }
        }

        private static System.Windows.Media.Color ParseAdvisorColor(string? value, System.Windows.Media.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            try
            {
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value.Trim());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or InvalidCastException)
            {
                return fallback;
            }
        }

        private static readonly SolidColorBrush ArrowOutlineBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(204, 0, 0, 0));

        private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal static System.Windows.Point[] BuildArrowVertices(double x1, double y1, double x2, double y2, double thickness)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3)
                return Array.Empty<System.Windows.Point>();

            double ux = dx / len;
            double uy = dy / len;
            double nx = -uy;
            double ny = ux;

            double halfShaft = thickness * 0.5;
            double headLen = Math.Min(thickness * 2.6, len * 0.55);
            double halfWing = Math.Max(halfShaft + 2.0, headLen * Math.Tan(Math.PI / 6.0));
            double neckX = x2 - ux * headLen;
            double neckY = y2 - uy * headLen;

            return new[]
            {
                new System.Windows.Point(x1 + nx * halfShaft, y1 + ny * halfShaft),
                new System.Windows.Point(neckX + nx * halfShaft, neckY + ny * halfShaft),
                new System.Windows.Point(neckX + nx * halfWing, neckY + ny * halfWing),
                new System.Windows.Point(x2, y2),
                new System.Windows.Point(neckX - nx * halfWing, neckY - ny * halfWing),
                new System.Windows.Point(neckX - nx * halfShaft, neckY - ny * halfShaft),
                new System.Windows.Point(x1 - nx * halfShaft, y1 - ny * halfShaft)
            };
        }

        private void DrawSingleArrow(string uciMove, System.Windows.Media.Color argbColor, double thickness, double opacity = 1.0, bool highContrastOutline = false)
        {
            System.Windows.Rect board = GetBoardDipRect();
            if (board.Width <= 0 || board.Height <= 0)
                return;

            int c1 = uciMove[0] - 'a';
            int r1 = 8 - (uciMove[1] - '0');
            int c2 = uciMove[2] - 'a';
            int r2 = 8 - (uciMove[3] - '0');

            if (_playAsBlack)
            {
                c1 = 7 - c1; r1 = 7 - r1;
                c2 = 7 - c2; r2 = 7 - r2;
            }

            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;
            double x1 = board.X + (c1 + 0.5) * cellW;
            double y1 = board.Y + (r1 + 0.5) * cellH;
            double x2 = board.X + (c2 + 0.5) * cellW;
            double y2 = board.Y + (r2 + 0.5) * cellH;

            System.Windows.Point[] vertices = BuildArrowVertices(x1, y1, x2, y2, thickness);
            if (vertices.Length != 7)
                return;

            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(vertices[0], isFilled: true, isClosed: true);
                for (int i = 1; i < vertices.Length; i++)
                    ctx.LineTo(vertices[i], isStroked: true, isSmoothJoin: true);
            }
            geometry.Freeze();

            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = CreateFrozenBrush(argbColor),
                Stroke = ArrowOutlineBrush,
                StrokeThickness = highContrastOutline ? 1.5 : 1.0,
                StrokeLineJoin = PenLineJoin.Round,
                Opacity = opacity
            };
            OverlayCanvas.Children.Add(path);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_readyToClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            try { _scanTimer.Stop(); } catch { }
            try { UnregisterAllHotkeys(); } catch { }
            try { _scanCts?.Cancel(); } catch { }
            try { _appLifetimeCts.Cancel(); } catch { }

            _ = ShutdownAndCloseAsync();
        }

        private async Task ShutdownAndCloseAsync()
        {
            try
            {
                var scanTask = _activeScanTask;
                bool scanCompleted = scanTask is null;
                if (scanTask is not null)
                {
                    try
                    {
                        scanCompleted = await Task.WhenAny(scanTask, Task.Delay(1500)) == scanTask;
                    }
                    catch
                    {
                        scanCompleted = false;
                    }
                }
                if (!scanCompleted)
                    _logger.Warn("Active board scan did not finish within the shutdown timeout; shared capture resources are left for the OS to reclaim on exit.");

                try { if (scanCompleted) { _captureBitmap?.Dispose(); _captureBitmap = null; } } catch { }
                try { if (scanCompleted) (_recognizer as IDisposable)?.Dispose(); } catch { }
                try
                {
                    if (_engine is IAsyncDisposable asyncEng)
                        await Task.WhenAny(asyncEng.DisposeAsync().AsTask(), Task.Delay(1500));
                    else
                        (_engine as IDisposable)?.Dispose();
                }
                catch { }

                try { _scanCts?.Dispose(); } catch { }
                try { _appLifetimeCts.Dispose(); } catch { }
            }
            finally
            {
                _readyToClose = true;
                Close();
            }
        }

        private void UnregisterAllHotkeys() => UnregisterHotKeys();

        private void UnregisterHotKeys()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            foreach (int id in _registeredHotkeys)
            {
                if (Win32Interop.TryUnregisterHotKey(hwnd, id, out int errorCode))
                    continue;
                if (errorCode != 0)
                    _logger.Warn($"UnregisterHotKey failed for id {id}; Win32 error {errorCode}.");
            }
            _registeredHotkeys.Clear();
        }
    }
}