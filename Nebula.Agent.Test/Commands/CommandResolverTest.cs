using Nebula.Core.Commands;
using Nebula.Core.Safety;
using Nebula.Services.Commands;
using Nebula.Services.Safety;

namespace Nebula.Agent.Test.Commands;

public sealed class CommandResolverTest
{
    private readonly ICommandIntentParser parser = new CommandIntentParser();
    private readonly ICommandResolver resolver = new CommandResolver();

    [Theory]
    [InlineData("listar arquivos da unidade D", "ls")]
    [InlineData("executar dir no D", "dir")]
    [InlineData("dir D:", "dir D:")]
    [InlineData("mostre os arquivos do D", "ls")]
    public void windows_directory_requests_resolve_to_powershell_and_normalize_drive(
        string userText,
        string rawCommand)
    {
        var environment = WindowsPowerShell();
        var request = parser.Parse(userText, rawCommand, environment.WorkingDirectory);

        var resolved = resolver.Resolve(request, environment);

        Assert.Equal("D", request.RequestedDrive);
        Assert.Equal(@"D:\", request.RequestedPath);
        Assert.Equal("powershell.exe", resolved.FileName);
        Assert.Contains("Get-ChildItem", resolved.DisplayCommand);
        Assert.Contains(@"D:\", resolved.DisplayCommand);
        Assert.DoesNotContain(" ls ", $" {resolved.DisplayCommand} ", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C", "C")]
    [InlineData("C:", "C")]
    [InlineData(@"C:\", "C")]
    [InlineData("unidade C", "C")]
    [InlineData("drive C", "C")]
    public void windows_drive_variants_are_normalized(string input, string expectedDrive)
    {
        var request = parser.Parse(
            $"listar arquivos {input}",
            "dir",
            Environment.CurrentDirectory);

        Assert.Equal(expectedDrive, request.RequestedDrive);
        Assert.Equal($@"{expectedDrive}:\", request.RequestedPath);
    }

    [Fact]
    public void linux_directory_request_resolves_to_ls()
    {
        var environment = new RuntimeCommandEnvironment(
            OperatingSystemKind.Linux,
            ShellKind.Bash,
            "/workspace");
        var request = parser.Parse("listar /tmp", "ls /tmp", environment.WorkingDirectory);

        var resolved = resolver.Resolve(request, environment);

        Assert.Equal("/bin/bash", resolved.FileName);
        Assert.Contains("ls -la", resolved.DisplayCommand);
        Assert.Contains("/tmp", resolved.DisplayCommand);
    }

    [Fact]
    public void windows_ls_is_converted_to_get_child_item()
    {
        var environment = WindowsPowerShell();
        var request = parser.Parse(@"listar D:\", @"ls D:\", environment.WorkingDirectory);

        var resolved = resolver.Resolve(request, environment);

        Assert.Contains("Get-ChildItem", resolved.DisplayCommand);
        Assert.DoesNotContain("ls D:", resolved.DisplayCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task policy_evaluates_the_resolved_windows_command()
    {
        var environment = WindowsPowerShell();
        var request = parser.Parse(
            "listar arquivos da unidade D",
            "ls D:",
            environment.WorkingDirectory);
        var resolved = resolver.Resolve(request, environment);
        var policy = CreatePolicy();

        var decision = await policy.EvaluateAsync(resolved.DisplayCommand);

        Assert.Contains("powershell.exe", resolved.DisplayCommand);
        Assert.Contains("Get-ChildItem", resolved.DisplayCommand);
        Assert.Equal(CommandSafetyDecisionType.Allow, decision.Decision);
    }

    [Fact]
    public void windows_cmd_fallback_uses_dir()
    {
        var environment = new RuntimeCommandEnvironment(
            OperatingSystemKind.Windows,
            ShellKind.Cmd,
            Environment.CurrentDirectory);
        var request = parser.Parse(
            "listar arquivos da unidade D",
            "ls D:",
            environment.WorkingDirectory);

        var resolved = resolver.Resolve(request, environment);

        Assert.Equal("cmd.exe", resolved.FileName);
        Assert.Contains("/c dir", resolved.DisplayCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"D:\", resolved.DisplayCommand);
        Assert.DoesNotContain(" ls ", $" {resolved.DisplayCommand} ", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task windows_rm_rf_is_not_translated_to_delete_and_requires_policy()
    {
        var environment = WindowsPowerShell();
        var request = parser.Parse("remover tudo", "rm -rf /tmp/data", environment.WorkingDirectory);
        var resolved = resolver.Resolve(request, environment);
        var policy = CreatePolicy();

        var decision = await policy.EvaluateAsync(resolved.DisplayCommand);

        Assert.Contains("rm -rf", resolved.DisplayCommand, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(CommandSafetyDecisionType.Allow, decision.Decision);
    }

    [Theory]
    [InlineData(ShellKind.PowerShell, "Get-Location")]
    [InlineData(ShellKind.Cmd, "cd")]
    public void windows_current_directory_uses_shell_catalog(
        ShellKind shell,
        string expectedCommand)
    {
        var environment = new RuntimeCommandEnvironment(
            OperatingSystemKind.Windows,
            shell,
            Environment.CurrentDirectory);
        var request = parser.Parse("mostrar diretorio atual", "pwd", environment.WorkingDirectory);

        var resolved = resolver.Resolve(request, environment);

        Assert.Contains(expectedCommand, resolved.DisplayCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeCommandEnvironment WindowsPowerShell() =>
        new(
            OperatingSystemKind.Windows,
            ShellKind.PowerShell,
            Environment.CurrentDirectory);

    private static ICommandPolicyEngine CreatePolicy()
    {
        var deterministic = new DeterministicCommandClassifier(Environment.CurrentDirectory);
        var unavailableMl = new MlNetCommandClassifier(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"));
        return new CommandPolicyEngine(
            new CompositeCommandClassifier(deterministic, unavailableMl));
    }
}
