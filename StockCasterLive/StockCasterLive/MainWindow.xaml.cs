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

    public MainWindow()
    {
        InitializeComponent();

        _previewTimer.Tick += PreviewTimer_Tick;
        _elapsedTimer.Tick += ElapsedTimer_Tick;
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
        StartRiskNoticeAnimation();
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

    private void RiskTickerViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
            StartRiskNoticeAnimation();
    }

    private void StartRiskNoticeAnimation()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            double viewportWidth = RiskTickerViewport.ActualWidth;
            RiskNoticeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = RiskNoticeText.DesiredSize.Width;
            if (viewportWidth <= 0 || textWidth <= 0 || RiskNoticeText.RenderTransform is not TranslateTransform transform)
                return;

            double travelDistance = viewportWidth + textWidth;
            var animation = new DoubleAnimation
            {
                From = viewportWidth,
                To = -textWidth,
                Duration = TimeSpan.FromSeconds(Math.Max(12, travelDistance / 72d)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
        });
    }

    private void ApplySavedSettings()
    {
        ServerUrlBox.Text = string.IsNullOrWhiteSpace(_settings.ServerUrl) ? "rtmp://" : _settings.ServerUrl;
        RememberKeyCheck.IsChecked = _settings.RememberStreamKey;
        if (_settings.RememberStreamKey)
            StreamKeyBox.Password = _settings.StreamKey;

        ResolutionCombo.SelectedIndex = _settings.OutputHeight == 1080 ? 1 : 0;
        FrameRateCombo.SelectedIndex = _settings.FrameRate == 60 ? 1 : 0;
        UseMicCheck.IsChecked = _settings.UseMicrophone;

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
            WindowCombo.SelectedItem = _customRegionSource;
            _privacyMasks.Clear();
            foreach (PrivacyMask mask in _settings.SavedPrivacyMasks ?? new List<PrivacyMask>())
            {
                if (IsValidPrivacyMask(mask))
                    _privacyMasks.Add(mask);
            }

            RenderPrivacyMasks();
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
            : windows.FirstOrDefault(window => window.Region is null && window.Handle == selectedHandle);

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
        string? previousName = (AudioDeviceCombo.SelectedItem as AudioDevice)?.DisplayName ?? _settings.AudioDeviceName;
        AudioDeviceCombo.IsEnabled = false;

        try
        {
            var devices = await Task.Run(_streamer.ListAudioDevices);
            AudioDeviceCombo.ItemsSource = devices;
            AudioDeviceCombo.SelectedItem = devices.FirstOrDefault(device => device.DisplayName == previousName);
            if (AudioDeviceCombo.SelectedItem is null && devices.Count > 0)
                AudioDeviceCombo.SelectedIndex = 0;

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
            BitmapSource? frame = selectedWindow.Region is CaptureRegion region
                ? WindowCaptureService.CaptureScreenRegion(region, 960, 540)
                : WindowCaptureService.CaptureWindow(selectedWindow.Handle);
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
            string sourceKey = window.Region is CaptureRegion region
                ? $"region:{region.X}:{region.Y}:{region.Width}:{region.Height}"
                : $"window:{window.Handle}";
            if (_maskSourceKey != sourceKey)
            {
                _maskSourceKey = sourceKey;
                _privacyMasks.Clear();
                _previewPixelWidth = 0;
                _previewPixelHeight = 0;
                PrivacyMaskToggle.IsChecked = false;
                RenderPrivacyMasks();
            }
            SetStatus("차트 화면이 선택되었습니다", window.DisplayName, true);
        }
    }

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

        // 실제 방송과 동일하게 중앙을 기준으로 화면을 꽉 채운다.
        // 캔버스 밖으로 나간 가장자리는 미리보기와 송출 영상에서 모두 잘린다.
        double scale = Math.Max(canvasWidth / _previewPixelWidth, canvasHeight / _previewPixelHeight);
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
        UpdateCaptureLayoutSettings();
        _settings.Save();
    }

    private void PersistCaptureLayout()
    {
        if (WindowCombo.SelectedItem is not WindowInfo { Region: not null })
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
        if (WindowCombo.SelectedItem is not WindowInfo { Region: CaptureRegion region })
            return;

        _settings.SavedCaptureRegion = region;
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
        await _streamer.StopAsync();
        ApplyStoppedUi("방송이 종료되었습니다", "설정을 확인한 뒤 다시 방송을 시작할 수 있습니다.", false);
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
            await _streamer.StopAsync();
            Close();
            return;
        }

        _previewTimer.Stop();
        _elapsedTimer.Stop();
    }
}
