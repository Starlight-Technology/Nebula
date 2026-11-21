using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nebula.App.Shared.State;
using Nebula.Postgres.Context;

namespace Nebula.App;

public sealed class MauiDatabaseInitializationService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly DatabaseInitializationState state;
    private readonly ILogger<MauiDatabaseInitializationService> logger;
    private readonly DatabaseInitializationOptions options;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private Task? startupTask;

    public MauiDatabaseInitializationService(
        IServiceScopeFactory scopeFactory,
        DatabaseInitializationState state,
        ILogger<MauiDatabaseInitializationService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.state = state;
        this.logger = logger;
        options = DatabaseInitializationOptions.FromEnvironment();
        state.SetRetryHandler(RetryAsync);
    }

    public void Start()
    {
        if (startupTask is not null)
        {
            return;
        }

        startupTask = Task.Run(() => InitializeAsync(CancellationToken.None));
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if (state.Status == DatabaseInitializationStatus.Ready)
        {
            return;
        }

        await Task.Run(
                () => InitializeAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!await initializationGate
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var attempts = 0;

        try
        {
            await DatabaseInitializationRetry.ExecuteAsync(
                    async token =>
                    {
                        attempts++;
                        state.MarkInitializing(attempts);

                        await using var scope = scopeFactory.CreateAsyncScope();
                        var postgresContext =
                            scope.ServiceProvider.GetRequiredService<PostgresContext>();

                        await PostgresDatabaseInitializer
                            .InitializeAsync(postgresContext, token)
                            .ConfigureAwait(false);
                    },
                    options,
                    (exception, attempt) => logger.LogWarning(
                        exception,
                        "Falha ao inicializar o Postgres na tentativa {Attempt}/{MaxAttempts}.",
                        attempt,
                        options.MaxAttempts),
                    (delay, token) =>
                    {
                        logger.LogInformation(
                            "Nova tentativa de inicializacao do Postgres em {Delay}.",
                            delay);
                        return Task.Delay(delay, token);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            state.MarkReady();
            logger.LogInformation("Postgres inicializado com sucesso.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Inicializacao do Postgres cancelada.");
            state.MarkFailed("Inicializacao do banco de dados cancelada.", attempts);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Nao foi possivel inicializar o Postgres apos {Attempts} tentativa(s).",
                attempts);
            state.MarkFailed(exception.Message, attempts);
        }
        finally
        {
            initializationGate.Release();
        }
    }
}
