using Moq;

using Nebula.Core.Agent;
using Nebula.Core.Operations;
using Nebula.Runner;

using DeterministicVerificationService = Nebula.Agent.Application.DeterministicVerificationService;

namespace Nebula.Agent.Test.Safety;

public sealed class DeterministicVerificationServiceTest
{
    [Fact]
    public async Task verify_must_be_not_applicable_when_no_code_was_touched()
    {
        var service = CreateService(
            stackKind: WorkspaceStackKind.DotNet,
            exitCode: 0,
            output: "build ok");

        var result = await service.VerifyAsync(
            "C:\\repo",
            [TerminalOnlyEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.NotApplicable, result.Verdict);
        Assert.Null(result.Command);
    }

    [Fact]
    public async Task verify_must_be_not_applicable_when_stack_is_unknown()
    {
        var service = CreateService(
            stackKind: WorkspaceStackKind.Unknown,
            exitCode: 0,
            output: string.Empty);

        var result = await service.VerifyAsync(
            "C:\\empty",
            [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.NotApplicable, result.Verdict);
    }

    [Fact]
    public async Task verify_must_pass_when_build_succeeds()
    {
        var service = CreateService(
            stackKind: WorkspaceStackKind.DotNet,
            exitCode: 0,
            output: "Build succeeded.");

        var result = await service.VerifyAsync(
            "C:\\repo",
            [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Passed, result.Verdict);
        Assert.Equal(WorkspaceStackKind.DotNet.ToString(), result.Tool);
        Assert.NotNull(result.Command);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task verify_must_fail_when_build_fails()
    {
        var service = CreateService(
            stackKind: WorkspaceStackKind.DotNet,
            exitCode: 1,
            output: "error CS1002: ; expected");

        var result = await service.VerifyAsync(
            "C:\\repo",
            [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Failed, result.Verdict);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("CS1002", result.Output);
    }

    [Fact]
    public async Task verify_must_prefer_test_command_over_build_for_python()
    {
        var executor = new Mock<IShellExecutor>();
        string? capturedCommand = null;
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((command, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = "C:\\repo",
                    StandardOutput = "1 passed",
                    ExitCode = 0
                });
            });

        var detector = new WorkspaceDetectorForTesting(
            WorkspaceStackKind.Python,
            testCommand: "python -m pytest",
            parseCommand: "python -m py_compile \"app.py\"");
        var service = new DeterministicVerificationService(
            detector,
            executor.Object,
            new TestLogger());

        var result = await service.VerifyAsync("C:\\repo", [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Passed, result.Verdict);
        Assert.Equal("python -m pytest", capturedCommand);
    }

    [Fact]
    public async Task verify_must_return_error_when_executor_throws()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dotnet not found"));

        var service = new DeterministicVerificationService(
            new WorkspaceDetectorKind(
                WorkspaceStackKind.DotNet,
                buildCommand: "dotnet build \"App.csproj\"",
                testCommand: null,
                parseCommand: null),
            executor.Object,
            new TestLogger());

        var result = await service.VerifyAsync("C:\\repo", [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Error, result.Verdict);
        Assert.Contains("dotnet not found", result.Output);
    }

    [Fact]
    public async Task verify_must_fail_when_lint_fails_after_build_passes()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((command, _, _) =>
                Task.FromResult(new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = "C:\\repo",
                    StandardOutput = command.Contains("format", StringComparison.OrdinalIgnoreCase)
                        ? "1 file would be reformatted"
                        : "Build succeeded.",
                    ExitCode = command.Contains("format", StringComparison.OrdinalIgnoreCase) ? 1 : 0
                }));

        var service = new DeterministicVerificationService(
            new WorkspaceDetectorWithLint(
                WorkspaceStackKind.DotNet,
                buildCommand: "dotnet build \"App.csproj\"",
                lintCommand: "dotnet format --verify-no-changes --no-restore"),
            executor.Object,
            new TestLogger());

        var result = await service.VerifyAsync("C:\\repo", [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Failed, result.Verdict);
        Assert.Contains("format", result.Command);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Lint/format check failed", result.Output);
        Assert.Contains("would be reformatted", result.Output);
    }

    [Fact]
    public async Task verify_must_pass_when_lint_passes_after_build()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    StandardOutput = "ok",
                    ExitCode = 0
                });

        var service = new DeterministicVerificationService(
            new WorkspaceDetectorWithLint(
                WorkspaceStackKind.DotNet,
                buildCommand: "dotnet build \"App.csproj\"",
                lintCommand: "dotnet format --verify-no-changes --no-restore"),
            executor.Object,
            new TestLogger());

        var result = await service.VerifyAsync("C:\\repo", [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Passed, result.Verdict);
        Assert.Equal("dotnet format --verify-no-changes --no-restore", result.Command);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task verify_must_skip_lint_when_stack_has_no_lint_command()
    {
        var executor = new Mock<IShellExecutor>();
        string? capturedCommand = null;
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((command, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = "C:\\repo",
                    StandardOutput = "Build succeeded.",
                    ExitCode = 0
                });
            });

        var service = new DeterministicVerificationService(
            new WorkspaceDetectorByKind(WorkspaceStackKind.DotNet),
            executor.Object,
            new TestLogger());

        var result = await service.VerifyAsync("C:\\repo", [FileWriteEvidence()]);

        Assert.Equal(DeterministicVerificationVerdict.Passed, result.Verdict);
        Assert.Equal("dotnet build \"App.csproj\"", capturedCommand);
        Assert.DoesNotContain("format", capturedCommand);
    }

