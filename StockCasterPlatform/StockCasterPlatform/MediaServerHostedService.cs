using System.Diagnostics;
using System.Net.Sockets;

namespace StockCasterPlatform;

public sealed class MediaServerHostedService : BackgroundService
{
    private readonly ILogger<MediaServerHostedService> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly BroadcastSecurityService _broadcastSecurity;
    private Process? _process;
    private bool _ownsProcess;
    private volatile bool _shutdownRequested;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false } || IsApiListening();
            }
            catch
            {
                return false;
            }
        }
    }

    public MediaServerHostedService(
        ILogger<MediaServerHostedService> logger,
        IWebHostEnvironment environment,
        BroadcastSecurityService broadcastSecurity)
    {
        _logger = logger;
        _environment = environment;
        _broadcastSecurity = broadcastSecurity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsApiListening())
        {
            await _broadcastSecurity.ApplyToRunningServerAsync(stoppingToken);
            _logger.LogInformation("기존 MediaMTX 서버를 사용합니다.");
            await WaitUntilCancelled(stoppingToken);
            return;
        }

        string mediaDirectory = Path.Combine(AppContext.BaseDirectory, "MediaServer");
        string executablePath = Path.Combine(mediaDirectory, "mediamtx.exe");
        string configurationPath = Path.Combine(mediaDirectory, "mediamtx.yml");

        if (!File.Exists(executablePath) || !File.Exists(configurationPath))
            throw new FileNotFoundException("MediaMTX 실행 파일 또는 설정 파일을 찾을 수 없습니다.", mediaDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = mediaDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(configurationPath);
        BroadcastCredentials credentials = await _broadcastSecurity.GetCredentialsAsync(stoppingToken);
        startInfo.Environment["MTX_AUTHINTERNALUSERS_0_USER"] = credentials.Username;
        startInfo.Environment["MTX_AUTHINTERNALUSERS_0_PASS"] = credentials.Password;
        string recordingPath = Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "recordings",
            "%path",
            "%Y-%m-%d_%H-%M-%S-%f").Replace('\\', '/');
        startInfo.Environment["MTX_PATHDEFAULTS_RECORDPATH"] = recordingPath;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => LogMediaServerLine(args.Data);
        _process.ErrorDataReceived += (_, args) => LogMediaServerLine(args.Data);

        if (!_process.Start())
            throw new InvalidOperationException("MediaMTX 방송 서버를 시작하지 못했습니다.");

        _ownsProcess = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _logger.LogInformation("MediaMTX 방송 서버를 시작했습니다. PID: {ProcessId}", _process.Id);

        try
        {
            await _process.WaitForExitAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested && !_shutdownRequested)
                _logger.LogError("MediaMTX 방송 서버가 예기치 않게 종료되었습니다. 종료 코드: {ExitCode}", _process.ExitCode);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 정상적인 애플리케이션 종료입니다.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ownsProcess && _process is { HasExited: false } process)
        {
            try
            {
                _shutdownRequested = true;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                _logger.LogWarning("MediaMTX 종료 처리 중 알림: {Message}", ex.Message);
            }
        }

        _process?.Dispose();
        _process = null;
        await base.StopAsync(cancellationToken);
    }

    private static bool IsApiListening()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 9997).Wait(TimeSpan.FromMilliseconds(180));
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitUntilCancelled(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private void LogMediaServerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (line.Contains("ERR", StringComparison.OrdinalIgnoreCase))
            _logger.LogError("MediaMTX: {Message}", line);
        else if (line.Contains("WAR", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("MediaMTX: {Message}", line);
        else
            _logger.LogDebug("MediaMTX: {Message}", line);
    }
}
