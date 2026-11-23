using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class TestRunnerServiceTest
{
    [Fact]
    public void test_run_request_must_default_to_no_timeout()
    {
        var request = new TestRunRequest("/workspace", [], TestFramework.DotNet);

        Assert.Equal(0, request.TimeoutSeconds);
    }

    [Fact]
    public async Task run_all_tests_must_allow_long_runs_by_default()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .Setup(x => x.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken cancellationToken) =>
            {
                await Task.Delay(100, cancellationToken);
                return "Passed: 1";
            });
        var service = new TestRunnerService(executor.Object, new TestLogger());

        var result = await service.RunAllTestsAsync(
            Path.GetTempPath(),
            TestFramework.DotNet);

        Assert.True(result.Success);
        Assert.Equal(1, result.Passed);
    }

    [Fact]
    public async Task run_all_tests_must_honor_explicit_timeout()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .Setup(x => x.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "";
            });
        var service = new TestRunnerService(executor.Object, new TestLogger());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await service.RunAllTestsAsync(
            Path.GetTempPath(),
            TestFramework.DotNet,
            timeoutSeconds: 1,
            guard.Token);

        Assert.False(result.Success);
        Assert.Contains("Timeout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestLogger : Nebula.Agent.ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