    private static DeterministicVerificationService CreateService(
        WorkspaceStackKind stackKind,
        int exitCode,
        string output)
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    StandardOutput = output,
                    ExitCode = exitCode
                });

        return new DeterministicVerificationService(
            new WorkspaceDetectorByKind(stackKind),
            executor.Object,
            new TestLogger());
    }

    private static ExecutionEvidence TerminalOnlyEvidence()
    {
        return new ExecutionEvidence(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            OperationKind.TerminalCommand,
            "ls",
            null,
            null,
            Executed: true,
            ExitCode: 0,
            StdOut: "listing",
            StdErr: string.Empty,
            Success: true,
            DateTimeOffset.UtcNow);
    }

    private static ExecutionEvidence FileWriteEvidence()
    {
        return new ExecutionEvidence(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            OperationKind.FileWrite,
            "write-file",
            "C:\\repo\\src\\App.cs",
            "abc123",
            Executed: true,
            ExitCode: 0,
            StdOut: string.Empty,
            StdErr: string.Empty,
            Success: true,
            DateTimeOffset.UtcNow);
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

    private sealed class WorkspaceDetectorWithLint : IWorkspaceStackDetector
    {
        private readonly WorkspaceStack stack;

        public WorkspaceDetectorWithLint(
            WorkspaceStackKind kind,
            string? buildCommand,
            string? lintCommand)
        {
            stack = new WorkspaceStack(kind, null, buildCommand, null, null, lintCommand);
        }

        public WorkspaceStack Detect(string workingDirectory) => stack;
    }

    private sealed class WorkspaceDetectorByKind : IWorkspaceStackDetector
    {
        private readonly WorkspaceStackKind kind;

        public WorkspaceDetectorByKind(WorkspaceStackKind kind)
        {
            this.kind = kind;
        }

        public WorkspaceStack Detect(string workingDirectory)
        {
            var command = kind switch
            {
                WorkspaceStackKind.DotNet => "dotnet build \"App.csproj\"",
                WorkspaceStackKind.Node => "npm test",
                WorkspaceStackKind.Python => "python -m pytest",
                _ => null
            };
            return new WorkspaceStack(kind, null, command, command, command);
        }
    }

    private sealed class WorkspaceDetectorForTesting : IWorkspaceStackDetector
    {
        private readonly WorkspaceStack stack;

        public WorkspaceDetectorForTesting(
            WorkspaceStackKind kind,
            string? testCommand,
            string? parseCommand)
        {
            stack = new WorkspaceStack(kind, null, null, testCommand, parseCommand);
        }

        public WorkspaceStack Detect(string workingDirectory) => stack;
    }

    private sealed class WorkspaceDetectorKind : IWorkspaceStackDetector
    {
        private readonly WorkspaceStack stack;

        public WorkspaceDetectorKind(
            WorkspaceStackKind kind,
            string? buildCommand,
            string? testCommand,
            string? parseCommand)
        {
            stack = new WorkspaceStack(kind, null, buildCommand, testCommand, parseCommand);
        }

        public WorkspaceStack Detect(string workingDirectory) => stack;
    }
}