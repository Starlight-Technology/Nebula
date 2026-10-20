using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class MigrationService(
    IShellExecutor executor,
    ILogger logger) : IMigrationService
{
    public async Task<MigrationResult> CreateMigrationAsync(MigrationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            return new MigrationResult(false, null, null, null, null, "Working directory does not exist.");
        }

        return request.Framework switch
        {
            MigrationFramework.EfCore => await CreateEfCoreMigrationAsync(request, cancellationToken),
            MigrationFramework.NodeJs => await CreateNodeMigrationAsync(request, cancellationToken),
            _ => new MigrationResult(false, null, null, null, null, $"Framework {request.Framework} nao suportado para migracoes")
        };
    }

    public async Task<MigrationResult> ApplyMigrationsAsync(string workingDirectory, MigrationFramework framework, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new MigrationResult(false, null, null, null, null, "Working directory does not exist.");
        }

        return framework switch
        {
            MigrationFramework.EfCore => await ApplyEfCoreMigrationsAsync(workingDirectory, cancellationToken),
            MigrationFramework.NodeJs => await ApplyNodeMigrationsAsync(workingDirectory, cancellationToken),
            _ => new MigrationResult(false, null, null, null, null, $"Framework {framework} nao suportado")
        };
    }

    public async Task<IReadOnlyList<PendingMigrationInfo>> GetPendingMigrationsAsync(string workingDirectory, MigrationFramework framework, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return [];
        }

        return framework switch
        {
            MigrationFramework.EfCore => await GetPendingEfCoreMigrationsAsync(workingDirectory, cancellationToken),
            MigrationFramework.NodeJs => await GetPendingNodeMigrationsAsync(workingDirectory, cancellationToken),
            _ => []
        };
    }

    public async Task<MigrationFramework> DetectFrameworkAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return MigrationFramework.Unknown;
        }

        try
        {
            var files = Directory.GetFiles(workingDirectory, "*.*", SearchOption.AllDirectories)
                .Select(f => Path.GetFileName(f).ToLowerInvariant())
                .ToHashSet();

            // EF Core
            if (files.Any(f => f.EndsWith(".csproj") || f.EndsWith(".sln") || f.EndsWith(".fsproj")))
            {
                var csprojFiles = Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories);
                foreach (var csproj in csprojFiles)
                {
                    var content = await File.ReadAllTextAsync(csproj, cancellationToken);
                    if (content.Contains("Microsoft.EntityFrameworkCore"))
                    {
                        return MigrationFramework.EfCore;
                    }
                }
            }

            // Node.js
            if (File.Exists(Path.Combine(workingDirectory, "package.json")))
            {
                var content = await File.ReadAllTextAsync(Path.Combine(workingDirectory, "package.json"), cancellationToken);
                if (content.Contains("knex") || content.Contains("typeorm") || content.Contains("prisma") || content.Contains("sequelize"))
                {
                    return MigrationFramework.NodeJs;
                }
            }

            // Python (Alembic)
            if (File.Exists(Path.Combine(workingDirectory, "alembic.ini")) ||
                File.Exists(Path.Combine(workingDirectory, "requirements.txt")))
            {
                var reqContent = await File.ReadAllTextAsync(Path.Combine(workingDirectory, "requirements.txt"), cancellationToken);
                if (reqContent.Contains("alembic", StringComparison.OrdinalIgnoreCase))
                {
                    return MigrationFramework.Python;
                }
            }
        }
        catch { }

        return MigrationFramework.Unknown;
    }

    private async Task<MigrationResult> CreateEfCoreMigrationAsync(MigrationRequest request, CancellationToken cancellationToken)
    {
        var migrationName = request.MigrationName ?? $"Migration_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var contextArg = string.IsNullOrWhiteSpace(request.ContextName) ? "" : $"--context {request.ContextName}";
        var dryRunArg = request.DryRun ? "--dry-run" : "";

        var cmd = $"dotnet ef migrations add {migrationName} {contextArg} {dryRunArg} --no-build";

        var result = await RunWithTimeoutAsync(cmd, request.WorkingDirectory, 120, cancellationToken);

        if (!result.Success && !request.DryRun)
        {
            return new MigrationResult(false, null, null, null, result.StandardOutput, result.StandardError ?? result.StandardOutput);
        }

        // Find the created migration file
        string? migrationFile = null;
        if (!request.DryRun)
        {
            var migrationsDir = FindMigrationsDirectory(request.WorkingDirectory);
            if (Directory.Exists(migrationsDir))
            {
                var files = Directory.GetFiles(migrationsDir, $"*{migrationName}.cs", SearchOption.AllDirectories);
                migrationFile = files.FirstOrDefault();
            }
        }

        return new MigrationResult(
            result.Success || request.DryRun,
            migrationFile,
            migrationName,
            request.DryRun ? result.StandardOutput : null,
            result.StandardOutput,
            result.Success ? null : result.StandardError);
    }

    private async Task<MigrationResult> CreateNodeMigrationAsync(MigrationRequest request, CancellationToken cancellationToken)
    {
        var pkgJsonPath = Path.Combine(request.WorkingDirectory, "package.json");
        if (!File.Exists(pkgJsonPath))
        {
            return new MigrationResult(false, null, null, null, null, "package.json nao encontrado");
        }

        var content = await File.ReadAllTextAsync(pkgJsonPath, cancellationToken);
        string cmd;

        if (content.Contains("knex"))
        {
            var migrationName = request.MigrationName ?? $"migration_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            cmd = $"npx knex migrate:make {migrationName}";
        }
        else if (content.Contains("typeorm"))
        {
            var migrationName = request.MigrationName ?? $"Migration{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            cmd = $"npx typeorm migration:create ./src/migrations/{migrationName}";
        }
        else if (content.Contains("prisma"))
        {
            cmd = "npx prisma migrate dev --create-only";
        }
        else if (content.Contains("sequelize"))
        {
            var migrationName = request.MigrationName ?? $"migration-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            cmd = $"npx sequelize-cli migration:generate --name {migrationName}";
        }
        else
        {
            return new MigrationResult(false, null, null, null, null, "Nenhuma ferramenta de migracao Node.js detectada (knex, typeorm, prisma, sequelize)");
        }

        var result = await RunWithTimeoutAsync(cmd, request.WorkingDirectory, 120, cancellationToken);

        if (!result.Success)
        {
            return new MigrationResult(false, null, null, null, result.StandardOutput, result.StandardError ?? result.StandardOutput);
        }

        return new MigrationResult(true, null, request.MigrationName, null, result.StandardOutput, null);
    }

    private async Task<MigrationResult> ApplyEfCoreMigrationsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var cmd = "dotnet ef database update --no-build";
        var result = await RunWithTimeoutAsync(cmd, workingDirectory, 180, cancellationToken);

        return new MigrationResult(
            result.Success,
            null,
            null,
            null,
            result.StandardOutput,
            result.Success ? null : result.StandardError);
    }

    private async Task<MigrationResult> ApplyNodeMigrationsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var pkgJsonPath = Path.Combine(workingDirectory, "package.json");
        if (!File.Exists(pkgJsonPath))
        {
            return new MigrationResult(false, null, null, null, null, "package.json nao encontrado");
        }

        var content = await File.ReadAllTextAsync(pkgJsonPath, cancellationToken);
        string cmd;

        if (content.Contains("knex"))
        {
            cmd = "npx knex migrate:latest";
        }
        else if (content.Contains("typeorm"))
        {
            cmd = "npx typeorm migration:run";
        }
        else if (content.Contains("prisma"))
        {
            cmd = "npx prisma migrate deploy";
        }
        else if (content.Contains("sequelize"))
        {
            cmd = "npx sequelize-cli db:migrate";
        }
        else
        {
            return new MigrationResult(false, null, null, null, null, "Nenhuma ferramenta de migracao Node.js detectada");
        }

        var result = await RunWithTimeoutAsync(cmd, workingDirectory, 180, cancellationToken);

        return new MigrationResult(
            result.Success,
            null,
            null,
            null,
            result.StandardOutput,
            result.Success ? null : result.StandardError);
    }

    private async Task<IReadOnlyList<PendingMigrationInfo>> GetPendingEfCoreMigrationsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var cmd = "dotnet ef migrations list --no-build";
        var result = await RunWithTimeoutAsync(cmd, workingDirectory, 60, cancellationToken);

        if (!result.Success)
        {
            return [];
        }

        var pending = new List<PendingMigrationInfo>();
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                pending.Add(new PendingMigrationInfo(
                    MigrationId: parts[0],
                    Name: parts[1],
                    CreatedAt: "",
                    IsApplied: line.Contains("Applied", StringComparison.OrdinalIgnoreCase) || line.Contains("(applied)", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return pending;
    }

    private async Task<IReadOnlyList<PendingMigrationInfo>> GetPendingNodeMigrationsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var pkgJsonPath = Path.Combine(workingDirectory, "package.json");
        if (!File.Exists(pkgJsonPath))
        {
            return [];
        }

        var content = await File.ReadAllTextAsync(pkgJsonPath, cancellationToken);
        string cmd;

        if (content.Contains("knex"))
        {
            cmd = "npx knex migrate:list";
        }
        else if (content.Contains("prisma"))
        {
            cmd = "npx prisma migrate status";
        }
        else
        {
            return [];
        }

        var result = await RunWithTimeoutAsync(cmd, workingDirectory, 60, cancellationToken);

        if (!result.Success)
        {
            return [];
        }

        var pending = new List<PendingMigrationInfo>();
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.Contains("pending", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("not run", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    pending.Add(new PendingMigrationInfo(
                        MigrationId: parts[0],
                        Name: string.Join(" ", parts.Skip(1)),
                        CreatedAt: "",
                        IsApplied: false));
                }
            }
        }

        return pending;
    }

    private async Task<ShellCommandResult> RunWithTimeoutAsync(string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (executor is IDetailedShellExecutor detailedExecutor)
            {
                return await detailedExecutor.RunCommandDetailedAsync(command, workingDirectory, cts.Token);
            }

            var output = await executor.RunCommandAsync(command, cts.Token);
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = output,
                ExitCode = 0,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = string.Empty,
                StandardError = "Timeout: comando excedeu o tempo limite",
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = string.Empty,
                StandardError = ex.Message,
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    private static string? FindMigrationsDirectory(string workingDirectory)
    {
        var dirs = Directory.GetDirectories(workingDirectory, "Migrations", SearchOption.AllDirectories);
        return dirs.FirstOrDefault();
    }
}