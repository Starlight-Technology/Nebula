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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Request>(b =>
        {
            b.ToTable("requests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Prompt).IsRequired();
            b.Property(x => x.Classification).IsRequired();
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
    }
}