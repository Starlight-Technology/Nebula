using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent;
using Nebula.App.Shared.Pages;
using Nebula.Core.Agent;
using Nebula.Core.Execution;
using Nebula.Core.Interactions;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.App.Test;

public sealed class GitPageTests : HomePageTestContext
{
    [Fact]
    public void revert_file_requires_confirmation_before_calling_service()
    {
        var git = new FakeGitService
        {
            Status = new GitStatusResult(
                true,
                "feature/revert",
                true,
                ["staged.cs"],
                ["unstaged.cs"],
                [],
                null),
            Branches = new GitBranchResult(
                true,
                "feature/revert",
                ["feature/revert"],
                null)
        };

        RegisterPageServices(new FakeManager(), new FakeLlamaClient());
        Services.AddSingleton<IGitService>(git);
        Services.AddSingleton<IPullRequestService>(new FakePullRequestService());
        Services.AddSingleton<IShellExecutor>(new FakeShellExecutor());

        var component = Render<Git>();

        component.WaitForAssertion(() =>
            Assert.Contains("unstaged.cs", component.Markup));

        var revertButtons = component.FindAll("button")
            .Where(button => button.TextContent.Trim() == "Reverter")
            .ToList();
        Assert.Equal(2, revertButtons.Count);

        revertButtons[0].Click();

        Assert.Contains("Confirmar", component.Markup);
        Assert.Empty(git.RevertedFiles);

        var confirm = component.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Confirmar");
        confirm.Click();

        component.WaitForAssertion(() =>
        {
            var revert = Assert.Single(git.RevertedFiles);
            Assert.Equal("staged.cs", revert.FilePath);
            Assert.True(revert.Staged);
        });
    }

    [Fact]
    public void revert_file_is_disabled_on_protected_branch()
    {
        var git = new FakeGitService
        {
            Status = new GitStatusResult(
                true,
                "main",
                true,
                [],
                ["unstaged.cs"],
                [],
                null),
            Branches = new GitBranchResult(
                true,
                "main",
                ["main"],
                null)
        };

        RegisterPageServices(new FakeManager(), new FakeLlamaClient());
        Services.AddSingleton<IGitService>(git);
        Services.AddSingleton<IPullRequestService>(new FakePullRequestService());
        Services.AddSingleton<IShellExecutor>(new FakeShellExecutor());

        var component = Render<Git>();

        component.WaitForAssertion(() =>
            Assert.Contains("unstaged.cs", component.Markup));

        var revertButton = component.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Reverter");

        Assert.True(revertButton.HasAttribute("disabled"));
    }

    [Fact]
    public void pull_request_uses_current_branch_and_default_target()
    {
        var git = new FakeGitService
        {
            Status = new GitStatusResult(
                true,
                "feature/revert",
                false,
                [],
                [],
                [],
                null),
            Branches = new GitBranchResult(
                true,
                "feature/revert",
                ["feature/revert", "main"],
                null)
        };
        var pullRequests = new FakePullRequestService();

        RegisterPageServices(new FakeManager(), new FakeLlamaClient());
        Services.AddSingleton<IGitService>(git);
        Services.AddSingleton<IPullRequestService>(pullRequests);
        Services.AddSingleton<IShellExecutor>(new FakeShellExecutor());

        var component = Render<Git>();

        component.WaitForAssertion(() =>
            Assert.Contains("Criar Pull Request", component.Markup));

        var inputs = component.FindAll(".nebula-git-pr input");
        Assert.Equal("feature/revert", inputs[1].GetAttribute("value"));
        Assert.Equal("main", inputs[2].GetAttribute("value"));

        inputs[0].Input("Add selective revert");

        var createButton = component.FindAll("button")
            .Single(button =>
                button.TextContent.Contains(
                    "Criar Pull Request",
                    StringComparison.Ordinal));
        Assert.False(createButton.HasAttribute("disabled"));

        createButton.Click();

        component.WaitForAssertion(() =>
        {
            Assert.NotNull(pullRequests.LastRequest);
            Assert.Equal(
                "feature/revert",
                pullRequests.LastRequest.SourceBranch);
            Assert.Equal("main", pullRequests.LastRequest.TargetBranch);
            Assert.Equal(
                PullRequestProvider.GitHub,
                pullRequests.LastRequest.Provider);
            Assert.Contains(
                "https://github.com/owner/repo/pull/1",
                component.Markup);
        });
    }

    private sealed class FakePullRequestService : IPullRequestService
    {
        public PullRequestRequest? LastRequest { get; private set; }

        public string DefaultTargetBranch => "main";

        public bool IsProviderConfigured(PullRequestProvider provider) => true;

        public Task<PullRequestResult> CreatePullRequestAsync(
            PullRequestRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new PullRequestResult(
                true,
                "https://github.com/owner/repo/pull/1",
                1,
                null));
        }

