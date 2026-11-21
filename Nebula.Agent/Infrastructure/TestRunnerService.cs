using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class TestRunnerService(
    IShellExecutor executor,
    ILogger logger) : Core.Agent.ITestRunnerService
{
    public async Task<TestRunResult> RunTargetedTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default)
    {
        var mappings = await DiscoverTestProjectsAsync(request.WorkingDirectory, cancellationToken);
        var testFilter = MapChangedFilesToTests(request.ChangedFiles, mappings);

        if (testFilter.Count == 0)
        {
            if (request.RunAllIfNoMapping)
            {
                return await RunAllTestsAsync(request.WorkingDirectory, request.Framework, request.TimeoutSeconds, cancellationToken);
            }
            return new TestRunResult(
                true, 0, 0, 0, 0, 0, [],
                "Nenhum teste mapeado para os arquivos alterados. Use RunAllIfNoMapping=true para executar todos.",
                null);
        }

        return request.Framework switch
        {
            TestFramework.DotNet => await RunDotNetTestsAsync(request.WorkingDirectory, testFilter, request.TimeoutSeconds, cancellationToken),
            TestFramework.NodeJs => await RunNodeTestsAsync(request.WorkingDirectory, testFilter, request.TimeoutSeconds, cancellationToken),
            TestFramework.Python => await RunPythonTestsAsync(request.WorkingDirectory, testFilter, request.TimeoutSeconds, cancellationToken),
            _ => new TestRunResult(false, 0, 0, 0, 0, 0, [], null, $"Framework {request.Framework} nao suportado")
        };
    }

    public async Task<TestRunResult> RunAllTestsAsync(string workingDirectory, TestFramework framework, int timeoutSeconds = 300, CancellationToken cancellationToken = default)
    {
        return framework switch
        {
            TestFramework.DotNet => await RunDotNetTestsAsync(workingDirectory, [], timeoutSeconds, cancellationToken),
            TestFramework.NodeJs => await RunNodeTestsAsync(workingDirectory, [], timeoutSeconds, cancellationToken),
            TestFramework.Python => await RunPythonTestsAsync(workingDirectory, [], timeoutSeconds, cancellationToken),
            _ => new TestRunResult(false, 0, 0, 0, 0, 0, [], null, $"Framework {framework} nao suportado")
        };
    }

    public async Task<TestFramework> DetectFrameworkAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return TestFramework.Unknown;
        }

        var files = Directory.GetFiles(workingDirectory, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f).ToLowerInvariant())
            .ToHashSet();

        if (files.Any(f => f.EndsWith(".csproj") || f.EndsWith(".sln") || f.EndsWith(".fsproj") || f.EndsWith(".vbproj")))
            return TestFramework.DotNet;

        if (files.Contains("package.json"))
            return TestFramework.NodeJs;

        if (files.Any(f => f.EndsWith(".py") || f == "pytest.ini" || f == "pyproject.toml" || f == "requirements.txt"))
            return TestFramework.Python;

        if (files.Any(f => f.EndsWith(".java") || f == "pom.xml" || f == "build.gradle" || f == "build.gradle.kts"))
            return TestFramework.Java;

        if (files.Any(f => f.EndsWith(".go") || f == "go.mod"))
            return TestFramework.Go;

        return TestFramework.Unknown;
    }

    public async Task<IReadOnlyList<TestProjectMapping>> DiscoverTestProjectsAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var mappings = new List<TestProjectMapping>();

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return mappings;
        }

        try
        {
            var csprojFiles = Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories);
            foreach (var csproj in csprojFiles)
            {
                var content = await File.ReadAllTextAsync(csproj, cancellationToken);
                var isTestProject = content.Contains("Microsoft.NET.Test.Sdk") ||
                                   content.Contains("xunit") ||
                                   content.Contains("NUnit") ||
                                   content.Contains("MSTest") ||
                                   Path.GetFileName(csproj).EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetFileName(csproj).EndsWith(".Test.csproj", StringComparison.OrdinalIgnoreCase);

                if (isTestProject)
                {
                    var projectDir = Path.GetDirectoryName(csproj)!;
                    var sourceProject = FindSourceProject(projectDir, csprojFiles);
                    mappings.Add(new TestProjectMapping(
                        sourceProject ?? projectDir,
                        csproj,
                        TestFramework.DotNet,
                        ["*.cs"]));
                }
            }

            var packageJsonFiles = Directory.GetFiles(workingDirectory, "package.json", SearchOption.AllDirectories);
            foreach (var pkgJson in packageJsonFiles)
            {
                var content = await File.ReadAllTextAsync(pkgJson, cancellationToken);
                var pkgDir = Path.GetDirectoryName(pkgJson)!;
                var hasTestScript = content.Contains("\"test\"") && content.Contains("jest") || content.Contains("vitest") || content.Contains("mocha") || content.Contains("cypress") || content.Contains("playwright");
                
                if (hasTestScript)
                {
                    mappings.Add(new TestProjectMapping(
                        pkgDir,
                        pkgJson,
                        TestFramework.NodeJs,
                        ["*.ts", "*.tsx", "*.js", "*.jsx"]));
                }
            }

            var pythonTestDirs = FindPythonTestDirs(workingDirectory);
            foreach (var testDir in pythonTestDirs)
            {
                mappings.Add(new TestProjectMapping(
                    testDir,
                    testDir,
                    TestFramework.Python,
                    ["*.py"]));
            }
        }
        catch (Exception ex)
        {
            logger.Log($"[TEST_RUNNER] Error discovering test projects: {ex.Message}");
        }

        return mappings;
    }

    public IReadOnlyList<string> MapChangedFilesToTests(IReadOnlyList<string> changedFiles, IReadOnlyList<TestProjectMapping> mappings)
    {
        var testFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var changedFile in changedFiles)
        {
            var normalizedChanged = NormalizePath(changedFile);

            foreach (var mapping in mappings)
            {
                var normalizedMapping = NormalizePath(mapping.TestProjectPath);
                var testDir = Path.GetDirectoryName(normalizedMapping)!;

                if (IsFileInOrUnder(normalizedChanged, testDir))
                {
                    testFilters.Add(testDir);
                }
                else
                {
                    foreach (var pattern in mapping.SourcePatterns)
                    {
                        if (MatchesPattern(normalizedChanged, pattern))
                        {
                            testFilters.Add(testDir);
                            break;
                        }
                    }
                }
            }
        }

        return testFilters.ToList();
    }

    private async Task<TestRunResult> RunDotNetTestsAsync(string workingDirectory, IReadOnlyList<string> testFilters, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var filterArg = testFilters.Count > 0
            ? $" --filter \"FullyQualifiedName~{string.Join("|", testFilters.Select(EscapeFilter))}\""
            : "";

        var cmd = $"dotnet test --no-build --logger \"trx;LogFileName=test_results.trx\"{filterArg}";
        
        var result = await RunWithTimeoutAsync(cmd, workingDirectory, timeoutSeconds, cancellationToken);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalSeconds;

        return ParseDotNetTestResult(result, duration);
    }

    private async Task<TestRunResult> RunNodeTestsAsync(string workingDirectory, IReadOnlyList<string> testFilters, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        
        var pkgJson = Path.Combine(workingDirectory, "package.json");
        var testCommand = "npm test";
        
        if (File.Exists(pkgJson))
        {
            var content = await File.ReadAllTextAsync(pkgJson, cancellationToken);
            if (content.Contains("vitest"))
                testCommand = "npx vitest run";
            else if (content.Contains("jest"))
                testCommand = "npx jest";
            else if (content.Contains("mocha"))
                testCommand = "npx mocha";
        }

        var filterArg = testFilters.Count > 0
            ? $" --testPathPattern=\"{string.Join("|", testFilters)}\""
            : "";

        var cmd = $"{testCommand}{filterArg}";
        var result = await RunWithTimeoutAsync(cmd, workingDirectory, timeoutSeconds, cancellationToken);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalSeconds;

        return ParseNodeTestResult(result, duration);
    }

    private async Task<TestRunResult> RunPythonTestsAsync(string workingDirectory, IReadOnlyList<string> testFilters, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        
        var filterArg = testFilters.Count > 0
            ? $" -k \"{string.Join(" or ", testFilters)}\""
            : "";

        var cmd = $"python -m pytest --tb=short -v{filterArg}";
        var result = await RunWithTimeoutAsync(cmd, workingDirectory, timeoutSeconds, cancellationToken);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalSeconds;

        return ParsePythonTestResult(result, duration);
    }

    private async Task<ShellCommandResult> RunWithTimeoutAsync(string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (executor is IDetailedShellExecutor detailedExecutor)
            {
                return await detailedExecutor.RunCommandDetailedAsync(command, workingDirectory, cts.Token);
            }

            var output = await executor.RunCommandAsync(command, cts.Token);
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = output,
                ExitCode = 0,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = string.Empty,
                StandardError = "Timeout: teste excedeu o tempo limite",
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = string.Empty,
                StandardError = ex.Message,
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    private TestRunResult ParseDotNetTestResult(ShellCommandResult result, double duration)
    {
        var tests = new List<TestResult>();
        int passed = 0, failed = 0, skipped = 0;

        if (result.ExitCode == 0)
        {
            passed = ExtractCount(result.StandardOutput, "Passed:");
            failed = ExtractCount(result.StandardOutput, "Failed:");
            skipped = ExtractCount(result.StandardOutput, "Skipped:");
        }
        else
        {
            failed = ExtractCount(result.StandardOutput, "Failed:");
            passed = ExtractCount(result.StandardOutput, "Passed:");
        }

        var total = passed + failed + skipped;

        return new TestRunResult(
            result.ExitCode == 0,
            total,
            passed,
            failed,
            skipped,
            duration,
            tests,
            result.StandardOutput,
            result.ExitCode != 0 ? result.StandardError : null);
    }

    private TestRunResult ParseNodeTestResult(ShellCommandResult result, double duration)
    {
        var tests = new List<TestResult>();
        int passed = 0, failed = 0, skipped = 0;

        var output = result.StandardOutput + result.StandardError;

        if (output.Contains("test suites passed") || output.Contains("tests passed"))
        {
            passed = ExtractCount(output, "passed");
            failed = ExtractCount(output, "failed");
            skipped = ExtractCount(output, "skipped");
        }

        var total = passed + failed + skipped;

        return new TestRunResult(
            result.ExitCode == 0 && failed == 0,
            total,
            passed,
            failed,
            skipped,
            duration,
            tests,
            output,
            result.ExitCode != 0 || failed > 0 ? output : null);
    }

    private TestRunResult ParsePythonTestResult(ShellCommandResult result, double duration)
    {
        var tests = new List<TestResult>();
        int passed = 0, failed = 0, skipped = 0;

        var output = result.StandardOutput + result.StandardError;

        passed = ExtractCount(output, "passed");
        failed = ExtractCount(output, "failed");
        skipped = ExtractCount(output, "skipped");

        var total = passed + failed + skipped;

        return new TestRunResult(
            result.ExitCode == 0 && failed == 0,
            total,
            passed,
            failed,
            skipped,
            duration,
            tests,
            output,
            result.ExitCode != 0 || failed > 0 ? output : null);
    }

    private static int ExtractCount(string output, string keyword)
    {
        try
        {
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (int.TryParse(part, out var count))
                            return count;
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    private static string EscapeFilter(string filter)
    {
        return filter.Replace("\"", "\\\"").Replace("(", "\\(").Replace(")", "\\)");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    private static bool IsFileInOrUnder(string file, string directory)
    {
        var normFile = NormalizePath(file);
        var normDir = NormalizePath(directory);
        return normFile.StartsWith(normDir + Path.DirectorySeparatorChar) || normFile == normDir;
    }

    private static bool MatchesPattern(string file, string pattern)
    {
        if (pattern == "*.*") return true;
        var ext = pattern.TrimStart('*');
        return file.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindSourceProject(string testProjectDir, string[] allCsprojFiles)
    {
        var testProjectName = Path.GetFileNameWithoutExtension(
            Directory.GetFiles(testProjectDir, "*.csproj").FirstOrDefault() ?? "");
        
        var sourceName = testProjectName
            .Replace(".Tests", "")
            .Replace(".Test", "");

        foreach (var csproj in allCsprojFiles)
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name.Equals(sourceName, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(sourceName + ".", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(csproj);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> FindPythonTestDirs(string workingDirectory)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            var testFiles = Directory.GetFiles(workingDirectory, "*test*.py", SearchOption.AllDirectories);
            foreach (var file in testFiles)
            {
                dirs.Add(Path.GetDirectoryName(file)!);
            }

            var pytestIni = Directory.GetFiles(workingDirectory, "pytest.ini", SearchOption.AllDirectories);
            foreach (var file in pytestIni)
            {
                dirs.Add(Path.GetDirectoryName(file)!);
            }
        }
        catch { }

        return dirs.ToList();
    }
}