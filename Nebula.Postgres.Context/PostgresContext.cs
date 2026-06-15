using Microsoft.EntityFrameworkCore;
using Nebula.Postgres.Context.Entities;

namespace Nebula.Postgres.Context;

public class PostgresContext : DbContext
{
    public PostgresContext(DbContextOptions<PostgresContext> options) : base(options)
    {
    }

    public DbSet<Request> Requests { get; set; } = null!;
    public DbSet<StoredCommand> Commands { get; set; } = null!;
    public DbSet<CommandVerification> CommandVerifications { get; set; } = null!;
    public DbSet<ConversationMessage> ConversationMessages { get; set; } = null!;
    public DbSet<ConversationState> ConversationStates { get; set; } = null!;
    public DbSet<KnowledgeItem> KnowledgeItems { get; set; } = null!;
    public DbSet<KnowledgeExperiment> KnowledgeExperiments { get; set; } = null!;
    public DbSet<KnowledgeSource> KnowledgeSources { get; set; } = null!;
    public DbSet<KnowledgeFact> KnowledgeFacts { get; set; } = null!;
    public DbSet<FetchedPageCache> FetchedPageCaches { get; set; } = null!;
    public DbSet<MlModelArtifact> MlModelArtifacts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Request>(b =>
        {
            b.ToTable("requests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Prompt).IsRequired();
            b.Property(x => x.Classification).IsRequired();
            b.Property(x => x.Response);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<StoredCommand>(b =>
        {
            b.ToTable("commands");
            b.HasKey(x => x.Id);
            b.Property(x => x.Objective).IsRequired();
            b.Property(x => x.Command).IsRequired();
            b.Property(x => x.OsType).IsRequired();
            b.Property(x => x.Executed).HasDefaultValue(false);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            b.HasOne(x => x.Request)
                .WithMany(r => r.Commands)
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.RequestId);
            b.HasIndex(x => x.OsType);
            b.HasIndex(x => x.Executed);
            b.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<CommandVerification>(b =>
        {
            b.ToTable("command_verifications");
            b.HasKey(x => x.Id);
            b.Property(x => x.IsCorrect).IsRequired();
            b.Property(x => x.IsSafe).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            b.HasOne(x => x.Command)
                .WithMany()
                .HasForeignKey(x => x.CommandId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.CommandId);
            b.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<ConversationMessage>(b =>
        {
            b.ToTable("conversation_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Role).IsRequired();
            b.Property(x => x.Content).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            b.HasIndex(x => x.ConversationId);
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        });

        modelBuilder.Entity<ConversationState>(b =>
        {
            b.ToTable("conversation_states");
            b.HasKey(x => x.ConversationId);
            b.Property(x => x.Summary);
            b.Property(x => x.CurrentGoal);
            b.Property(x => x.CurrentPlan);
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            b.HasIndex(x => x.UpdatedAt);
        });

        modelBuilder.Entity<KnowledgeItem>(b =>
        {
            b.ToTable("knowledge_items");
            b.HasKey(x => x.Id);
            b.Property(x => x.Domain).HasConversion<string>().IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().IsRequired();
            b.Property(x => x.Topic).IsRequired();
            b.Property(x => x.Title).IsRequired();
            b.Property(x => x.Content).IsRequired();
            b.Property(x => x.Summary).IsRequired();
            b.Property(x => x.Examples).IsRequired();
            b.Property(x => x.Warnings).IsRequired();
            b.Property(x => x.SourceUrl).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            b.HasIndex(x => x.Domain);
            b.HasIndex(x => x.Topic);
            b.HasIndex(x => x.FinalScore);
        });

        modelBuilder.Entity<KnowledgeExperiment>(b =>
        {
            b.ToTable("knowledge_experiments");
            b.HasKey(x => x.Id);
            b.Property(x => x.VerificationKind)
                .HasConversion<string>()
                .IsRequired();
            b.Property(x => x.EvidenceHash).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            b.HasOne(x => x.KnowledgeItem)
                .WithMany(x => x.Experiments)
                .HasForeignKey(x => x.KnowledgeItemId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.KnowledgeItemId);
        });

        modelBuilder.Entity<KnowledgeSource>(b =>
        {
            b.ToTable("knowledge_sources");
            b.HasKey(x => x.Id);
            b.Property(x => x.Url).IsRequired();
            b.Property(x => x.Title).IsRequired();
            b.Property(x => x.Publisher).IsRequired();
            b.Property(x => x.ExtractedContent).IsRequired();
            b.Property(x => x.RetrievedAt).HasDefaultValueSql("now()");

            b.HasOne(x => x.KnowledgeItem)
                .WithMany(x => x.Sources)
                .HasForeignKey(x => x.KnowledgeItemId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.KnowledgeItemId);
            b.HasIndex(x => x.Url);
        });

        modelBuilder.Entity<KnowledgeFact>(b =>
        {
            b.ToTable("knowledge_facts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Fact).IsRequired();
            b.Property(x => x.SourceUrl).IsRequired();

            b.HasOne(x => x.KnowledgeItem)
                .WithMany(x => x.Facts)
                .HasForeignKey(x => x.KnowledgeItemId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.KnowledgeItemId);
            b.HasIndex(x => x.SourceUrl);
        });

        modelBuilder.Entity<FetchedPageCache>(b =>
        {
            b.ToTable("fetched_page_cache");
            b.HasKey(x => x.Url);
            b.Property(x => x.Url).IsRequired();
            b.Property(x => x.Html).IsRequired();
            b.Property(x => x.HtmlHash).IsRequired();
            b.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<MlModelArtifact>(b =>
        {
            b.ToTable("ml_model_artifacts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.ModelData).HasColumnType("bytea").IsRequired();
            b.Property(x => x.SchemaJson).HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.IsActive).HasDefaultValue(false);

            b.HasIndex(x => new { x.Name, x.Version }).IsUnique();
            b.HasIndex(x => x.Name)
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE");
        });
    }
}
