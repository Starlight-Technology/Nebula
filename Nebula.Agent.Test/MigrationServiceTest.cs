using Moq;

using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class MigrationServiceTest
{
    [Fact]
    public async Task create_ef_dry_run_returns_planned_command_without_executing()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new Mock<IShellExecutor>();
        var service = new MigrationService(executor.Object);

        var result = await service.CreateMigrationAsync(
            new MigrationRequest(
                workspace.Path,
                MigrationFramework.EfCore,
                "AddUsers",
                DryRun: true));

        Assert.True(result.Success);
        Assert.Equal("AddUsers", result.MigrationName);
        Assert.Contains(
            "dotnet ef migrations add AddUsers",
            result.Output,
            StringComparison.Ordinal);
        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task create_node_dry_run_returns_planned_command_without_executing()
    {
        using var workspace = new TempTestWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "package.json"),
            """{"dependencies":{"knex":"latest"}}""");
        var executor = new Mock<IShellExecutor>();
        var service = new MigrationService(executor.Object);

        var result = await service.CreateMigrationAsync(
            new MigrationRequest(
                workspace.Path,
                MigrationFramework.NodeJs,
                "add_users",
                DryRun: true));

        Assert.True(result.Success);
        Assert.Contains("npx knex migrate:make add_users", result.Output);
        executor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("AddUsers; rm -rf /")]
    [InlineData("AddUsers && whoami")]
    [InlineData("../AddUsers")]
    public async Task create_migration_rejects_unsafe_names(string migrationName)
    {
        using var workspace = new TempTestWorkspace();
        var service = new MigrationService(new Mock<IShellExecutor>().Object);

        var result = await service.CreateMigrationAsync(
            new MigrationRequest(
                workspace.Path,
                MigrationFramework.EfCore,
                migrationName));

        Assert.False(result.Success);
        Assert.Contains("Migration name", result.Error);
    }

    [Fact]
    public async Task detect_framework_finds_ef_core_projects()
    {
        using var workspace = new TempTestWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);
        var service = new MigrationService(new Mock<IShellExecutor>().Object);

        var framework = await service.DetectFrameworkAsync(workspace.Path);

        Assert.Equal(MigrationFramework.EfCore, framework);
    }
}
