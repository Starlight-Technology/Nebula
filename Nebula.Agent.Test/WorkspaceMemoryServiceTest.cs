using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Execution;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Services.Memory;

namespace Nebula.Agent.Test;

public sealed class WorkspaceMemoryServiceTest
{
    [Fact]
    public async Task record_successful_command_must_store_working_command()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);
        var execution = CreateExecution(
            "Get-ChildItem",
            exitCode: 0,
            output: "Directory logged");

        await service.RecordSuccessfulCommandAsync("ws", execution);

        var entries = await store.GetRecentAsync("ws");
        Assert.Contains(entries, entry => entry.Kind == WorkspaceMemoryKind.WorkingCommand);
    }

[Fact]
    public async Task record_successful_command_must_detect_ports()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);
        var execution = CreateExecution(
            "node server.js",
            exitCode: 0,
            output: "Listening on http://localhost:8080");

        await service.RecordSuccessfulCommandAsync("ws1", execution);

        var entries = await store.GetRecentAsync("ws1");
        Assert.Contains(entries, entry =>
            entry.Kind == WorkspaceMemoryKind.UsedPort && entry.Value == "8080");
    }

    [Fact]
    public async Task record_failed_command_must_not_store()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);
        var execution = CreateExecution("dotnet build", exitCode: 1);

        await service.RecordSuccessfulCommandAsync("ws1", execution);

        var entries = await store.GetRecentAsync("ws1");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task build_summary_must_include_known_commands_and_ports()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);
        await service.RecordSuccessfulCommandAsync(
            "ws1",
            CreateExecution("dotnet build", exitCode: 0, output: "http://localhost:5000"));

        var summary = await service.BuildSummaryAsync("ws1");

        Assert.Contains("dotnet build", summary);
        Assert.Contains("5000", summary);
    }

    [Fact]
    public async Task build_summary_must_be_empty_for_unknown_workspace()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);

        var summary = await service.BuildSummaryAsync("unknown-ws");

        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public async Task record_working_strategy_must_store_strategy_entry()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);

        await service.RecordWorkingStrategyAsync(
            "ws1",
            "DotNet",
            "error CS0103",
            "dotnet build -c Release");

        var entries = await store.GetRecentAsync("ws1");
        var strategy = Assert.Single(entries, entry => entry.Kind == WorkspaceMemoryKind.Strategy);
        Assert.Equal("dotnet|error cs0103", strategy.Key);
        Assert.Equal("dotnet build -c Release", strategy.Value);
    }

    [Fact]
    public async Task record_working_strategy_must_deduplicate()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);

        await service.RecordWorkingStrategyAsync("ws1", "DotNet", "build error", "dotnet build");
        await service.RecordWorkingStrategyAsync("ws1", "DotNet", "build error", "dotnet build");

        var entries = await store.GetRecentAsync("ws1");
        var strategies = entries.Where(entry => entry.Kind == WorkspaceMemoryKind.Strategy).ToList();
        Assert.Single(strategies);
    }

    [Fact]
    public async Task build_strategies_summary_must_include_known_strategies()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);
        await service.RecordWorkingStrategyAsync(
            "ws1",
            "DotNet",
            "XML parse failure",
            "dotnet restore --force-evaluate");

        var summary = await service.BuildStrategiesSummaryAsync("ws1", "DotNet");

        Assert.Contains("DotNet", summary);
        Assert.Contains("dotnet restore --force-evaluate", summary);
    }

    [Fact]
    public async Task build_strategies_summary_must_be_empty_for_unknown_workspace()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var service = CreateService(store);

        var summary = await service.BuildStrategiesSummaryAsync("unknown-ws", "DotNet");

        Assert.Equal(string.Empty, summary);
    }

    private static WorkspaceMemoryService CreateService(
        IWorkspaceMemoryStore store) =>
        new(store, new Mock<ILogger>().Object);

    private static CommandExecution CreateExecution(
        string command,
        int? exitCode,
        string output = "") =>
        new()
        {
            OperationKind = OperationKind.TerminalCommand,
            Run = command,
            ExitCode = exitCode,
            StandardOutput = output,
            WorkingDirectory = "ws1"
        };
}
