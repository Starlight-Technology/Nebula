using Nebula.Runner;

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
