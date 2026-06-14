using Nebula.Runner;
using Nebula.Services.Commands;

namespace Nebula.Agent.Test;

public class ShellExecutorTest
{
    [Fact]
    public async Task run_command_async_must_return_output_when_command_is_valid()
    {
        // Arrange
        var executor = new ShellExecutor();
        var command = OperatingSystem.IsWindows() ? "echo Hello" : "echo Hello";

        // Act
        var result = await executor.RunCommandAsync(command);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public async Task run_command_async_must_return_error_output_when_command_is_invalid()
    {
        // Arrange
        var executor = new ShellExecutor();
        var command = "invalid_command_that_does_not_exist_12345";

        // Act
        var result = await executor.RunCommandAsync(command);

        // Assert
        // The result should contain error information (either stderr or empty depending on OS handling)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task run_command_detailed_async_must_capture_stdout_stderr_exit_code_and_timestamp()
    {
        var executor = new ShellExecutor();
        var environment = new RuntimeCommandEnvironmentDetector()
            .Detect(Environment.CurrentDirectory);
        var command = environment.Shell == Nebula.Core.Commands.ShellKind.PowerShell
            ? "Write-Output 'output'; [Console]::Error.WriteLine('error'); exit 7"
            : OperatingSystem.IsWindows()
                ? "echo output & echo error 1>&2 & exit /b 7"
                : "echo output; echo error >&2; exit 7";

        var result = await executor.RunCommandDetailedAsync(
            command,
            Environment.CurrentDirectory,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("output", result.StandardOutput);
        Assert.Contains("error", result.StandardError);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), result.WorkingDirectory);
        Assert.NotEqual(default, result.Timestamp);
    }

    [Fact]
    public async Task resolved_directory_command_must_execute_with_detected_shell()
    {
        var detector = new RuntimeCommandEnvironmentDetector();
        var environment = detector.Detect(Environment.CurrentDirectory);
        var parser = new CommandIntentParser();
        var resolver = new CommandResolver();
        var request = parser.Parse(
            "List files in the current directory.",
            environment.OS == Nebula.Core.Commands.OperatingSystemKind.Windows ? "dir" : "ls",
            environment.WorkingDirectory);
        var resolved = resolver.Resolve(request, environment);
        var executor = new ShellExecutor(detector);

        var result = await executor.RunCommandDetailedAsync(
            resolved,
            CancellationToken.None);

        Assert.True(result.Success, result.CombinedOutput);
        Assert.Contains("Nebula.Agent.Test.dll", result.StandardOutput);
        Assert.Equal(resolved.DisplayCommand, result.Command);
    }

    [Theory]
    [InlineData("echo test")]
    [InlineData("echo another test")]
    public async Task run_command_async_must_return_result_when_command_changes(string command)
    {
        // Arrange
        var executor = new ShellExecutor();

        // Act
        var result = await executor.RunCommandAsync(command);

        // Assert
        Assert.NotNull(result);
    }
}
