using System.Text.Json;
using System.Net.Http.Json;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class SmokeTestService(
    IShellExecutor executor,
    ILogger logger,
    IHttpClientFactory httpClientFactory) : ISmokeTestService
{
    public async Task<SmokeTestResult> RunSmokeTestsAsync(SmokeTestRequest request, CancellationToken cancellationToken = default)
    {
        var checks = new List<SmokeTestCheck>();
        var urls = request.ExplicitUrls?.Count > 0 
            ? request.ExplicitUrls 
            : await DetectWebAppUrlsAsync(request.WorkingDirectory, cancellationToken);

        if (urls.Count == 0)
        {
            return new SmokeTestResult(
                true,
                [],
                "Nenhuma URL de aplicacao web detectada. Forneca URLs explicitas ou configure launchSettings.json/docker-compose.yml");
        }

        using var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);

        foreach (var url in urls)
        {
            var check = await RunSingleCheckAsync(httpClient, url, cancellationToken);
            checks.Add(check);
        }

        var allHealthy = checks.All(c => c.Healthy);
        return new SmokeTestResult(allHealthy, checks, null);
    }

    private async Task<SmokeTestCheck> RunSingleCheckAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);
            stopwatch.Stop();

            return new SmokeTestCheck(
                Name: ExtractNameFromUrl(url),
                Url: url,
                Healthy: response.IsSuccessStatusCode,
                StatusCode: (int)response.StatusCode,
                ResponseTimeMs: stopwatch.Elapsed.TotalMilliseconds,
                Error: response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            return new SmokeTestCheck(
                Name: ExtractNameFromUrl(url),
                Url: url,
                Healthy: false,
                StatusCode: null,
                ResponseTimeMs: stopwatch.Elapsed.TotalMilliseconds,
                Error: "Timeout");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new SmokeTestCheck(
                Name: ExtractNameFromUrl(url),
                Url: url,
                Healthy: false,
                StatusCode: null,
                ResponseTimeMs: stopwatch.Elapsed.TotalMilliseconds,
                Error: ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SmokeTestCheck(
                Name: ExtractNameFromUrl(url),
                Url: url,
                Healthy: false,
                StatusCode: null,
                ResponseTimeMs: stopwatch.Elapsed.TotalMilliseconds,
                Error: ex.Message);
        }
    }

    private static string ExtractNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;
            var port = uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "";
            return $"{host}{port}";
        }
        catch
        {
            return url;
        }
    }

    public async Task<IReadOnlyList<string>> DetectWebAppUrlsAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return urls.ToList();
        }

        try
        {
            // 1. launchSettings.json (ASP.NET Core)
            var launchSettingsPath = Path.Combine(workingDirectory, "Properties", "launchSettings.json");
            if (File.Exists(launchSettingsPath))
            {
                var content = await File.ReadAllTextAsync(launchSettingsPath, cancellationToken);
                var urlsFromLaunch = ExtractUrlsFromLaunchSettings(content);
                foreach (var url in urlsFromLaunch)
                    urls.Add(url);
            }

            // 2. docker-compose.yml / docker-compose.override.yml
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.override.yml", "docker-compose.local.yml" };
            foreach (var composeFile in composeFiles)
            {
                var path = Path.Combine(workingDirectory, composeFile);
                if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path, cancellationToken);
                    var urlsFromCompose = ExtractUrlsFromDockerCompose(content);
                    foreach (var url in urlsFromCompose)
                        urls.Add(url);
                }
            }

            // 3. package.json (Node.js apps)
            var packageJsonPath = Path.Combine(workingDirectory, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var content = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
                var urlsFromPackage = ExtractUrlsFromPackageJson(content);
                foreach (var url in urlsFromPackage)
                    urls.Add(url);
            }

            // 4. .env / appsettings.json (common ports)
            var envFiles = new[] { ".env", ".env.local", ".env.development", "appsettings.json", "appsettings.Development.json" };
            foreach (var envFile in envFiles)
            {
                var path = Path.Combine(workingDirectory, envFile);
                if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path, cancellationToken);
                    var urlsFromEnv = ExtractUrlsFromEnv(content);
                    foreach (var url in urlsFromEnv)
                        urls.Add(url);
                }
            }

            // 5. Common default URLs for known frameworks
            urls.UnionWith(GetDefaultFrameworkUrls(workingDirectory));
        }
        catch (Exception ex)
        {
            logger.Log($"[SMOKE_TEST] Error detecting URLs: {ex.Message}");
        }

        return urls.ToList();
    }

    private static IReadOnlyList<string> ExtractUrlsFromLaunchSettings(string content)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("profiles", out var profiles))
            {
                foreach (var profile in profiles.EnumerateObject())
                {
                    if (profile.Value.TryGetProperty("applicationUrl", out var appUrl))
                    {
                        var url = appUrl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            urls.AddRange(url.Split(';').Select(u => u.Trim()));
                        }
                    }
                }
            }
        }
        catch { }
        return urls;
    }

    private static IReadOnlyList<string> ExtractUrlsFromDockerCompose(string content)
    {
        var urls = new List<string>();
        try
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ports:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (trimmed.Contains(":", StringComparison.Ordinal) && trimmed.Contains("\"", StringComparison.Ordinal))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"""(\d+):(\d+)""");
                    if (match.Success)
                    {
                        var hostPort = match.Groups[1].Value;
                        urls.Add($"http://localhost:{hostPort}");
                        urls.Add($"http://127.0.0.1:{hostPort}");
                    }
                }
            }
        }
        catch { }
        return urls;
    }

    private static IReadOnlyList<string> ExtractUrlsFromPackageJson(string content)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("scripts", out var scripts))
            {
                foreach (var script in scripts.EnumerateObject())
                {
                    if (script.Value.GetString()?.Contains("vite") == true ||
                        script.Value.GetString()?.Contains("next") == true ||
                        script.Value.GetString()?.Contains("react-scripts") == true)
                    {
                        urls.Add("http://localhost:3000");
                        urls.Add("http://localhost:5173");
                        urls.Add("http://localhost:8080");
                    }
                }
            }
            if (doc.RootElement.TryGetProperty("config", out var config))
            {
                if (config.TryGetProperty("port", out var port))
                {
                    var portNum = port.GetInt32();
                    urls.Add($"http://localhost:{portNum}");
                }
            }
        }
        catch { }
        return urls;
    }

    private static IReadOnlyList<string> ExtractUrlsFromEnv(string content)
    {
        var urls = new List<string>();
        try
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#") || !trimmed.Contains("=")) continue;

                var parts = trimmed.Split('=', 2);
                var key = parts[0].Trim().ToUpperInvariant();
                var value = parts.Length > 1 ? parts[1].Trim().Trim('"', '\'') : "";

                if ((key.Contains("PORT") || key.Contains("URL") || key.Contains("HOST")) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    if (int.TryParse(value, out var port))
                    {
                        urls.Add($"http://localhost:{port}");
                        urls.Add($"http://127.0.0.1:{port}");
                    }
                    else if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(value);
                    }
                }
            }
        }
        catch { }
        return urls;
    }

    private static IReadOnlyList<string> GetDefaultFrameworkUrls(string workingDirectory)
    {
        var urls = new List<string>();

        // .NET
        if (Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories).Any())
        {
            urls.Add("http://localhost:5000");
            urls.Add("http://localhost:5001");
            urls.Add("http://localhost:5166");
            urls.Add("http://localhost:7157");
        }

        // Node.js
        if (File.Exists(Path.Combine(workingDirectory, "package.json")))
        {
            urls.Add("http://localhost:3000");
            urls.Add("http://localhost:5173");
            urls.Add("http://localhost:8080");
            urls.Add("http://localhost:4200");
        }

        // Python
        if (File.Exists(Path.Combine(workingDirectory, "requirements.txt")) ||
            File.Exists(Path.Combine(workingDirectory, "pyproject.toml")) ||
            File.Exists(Path.Combine(workingDirectory, "manage.py")))
        {
            urls.Add("http://localhost:8000");
            urls.Add("http://localhost:5000");
        }

        // Go
        if (File.Exists(Path.Combine(workingDirectory, "go.mod")))
        {
            urls.Add("http://localhost:8080");
            urls.Add("http://localhost:8000");
        }

        return urls;
    }
}