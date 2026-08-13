using Microsoft.EntityFrameworkCore;

using Nebula.Agent.Data;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresCommandRepositoryTest
{
    [Fact]
    public async Task save_must_round_trip_full_execution_details()
    {
        var context = CreateContext();
        var repository = new PostgresCommandRepository(context);
        var command = new StoredCommand
        {
            RequestId = Guid.NewGuid(),
            Objective = "List files",
            Command = "Get-ChildItem",
            OsType = "Windows",
            WorkingDirectory = "C:\\work",
            Shell = "powershell",
            SafetyDecision = "Allow",
            ApprovedByUser = true,
            AutoApproved = false,
            Required = true,
            ExecutedAt = DateTimeOffset.UtcNow
        };

        var saved = await repository.SaveAsync(command);
        var loaded = await repository.GetByIdAsync(saved.Id);

        Assert.NotNull(loaded);
        Assert.Equal(command.Command, loaded.Command);
        Assert.Equal("C:\\work", loaded.WorkingDirectory);
        Assert.Equal("powershell", loaded.Shell);
        Assert.Equal("Allow", loaded.SafetyDecision);
        Assert.True(loaded.ApprovedByUser);
        Assert.False(loaded.AutoApproved);
        Assert.NotNull(loaded.ExecutedAt);
    }

    [Fact]
    public async Task update_execution_details_must_persist_output_and_exit_code()
    {
        var context = CreateContext();
        var repository = new PostgresCommandRepository(context);
        var saved = await repository.SaveAsync(new StoredCommand
        {
            RequestId = Guid.NewGuid(),
            Objective = "Run tests",
            Command = "dotnet test",
            OsType = "Windows"
        });

        var updated = await repository.UpdateExecutionDetailsAsync(
            saved.Id,
            executed: true,
            result: "Passed! 200 passed.",
            exitCode: 0,
            standardOutput: "Passed! 200 passed.",
            standardError: null,
            executedAt: DateTimeOffset.UtcNow);

        Assert.True(updated.Executed);
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal("Passed! 200 passed.", updated.StandardOutput);

        var loaded = await repository.GetByIdAsync(saved.Id);
        Assert.True(loaded!.Executed);
        Assert.Equal(0, loaded.ExitCode);
        Assert.Equal("Passed! 200 passed.", loaded.StandardOutput);
    }

    [Fact]
    public async Task get_approved_commands_must_return_only_approved_commands_ordered_by_recency()
    {
        var context = CreateContext();
        var repository = new PostgresCommandRepository(context);
        await repository.SaveAsync(new StoredCommand
        {
            RequestId = Guid.NewGuid(),
            Objective = "Not approved",
            Command = "dir",
            OsType = "Windows"
        });
        var older = await repository.SaveAsync(new StoredCommand
        {
            RequestId = Guid.NewGuid(),
            Objective = "Manual approval",
            Command = "Get-ChildItem",
            OsType = "Windows",
            ApprovedByUser = true,
            ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        var newer = await repository.SaveAsync(new StoredCommand
        {
            RequestId = Guid.NewGuid(),
            Objective = "Auto approval",
            Command = "dotnet build",
            OsType = "Windows",
            AutoApproved = true,
            ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });

        var approved = (await repository.GetApprovedCommandsAsync()).ToList();

        Assert.Equal(2, approved.Count);
        Assert.Equal(newer.Id, approved[0].Id);
        Assert.Equal(older.Id, approved[1].Id);
        Assert.All(approved, command => Assert.True(command.ApprovedByUser || command.AutoApproved));
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-commands-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}