        public Task<GitRepositoryInfo> GetRepositoryInfoAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitRepositoryInfo(
                true,
                "git@github.com:owner/repo.git",
                "owner",
                "repo",
                "GitHub"));
        }

        public Task<bool> HasUncommittedChangesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> IsBranchPushedAsync(
            string workingDirectory,
            string branchName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeGitService : IGitService
    {
        public GitStatusResult Status { get; set; } =
            GitStatusResult.NotRepository();

        public GitBranchResult Branches { get; set; } =
            new(false, null, [], null);

        public List<(string FilePath, bool Staged)> RevertedFiles { get; } = [];

        public Task<GitStatusResult> GetStatusAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Status);
        }

        public Task<GitBranchResult> GetBranchesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Branches);
        }

        public Task<GitCommitResult> CommitAsync(
            string workingDirectory,
            string message,
            bool amend = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitCommitResult(true, "abc1234", null));
        }

        public Task<GitCommitResult> CommitAllAsync(
            string workingDirectory,
            string message,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitCommitResult(true, "abc1234", null));
        }

        public Task<GitRevertResult> RevertAsync(
            string workingDirectory,
            string commitHash,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitRevertResult(true, "def5678", null));
        }

        public Task<GitFileRevertResult> RevertFileAsync(
            string workingDirectory,
            string relativePath,
            bool staged = false,
            string? commitHash = null,
            CancellationToken cancellationToken = default)
        {
            RevertedFiles.Add((relativePath, staged));
            return Task.FromResult(
                new GitFileRevertResult(true, relativePath, commitHash, null));
        }

        public Task<GitDiffResult> GetDiffAsync(
            string workingDirectory,
            bool staged = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new GitDiffResult(true, [], "diff --stat", null));
        }

        public Task<GitLogResult> GetLogAsync(
            string workingDirectory,
            int count = 10,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitLogResult(true, [], null));
        }

        public Task<GitRemoteResult> GetRemotesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitRemoteResult(true, [], null));
        }

        public Task<string> GenerateCommitMessageAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("feat: update files");
        }

        public Task<bool> IsProtectedBranchAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                GitProtectedBranches.IsProtected(Status.CurrentBranch));
        }

        public Task<string> GetCurrentBranchAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Status.CurrentBranch);
        }
    }

    private sealed class FakeShellExecutor : IShellExecutor
    {
        public Task<string> RunCommandAsync(string command)
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> RunCommandAsync(
            string command,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FakeManager : IManager
    {
        public Guid ActiveConversationId { get; private set; } = Guid.NewGuid();

        public Task<string> ManageResponse(UserMessage message)
        {
            return Task.FromResult(message.Content);
        }

        public Task<ConversationTurn> ManageConversationAsync(UserMessage message)
        {
            return Task.FromResult(new ConversationTurn());
        }

        public Task<ConversationTurn> ManageConversationAsync(
            UserMessage message,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConversationTurn());
        }

        public Task<ConversationTurn> RunApprovedCommandAsync(
            CommandExecution command,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConversationTurn());
        }

        public Task<ConversationTurn> RunApprovedCommandAsync(
            CommandExecution command,
            IProgress<ConversationTurn>? progress,
            ApprovalScope scope,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConversationTurn());
        }

        public Task<ConversationTurn> ResumeTaskAsync(
            AgentRun run,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConversationTurn());
        }

        public Guid StartNewConversation()
        {
            ActiveConversationId = Guid.NewGuid();
            return ActiveConversationId;
        }

        public Guid SelectConversation(Guid conversationId)
        {
            ActiveConversationId = conversationId;
            return conversationId;
        }

        public Task<string> GenerateCommandSteps(string userRequest)
        {
            return Task.FromResult(string.Empty);
        }

        public Task<bool> VerifyCommandCorrectAsync(Command command)
        {
            return Task.FromResult(true);
        }

        public Task<bool> VerifyCommandSafetyAsync(Command command)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeLlamaClient : ILlamaClient
    {
        public string LlamaUrl { get; set; } = "http://localhost:11434/api/generate";

        public string SelectedModel => "qwen3:8b";

        public Task<string> GetResponseAsync(string prompt)
        {
            return Task.FromResult(prompt);
        }

        public Task<string> GetResponseAsync(
            string prompt,
            IProgress<LlamaStreamUpdate>? progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(prompt);
        }

        public Task<LlamaRuntimeState> GetRuntimeStateAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlamaRuntimeState
            {
                SelectedModel = SelectedModel,
                IsAvailable = true,
                SelectedModelInstalled = true
            });
        }

        public Task<string?> GetServerVersionAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("0.24.0");
        }

        public Task<IReadOnlyList<LlamaModelInfo>> GetInstalledModelsAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlamaModelInfo>>([]);
        }

        public Task<bool> SelectModelAsync(
            string modelName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> EnsureInstalledModelSelectedAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<LlamaPullResult> PullModelAsync(
            string modelName,
            bool activateAfterInstall = false,
            IProgress<LlamaPullProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlamaPullResult
            {
                Success = true,
                ModelName = modelName
            });
        }
    }
}
