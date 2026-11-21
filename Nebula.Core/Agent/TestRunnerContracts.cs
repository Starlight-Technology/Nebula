namespace Nebula.Core.Agent;

public sealed record TestRunRequest(
    string WorkingDirectory,
    IReadOnlyList<string> ChangedFiles,
    TestFramework Framework,
    bool RunAllIfNoMapping = false,
    int TimeoutSeconds = 300);

public sealed record TestRunResult(
    bool Success,
    int TotalTests,
    int Passed,
    int Failed,
    int Skipped,
    double DurationSeconds,
    IReadOnlyList<TestResult> Tests,
    string? Output,
    string? Error);

public sealed record TestResult(
    string Name,
    string ClassName,
    string MethodName,
    TestOutcome Outcome,
    double DurationSeconds,
    string? ErrorMessage,
    string? StackTrace);

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    None
}

public enum TestFramework
{
    Unknown,
    DotNet,
    NodeJs,
    Python,
    Java,
    Go
}

public sealed record TestProjectMapping(
    string ProjectPath,
    string TestProjectPath,
    TestFramework Framework,
    IReadOnlyList<string> SourcePatterns);

public interface ITestRunnerService
{
    Task<TestRunResult> RunTargetedTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default);
    Task<TestRunResult> RunAllTestsAsync(string workingDirectory, TestFramework framework, int timeoutSeconds = 300, CancellationToken cancellationToken = default);
    Task<TestFramework> DetectFrameworkAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestProjectMapping>> DiscoverTestProjectsAsync(string workingDirectory, CancellationToken cancellationToken = default);
    IReadOnlyList<string> MapChangedFilesToTests(IReadOnlyList<string> changedFiles, IReadOnlyList<TestProjectMapping> mappings);
}