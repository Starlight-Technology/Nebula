using Microsoft.EntityFrameworkCore;

using Nebula.Core.Learning;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresKnowledgeStoreTest
{
    [Fact]
    public async Task save_new_item_with_experiment_must_persist()
    {
        var context = CreateContext();
        var store = new PostgresKnowledgeStore(context);
        var (item, experiment) = CreateCommandLearning();

        await store.SaveAsync(item, [], [], experiment);

        var loaded = await store.FindByHashAsync(item.Hash);
        Assert.NotNull(loaded);
        Assert.Equal(item.Topic, loaded.Item.Topic);
        Assert.Single(loaded.Experiments);
        Assert.True(loaded.Experiments[0].Success);
    }

    [Fact]
    public async Task update_existing_item_must_insert_new_experiment_not_update()
    {
        var context = CreateContext();
        var store = new PostgresKnowledgeStore(context);
        var (item, experiment) = CreateCommandLearning();

        await store.SaveAsync(item, [], [], experiment);

        var observed = new List<EntityState>();
        context.SavingChanges += (_, _) =>
        {
            observed.AddRange(context.ChangeTracker
                .Entries<Postgres.Context.Entities.KnowledgeExperiment>()
                .Where(entry => entry.State == EntityState.Added)
                .Select(entry => entry.State));
        };

        var updated = Clone(item);
        updated.Summary = "Updated summary";
        updated.VerificationScore = 0.95;
        updated.ObservationCount = 2;
        var updateExperiment = new KnowledgeExperiment
        {
            KnowledgeItemId = item.Id,
            VerificationKind = VerificationKind.SafeExecution,
            CommandExecuted = item.Topic,
            ResolvedCommand = item.Topic,
            ExitCode = 0,
            StdOut = "ok again",
            Success = true,
            EvidenceHash = item.Hash
        };

        await store.SaveAsync(updated, [], [], updateExperiment);

        Assert.Contains(EntityState.Added, observed);

        var loaded = await store.FindByHashAsync(item.Hash);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Experiments);
        Assert.Equal("ok again", loaded.Experiments[0].StdOut);
    }

    private static (KnowledgeItem Item, KnowledgeExperiment Experiment) CreateCommandLearning()
    {
        var command = $"repro-{Guid.NewGuid():N}";
        var content = $"Command: {command}\nResolved: {command}\nExitCode: 0";
        var hash = Nebula.Services.Learning.KnowledgeHash.Create(
            KnowledgeDomain.General,
            command,
            content,
            content);

        var item = new KnowledgeItem
        {
            Domain = KnowledgeDomain.General,
            Kind = KnowledgeItemKind.Command,
            Topic = command,
            Title = $"Command: {command}",
            Content = content,
            Summary = "Learned from successful agent execution.",
            Tags = "command,auto-learned,success",
            NormalizedCommand = command,
            Language = "shell",
            OS = "Win32NT",
            Shell = "powershell",
            SourceUrl = "session://repro/step-1",
            SourceType = LearningSourceType.ExistingKnowledgeBase,
            SourceName = "LearningFromExecutionService",
            RiskLevel = KnowledgeRiskLevel.Safe,
            ConfidenceScore = 0.85,
            SourceScore = 0.90,
            ClassificationConfidence = 0.85,
            SafetyScore = 1.0,
            VerificationScore = 0.85,
            Hash = hash,
            IsExecutableAdvice = true,
            IsDangerousInstruction = false,
            IsValidated = true,
            ValidationNotes = "Learned from successful agent execution.",
            UpdatedAt = DateTimeOffset.UtcNow,
            FinalScore = 0.9
        };

        var experiment = new KnowledgeExperiment
        {
            KnowledgeItemId = item.Id,
            VerificationKind = VerificationKind.SafeExecution,
            CommandExecuted = command,
            ResolvedCommand = command,
            ExitCode = 0,
            StdOut = "ok",
            Success = true,
            EvidenceHash = hash
        };

        return (item, experiment);
    }

    private static KnowledgeItem Clone(KnowledgeItem item)
    {
        var clone = new KnowledgeItem();
        foreach (var property in typeof(KnowledgeItem).GetProperties())
        {
            property.SetValue(clone, property.GetValue(item));
        }

        return clone;
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-knowledge-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}
