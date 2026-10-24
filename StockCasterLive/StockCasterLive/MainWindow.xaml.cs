using System.Diagnostics;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace StockCasterLive;

public partial class MainWindow : Window
{
    private readonly FfmpegStreamingService _streamer = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // 방송 인코딩과는 완전히 별개로, 설정 화면의 마이크/PC 소리 막대만을 위한
    // 레벨 측정기. 방송 중이 아니어도(마이크 테스트 등) 항상 켜 둘 수 있다.
    private readonly AudioLevelMeter _levelMeter = new();
    private readonly DispatcherTimer _levelMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly StreamingSettings _settings = StreamingSettings.Load();
    private readonly List<PrivacyMask> _privacyMasks = new();
    private DateTime _broadcastStartedUtc;
    private bool _closing;
    private string _maskSourceKey = string.Empty;
    private WindowInfo? _customRegionSource;
    private int _previewPixelWidth;
    private int _previewPixelHeight;
    private Point? _maskDragStart;
    private Rectangle? _draftMaskRectangle;
    private bool _savedRegionUnavailable;

    private static string DefaultRecordingFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StockCaster 녹화");

    public MainWindow()
    {
        InitializeComponent();

        _previewTimer.Tick += PreviewTimer_Tick;
        _elapsedTimer.Tick += ElapsedTimer_Tick;
        _levelMeterTimer.Tick += LevelMeterTimer_Tick;
        _streamer.StatusReceived += Streamer_StatusReceived;
        _streamer.StreamEnded += Streamer_StreamEnded;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpgradeQualityProfile();
        ApplySavedSettings();
        RefreshWindows();
        RestoreSavedCaptureLayout();
        await RefreshAudioDevicesAsync();
        _previewTimer.Start();
        UpdateLevelMeterSources();
        _levelMeterTimer.Start();
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        Version? currentVersion = typeof(MainWindow).Assembly.GetName().Version;
        if (currentVersion is null)
            return;

        AppUpdate? update;
        try
        {
            update = await UpdateService.CheckForUpdateAsync(currentVersion);
        }
        catch
        {
            // 업데이트 서버에 연결할 수 없어도 앱 실행은 계속한다.
            return;
        }

        if (update is null)
            return;

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"새 버전 {update.Version}이 있습니다.\n현재 버전: {currentVersion.ToString(3)}\n\n지금 다운로드하여 설치하시겠습니까?",
            "EdgarCaster Live 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
            return;

        var progressWindow = new Window
        {
            Title = "업데이트 다운로드",
            Width = 390,
            Height = 138,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            Background = Brushes.White,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"버전 {update.Version} 설치 파일을 다운로드하고 있습니다.",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromRgb(36, 48, 69))
                    },
                    new ProgressBar
                    {
                        Height = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        IsIndeterminate = true
                    }
                }
            }
        };
        progressWindow.Show();

        try
        {
            string installerPath = await UpdateService.DownloadInstallerAsync(update);
            progressWindow.Close();
            if (Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            }) is null)
            {
                throw new InvalidOperationException("설치 프로그램을 실행하지 못했습니다.");
            }
            Close();
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            MessageBox.Show(
                this,
                $"업데이트 파일을 다운로드하거나 실행하지 못했습니다.\n인터넷 연결을 확인한 뒤 다시 시도해 주세요.\n\n{ex.Message}",
                "업데이트 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // 마이크 체크박스/장치 선택, PC 소리 체크박스가 바뀔 때마다 호출해 레벨
    // 측정기가 지금 UI 상태와 같은 장치/소스를 듣고 있도록 맞춘다.
    private void UpdateLevelMeterSources()
    {
        // UseMicCheck는 XAML에 IsChecked="True"로 적혀 있어서, InitializeComponent()가
        // 이 체크박스까지만 읽은 시점에 Checked 이벤트가 곧바로(동기적으로) 발생한다.
        // 그때는 아래쪽에 선언된 AudioDeviceCombo/UseSecondaryAudioCheck 필드가 아직
        // XAML 파서에 의해 연결되기 전이라 null이므로, 여기서 먼저 걸러낸다. 진짜 초기
        // 동기화는 MainWindow_Loaded에서 한 번 더 호출되는 UpdateLevelMeterSources()가
        // 맡는다.
        if (UseMicCheck is null || AudioDeviceCombo is null || UseSecondaryAudioCheck is null)
            return;

        string? micName = UseMicCheck.IsChecked == true
            ? (AudioDeviceCombo.SelectedItem as AudioDevice)?.DisplayName
            : null;
        _levelMeter.SetMicDevice(micName);
        _levelMeter.SetSystemAudioEnabled(UseSecondaryAudioCheck.IsChecked == true);
    }

    private void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateLevelMeterSources();

    private void UseSecondaryAudioCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateLevelMeterSources();

    // 80ms마다 마이크/PC 소리 막대를 갱신한다. 원시 피크값(선형 0~1)은 보통 말소리
    // 정도로는 매우 작게 나와서(예: 0.05~0.2) 막대가 거의 안 보이므로, 제곱근으로
    // 완만하게 키워 보여준다(사람이 느끼는 음량에 더 가까운 곡선).
    private void LevelMeterTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLevelBar(MicLevelTrack, MicLevelFill, _levelMeter.ReadMicLevel());
        UpdateLevelBar(SystemLevelTrack, SystemLevelFill, _levelMeter.ReadSystemLevel());
    }

    private static void UpdateLevelBar(FrameworkElement track, FrameworkElement fill, float rawLevel)
    {
        float level = (float)Math.Sqrt(Math.Clamp(rawLevel, 0f, 1f));
        double trackWidth = track.ActualWidth;
        fill.Width = trackWidth > 0 ? trackWidth * level : 0;
    }

    private void UpgradeQualityProfile()
    {
        // 0.1에서 저장된 720p 기본값을 한 번만 1080p 권장값으로 올린다.
        // 이후 사용자가 720p를 직접 선택하면 그 선택은 그대로 유지된다.
        if (_settings.QualityProfileVersion >= 1)
            return;

        _settings.OutputHeight = 1080;
        _settings.QualityProfileVersion = 1;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 설정 파일을 쓸 수 없어도 현재 실행에는 1080p를 적용한다.
        }
    }

    private void ApplySavedSettings()
    {
        ServerUrlBox.Text = string.IsNullOrWhiteSpace(_settings.ServerUrl) ? "rtmp://" : _settings.ServerUrl;
        RememberKeyCheck.IsChecked = _settings.RememberStreamKey;
        if (_settings.RememberStreamKey)
            StreamKeyBox.Password = _settings.StreamKey;

        ResolutionCombo.SelectedIndex = _settings.OutputHeight == 1080 ? 1 : 0;
        FrameRateCombo.SelectedIndex = _settings.FrameRate switch
        {
            24 => 0,
            60 => 2,
            _ => 1
        };
        UseMicCheck.IsChecked = _settings.UseMicrophone;
        UseSecondaryAudioCheck.IsChecked = _settings.UseSecondaryAudio;

        RecordingFolderBox.Text = string.IsNullOrWhiteSpace(_settings.RecordingFolderPath)
            ? DefaultRecordingFolder
            : _settings.RecordingFolderPath;

        if (_settings.SavedCaptureRegion is CaptureRegion savedRegion)
        {
            if (WindowCaptureService.IsRegionAvailable(savedRegion))
            {
                _customRegionSource = new WindowInfo
                {
                    Region = savedRegion,
                    Title = "사용자 지정 영역",
                    Width = savedRegion.Width,
                    Height = savedRegion.Height
                };
            }
            else
            {
                _savedRegionUnavailable = true;
            }
        }
    }

    private void RestoreSavedCaptureLayout()
    {
        if (_customRegionSource is not null)
        {
            // 일반적인 경우 모자이크는 이미 RefreshWindows → WindowCombo_SelectionChanged 경로에서
            // SavedMaskSourceKey로 자동 복원되었다. SavedMaskSourceKey가 없는 이전 버전 설정 파일을
            // 열었을 때만(그리고 이 실행에서 지정 영역이 선택되어 있을 때만) 위치 기준으로 한 번 더
            // 복원을 시도해 기존 사용자의 모자이크가 사라지지 않게 한다.
            if (WindowCombo.SelectedItem == _customRegionSource && _privacyMasks.Count == 0)
            {
                foreach (PrivacyMask mask in _settings.SavedPrivacyMasks ?? new List<PrivacyMask>())
                {
                    if (IsValidPrivacyMask(mask))
                        _privacyMasks.Add(mask);
                }

                if (_privacyMasks.Count > 0)
                    RenderPrivacyMasks();
            }

            CaptureRegion region = _customRegionSource.Region!;
            SetStatus("저장된 방송 영역을 불러왔습니다",
                $"크기 {region.Width} × {region.Height}  ·  민감정보 모자이크 {_privacyMasks.Count}개", true);
        }
        else if (_savedRegionUnavailable)
        {
            SetStatus("저장된 방송 영역을 사용할 수 없습니다",
                "모니터 해상도나 배치가 변경되었습니다. 영역을 다시 지정해 주세요.", false);
        }
    }

    private void RefreshWindows()
    {
        WindowInfo? selectedSource = WindowCombo.SelectedItem as WindowInfo;
        nint selectedHandle = selectedSource?.Handle ?? 0;
        bool customRegionWasSelected = selectedSource?.Region is not null;
        var windows = WindowCaptureService.GetCapturableWindows();
        if (_customRegionSource is not null)
            windows.Insert(0, _customRegionSource);

        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = customRegionWasSelected
            ? _customRegionSource
            : windows.FirstOrDefault(window => window.Region is null && window.Handle == selectedHandle)
              // 앱을 새로 시작한 직후에는 창 핸들이 이전 실행과 달라서 위 매칭이 항상 실패한다.
              // 이전에 민감정보 모자이크를 저장해 둔 창과 같은 창(프로세스명+제목)이 지금도 열려 있으면
              // 자동으로 다시 선택해서 모자이크가 함께 복원되도록 한다.
              ?? (selectedSource is null && !string.IsNullOrEmpty(_settings.SavedMaskSourceKey)
                  ? windows.FirstOrDefault(window => ComputeSourceKey(window) == _settings.SavedMaskSourceKey)
                  : null);

        if (WindowCombo.SelectedItem is null && windows.Count > 0)
            WindowCombo.SelectedIndex = 0;

        if (windows.Count == 0)
        {
            PreviewImage.Source = null;
            PreviewEmptyPanel.Visibility = Visibility.Visible;
            PreviewInfoText.Text = "열려 있는 창이 없습니다";
        }
    }

    private async Task RefreshAudioDevicesAsync()
    {
        string? previousMicName = (AudioDeviceCombo.SelectedItem as AudioDevice)?.DisplayName ?? _settings.AudioDeviceName;
        AudioDeviceCombo.IsEnabled = false;

        try
        {
            var devices = await Task.Run(_streamer.ListAudioDevices);

            AudioDeviceCombo.ItemsSource = devices;
            AudioDeviceCombo.SelectedItem = devices.FirstOrDefault(device => device.DisplayName == previousMicName);
            if (AudioDeviceCombo.SelectedItem is null && devices.Count > 0)
                AudioDeviceCombo.SelectedIndex = 0;

            // PC 소리는 더 이상 이 목록(dshow 장치)에서 고르지 않는다 — WASAPI 루프백으로
            // 기본 재생 장치의 소리를 직접 캡처하므로 "스테레오 믹스" 같은 녹음 장치가
            // 목록에 있을 필요가 없다. (SystemAudioCapture 참고)

            if (devices.Count == 0 && UseMicCheck.IsChecked == true)
                SetStatus("마이크 장치를 찾지 못했습니다", "장치를 연결한 뒤 새로고침해 주세요.", false);
        }
        catch (Exception ex)
        {
            SetStatus("마이크 목록을 불러오지 못했습니다", ex.Message, false);
        }
        finally
        {
            AudioDeviceCombo.IsEnabled = UseMicCheck.IsChecked == true;
            UpdateLevelMeterSources();
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (WindowCombo.SelectedItem is not WindowInfo selectedWindow)
            return;

        if (selectedWindow.Region is null && !WindowCaptureService.IsWindowAvailable(selectedWindow.Handle))
        {
            PreviewInfoText.Text = "선택한 창이 닫혔습니다";
            PreviewImage.Source = null;
            PreviewEmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            // Always preview via the same desktop-region capture the broadcast itself
            // uses (see FfmpegStreamingService.StartAsync), instead of the previous
            // PrintWindow-based capture for "select window" mode. PrintWindow renders
            // a window's content directly and can disagree with what ffmpeg's screen
            // capture actually sends to viewers (bounds, occlusion), which let a
            // privacy mask look correctly placed here while missing its target in the
            // real broadcast. Matching capture methods keeps preview and stream
            // pixel-aligned, so what's masked here is what's masked live.
            CaptureRegion? previewRegion = selectedWindow.Region
                ?? WindowCaptureService.GetWindowRegion(selectedWindow.Handle);
            BitmapSource? frame = previewRegion is not null
                ? WindowCaptureService.CaptureScreenRegion(previewRegion, 960, 540)
                : null;
            if (frame is null || _closing)
                return;

            PreviewImage.Source = frame;
            PreviewEmptyPanel.Visibility = Visibility.Collapsed;
            PreviewInfoText.Text = $"{frame.PixelWidth} × {frame.PixelHeight}";
            PreviewBadgeText.Text = _streamer.IsStreaming ? "송출 중" : "미리보기";

            if (_previewPixelWidth != frame.PixelWidth || _previewPixelHeight != frame.PixelHeight)
            {
                _previewPixelWidth = frame.PixelWidth;
                _previewPixelHeight = frame.PixelHeight;
            }

            if (_maskDragStart is null)
                RenderPrivacyMasks();
        }
        catch
        {
            // 창 전환 중 일시적인 캡처 실패는 다음 프레임에서 다시 시도한다.
        }
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PreviewImage.Source = null;
        PreviewEmptyPanel.Visibility = WindowCombo.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        if (WindowCombo.SelectedItem is WindowInfo window)
        {
            string sourceKey = ComputeSourceKey(window);
            if (_maskSourceKey != sourceKey)
            {
                _maskSourceKey = sourceKey;
                _privacyMasks.Clear();
                _previewPixelWidth = 0;
                _previewPixelHeight = 0;
                PrivacyMaskToggle.IsChecked = false;

                // 이전에 이 창(또는 이 지정 영역)에 대해 저장해 둔 모자이크가 있으면 그대로 복원한다.
                // 창 선택 모드에서도 복원되도록, 재실행 시 값이 바뀌는 창 핸들 대신
                // 저장 시점에 함께 기록해 둔 소스 키(프로세스명+제목 또는 영역 좌표)로 비교한다.
                if (!string.IsNullOrEmpty(sourceKey) && sourceKey == _settings.SavedMaskSourceKey)
                {
                    foreach (PrivacyMask mask in _settings.SavedPrivacyMasks ?? new List<PrivacyMask>())
                    {
                        if (IsValidPrivacyMask(mask))
                            _privacyMasks.Add(mask);
                    }
                }

                RenderPrivacyMasks();
            }

            SetStatus("차트 화면이 선택되었습니다",
                _privacyMasks.Count > 0
                    ? $"{window.DisplayName}  ·  민감정보 모자이크 {_privacyMasks.Count}개 자동 복원됨"
                    : window.DisplayName,
                true);
        }
    }

    // 창 핸들은 앱을 재시작하면 바뀔 수 있으므로 저장/복원용 키로 쓰지 않는다.
    // 지정 영역은 좌표로, 일반 창은 프로세스명+제목으로 같은 방송 화면인지 식별한다.
    private static string ComputeSourceKey(WindowInfo window) =>
        window.Region is CaptureRegion region
            ? $"region:{region.X}:{region.Y}:{region.Width}:{region.Height}"
            : $"window:{window.ProcessName}|{window.Title}";

    private void PrivacyMaskToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            PrivacyMaskToggle.IsChecked = false;
            MessageBox.Show("먼저 방송할 차트 창을 선택하고 미리보기를 확인해 주세요.", "방송 화면 필요",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PrivacyMaskToggle.Content = "✓ 모자이크 편집 완료";
        PrivacyMaskCanvas.IsHitTestVisible = true;
        PrivacyMaskCanvas.Cursor = Cursors.Cross;
        SetStatus("민감정보 모자이크 편집 중", "미리보기에서 계좌번호 등 숨길 부분을 마우스로 드래그하세요.", true);
    }

    private void PrivacyMaskToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (PrivacyMaskCanvas is null)
            return;

        CancelDraftMask();
        PrivacyMaskToggle.Content = "▦ 민감정보 모자이크";
        PrivacyMaskCanvas.IsHitTestVisible = false;
        PrivacyMaskCanvas.Cursor = Cursors.Arrow;

        if (_privacyMasks.Count > 0)
            SetStatus($"민감정보 모자이크 {_privacyMasks.Count}개 적용", "미리보기와 실제 방송 영상에서 픽셀 모자이크 처리됩니다.", true);
    }

    private void ClearPrivacyMasksButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDraftMask();
        _privacyMasks.Clear();
        RenderPrivacyMasks();
        PersistCaptureLayout();
        SetStatus("민감정보 모자이크 영역을 모두 삭제했습니다", "필요하면 다시 영역을 지정할 수 있습니다.", true);
    }

    private void PrivacyMaskCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Rect imageBounds = GetPreviewImageBounds();
        Point position = e.GetPosition(PrivacyMaskCanvas);
        if (imageBounds.IsEmpty || !imageBounds.Contains(position))
            return;

        _maskDragStart = ClampToRect(position, imageBounds);
        _draftMaskRectangle = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(222, 18, 22, 31)),
            Stroke = new SolidColorBrush(Color.FromRgb(255, 196, 80)),
            StrokeThickness = 1.5
        };
        PrivacyMaskCanvas.Children.Add(_draftMaskRectangle);
        PrivacyMaskCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void PrivacyMaskCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_maskDragStart is null || _draftMaskRectangle is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = ClampToRect(e.GetPosition(PrivacyMaskCanvas), GetPreviewImageBounds());
        UpdateDraftMask(_maskDragStart.Value, current);
    }

    private void PrivacyMaskCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_maskDragStart is null || _draftMaskRectangle is null)
            return;

        Rect imageBounds = GetPreviewImageBounds();
        Point current = ClampToRect(e.GetPosition(PrivacyMaskCanvas), imageBounds);
        Point start = _maskDragStart.Value;
        double left = Math.Min(start.X, current.X);
        double top = Math.Min(start.Y, current.Y);
        double width = Math.Abs(current.X - start.X);
        double height = Math.Abs(current.Y - start.Y);

        CancelDraftMask();
        if (width < 8 || height < 8 || imageBounds.IsEmpty)
            return;

        _privacyMasks.Add(new PrivacyMask(
            (left - imageBounds.Left) / imageBounds.Width,
            (top - imageBounds.Top) / imageBounds.Height,
            width / imageBounds.Width,
            height / imageBounds.Height));
        RenderPrivacyMasks();
        PersistCaptureLayout();
        SetStatus($"민감정보 모자이크 {_privacyMasks.Count}개 적용", "계속 드래그하면 여러 위치를 추가할 수 있습니다.", true);
        e.Handled = true;
    }

    private void PrivacyMaskCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderPrivacyMasks();

    private void UpdateDraftMask(Point start, Point current)
    {
        if (_draftMaskRectangle is null)
            return;

        double left = Math.Min(start.X, current.X);
        double top = Math.Min(start.Y, current.Y);
        Canvas.SetLeft(_draftMaskRectangle, left);
        Canvas.SetTop(_draftMaskRectangle, top);
        _draftMaskRectangle.Width = Math.Abs(current.X - start.X);
        _draftMaskRectangle.Height = Math.Abs(current.Y - start.Y);
    }

    private void CancelDraftMask()
    {
        if (_draftMaskRectangle is not null)
            PrivacyMaskCanvas.Children.Remove(_draftMaskRectangle);

        _draftMaskRectangle = null;
        _maskDragStart = null;
        PrivacyMaskCanvas.ReleaseMouseCapture();
    }

    private void RenderPrivacyMasks()
    {
        if (PrivacyMaskCanvas is null || _draftMaskRectangle is not null)
            return;

        PrivacyMaskCanvas.Children.Clear();
        Rect bounds = GetPreviewImageBounds();
        if (bounds.IsEmpty)
            return;

        BitmapSource? currentFrame = PreviewImage.Source as BitmapSource;
        foreach (PrivacyMask mask in _privacyMasks)
        {
            double width = mask.Width * bounds.Width;
            double height = mask.Height * bounds.Height;
            var cover = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(238, 14, 18, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 196, 80)),
                BorderThickness = PrivacyMaskToggle.IsChecked == true ? new Thickness(1) : new Thickness(0),
                IsHitTestVisible = false
            };

            if (currentFrame is not null)
            {
                try
                {
                    int sourceX = Math.Clamp((int)Math.Floor(mask.X * currentFrame.PixelWidth), 0, currentFrame.PixelWidth - 1);
                    int sourceY = Math.Clamp((int)Math.Floor(mask.Y * currentFrame.PixelHeight), 0, currentFrame.PixelHeight - 1);
                    int sourceWidth = Math.Clamp((int)Math.Ceiling(mask.Width * currentFrame.PixelWidth),
                        1, currentFrame.PixelWidth - sourceX);
                    int sourceHeight = Math.Clamp((int)Math.Ceiling(mask.Height * currentFrame.PixelHeight),
                        1, currentFrame.PixelHeight - sourceY);
                    var cropped = new CroppedBitmap(currentFrame,
                        new Int32Rect(sourceX, sourceY, sourceWidth, sourceHeight));
                    cropped.Freeze();

                    const int mosaicBlockSize = 16;
                    int tinyWidth = Math.Max(1, sourceWidth / mosaicBlockSize);
                    int tinyHeight = Math.Max(1, sourceHeight / mosaicBlockSize);
                    var pixelated = new TransformedBitmap(cropped,
                        new ScaleTransform(tinyWidth / (double)sourceWidth, tinyHeight / (double)sourceHeight));
                    pixelated.Freeze();

                    var mosaicImage = new Image
                    {
                        Source = pixelated,
                        Stretch = Stretch.Fill,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(mosaicImage, BitmapScalingMode.NearestNeighbor);
                    cover.Child = mosaicImage;
                }
                catch
                {
                    // 프레임이 갱신되는 순간에는 다음 미리보기 프레임에서 다시 만든다.
                }
            }

            Canvas.SetLeft(cover, bounds.Left + mask.X * bounds.Width);
            Canvas.SetTop(cover, bounds.Top + mask.Y * bounds.Height);
            PrivacyMaskCanvas.Children.Add(cover);
        }
    }

    private Rect GetPreviewImageBounds()
    {
        double canvasWidth = PrivacyMaskCanvas?.ActualWidth ?? 0;
        double canvasHeight = PrivacyMaskCanvas?.ActualHeight ?? 0;
        if (canvasWidth <= 0 || canvasHeight <= 0 || _previewPixelWidth <= 0 || _previewPixelHeight <= 0)
            return Rect.Empty;

        // 실제 방송(ffmpeg)은 캡처한 프레임을 자르지 않고 그대로 내보내므로, 미리보기도
        // 잘라내지 않고 전체 프레임이 보이도록 중앙 기준으로 여백을 두고 맞춘다(Image의
        // Stretch="Uniform"과 동일한 계산). Math.Max(꽉 채우고 가장자리를 자름)를 쓰면
        // 프레임 가장자리가 미리보기에서는 안 보이는데 실제 방송에는 그대로 나가서,
        // 그 부분의 민감정보를 마스킹할 수 없게 되는 문제가 있었다.
        double scale = Math.Min(canvasWidth / _previewPixelWidth, canvasHeight / _previewPixelHeight);
        double width = _previewPixelWidth * scale;
        double height = _previewPixelHeight * scale;
        return new Rect((canvasWidth - width) / 2d, (canvasHeight - height) / 2d, width, height);
    }

    private static Point ClampToRect(Point point, Rect bounds)
    {
        if (bounds.IsEmpty)
            return point;

        return new Point(
            Math.Clamp(point.X, bounds.Left, bounds.Right),
            Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        Hide();

        try
        {
            var selector = new RegionSelectionWindow();
            if (selector.ShowDialog() == true && selector.SelectedRegion is CaptureRegion region)
            {
                _customRegionSource = new WindowInfo
                {
                    Region = region,
                    Title = "사용자 지정 영역",
                    Width = region.Width,
                    Height = region.Height
                };
                RefreshWindows();
                WindowCombo.SelectedItem = _customRegionSource;
                PersistCaptureLayout();
                SetStatus("사용자 지정 방송 영역을 선택하고 자동 저장했습니다",
                    $"위치 {region.X}, {region.Y}  ·  크기 {region.Width} × {region.Height}", true);
            }
        }
        finally
        {
            Show();
            Activate();
            _previewTimer.Start();
        }
    }

    private async void RefreshAudioButton_Click(object sender, RoutedEventArgs e) => await RefreshAudioDevicesAsync();

    private void UseMicCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (AudioDeviceCombo is not null)
            AudioDeviceCombo.IsEnabled = UseMicCheck.IsChecked == true;
        UpdateLevelMeterSources();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        SetStatus("방송 설정을 저장했습니다", "다음 실행 시 현재 설정을 불러옵니다.", true);
    }

    private void SaveSettings()
    {
        _settings.ServerUrl = ServerUrlBox.Text.Trim();
        _settings.RememberStreamKey = RememberKeyCheck.IsChecked == true;
        _settings.StreamKey = _settings.RememberStreamKey ? StreamKeyBox.Password : string.Empty;
        _settings.QualityProfileVersion = 1;
        _settings.OutputHeight = SelectedTag(ResolutionCombo, 1080);
        _settings.FrameRate = SelectedTag(FrameRateCombo, 30);
        _settings.UseMicrophone = UseMicCheck.IsChecked == true;
        _settings.AudioDeviceName = (AudioDeviceCombo.SelectedItem as AudioDevice)?.DisplayName ?? string.Empty;
        _settings.UseSecondaryAudio = UseSecondaryAudioCheck.IsChecked == true;
        _settings.RecordingFolderPath = RecordingFolderBox.Text.Trim();
        UpdateCaptureLayoutSettings();
        _settings.Save();
    }

    private void ChooseRecordingFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "녹화 파일을 저장할 폴더 선택",
            InitialDirectory = Directory.Exists(RecordingFolderBox.Text) ? RecordingFolderBox.Text : DefaultRecordingFolder
        };

        if (dialog.ShowDialog(this) == true)
        {
            RecordingFolderBox.Text = dialog.FolderName;
            SaveSettings();
        }
    }

    private void PersistCaptureLayout()
    {
        // 예전에는 "사용자 지정 영역"을 선택했을 때만 자동 저장했다. 그래서 특정 창을 선택해
        // 방송하는 경우(가장 흔한 사용 방식) 모자이크가 메모리에만 남아 있다가 앱을 재시작하면
        // 조용히 사라졌고, 이를 잊고 방송을 시작하면 모자이크 없이 민감정보가 그대로 노출됐다.
        // 이제는 창 선택 모드에서도 저장한다.
        if (WindowCombo.SelectedItem is not WindowInfo)
            return;

        try
        {
            UpdateCaptureLayoutSettings();
            _settings.Save();
        }
        catch (Exception ex)
        {
            SetStatus("방송 영역을 자동 저장하지 못했습니다", ex.Message, false);
        }
    }

    private void UpdateCaptureLayoutSettings()
    {
        if (WindowCombo.SelectedItem is not WindowInfo window)
            return;

        // 지정 영역을 선택한 경우에만 좌표를 갱신한다. 창 선택 모드에서 저장하면서
        // 이전에 저장해 둔 지정 영역 좌표(SavedCaptureRegion)를 null로 덮어써
        // 사라지게 만들지 않기 위함이다.
        if (window.Region is CaptureRegion region)
            _settings.SavedCaptureRegion = region;

        _settings.SavedMaskSourceKey = ComputeSourceKey(window);
        _settings.SavedPrivacyMasks = _privacyMasks.Where(IsValidPrivacyMask).ToList();
    }

    private static bool IsValidPrivacyMask(PrivacyMask mask)
    {
        return mask.X >= 0 && mask.Y >= 0 && mask.Width > 0 && mask.Height > 0 &&
               mask.X + mask.Width <= 1.000001 && mask.Y + mask.Height <= 1.000001;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not WindowInfo selectedWindow)
        {
            MessageBox.Show("먼저 방송할 증권 차트 창을 선택해 주세요.", "방송 화면 필요",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string serverUrl = ServerUrlBox.Text.Trim();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != "rtmp" && uri.Scheme != "rtmps"))
        {
            MessageBox.Show("RTMP 서버 주소를 정확히 입력해 주세요.\n예: rtmp://방송서버/live",
                "서버 주소 확인", MessageBoxButton.OK, MessageBoxImage.Information);
            ServerUrlBox.Focus();
            return;
        }

        AudioDevice? audioDevice = null;
        if (UseMicCheck.IsChecked == true)
        {
            audioDevice = AudioDeviceCombo.SelectedItem as AudioDevice;
            if (audioDevice is null)
            {
                MessageBox.Show("사용할 마이크 장치를 선택해 주세요.", "마이크 확인",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var options = new StreamingOptions
        {
            SourceWindow = selectedWindow,
            ServerUrl = serverUrl,
            StreamKey = StreamKeyBox.Password.Trim(),
            OutputHeight = SelectedTag(ResolutionCombo, 1080),
            FrameRate = SelectedTag(FrameRateCombo, 30),
            AudioDevice = audioDevice,
            CaptureSystemAudio = UseSecondaryAudioCheck.IsChecked == true,
            PrivacyMasks = _privacyMasks.ToList()
        };

        PrivacyMaskToggle.IsChecked = false;
        SetControlsEnabled(false);
        SetStatus("방송 서버에 연결하는 중입니다", "잠시만 기다려 주세요.", true);

        try
        {
            SaveSettings();
            await _streamer.StartAsync(options);
            _broadcastStartedUtc = DateTime.UtcNow;
            _elapsedTimer.Start();
            StartButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            LiveOverlay.Visibility = Visibility.Visible;
            PreviewBadge.Background = new SolidColorBrush(Color.FromRgb(255, 232, 237));
            PreviewBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(199, 36, 78));
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 66, 104));
            HeaderStatusText.Text = "방송 중";
            SetStatus("실시간 방송을 송출하고 있습니다", "방송 종료 버튼을 누르면 안전하게 연결을 종료합니다.", true);

            // 방송이 시작된 뒤에야(RTMP pull 방식이라 서버에 스트림이 이미 올라가
            // 있어야 함) 녹화를 시작할 수 있으므로, 여기서 비로소 녹화 버튼을 켠다.
            RecordingStartButton.Visibility = Visibility.Visible;
            RecordingStartButton.IsEnabled = true;
            RecordingStopButton.Visibility = Visibility.Collapsed;
            RecordingStatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SetControlsEnabled(true);
            SetStatus("방송을 시작하지 못했습니다", ex.Message, false);
            MessageBox.Show(ex.Message, "방송 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        SetStatus("방송을 종료하는 중입니다", "서버 연결을 안전하게 닫고 있습니다.", true);
        bool wasRecording = _streamer.CurrentRecordingFilePath is not null;
        await _streamer.StopAsync();
        // StopAsync 안에서 방송을 끊은 뒤 녹화 파일을 mp4로 변환까지 마치므로,
        // 여기서 다시 읽으면 변환에 성공했을 때는 mp4 경로를, 실패했어도 최소한
        // 원본 .ts 경로를 얻는다.
        string? recordedFilePath = wasRecording ? _streamer.CurrentRecordingFilePath : null;

        string detail = recordedFilePath is not null
            ? $"녹화 파일이 저장되었습니다: {recordedFilePath}"
            : "설정을 확인한 뒤 다시 방송을 시작할 수 있습니다.";
        ApplyStoppedUi("방송이 종료되었습니다", detail, false);
    }

    private async void RecordingStartButton_Click(object sender, RoutedEventArgs e)
    {
        RecordingStartButton.IsEnabled = false;
        RecordingStatusText.Text = "녹화를 시작하는 중입니다...";

        string folderPath = string.IsNullOrWhiteSpace(RecordingFolderBox.Text)
            ? DefaultRecordingFolder
            : RecordingFolderBox.Text.Trim();

        try
        {
            string? recordingFilePath = await _streamer.StartRecordingAsync(folderPath);
            if (recordingFilePath is not null)
            {
                RecordingStartButton.Visibility = Visibility.Collapsed;
                RecordingStopButton.Visibility = Visibility.Visible;
                RecordingStopButton.IsEnabled = true;
                RecordingStatusText.Text = $"녹화 중입니다: {recordingFilePath}";
            }
            else
            {
                RecordingStatusText.Text = "녹화를 시작하지 못했습니다. 잠시 후 다시 시도해 주세요.";
                RecordingStartButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = $"녹화를 시작하지 못했습니다: {ex.Message}";
            RecordingStartButton.IsEnabled = true;
        }
    }

    private async void RecordingStopButton_Click(object sender, RoutedEventArgs e)
    {
        RecordingStopButton.IsEnabled = false;
        RecordingStatusText.Text = "녹화를 mp4로 변환하는 중입니다...";

        try
        {
            string? savedPath = await _streamer.StopRecordingAsync();
            RecordingStatusText.Text = savedPath is not null
                ? $"녹화 파일이 저장되었습니다: {savedPath}"
                : "녹화가 중지되었습니다.";
        }
        finally
        {
            RecordingStopButton.Visibility = Visibility.Collapsed;
            RecordingStopButton.IsEnabled = true;
            RecordingStartButton.Visibility = Visibility.Visible;
            // 그 사이 방송 자체가 끝나버렸다면(거의 동시에 방송 종료를 누른 경우)
            // 녹화도 다시 시작할 수 없으므로 버튼을 비활성 상태로 둔다.
            RecordingStartButton.IsEnabled = _streamer.IsStreaming;
        }
    }

    private void Streamer_StatusReceived(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_streamer.IsStreaming)
                StatusDetailText.Text = status;
        });
    }

    private void Streamer_StreamEnded(string? error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
                return;

            ApplyStoppedUi(error is null ? "방송이 종료되었습니다" : "방송 연결이 끊어졌습니다",
                error ?? "설정을 확인한 뒤 다시 방송을 시작할 수 있습니다.", error is not null);
        });
    }

    private void ApplyStoppedUi(string title, string detail, bool error)
    {
        _elapsedTimer.Stop();
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        LiveOverlay.Visibility = Visibility.Collapsed;
        PreviewBadge.Background = new SolidColorBrush(Color.FromRgb(238, 240, 255));
        PreviewBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(93, 87, 217));
        PreviewBadgeText.Text = "미리보기";
        HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(124, 246, 210));
        HeaderStatusText.Text = "방송 준비";
        SetControlsEnabled(true);
        SetStatus(title, detail, !error);

        // 방송이 끝나면 녹화도 더 이상 켤 수 없다(RTMP pull 방식이라 방송 중일
        // 때만 가능) — 다음 방송을 시작할 때 다시 활성화된다.
        RecordingStartButton.Visibility = Visibility.Visible;
        RecordingStartButton.IsEnabled = false;
        RecordingStopButton.Visibility = Visibility.Collapsed;
        // 녹화 저장 경로는 위 detail(본문 상태 메시지)에 이미 포함되어 있으므로,
        // 녹화 버튼 아래의 보조 텍스트는 비워 중복 표시를 피한다.
        RecordingStatusText.Text = string.Empty;
    }

    private void ElapsedTimer_Tick(object? sender, EventArgs e)
    {
        TimeSpan elapsed = DateTime.UtcNow - _broadcastStartedUtc;
        ElapsedText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void SetControlsEnabled(bool enabled)
    {
        WindowCombo.IsEnabled = enabled;
        ResolutionCombo.IsEnabled = enabled;
        FrameRateCombo.IsEnabled = enabled;
        UseMicCheck.IsEnabled = enabled;
        AudioDeviceCombo.IsEnabled = enabled && UseMicCheck.IsChecked == true;
        UseSecondaryAudioCheck.IsEnabled = enabled;
        ServerUrlBox.IsEnabled = enabled;
        StreamKeyBox.IsEnabled = enabled;
        RememberKeyCheck.IsEnabled = enabled;
        SaveSettingsButton.IsEnabled = enabled;
        PrivacyMaskToggle.IsEnabled = enabled;
        ClearPrivacyMasksButton.IsEnabled = enabled;
        SelectRegionButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
    }

    private void SetStatus(string title, string detail, bool healthy)
    {
        StatusText.Text = title;
        StatusDetailText.Text = detail;
        StatusDot.Fill = new SolidColorBrush(healthy ? Color.FromRgb(40, 199, 111) : Color.FromRgb(255, 159, 67));
    }

    private static int SelectedTag(ComboBox comboBox, int fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int value)
            ? value
            : fallback;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
            return;

        if (_streamer.IsStreaming)
        {
            e.Cancel = true;
            _closing = true;
            _previewTimer.Stop();
            _elapsedTimer.Stop();
            _levelMeterTimer.Stop();
            _levelMeter.Dispose();
            await _streamer.StopAsync();
            Close();
            return;
        }

        _previewTimer.Stop();
        _elapsedTimer.Stop();
        _levelMeterTimer.Stop();
        _levelMeter.Dispose();
    }
}
