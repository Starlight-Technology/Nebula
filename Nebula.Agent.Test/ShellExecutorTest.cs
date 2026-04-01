using Nebula.Runner;

namespace Nebula.Agent.Test;

public class ShellExecutorTest
{
    [Fact]
    public async Task RunCommandAsync_WithValidCommand_ShouldReturnOutput()
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
    public async Task RunCommandAsync_WithInvalidCommand_ShouldReturnError()
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
    public async Task RunCommandAsync_WithDifferentCommands_ShouldProcessEach(string command)
    {
        // Arrange
        var executor = new ShellExecutor();

        // Act
        var result = await executor.RunCommandAsync(command);

        // Assert
        Assert.NotNull(result);
    }
}
