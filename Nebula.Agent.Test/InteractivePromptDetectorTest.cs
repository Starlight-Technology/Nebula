using System.Text;

using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class InteractivePromptDetectorTest
{
    private readonly InteractivePromptDetector detector = new();

    [Theory]
    [InlineData("Press any key to continue...")]
    [InlineData("Press ENTER to continue")]
    [InlineData("Continue?")]
    [InlineData("Are you sure you want to continue?")]
    [InlineData("Do you want to proceed?")]
    [InlineData("Overwrite existing file? [y/N]")]
    [InlineData("Install package? [Y/n]")]
    [InlineData("Delete all files? (y/n)")]
    [InlineData("Continue? (yes/no)")]
    [InlineData("Type 'yes' to continue")]
    [InlineData("Enter passphrase:")]
    [InlineData("Password: ")]
    [InlineData("Enter username:")]
    [InlineData("Login:")]
    [InlineData("Select a number:")]
    [InlineData("Select a choice:")]
    [InlineData("More?")]
    public void must_detect_interactive_prompts(string output)
    {
        var builder = new StringBuilder(output);

        Assert.True(detector.EndsWithInteractivePrompt(builder));
    }

    [Theory]
    [InlineData("Press any key to continue... and exit")]
    [InlineData("Build succeeded. 0 errors.")]
    [InlineData("Running test [y/N] syntax in docs")]
    [InlineData("Total: 12 passed, 0 failed")]
    [InlineData("Continue? feature was removed")]
    [InlineData("The password: field is required")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Finished in 1.2s")]
    public void must_not_detect_non_prompts(string output)
    {
        var builder = new StringBuilder(output);

        Assert.False(detector.EndsWithInteractivePrompt(builder));
    }

    [Fact]
    public void must_consider_only_the_last_line()
    {
        var builder = new StringBuilder(
            "Build succeeded.\nSome docs say [y/N] here.\nContinue? ");

        Assert.True(detector.EndsWithInteractivePrompt(builder));
    }

    [Fact]
    public void must_detect_prompt_without_trailing_newline()
    {
        var builder = new StringBuilder("Password: ");

        Assert.True(detector.EndsWithInteractivePrompt(builder));
    }
}
