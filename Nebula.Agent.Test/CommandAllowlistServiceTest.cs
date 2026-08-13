using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Memory;
using Nebula.Services.Memory;

namespace Nebula.Agent.Test;

public sealed class CommandAllowlistServiceTest
{
    [Fact]
    public async Task add_then_is_allowed_must_match_normalized_command()
    {
        var service = CreateService();

        await service.AddAsync(@"C:\work", "dotnet build MyApp.sln");

        Assert.True(await service.IsAllowedAsync(@"C:\work", "dotnet build MyApp.sln"));
        Assert.True(await service.IsAllowedAsync(@"C:\work", "  dotnet   build MyApp.sln  "));
        Assert.False(await service.IsAllowedAsync(@"C:\work", "dotnet test MyApp.sln"));
        Assert.False(await service.IsAllowedAsync(@"C:\other", "dotnet build MyApp.sln"));
    }

    [Fact]
    public async Task is_allowed_must_ignore_empty_workspace_or_command()
    {
        var service = CreateService();

        Assert.False(await service.IsAllowedAsync(string.Empty, "dotnet build"));
        Assert.False(await service.IsAllowedAsync(@"C:\work", string.Empty));
    }

    [Fact]
    public async Task add_must_be_idempotent()
    {
        var service = CreateService();

        await service.AddAsync(@"C:\work", "dotnet test");
        await service.AddAsync(@"C:\work", "dotnet test");

        var list = await service.ListAsync(@"C:\work");
        Assert.Single(list);
    }

    [Fact]
    public async Task list_must_return_only_allowlisted_commands_of_workspace()
    {
        var service = CreateService();

        await service.AddAsync(@"C:\work", "dotnet build");
        await service.AddAsync(@"C:\work", "npm run lint");
        await service.AddAsync(@"C:\other", "dotnet test");

        var list = await service.ListAsync(@"C:\work");

        Assert.Equal(2, list.Count);
        Assert.Contains(list, entry => entry.Value == "dotnet build");
        Assert.Contains(list, entry => entry.Value == "npm run lint");
        Assert.DoesNotContain(list, entry => entry.Value == "dotnet test");
    }

    private static CommandAllowlistService CreateService()
    {
        return new CommandAllowlistService(
            new InMemoryWorkspaceMemoryStore(),
            new Mock<ILogger>().Object);
    }
}