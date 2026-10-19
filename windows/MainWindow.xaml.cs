using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.IO.MemoryMappedFiles;
using PhonecamCameraSource;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Phonecam;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(6)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 18766);
    private CancellationTokenSource? _streamCancellation;
    private byte[]? _latestCameraFrame;
    private Process? _cameraHost;
    private Uri? _settingsUri;
    private bool _updatingPhoneSettings;
    private readonly SemaphoreSlim _settingsUpdate = new(1, 1);
    private CancellationTokenSource? _settingsDebounce;
    private CameraResolution _cameraResolution = new(640, 480);
    private CameraResolution? _hostResolution;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfAccessor;
    private ulong _frameSequence;
    private readonly byte[] _pixelBufferA = new byte[Shared.MaxFrameWidth * Shared.MaxFrameHeight * 4];
    private readonly byte[] _pixelBufferB = new byte[Shared.MaxFrameWidth * Shared.MaxFrameHeight * 4];
    private bool _useBufferA = true;
    private RenderTargetBitmap? _renderBitmap;
    private CameraResolution? _renderBitmapResolution;
    private WriteableBitmap? _writeableBitmap;
    private volatile bool _uiPending;
    private string? _latestStatsText;
    private volatile bool _reconnectNeeded;
    private bool _mirrorVideo;
    private bool _torchActive;
    private bool _hasTorch;
    private System.Net.Sockets.UdpClient? _discoveryClient;
    private CancellationTokenSource? _discoveryCts;
    private string? _discoveredAddress;
    private int _statsFrameCount;
    private long _statsByteCount;
    private long _statsWindowStartTicks = Stopwatch.GetTimestamp();
    private double _latestRttMs;
    private double _latestDecodeMs;
    private double _latestFps;
    private double _latestMbps;
    private double _latestLatencyMs;
    private long _lastChunkReceivedTicks;
    private CancellationTokenSource? _pingCts;

    // Audio recording & playback fields
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _selectedAudioDevice;
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveProvider;
    private volatile bool _micMuted;
    private volatile float _audioVolume = 1.0f;
    private CancellationTokenSource? _audioCts;

    // Manual camera control fields
    private double _zoomRatio = 1.0;
    private double _minZoom = 1.0;
    private double _maxZoom = 4.0;
    private int _aeComp = 0;
    private int _minAeComp = -2;
    private int _maxAeComp = 2;
    private double _aeStep = 0.1666;
    private bool _aeLocked = false;
    private int _awbMode = 1;
    private bool _awbLocked = false;
    private bool _manualFocus = false;
    private double _focusDistance = 0.0;
    private double _maxFocusDistance = 10.0;
    private CancellationTokenSource? _manualDebounce;

    private sealed record CameraOption(string Id, string Label);
    private sealed record SizeOption(int Width, int Height, string Label);
    private sealed record FpsOption(int Min, int Max, string Label);
    private sealed record CameraResolution(int Width, int Height);
    private sealed record AudioDeviceOption(string Id, string Name, MMDevice Device);

    public MainWindow()
    {
        InitializeComponent();
        InitializeSharedMemory();
        StartDiscoveryListener();
        InitializeAudioDevices();
        if (CameraSourceRegistered()) CameraDeviceText.Text = "Connect to your phone to start the Windows camera.";
        else
        {
            CameraDeviceText.Text = "Install the Windows camera to use Phonecam in apps.";
            InstallCameraButton.Visibility = Visibility.Visible;
        }
        try
        {
            _listener.Start();
            _ = ServeOutputAsync();
        }
        catch (System.Net.Sockets.SocketException)
        {
            SetStatus("Output port unavailable", false);
        }
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PHONECAM_AUTO_CONNECT"), "1", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                Connect_Click(this, new RoutedEventArgs());
            });
        }

        var capturePath = Environment.GetEnvironmentVariable("PHONECAM_CAPTURE_PATH");
        if (!string.IsNullOrEmpty(capturePath))
        {
            Dispatcher.InvokeAsync(async () =>
            {
                var delayMs = string.Equals(Environment.GetEnvironmentVariable("PHONECAM_AUTO_CONNECT"), "1", StringComparison.OrdinalIgnoreCase) ? 12000 : 1000;
                await Task.Delay(delayMs);
                try
                {
                    var width = (int)ActualWidth;
                    var height = (int)ActualHeight;
                    if (width <= 0) width = 1050;
                    if (height <= 0) height = 860;
                    SidebarScrollViewer.ScrollToBottom();
                    await Task.Delay(200);
                    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(this);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using var stream = File.Create(capturePath);
                    encoder.Save(stream);
                    Debug.WriteLine($"Saved window screenshot to {capturePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to capture window screenshot: {ex.Message}");
                }
            });
        }
    }

    private void InitializeSharedMemory()
    {
        try
        {
            _mmf = MemoryMappedFile.CreateOrOpen(Shared.SharedMemoryName, Shared.MaxBufferSize, MemoryMappedFileAccess.ReadWrite);
            _mmfAccessor = _mmf.CreateViewAccessor(0, Shared.MaxBufferSize, MemoryMappedFileAccess.Write);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize shared memory: {ex.Message}");
        }
    }

    private unsafe void WriteSharedFrame(int width, int height, byte[] pixels, ulong sequence)
    {
        if (_mmfAccessor is null) return;
        byte* ptr = null;
        try
        {
            _mmfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            if (ptr != null)
            {
                *(uint*)(ptr + 0) = Shared.SharedMemoryMagic;
                *(uint*)(ptr + 4) = (uint)width;
                *(uint*)(ptr + 8) = (uint)height;
                *(uint*)(ptr + 12) = (uint)(width * 4);
                *(ulong*)(ptr + 16) = sequence;
                *(long*)(ptr + 24) = Stopwatch.GetTimestamp();
                var pixelLength = Math.Min(pixels.Length, width * height * 4);
                fixed (byte* pPixels = pixels)
                {
                    Buffer.MemoryCopy(pPixels, ptr + Shared.HeaderSize, Shared.MaxBufferSize - Shared.HeaderSize, pixelLength);
                }
            }
        }
        finally
        {
            if (ptr != null) _mmfAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private async Task ServeOutputAsync()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (System.Net.Sockets.SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync();
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var path = request?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                if (string.Equals(path, "/camera/mode", StringComparison.OrdinalIgnoreCase))
                {
                    var resolution = Volatile.Read(ref _cameraResolution);
                    var bytes = Encoding.ASCII.GetBytes($"{{\"width\":{resolution.Width},\"height\":{resolution.Height}}}");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(bytes);
                }
                else if (string.Equals(path, "/camera/frame", StringComparison.OrdinalIgnoreCase))
                {
                    var pixels = Volatile.Read(ref _latestCameraFrame);
                    var resolution = Volatile.Read(ref _cameraResolution);
                    var length = resolution.Width * resolution.Height * 4;
                    var status = pixels is null ? "503 Service Unavailable" : "200 OK";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nContent-Length: {(pixels is not null ? length : 0)}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    if (pixels is not null) await stream.WriteAsync(pixels.AsMemory(0, length));
                }
                else
                {
                    var streamUrl = AddressBox.Dispatcher.Invoke(() => AddressBox.Text.Trim());
                    var safeUrl = WebUtility.HtmlEncode(streamUrl);
                    var html = $$"""
                        <!doctype html>
                        <meta charset="utf-8">
                        <meta name="viewport" content="width=device-width,initial-scale=1">
                        <title>Phonecam output</title>
                        <style>
                          *{box-sizing:border-box}body{margin:0;background:#10151d;color:#e8edf5;font:15px system-ui,sans-serif;display:grid;min-height:100vh;place-items:center}
                          main{width:min(100vw,1280px);padding:20px}header{display:flex;align-items:center;gap:10px;margin-bottom:14px;color:#95a3b8}i{width:9px;height:9px;border-radius:50%;background:#70e1c1;display:inline-block}
                          img{display:block;width:100%;max-height:calc(100vh - 95px);object-fit:contain;background:#080b10;border-radius:14px}
                        </style>
                        <main><header><i></i>Phonecam - live camera</header><img src="{{safeUrl}}" alt="Phone camera"></main>
                        """;
                    var bytes = Encoding.UTF8.GetBytes(html);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(bytes);
                }
            }
            catch (IOException) { }
            catch (Exception) { }
        }
    }

    private bool _isConnectingOrStreaming;

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnectingOrStreaming)
        {
            _streamCancellation?.Cancel();
            _audioCts?.Cancel();
            _audioCts?.Dispose();
            _audioCts = null;
            _pingCts?.Cancel();
            _pingCts?.Dispose();
            _pingCts = null;
            _wasapiOut?.Stop();
            AudioMeter.Value = 0;
            _isConnectingOrStreaming = false;
            ConnectButton.Content = "Connect camera";
            SetStatus("Disconnected", false);
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        if (!Uri.TryCreate(AddressBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SetStatus("Enter a valid http or https address", false);
            return;
        }

        _streamCancellation?.Cancel();
        _streamCancellation = new CancellationTokenSource();
        var cancellationToken = _streamCancellation.Token;

        _isConnectingOrStreaming = true;
        ConnectButton.Content = "Disconnect";
        EmptyState.Visibility = Visibility.Collapsed;

        _ = ConnectLoopAsync(uri, cancellationToken);
    }

    private async Task ConnectLoopAsync(Uri uri, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var maxRetryDelay = TimeSpan.FromSeconds(6);

        while (!cancellationToken.IsCancellationRequested && _isConnectingOrStreaming)
        {
            var headersReceived = false;
            try
            {
                SetStatus("Connecting...", false);
                if ((uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) && uri.Port == 18765)
                {
                    SetStatus("Setting up USB connection...", false);
                    await EnsureUsbForwardAsync(cancellationToken);
                }

                _settingsUri = SettingsUri(uri);
                await LoadPhoneSettingsAsync(cancellationToken);
                await StartVirtualCameraAsync();

                var isUsb = (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) && uri.Port == 18765;
                var ffmpegPath = FindFfmpeg();
                HttpResponseMessage? response = null;
                var isH264 = false;
                var streamRotation = 0;

                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    try
                    {
                        var h264Uri = new UriBuilder(uri) { Path = "/stream.h264", Query = "", Fragment = "" }.Uri;
                        using var h264Timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        h264Timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        var testResp = await _http.GetAsync(h264Uri, HttpCompletionOption.ResponseHeadersRead, h264Timeout.Token);
                        if (testResp.IsSuccessStatusCode && testResp.Content.Headers.ContentType?.MediaType?.Contains("h264", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            response = testResp;
                            isH264 = true;
                            if (response.Headers.TryGetValues("X-Rotation", out var rotVals) && int.TryParse(rotVals.FirstOrDefault(), out var r)) streamRotation = r;
                            if (response.Headers.TryGetValues("X-Width", out var wVals) && int.TryParse(wVals.FirstOrDefault(), out var w) &&
                                response.Headers.TryGetValues("X-Height", out var hVals) && int.TryParse(hVals.FirstOrDefault(), out var h))
                            {
                                Volatile.Write(ref _cameraResolution, new CameraResolution(w, h));
                            }
                        }
                        else
                        {
                            testResp.Dispose();
                        }
                    }
                    catch { }
                }

                if (response == null)
                {
                    using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    headerTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                    response.EnsureSuccessStatusCode();
                }

                headersReceived = true;
                SetStatus("Connected", true, isUsb ? (isH264 ? "USB (H.264 HW)" : "USB") : (isH264 ? "Wi-Fi (H.264 HW)" : "Wi-Fi"));
                retryDelay = TimeSpan.FromSeconds(1); // Reset backoff on successful connection

                _audioCts?.Cancel();
                _audioCts?.Dispose();
                _audioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = StartAudioStreamAsync(uri, _audioCts.Token, ffmpegPath);

                _pingCts?.Cancel();
                _pingCts?.Dispose();
                _pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = StartPingLoopAsync(uri, _pingCts.Token);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                if (isH264 && !string.IsNullOrEmpty(ffmpegPath))
                {
                    await ReadH264Async(stream, cancellationToken, ffmpegPath, streamRotation, isUsb);
                }
                else
                {
                    await ReadMultipartAsync(stream, cancellationToken);
                }

                _audioCts?.Cancel();
                _pingCts?.Cancel();
                _wasapiOut?.Stop();
                await Dispatcher.InvokeAsync(() => AudioMeter.Value = 0);

                if (cancellationToken.IsCancellationRequested || !_isConnectingOrStreaming) break;
                SetStatus("Phone closed the camera stream", false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !_isConnectingOrStreaming)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                SetStatus(headersReceived ? "Camera feed stalled" : "Phone not reachable", false);
            }
            catch (HttpRequestException)
            {
                SetStatus("Phone not reachable", false);
            }
            catch (Exception error)
            {
                SetStatus($"Stream error: {error.Message}", false);
            }

            _audioCts?.Cancel();
            _pingCts?.Cancel();
            _wasapiOut?.Stop();
            await Dispatcher.InvokeAsync(() => AudioMeter.Value = 0);

            var autoReconnect = await Dispatcher.InvokeAsync(() => AutoReconnectBox.IsChecked == true);
            if (!autoReconnect || cancellationToken.IsCancellationRequested || !_isConnectingOrStreaming)
            {
                break;
            }

            await Dispatcher.InvokeAsync(() => EmptyState.Visibility = Visibility.Visible);
            SetStatus($"Reconnecting in {retryDelay.TotalSeconds:0}s...", false);

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(maxRetryDelay.TotalSeconds, retryDelay.TotalSeconds * 1.5));
        }

        _audioCts?.Cancel();
        _audioCts?.Dispose();
        _audioCts = null;
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        _pingCts = null;
        _wasapiOut?.Stop();
        await Dispatcher.InvokeAsync(() =>
        {
            AudioMeter.Value = 0;
            _isConnectingOrStreaming = false;
            ConnectButton.Content = "Connect camera";
            EmptyState.Visibility = Visibility.Visible;
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus("Disconnected", false);
            }
        });
    }

    private static Uri SettingsUri(Uri streamUri) => new UriBuilder(streamUri) { Path = "/settings", Query = "", Fragment = "" }.Uri;

    private async Task LoadPhoneSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var json = await _http.GetStringAsync(_settingsUri, timeout.Token);
            await Dispatcher.InvokeAsync(() => ApplyPhoneSettingsJson(json));
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetSettingsEnabled(false);
                SettingsStatusText.Text = "Phone settings are unavailable - update the Phonecam app on your phone.";
            });
        }
    }

    private void ApplyPhoneSettingsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var cameras = root.GetProperty("cameras").EnumerateArray().Select(item => new CameraOption(item.GetProperty("id").GetString() ?? "", item.GetProperty("label").GetString() ?? "Camera")).ToArray();
        var sizes = root.GetProperty("sizes").EnumerateArray().Select(item => new SizeOption(item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32(), item.GetProperty("label").GetString() ?? "Resolution")).ToArray();
        var fps = root.GetProperty("fpsRanges").EnumerateArray().Select(item => new FpsOption(item.GetProperty("min").GetInt32(), item.GetProperty("max").GetInt32(), item.GetProperty("label").GetString() ?? "Frame rate")).ToArray();
        Volatile.Write(ref _cameraResolution, new CameraResolution(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32()));
        _updatingPhoneSettings = true;
        CameraSettingsBox.ItemsSource = cameras;
        ResolutionSettingsBox.ItemsSource = sizes;
        FrameRateSettingsBox.ItemsSource = fps;
        CameraSettingsBox.SelectedItem = cameras.FirstOrDefault(option => option.Id == root.GetProperty("cameraId").GetString());
        ResolutionSettingsBox.SelectedItem = sizes.FirstOrDefault(option => option.Width == root.GetProperty("width").GetInt32() && option.Height == root.GetProperty("height").GetInt32());
        FrameRateSettingsBox.SelectedItem = fps.FirstOrDefault(option => option.Min == root.GetProperty("fpsMin").GetInt32() && option.Max == root.GetProperty("fpsMax").GetInt32());
        var quality = root.GetProperty("quality").GetInt32();
        QualitySettingsBox.SelectedItem = QualitySettingsBox.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == quality.ToString());

        _hasTorch = root.TryGetProperty("hasTorch", out var hasTorchProp) && hasTorchProp.GetBoolean();
        _torchActive = root.TryGetProperty("torch", out var torchProp) && torchProp.GetBoolean();
        TorchButton.IsEnabled = _hasTorch;
        TorchButton.Content = _torchActive ? "Torch: ON" : "Torch: OFF";

        if (root.TryGetProperty("micMuted", out var micProp))
        {
            _micMuted = micProp.GetBoolean();
            MicMuteButton.Content = _micMuted ? "Mic: MUTED" : "Mic: ON";
            MicMuteButton.Background = _micMuted ? new SolidColorBrush(Color.FromRgb(180, 50, 50)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
        }

        // Parse manual controls
        if (root.TryGetProperty("minZoom", out var minZ) && root.TryGetProperty("maxZoom", out var maxZ))
        {
            _minZoom = minZ.GetDouble();
            _maxZoom = maxZ.GetDouble();
            ZoomSlider.Minimum = _minZoom;
            ZoomSlider.Maximum = _maxZoom;
            ZoomSlider.IsEnabled = _maxZoom > _minZoom;
        }
        if (root.TryGetProperty("zoom", out var zProp))
        {
            _zoomRatio = zProp.GetDouble();
            ZoomSlider.Value = _zoomRatio;
            ZoomValueText.Text = $"{_zoomRatio:0.0}x";
        }
        if (root.TryGetProperty("minAeComp", out var minAe) && root.TryGetProperty("maxAeComp", out var maxAe) && root.TryGetProperty("aeStep", out var aeStepProp))
        {
            _minAeComp = minAe.GetInt32();
            _maxAeComp = maxAe.GetInt32();
            _aeStep = aeStepProp.GetDouble();
            EvSlider.Minimum = _minAeComp * _aeStep;
            EvSlider.Maximum = _maxAeComp * _aeStep;
            EvSlider.IsEnabled = _maxAeComp > _minAeComp;
        }
        if (root.TryGetProperty("aeComp", out var compProp))
        {
            _aeComp = compProp.GetInt32();
            var evVal = _aeComp * _aeStep;
            EvSlider.Value = evVal;
            EvValueText.Text = $"{(evVal >= 0 ? "+" : "")}{evVal:0.0} EV";
        }
        if (root.TryGetProperty("aeLocked", out var aelProp))
        {
            _aeLocked = aelProp.GetBoolean();
            AeLockButton.Content = _aeLocked ? "AE: Locked" : "Lock AE";
            AeLockButton.Background = _aeLocked ? new SolidColorBrush(Color.FromRgb(180, 120, 20)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
            AeLockButton.IsEnabled = true;
        }
        if (root.TryGetProperty("awbMode", out var awbProp))
        {
            _awbMode = awbProp.GetInt32();
            AwbModeBox.SelectedItem = AwbModeBox.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == _awbMode.ToString()) ?? AwbModeBox.Items[0];
            AwbModeBox.IsEnabled = true;
        }
        if (root.TryGetProperty("awbLocked", out var awblProp))
        {
            _awbLocked = awblProp.GetBoolean();
            AwbLockButton.Content = _awbLocked ? "AWB: Locked" : "Lock AWB";
            AwbLockButton.Background = _awbLocked ? new SolidColorBrush(Color.FromRgb(180, 120, 20)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
            AwbLockButton.IsEnabled = true;
        }
        if (root.TryGetProperty("maxFocusDistance", out var maxFdProp))
        {
            _maxFocusDistance = maxFdProp.GetDouble();
            FocusSlider.Maximum = _maxFocusDistance;
        }
        if (root.TryGetProperty("manualFocus", out var mfProp))
        {
            _manualFocus = mfProp.GetBoolean();
            ManualFocusCheck.IsChecked = _manualFocus;
            ManualFocusCheck.IsEnabled = _maxFocusDistance > 0;
            FocusSlider.IsEnabled = _manualFocus && _maxFocusDistance > 0;
        }
        if (root.TryGetProperty("focusDistance", out var fdProp))
        {
            _focusDistance = fdProp.GetDouble();
            FocusSlider.Value = _focusDistance;
            FocusValueText.Text = _manualFocus ? $"{_focusDistance:0.1} dpt" : "Auto (Continuous)";
        }

        _updatingPhoneSettings = false;
        SetSettingsEnabled(cameras.Length > 0 && sizes.Length > 0 && fps.Length > 0);
        SettingsStatusText.Text = "Settings apply to the live phone camera.";
    }

    private void SetSettingsEnabled(bool enabled)
    {
        CameraSettingsBox.IsEnabled = enabled;
        ResolutionSettingsBox.IsEnabled = enabled;
        FrameRateSettingsBox.IsEnabled = enabled;
        QualitySettingsBox.IsEnabled = enabled;
        TorchButton.IsEnabled = enabled && _hasTorch;
        ZoomSlider.IsEnabled = enabled && _maxZoom > _minZoom;
        EvSlider.IsEnabled = enabled && _maxAeComp > _minAeComp;
        AeLockButton.IsEnabled = enabled;
        AwbModeBox.IsEnabled = enabled;
        AwbLockButton.IsEnabled = enabled;
        ManualFocusCheck.IsEnabled = enabled && _maxFocusDistance > 0;
        FocusSlider.IsEnabled = enabled && _manualFocus && _maxFocusDistance > 0;
    }

    private async void CameraSettings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingPhoneSettings || _settingsUri is null || CameraSettingsBox.SelectedItem is not CameraOption camera || ResolutionSettingsBox.SelectedItem is not SizeOption size || FrameRateSettingsBox.SelectedItem is not FpsOption fps || QualitySettingsBox.SelectedItem is not System.Windows.Controls.ComboBoxItem qualityItem || !int.TryParse((string?)qualityItem.Tag, out var quality)) return;
        _settingsDebounce?.Cancel();
        _settingsDebounce?.Dispose();
        _settingsDebounce = new CancellationTokenSource();
        var token = _settingsDebounce.Token;
        try
        {
            await Task.Delay(180, token);
            await _settingsUpdate.WaitAsync(token);
            try
            {
                var payload = JsonSerializer.Serialize(new { cameraId = camera.Id, width = size.Width, height = size.Height, fpsMin = fps.Min, fpsMax = fps.Max, quality, torch = _torchActive });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var previousResolution = Volatile.Read(ref _cameraResolution);
                using var response = await _http.PostAsync(_settingsUri, content, timeout.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                await Dispatcher.InvokeAsync(() => ApplyPhoneSettingsJson(json));
                var currentResolution = Volatile.Read(ref _cameraResolution);
                _reconnectNeeded = true;
                if (previousResolution != currentResolution && _cameraHost is { HasExited: false })
                {
                    await Dispatcher.InvokeAsync(() => SettingsStatusText.Text = "Updating Windows camera resolution...");
                    await StartVirtualCameraAsync();
                    await Dispatcher.InvokeAsync(() => SettingsStatusText.Text = "Resolution updated. Reopen the camera in your video app.");
                }
            }
            finally { _settingsUpdate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            SettingsStatusText.Text = "Could not apply settings - check that the phone is still connected.";
        }
    }

    private async void TorchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsUri is null || !_hasTorch) return;
        TorchButton.IsEnabled = false;
        try
        {
            var targetTorch = !_torchActive;
            var payload = JsonSerializer.Serialize(new { torch = targetTorch });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await _http.PostAsync(_settingsUri, content, timeout.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            ApplyPhoneSettingsJson(json);
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Torch error: {ex.Message}";
        }
        finally
        {
            TorchButton.IsEnabled = _hasTorch;
        }
    }

    private void MirrorBox_Click(object sender, RoutedEventArgs e)
    {
        _mirrorVideo = MirrorBox.IsChecked == true;
        _reconnectNeeded = true;
    }

    private void ManualControl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPhoneSettings || _settingsUri is null) return;
        if (ReferenceEquals(sender, ZoomSlider))
        {
            _zoomRatio = ZoomSlider.Value;
            ZoomValueText.Text = $"{_zoomRatio:0.0}x";
        }
        else if (ReferenceEquals(sender, EvSlider))
        {
            if (_aeStep > 0)
            {
                _aeComp = (int)Math.Round(EvSlider.Value / _aeStep);
                _aeComp = Math.Clamp(_aeComp, _minAeComp, _maxAeComp);
                var evVal = _aeComp * _aeStep;
                EvValueText.Text = $"{(evVal >= 0 ? "+" : "")}{evVal:0.0} EV";
            }
        }
        else if (ReferenceEquals(sender, FocusSlider))
        {
            _focusDistance = FocusSlider.Value;
            if (_manualFocus) FocusValueText.Text = $"{_focusDistance:0.1} dpt";
        }
        ScheduleManualSettingsUpdate();
    }

    private void AeLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsUri is null) return;
        _aeLocked = !_aeLocked;
        AeLockButton.Content = _aeLocked ? "AE: Locked" : "Lock AE";
        AeLockButton.Background = _aeLocked ? new SolidColorBrush(Color.FromRgb(180, 120, 20)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
        ScheduleManualSettingsUpdate();
    }

    private void AwbLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsUri is null) return;
        _awbLocked = !_awbLocked;
        AwbLockButton.Content = _awbLocked ? "AWB: Locked" : "Lock AWB";
        AwbLockButton.Background = _awbLocked ? new SolidColorBrush(Color.FromRgb(180, 120, 20)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
        ScheduleManualSettingsUpdate();
    }

    private void AwbModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingPhoneSettings || _settingsUri is null) return;
        if (AwbModeBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && int.TryParse((string?)item.Tag, out var mode))
        {
            _awbMode = mode;
            ScheduleManualSettingsUpdate();
        }
    }

    private void ManualFocusCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsUri is null) return;
        _manualFocus = ManualFocusCheck.IsChecked == true;
        FocusSlider.IsEnabled = _manualFocus && _maxFocusDistance > 0;
        FocusValueText.Text = _manualFocus ? $"{_focusDistance:0.1} dpt" : "Auto (Continuous)";
        ScheduleManualSettingsUpdate();
    }

    private void ScheduleManualSettingsUpdate()
    {
        if (_settingsUri is null) return;
        _manualDebounce?.Cancel();
        _manualDebounce?.Dispose();
        _manualDebounce = new CancellationTokenSource();
        var token = _manualDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, token);
                await _settingsUpdate.WaitAsync(token);
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        zoom = _zoomRatio,
                        aeComp = _aeComp,
                        aeLocked = _aeLocked,
                        awbMode = _awbMode,
                        awbLocked = _awbLocked,
                        manualFocus = _manualFocus,
                        focusDistance = _focusDistance
                    });
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await _http.PostAsync(_settingsUri, content, timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(timeout.Token);
                        await Dispatcher.InvokeAsync(() => ApplyPhoneSettingsJson(json));
                    }
                }
                finally
                {
                    _settingsUpdate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to send manual camera controls: {ex.Message}");
            }
        }, token);
    }

    private async void AutoDetectUsb_Click(object sender, RoutedEventArgs e)
    {
        AutoDetectUsbButton.IsEnabled = false;
        SetStatus("Scanning for USB phone...", false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var devicesOutput = await RunAdbAsync(cts.Token, "devices");
            var lines = devicesOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var deviceFound = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    deviceFound = true;
                    break;
                }
            }

            if (!deviceFound)
            {
                SetStatus("No authorized Android phone detected. Enable USB debugging.", false);
                return;
            }

            AddressBox.Text = "http://127.0.0.1:18765/stream.mjpg";
            Connect_Click(sender, e);
        }
        catch (Exception ex)
        {
            SetStatus($"ADB error: {ex.Message}", false);
        }
        finally
        {
            AutoDetectUsbButton.IsEnabled = true;
        }
    }

    private static async Task EnsureUsbForwardAsync(CancellationToken cancellationToken)
    {
        var forwards = await RunAdbAsync(cancellationToken, "forward", "--list");
        foreach (var line in forwards.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3 || columns[^2] != "tcp:18765") continue;
            if (columns[^1] == "tcp:8765") return;
            throw new InvalidOperationException($"USB port 18765 is already forwarded to {columns[^1]}. Remove that ADB forward, then reconnect.");
        }

        await RunAdbAsync(cancellationToken, "forward", "--no-rebind", "tcp:18765", "tcp:8765");
    }

    private static async Task<string> RunAdbAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("adb")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start adb. Install Android Platform Tools and add adb to PATH.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("adb did not respond within 15 seconds. Check the USB debugging prompt on the phone.");
        }

        var output = await outputTask;
        var error = await errorTask;
        var result = (output + Environment.NewLine + error).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result) ? "adb failed to configure USB forwarding." : result);
        return result;
    }

    private static bool CameraSourceRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\{f15889e2-7b53-4ee1-bad4-9e7c51a94d60}\InprocServer32");
        return key is not null;
    }

    private async Task StartVirtualCameraAsync()
    {
        try
        {
            var resolution = Volatile.Read(ref _cameraResolution);
            if (_cameraHost is { HasExited: false } && _hostResolution == resolution) return;
            if (_cameraHost is { HasExited: false })
            {
                var previousHost = _cameraHost;
                try { previousHost.StandardInput.Close(); } catch { }
                try { await previousHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { try { previousHost.Kill(true); } catch { } }
                previousHost.Dispose();
                _cameraHost = null;
                await Task.Delay(300);
            }
            var path = Path.Combine(AppContext.BaseDirectory, "PhonecamCameraHost.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("Phonecam camera host is missing.", path);
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _cameraHost = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Phonecam camera host.");
            var ready = await _cameraHost.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
            if (ready != "READY")
            {
                var error = await _cameraHost.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows did not start the Phonecam virtual camera." : error.Trim());
            }
            _hostResolution = resolution;
            CameraDeviceText.Text = "Ready - select Phonecam (Windows Virtual Camera) in your app.";
            InstallCameraButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            CameraDeviceText.Text = $"Windows webcam unavailable - {error.Message}";
            InstallCameraButton.Visibility = Visibility.Visible;
        }
    }

    private async void InstallCamera_Click(object sender, RoutedEventArgs e)
    {
        InstallCameraButton.IsEnabled = false;
        CameraDeviceText.Text = "Installing the Windows camera...";
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "install-camera.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("The Phonecam camera installer is missing.", script);
            var install = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\""
            }) ?? throw new InvalidOperationException("Could not launch the camera installer.");
            await install.WaitForExitAsync();
            if (install.ExitCode != 0) throw new InvalidOperationException("Camera installation failed. Run install-camera.ps1 as administrator.");
            if (_settingsUri is not null) await StartVirtualCameraAsync();
            else
            {
                CameraDeviceText.Text = "Camera installed - connect to a phone to start it.";
                InstallCameraButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception error)
        {
            CameraDeviceText.Text = $"Camera installation stopped - {error.Message}";
        }
        finally { InstallCameraButton.IsEnabled = true; }
    }

    private static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        var relativeArtifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "artifacts", "windows", "ffmpeg.exe"));
        if (File.Exists(relativeArtifacts)) return relativeArtifacts;
        return null;
    }

    private async Task ReadH264Async(Stream networkStream, CancellationToken cancellationToken, string ffmpegPath, int rotationDegrees, bool isUsb)
    {
        var resolution = Volatile.Read(ref _cameraResolution);
        var targetW = resolution.Width;
        var targetH = resolution.Height;
        var frameBytes = targetW * targetH * 4;

        var filter = "";
        if (rotationDegrees == 90) filter = "transpose=clock";
        else if (rotationDegrees == 180) filter = "hflip,vflip";
        else if (rotationDegrees == 270) filter = "transpose=cclock";

        if (_mirrorVideo) filter += (filter.Length > 0 ? ",hflip" : "hflip");

        if (filter.Length > 0)
        {
            filter += $",scale={targetW}:{targetH}:force_original_aspect_ratio=decrease,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2";
        }

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-hwaccel");
        startInfo.ArgumentList.Add("auto");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("nobuffer");
        startInfo.ArgumentList.Add("-flags");
        startInfo.ArgumentList.Add("low_delay");
        startInfo.ArgumentList.Add("-probesize");
        startInfo.ArgumentList.Add("131072");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("h264");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        if (filter.Length > 0)
        {
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add(filter);
        }
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        using var ffmpeg = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to launch ffmpeg hardware decoder.");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Continuously drain stderr in background so ffmpeg never blocks on full pipe buffer
        var errorTask = Task.Run(async () =>
        {
            try
            {
                var errBuffer = new char[1024];
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var read = await ffmpeg.StandardError.ReadAsync(errBuffer.AsMemory(0, errBuffer.Length), linkedCts.Token);
                    if (read <= 0) break;
                }
            }
            catch { }
        }, linkedCts.Token);

        var feedTask = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token);
                    if (read <= 0) break;
                    Volatile.Write(ref _lastChunkReceivedTicks, Stopwatch.GetTimestamp());
                    await ffmpeg.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token);
                    await ffmpeg.StandardInput.BaseStream.FlushAsync(linkedCts.Token);
                    _statsByteCount += read;
                }
            }
            catch { }
            finally
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
            }
        }, linkedCts.Token);

        try
        {
            var frameBuffer = new byte[frameBytes];
            var stdout = ffmpeg.StandardOutput.BaseStream;

            _statsFrameCount = 0;
            _statsByteCount = 0;
            _statsWindowStartTicks = Stopwatch.GetTimestamp();
            var isCalibrated = false;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                var offset = 0;
                while (offset < frameBytes)
                {
                    var read = await stdout.ReadAsync(frameBuffer.AsMemory(offset, frameBytes - offset), linkedCts.Token);
                    if (read <= 0) throw new EndOfStreamException("FFmpeg decoder output ended.");
                    offset += read;
                }

                if (_reconnectNeeded)
                {
                    _reconnectNeeded = false;
                    break;
                }

                var targetBuffer = _useBufferA ? _pixelBufferA : _pixelBufferB;
                Buffer.BlockCopy(frameBuffer, 0, targetBuffer, 0, frameBytes);
                Volatile.Write(ref _latestCameraFrame, targetBuffer);
                WriteSharedFrame(targetW, targetH, targetBuffer, ++_frameSequence);
                _useBufferA = !_useBufferA;

                var nowTicks = Stopwatch.GetTimestamp();
                var lastChunk = Volatile.Read(ref _lastChunkReceivedTicks);
                if (lastChunk > 0)
                {
                    var decodeMs = (double)(nowTicks - lastChunk) * 1000.0 / Stopwatch.Frequency;
                    _latestDecodeMs = Math.Clamp(decodeMs, 1.0, 50.0);
                }

                if (!isCalibrated)
                {
                    isCalibrated = true;
                    _statsFrameCount = 0;
                    _statsByteCount = 0;
                    _statsWindowStartTicks = nowTicks;
                    _latestFps = 30.0;
                    _latestMbps = 4.0;
                    var rtt = Volatile.Read(ref _latestRttMs);
                    _latestLatencyMs = Math.Max(1.0, (rtt > 0 ? (rtt / 2.0) : 1.5) + _latestDecodeMs);
                    _latestStatsText = $"{_latestFps:0.0} FPS  •  {_latestMbps:0.0} Mbps  •  {_latestLatencyMs:0} ms";
                }
                else
                {
                    _statsFrameCount++;
                    var elapsedSec = (double)(nowTicks - _statsWindowStartTicks) / Stopwatch.Frequency;
                    if (elapsedSec >= 0.5)
                    {
                        var fps = _statsFrameCount / elapsedSec;
                        var mbps = (_statsByteCount * 8.0) / (elapsedSec * 1_000_000.0);
                        var rtt = Volatile.Read(ref _latestRttMs);
                        var totalLat = Math.Max(1.0, (rtt > 0 ? (rtt / 2.0) : 1.5) + _latestDecodeMs);
                        _latestFps = fps;
                        _latestMbps = mbps;
                        _latestLatencyMs = totalLat;
                        _latestStatsText = $"{fps:0.0} FPS  •  {mbps:0.0} Mbps  •  {totalLat:0} ms";
                        _statsFrameCount = 0;
                        _statsByteCount = 0;
                        _statsWindowStartTicks = nowTicks;
                    }
                }

                if (!_uiPending)
                {
                    _uiPending = true;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (_writeableBitmap is null || _writeableBitmap.PixelWidth != targetW || _writeableBitmap.PixelHeight != targetH)
                            {
                                _writeableBitmap = new WriteableBitmap(targetW, targetH, 96, 96, PixelFormats.Bgra32, null);
                                Preview.Source = _writeableBitmap;
                            }
                            _writeableBitmap.WritePixels(new Int32Rect(0, 0, targetW, targetH), targetBuffer, targetW * 4, 0);
                            ResolutionText.Text = $"{targetW} x {targetH}";
                            FpsStatsText.Text = $"{_latestFps:0.0} FPS";
                            BitrateStatsText.Text = $"{_latestMbps:0.0} Mbps";
                            LatencyStatsText.Text = $"{_latestLatencyMs:0} ms";
                            if (_latestStatsText != null)
                            {
                                StatsText.Text = _latestStatsText;
                            }
                            EmptyState.Visibility = Visibility.Collapsed;
                            StatusDot.Fill = (Brush)FindResource("Accent");
                        }
                        finally
                        {
                            _uiPending = false;
                        }
                    });
                }
            }
        }
        finally
        {
            linkedCts.Cancel();
            try { ffmpeg.StandardInput.Close(); } catch { }
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(true); } catch { }
            try { await feedTask; } catch { }
            try { await errorTask; } catch { }
        }
    }

    private async Task ReadMultipartAsync(Stream networkStream, CancellationToken cancellationToken)
    {
        // Wrap network stream in a 64KB buffer to eliminate single-byte/small read syscall overhead
        using var bufferedStream = new BufferedStream(networkStream, 65536);
        var lineBuffer = new byte[512];

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineBufferedAsync(bufferedStream, lineBuffer, cancellationToken);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            long length = 0;
            int rotationDegrees = 0;
            while (!string.IsNullOrEmpty(line = await ReadLineBufferedAsync(bufferedStream, lineBuffer, cancellationToken) ?? ""))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(line.AsSpan(15).Trim(), out length);
                }
                else if (line.StartsWith("X-Rotation:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.AsSpan(11).Trim(), out rotationDegrees);
                }
            }
            if (length is <= 0 or > 10_000_000) continue;

            var bytes = new byte[(int)length];
            using (var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                frameTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await bufferedStream.ReadExactlyAsync(bytes, frameTimeout.Token);
            }
            var bitmap = new BitmapImage();
            using (var input = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = input;
                bitmap.EndInit();
                bitmap.Freeze();
                var resolution = Volatile.Read(ref _cameraResolution);
                var isRotated = (rotationDegrees == 90 || rotationDegrees == 270);
                var orientedWidth = isRotated ? bitmap.PixelHeight : bitmap.PixelWidth;
                var orientedHeight = isRotated ? bitmap.PixelWidth : bitmap.PixelHeight;
                var scale = Math.Max((double)resolution.Width / orientedWidth, (double)resolution.Height / orientedHeight);
                var frameWidth = orientedWidth * scale;
                var frameHeight = orientedHeight * scale;
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var group = new TransformGroup();
                    if (rotationDegrees != 0)
                    {
                        group.Children.Add(new RotateTransform(rotationDegrees, resolution.Width / 2.0, resolution.Height / 2.0));
                    }
                    if (_mirrorVideo)
                    {
                        group.Children.Add(new ScaleTransform(-1, 1, resolution.Width / 2.0, resolution.Height / 2.0));
                    }
                    if (group.Children.Count > 0)
                    {
                        drawing.PushTransform(group);
                    }
                    var drawW = isRotated ? frameHeight : frameWidth;
                    var drawH = isRotated ? frameWidth : frameHeight;
                    drawing.DrawImage(bitmap, new Rect((resolution.Width - drawW) / 2, (resolution.Height - drawH) / 2, drawW, drawH));
                    if (group.Children.Count > 0)
                    {
                        drawing.Pop();
                    }
                }

                if (_renderBitmap is null || _renderBitmapResolution != resolution)
                {
                    _renderBitmap = new RenderTargetBitmap(resolution.Width, resolution.Height, 96, 96, PixelFormats.Pbgra32);
                    _renderBitmapResolution = resolution;
                }
                _renderBitmap.Clear();
                _renderBitmap.Render(visual);

                var targetBuffer = _useBufferA ? _pixelBufferA : _pixelBufferB;
                _renderBitmap.CopyPixels(new Int32Rect(0, 0, resolution.Width, resolution.Height), targetBuffer, resolution.Width * 4, 0);
                Volatile.Write(ref _latestCameraFrame, targetBuffer);
                WriteSharedFrame(resolution.Width, resolution.Height, targetBuffer, ++_frameSequence);
                _useBufferA = !_useBufferA;
            }

            _statsFrameCount++;
            _statsByteCount += bytes.Length;
            var nowTicks = Stopwatch.GetTimestamp();
            var elapsedSec = (double)(nowTicks - _statsWindowStartTicks) / Stopwatch.Frequency;
            string? statsDisplay = null;
            if (elapsedSec >= 0.8)
            {
                var fps = _statsFrameCount / elapsedSec;
                var mbps = (_statsByteCount * 8.0) / (elapsedSec * 1_000_000.0);
                var rtt = Volatile.Read(ref _latestRttMs);
                var totalLat = Math.Max(1.0, (rtt > 0 ? (rtt / 2.0) : 4.0) + 12.0);
                _latestFps = fps;
                _latestMbps = mbps;
                _latestLatencyMs = totalLat;
                statsDisplay = $"{fps:0.0} FPS  •  {mbps:0.0} Mbps  •  {totalLat:0} ms";
                _statsFrameCount = 0;
                _statsByteCount = 0;
                _statsWindowStartTicks = nowTicks;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                Preview.Source = bitmap;
                ResolutionText.Text = $"{bitmap.PixelWidth} x {bitmap.PixelHeight}";
                FpsStatsText.Text = $"{_latestFps:0.0} FPS";
                BitrateStatsText.Text = $"{_latestMbps:0.0} Mbps";
                LatencyStatsText.Text = $"{_latestLatencyMs:0} ms";
                if (statsDisplay != null)
                {
                    StatsText.Text = statsDisplay;
                }
                EmptyState.Visibility = Visibility.Collapsed;
                StatusDot.Fill = (Brush)FindResource("Accent");
            });
        }
    }

    private static async Task<string?> ReadLineBufferedAsync(BufferedStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (count < buffer.Length)
        {
            var single = new byte[1];
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cts.Token);
            if (read == 0) return count == 0 ? null : Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r');
            var b = single[0];
            if (b == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r');
            }
            buffer[count++] = b;
        }
        return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r');
    }

    private void StartDiscoveryListener()
    {
        _discoveryCts = new CancellationTokenSource();
        var token = _discoveryCts.Token;
        Task.Run(async () =>
        {
            try
            {
                _discoveryClient = new System.Net.Sockets.UdpClient();
                _discoveryClient.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                _discoveryClient.Client.Bind(new IPEndPoint(IPAddress.Any, 8767));
                while (!token.IsCancellationRequested)
                {
                    var result = await _discoveryClient.ReceiveAsync(token);
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    // Format: "PHONECAM|http://192.168.x.x:8765/stream.mjpg|DeviceName"
                    if (message.StartsWith("PHONECAM|", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = message.Split('|');
                        if (parts.Length >= 2)
                        {
                            var url = parts[1];
                            var name = parts.Length > 2 ? parts[2] : "Phonecam Device";
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!_isConnectingOrStreaming && (AddressBox.Text.Contains("127.0.0.1") || string.IsNullOrWhiteSpace(AddressBox.Text)))
                                {
                                    _discoveredAddress = url;
                                    DiscoveredDeviceText.Text = $"{name} ({result.RemoteEndPoint.Address})";
                                    DiscoveredPanel.Visibility = Visibility.Visible;
                                }
                            });
                        }
                    }
                }
            }
            catch (Exception) { }
        }, token);
    }

    private void ConnectDiscovered_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_discoveredAddress))
        {
            AddressBox.Text = _discoveredAddress;
            DiscoveredPanel.Visibility = Visibility.Collapsed;
            Connect_Click(sender, e);
        }
    }

    private void SetStatus(string message, bool connected, string? mode = null)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            StatusDot.Fill = connected ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(137, 151, 169));
            if (connected && !string.IsNullOrEmpty(mode))
            {
                ConnectionModeText.Text = mode;
                ModeBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ModeBadge.Visibility = Visibility.Collapsed;
            }
            if (!connected)
            {
                FpsStatsText.Text = "-- FPS";
                BitrateStatsText.Text = "-- Mbps";
                LatencyStatsText.Text = "-- ms";
                StatsText.Text = "";
            }
        });
    }

    private void InitializeAudioDevices()
    {
        try
        {
            _deviceEnumerator ??= new MMDeviceEnumerator();
            var devices = new List<AudioDeviceOption>();
            var renderDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            MMDevice? cableDevice = null;
            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }

            foreach (var dev in renderDevices)
            {
                var name = dev.FriendlyName;
                devices.Add(new AudioDeviceOption(dev.ID, name, dev));
                if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    cableDevice = dev;
                }
            }

            AudioDeviceBox.DisplayMemberPath = "Name";
            AudioDeviceBox.ItemsSource = devices;

            if (cableDevice != null)
            {
                MicDeviceText.Text = "VB-CABLE Virtual Microphone detected. Ready for Discord, Zoom, Teams.";
                InstallMicButton.Visibility = Visibility.Collapsed;
                AudioDeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Device.ID == cableDevice.ID);
            }
            else
            {
                MicDeviceText.Text = "VB-CABLE driver not installed. Install it to route Phonecam audio into Zoom/Discord/Teams.";
                InstallMicButton.Visibility = Visibility.Visible;
                if (defaultDevice != null)
                {
                    AudioDeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Device.ID == defaultDevice.ID);
                }
                else if (devices.Count > 0)
                {
                    AudioDeviceBox.SelectedIndex = 0;
                }
            }

            if (AudioDeviceBox.SelectedItem is AudioDeviceOption selected)
            {
                _selectedAudioDevice = selected.Device;
                SetupWasapiOutput();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enumerate audio devices: {ex.Message}");
            MicDeviceText.Text = $"Audio setup error: {ex.Message}";
        }
    }

    private async void InstallMicrophone_Click(object sender, RoutedEventArgs e)
    {
        InstallMicButton.IsEnabled = false;
        MicDeviceText.Text = "Installing VB-CABLE Virtual Microphone driver (Admin prompt may appear)...";
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "install-microphone.ps1"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "scripts", "install-microphone.ps1")),
                Path.Combine(Environment.CurrentDirectory, "scripts", "install-microphone.ps1")
            };
            var script = candidates.FirstOrDefault(File.Exists);
            if (script is not null)
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var p = Process.Start(psi);
                if (p is not null) await p.WaitForExitAsync();
            }
            else
            {
                var exeCandidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "vbcable", "VBCABLE_Setup_x64.exe"),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "windows", "vbcable", "VBCABLE_Setup_x64.exe")),
                    Path.Combine(Environment.CurrentDirectory, "windows", "vbcable", "VBCABLE_Setup_x64.exe")
                };
                var exe = exeCandidates.FirstOrDefault(File.Exists);
                if (exe is not null)
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        Arguments = "-i -h",
                        WorkingDirectory = Path.GetDirectoryName(exe),
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    var p = Process.Start(psi);
                    if (p is not null) await p.WaitForExitAsync();
                }
            }
        }
        catch (Exception ex)
        {
            MicDeviceText.Text = $"Driver installation failed: {ex.Message}";
        }
        finally
        {
            await Task.Delay(1500);
            InitializeAudioDevices();
            InstallMicButton.IsEnabled = true;
        }
    }

    private void AudioDeviceBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AudioDeviceBox.SelectedItem is AudioDeviceOption opt && opt.Device != _selectedAudioDevice)
        {
            _selectedAudioDevice = opt.Device;
            SetupWasapiOutput();
        }
    }

    private async void MicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _micMuted = !_micMuted;
        MicMuteButton.Content = _micMuted ? "Mic: MUTED" : "Mic: ON";
        MicMuteButton.Background = _micMuted ? new SolidColorBrush(Color.FromRgb(180, 50, 50)) : new SolidColorBrush(Color.FromRgb(38, 52, 69));
        if (_micMuted)
        {
            AudioMeter.Value = 0;
        }

        if (_settingsUri != null)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { micMuted = _micMuted });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _http.PostAsync(_settingsUri, content, timeout.Token);
            }
            catch { }
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioVolume = (float)e.NewValue;
    }

    private void SetupWasapiOutput()
    {
        if (_selectedAudioDevice == null) return;
        try
        {
            if (_wasapiOut != null)
            {
                try { _wasapiOut.Stop(); } catch { }
                try { _wasapiOut.Dispose(); } catch { }
                _wasapiOut = null;
            }

            _waveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };

            _wasapiOut = new WasapiOut(_selectedAudioDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _wasapiOut.Init(_waveProvider);
            _wasapiOut.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to setup WasapiOut: {ex.Message}");
        }
    }

    private async Task StartAudioStreamAsync(Uri baseUri, CancellationToken ct, string? ffmpegPath)
    {
        if (string.IsNullOrEmpty(ffmpegPath)) return;
        try
        {
            var audioUri = new UriBuilder(baseUri) { Path = "/audio.opus", Query = "", Fragment = "" }.Uri;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync(audioUri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await ReadOpusAudioAsync(stream, ct, ffmpegPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio stream error: {ex.Message}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => AudioMeter.Value = 0);
        }
    }

    private async Task ReadOpusAudioAsync(Stream networkStream, CancellationToken ct, string ffmpegPath)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("ogg");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("pipe:0");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("pipe:1");

        using var ffmpeg = Process.Start(psi);
        if (ffmpeg is null) return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var errorDrainTask = Task.Run(async () =>
        {
            var errBuf = new char[512];
            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var read = await ffmpeg.StandardError.ReadAsync(errBuf.AsMemory(0, errBuf.Length), linkedCts.Token);
                    if (read <= 0) break;
                }
            }
            catch { }
        }, linkedCts.Token);

        var feedTask = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var read = await networkStream.ReadAsync(buf.AsMemory(0, buf.Length), linkedCts.Token);
                    if (read <= 0) break;
                    await ffmpeg.StandardInput.BaseStream.WriteAsync(buf.AsMemory(0, read), linkedCts.Token);
                    await ffmpeg.StandardInput.BaseStream.FlushAsync(linkedCts.Token);
                }
            }
            catch { }
            finally
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
            }
        }, linkedCts.Token);

        try
        {
            var pcmBuf = new byte[1920]; // 20ms of 48kHz mono 16-bit
            var meterTick = 0;
            while (!linkedCts.Token.IsCancellationRequested)
            {
                var totalRead = 0;
                while (totalRead < pcmBuf.Length && !linkedCts.Token.IsCancellationRequested)
                {
                    var read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(pcmBuf.AsMemory(totalRead, pcmBuf.Length - totalRead), linkedCts.Token);
                    if (read <= 0) break;
                    totalRead += read;
                }
                if (totalRead <= 0) break;

                short peak = 0;
                var vol = _audioVolume;
                var muted = _micMuted;

                for (var i = 0; i < totalRead; i += 2)
                {
                    if (muted)
                    {
                        pcmBuf[i] = 0;
                        pcmBuf[i + 1] = 0;
                    }
                    else
                    {
                        var sample = (short)(pcmBuf[i] | (pcmBuf[i + 1] << 8));
                        if (Math.Abs(vol - 1.0f) > 0.01f)
                        {
                            var scaled = (int)(sample * vol);
                            if (scaled > 32767) scaled = 32767;
                            else if (scaled < -32768) scaled = -32768;
                            sample = (short)scaled;
                            pcmBuf[i] = (byte)(sample & 0xFF);
                            pcmBuf[i + 1] = (byte)((sample >> 8) & 0xFF);
                        }
                        var abs = Math.Abs(sample);
                        if (abs > peak) peak = (short)abs;
                    }
                }

                if (!muted)
                {
                    meterTick++;
                    if (meterTick % 3 == 0)
                    {
                        var level = Math.Min(100.0, (peak / 32768.0) * 100.0 * 1.5);
                        _ = Dispatcher.InvokeAsync(() => AudioMeter.Value = level);
                    }
                }
                else
                {
                    _ = Dispatcher.InvokeAsync(() => AudioMeter.Value = 0);
                }

                _waveProvider?.AddSamples(pcmBuf, 0, totalRead);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { linkedCts.Cancel(); } catch { }
            try { ffmpeg.StandardInput.Close(); } catch { }
            if (!ffmpeg.WaitForExit(1000))
            {
                try { ffmpeg.Kill(true); } catch { }
            }
        }
    }

    private async Task StartPingLoopAsync(Uri baseUri, CancellationToken ct)
    {
        var pingUri = new UriBuilder(baseUri) { Path = "/ping", Query = "", Fragment = "" }.Uri;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t0 = Stopwatch.GetTimestamp();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var resp = await _http.GetAsync(pingUri, linked.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var t1 = Stopwatch.GetTimestamp();
                    var rtt = (double)(t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    Volatile.Write(ref _latestRttMs, rtt);
                }
            }
            catch { }
            try { await Task.Delay(1000, ct); } catch { break; }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _streamCancellation?.Cancel();
        _audioCts?.Cancel();
        _audioCts?.Dispose();
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        try { _wasapiOut?.Stop(); } catch { }
        try { _wasapiOut?.Dispose(); } catch { }
        try { _deviceEnumerator?.Dispose(); } catch { }
        _discoveryCts?.Cancel();
        try { _discoveryClient?.Close(); } catch { }
        if (_cameraHost is { HasExited: false })
        {
            try { _cameraHost.StandardInput.Close(); } catch { }
            if (!_cameraHost.WaitForExit(1500))
            {
                try { _cameraHost.Kill(true); } catch { }
            }
        }
        _http.Dispose();
        _mmfAccessor?.Dispose();
        _mmf?.Dispose();
        _listener.Stop();
        base.OnClosed(e);
    }
}
