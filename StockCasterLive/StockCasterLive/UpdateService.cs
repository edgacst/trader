using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StockCasterLive;

internal sealed record AppUpdate(Version Version, string DownloadUrl);

internal static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/edgacst/trader/releases/latest";
    private const string InstallerPrefix = "EdgarCasterLive-Setup-";
    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(10));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(15));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EdgarCasterLiveUpdater", "1.1.3"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<AppUpdate?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await ApiClient.GetAsync(
            LatestReleaseApiUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out JsonElement tagElement) ||
            !TryParseVersion(tagElement.GetString(), out Version? releaseVersion) ||
            releaseVersion.CompareTo(currentVersion) <= 0)
        {
            return null;
        }

        string expectedAssetName = $"{InstallerPrefix}{releaseVersion}.exe";
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement nameElement) ||
                !string.Equals(nameElement.GetString(), expectedAssetName, StringComparison.OrdinalIgnoreCase) ||
                !asset.TryGetProperty("browser_download_url", out JsonElement urlElement))
            {
                continue;
            }

            string? downloadUrl = urlElement.GetString();
            if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return new AppUpdate(releaseVersion, uri.ToString());
            }
        }

        // 릴리스에 버전이 일치하는 설치 파일이 올라오기 전에는 업데이트를 알리지 않는다.
        return null;
    }

    public static async Task<string> DownloadInstallerAsync(AppUpdate update, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("업데이트 주소가 올바르지 않습니다.");
        }

        string fileName = $"{InstallerPrefix}{update.Version}.exe";
        string installerPath = Path.Combine(Path.GetTempPath(), fileName);
        string partialPath = installerPath + "." + Guid.NewGuid().ToString("N") + ".download";

        try
        {
            using var response = await DownloadClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            long? expectedLength = response.Content.Headers.ContentLength;

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            long downloadedLength = new FileInfo(partialPath).Length;
            if (downloadedLength == 0 || (expectedLength.HasValue && downloadedLength != expectedLength.Value))
                throw new InvalidDataException("다운로드한 설치 파일의 크기가 올바르지 않습니다.");

            File.Move(partialPath, installerPath, overwrite: true);
            return installerPath;
        }
        catch
        {
            try { File.Delete(partialPath); } catch { }
            throw;
        }
    }

    private static bool TryParseVersion(string? tagName, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
            return false;

        string normalized = tagName.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        int suffixStart = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffixStart >= 0)
            normalized = normalized[..suffixStart];

        if (!Version.TryParse(normalized, out Version? parsed) || parsed.Build < 0)
            return false;

        version = parsed;
        return true;
    }
}