using Corona.Components.Enums;

using Microsoft.JSInterop;

using Nebula.Agent;
using Nebula.App.Shared.Setup;
using Nebula.Core.Interactions;
using Nebula.Llama.Client;

namespace Nebula.App.Shared.State;

public sealed class NebulaWorkspaceState(
    IManager manager,
    ILlamaClient llamaClient,
    IRuntimeSetupAdvisor runtimeSetupAdvisor,
    IJSRuntime jsRuntime) : IDisposable, IAsyncDisposable
{
    private readonly List<ConversationEntryViewModel> turns = [];
    private readonly List<LlamaPullProgress> pullUpdates = [];

    private IJSObjectReference? environmentModule;
    private CancellationTokenSource? activeTurnCancellationSource;
    private bool isDisposed;

    public event Action? Changed;

    public IReadOnlyList<string> StarterPrompts { get; } =
    [
        "Resuma este projeto e destaque os modulos principais",
        "Explique a diferenca entre os modos Conversa e Agente",
        "Liste os riscos tecnicos mais importantes desta solucao",
        "Sugira um modelo melhor para tarefas de codigo neste projeto"
    ];

    public IReadOnlyList<SuggestedModelOption> SuggestedModels { get; } =
    [
        new("qwen3:8b", "Equilibrado para conversa, codigo e automacao local."),
        new("deepseek-r1:8b", "Mais foco em raciocinio passo a passo."),
        new("llama3.1:8b", "Boa opcao geral para chat e tarefas tecnicas."),
        new("phi4-mini", "Opcao leve para maquinas locais mais modestas.")
    ];

    public IReadOnlyList<AccelerationProfileOption> AccelerationProfiles { get; } =
    [
        new(
            "cpu",
            "CPU",
            "Modo padrao, sem dependencias extras de driver ou passthrough.",
            "docker compose up -d",
            "Seguro",
            CoronaColorSemantic.Neutral),
        new(
            "nvidia",
            "NVIDIA CUDA",
            "Perfil recomendado para Linux e Windows com WSL2 quando o host ja usa NVIDIA Container Toolkit.",
            "docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d",
            "Estavel",
            CoronaColorSemantic.Success),
        new(
            "amd",
            "AMD ROCm",
            "Perfil para Linux com `ollama/ollama:rocm` e dispositivos `/dev/kfd` + `/dev/dri` expostos ao container.",
            "docker compose -f docker-compose.yml -f docker-compose.amd.yml up -d",
            "Linux",
            CoronaColorSemantic.Warning),
        new(
            "intel",
            "Intel Vulkan",
            "Perfil experimental via Vulkan. Tambem pode servir como fallback para outras GPUs com driver Vulkan funcional.",
            "docker compose -f docker-compose.yml -f docker-compose.intel.yml up -d",
            "Experimental",
            CoronaColorSemantic.Warning)
    ];

    public IReadOnlyList<ConversationEntryViewModel> Turns => turns;

    public IReadOnlyList<LlamaPullProgress> PullUpdates => pullUpdates;

    public LlamaRuntimeState? RuntimeState { get; private set; }

    public RuntimeSetupRecommendation? SetupRecommendation { get; private set; }

    public ClientEnvironmentProbe? EnvironmentProbe { get; private set; }

    public string ComposerText { get; set; } = string.Empty;

    public InteractionMode SelectedInteractionMode { get; private set; } = InteractionMode.Chat;

    public string ModelInstallText { get; set; } = string.Empty;

    public string? ModelFeedback { get; private set; }

    public string? EnvironmentDetectionError { get; private set; }

    public bool IsSending { get; private set; }

    public bool IsRefreshingRuntime { get; private set; }

    public bool IsInstallingModel { get; private set; }

    public bool IsDetectingEnvironment { get; private set; }

    public bool IsRuntimeOnline => RuntimeState?.IsAvailable == true;

    public bool CanSend => !IsSending && !string.IsNullOrWhiteSpace(ComposerText);

    public bool IsModelBusy => IsRefreshingRuntime || IsInstallingModel || IsSending;

    public bool CanInstallModel => !IsModelBusy && !string.IsNullOrWhiteSpace(ModelInstallText);

    public string ActiveModelName => RuntimeState?.SelectedModel ?? llamaClient.SelectedModel;

    public string LlamaUrl => llamaClient.LlamaUrl;

    public int InstalledModelCount => RuntimeState?.InstalledModels.Count ?? 0;

    public async Task EnsureRuntimeAsync()
    {
        if (RuntimeState is null)
        {
            await RefreshRuntimeAsync();
        }
    }

    public async Task SendAsync()
    {
        await SubmitPromptAsync(ComposerText);
    }

    public async Task SendStarterAsync(string prompt)
    {
        await SubmitPromptAsync(prompt);
    }

    public void SelectInteractionMode(InteractionMode mode)
    {
        if (IsSending || SelectedInteractionMode == mode)
        {
            return;
        }

        SelectedInteractionMode = mode;
        NotifyChanged();
    }

    public void CancelActiveTurn()
    {
        if (!IsSending || activeTurnCancellationSource is null)
        {
            return;
        }

        activeTurnCancellationSource.Cancel();
        NotifyChanged();
    }

    public async Task SubmitPromptAsync(string prompt)
    {
        if (IsSending)
        {
            return;
        }

        var normalizedPrompt = prompt.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return;
        }

        var turn = new ConversationEntryViewModel
        {
            Prompt = normalizedPrompt,
            Mode = SelectedInteractionMode,
            RequestedModel = ActiveModelName
        };

        turns.Add(turn);
        ComposerText = string.Empty;
        IsSending = true;
        NotifyChanged();

        activeTurnCancellationSource?.Cancel();
        activeTurnCancellationSource?.Dispose();

        var turnCancellationSource = new CancellationTokenSource();
        activeTurnCancellationSource = turnCancellationSource;

        _ = CompleteTurnAsync(turn, normalizedPrompt, turnCancellationSource);
    }

    public async Task RefreshRuntimeAsync()
    {
        if (IsRefreshingRuntime)
        {
            return;
        }

        IsRefreshingRuntime = true;
        NotifyChanged();

        try
        {
            RuntimeState = await llamaClient.GetRuntimeStateAsync(forceRefresh: true);
            ModelFeedback = RuntimeState.IsAvailable
                ? $"Catalogo atualizado com {RuntimeState.InstalledModels.Count} modelo(s)."
                : "O runtime nao respondeu. Verifique se o Ollama esta ativo localmente.";
            UpdateSetupRecommendation();
        }
        finally
        {
            IsRefreshingRuntime = false;
            NotifyChanged();
        }
    }

    public async Task DetectEnvironmentAsync()
    {
        if (IsDetectingEnvironment)
        {
            return;
        }

        IsDetectingEnvironment = true;
        EnvironmentDetectionError = null;
        NotifyChanged();

        try
        {
            environmentModule ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/Nebula.App.Shared/nebula-runtime.js");

            EnvironmentProbe = await environmentModule.InvokeAsync<ClientEnvironmentProbe>("getClientEnvironment");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            EnvironmentProbe = null;
            EnvironmentDetectionError = "Nao consegui ler todos os detalhes do shell da interface, entao a recomendacao usa apenas os sinais do host do agente.";
        }
        finally
        {
            UpdateSetupRecommendation();
            IsDetectingEnvironment = false;
            NotifyChanged();
        }
    }

    public async Task SelectInstalledModelAsync(string modelName)
    {
        if (IsModelBusy)
        {
            return;
        }

        var changed = await llamaClient.SelectModelAsync(modelName);
        if (!changed)
        {
            await RefreshRuntimeAsync();
            ModelFeedback = $"Nao consegui ativar {modelName} porque ele nao aparece como instalado.";
            NotifyChanged();
            return;
        }

        await RefreshRuntimeAsync();
        ModelFeedback = $"Modelo ativo alterado para {llamaClient.SelectedModel}.";
        NotifyChanged();
    }

    public async Task InstallModelAsync()
    {
        await InstallSpecificModelAsync(ModelInstallText, false);
    }

    public async Task InstallAndActivateModelAsync()
    {
        await InstallSpecificModelAsync(ModelInstallText, true);
    }

    public async Task HandleSuggestedModelAsync(SuggestedModelOption suggestion)
    {
        if (ModelNamesMatch(ActiveModelName, suggestion.Name))
        {
            return;
        }

        var isInstalled = RuntimeState?.InstalledModels.Any(model => ModelNamesMatch(model.Name, suggestion.Name)) == true;
        if (isInstalled)
        {
            await SelectInstalledModelAsync(suggestion.Name);
            return;
        }

        await InstallSpecificModelAsync(suggestion.Name, true);
    }

    public async Task InstallSpecificModelAsync(string modelName, bool activateAfterInstall)
    {
        if (IsModelBusy)
        {
            return;
        }

        var normalizedModel = modelName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            return;
        }

        pullUpdates.Clear();
        IsInstallingModel = true;
        ModelFeedback = activateAfterInstall
            ? $"Instalando {normalizedModel} e preparando a troca do modelo ativo..."
            : $"Instalando {normalizedModel}...";

        NotifyChanged();

        try
        {
            var reporter = new Progress<LlamaPullProgress>(update =>
            {
                pullUpdates.Add(update);
                if (pullUpdates.Count > 6)
                {
                    pullUpdates.RemoveAt(0);
                }

                NotifyChanged();
            });

            var result = await llamaClient.PullModelAsync(
                normalizedModel,
                activateAfterInstall: activateAfterInstall,
                progress: reporter);

            if (result.Success)
            {
                ModelInstallText = string.Empty;
                await RefreshRuntimeAsync();
            }

            ModelFeedback = result.Message;
        }
        finally
        {
            IsInstallingModel = false;
            NotifyChanged();
        }
    }

    public Task ClearConversation()
    {
        if (IsSending)
        {
            return Task.CompletedTask;
        }

        turns.Clear();
        manager.StartNewConversation();
        NotifyChanged();
        return Task.CompletedTask;
    }

    public string GetHostSummary()
    {
        return SetupRecommendation is null
            ? "Lendo host do agente..."
            : $"{SetupRecommendation.Host.PlatformLabel} {SetupRecommendation.Host.ArchitectureLabel}";
    }

    public string GetClientShellSummary()
    {
        if (SetupRecommendation?.Client is null)
        {
            return IsDetectingEnvironment
                ? "Lendo shell da interface..."
                : "Sem leitura detalhada do shell";
        }

        return $"{SetupRecommendation.Client.BrowserLabel} em {SetupRecommendation.Client.PlatformLabel}";
    }

    public string GetAccelerationStatusText()
    {
        return SetupRecommendation is null
            ? "Analisando o melhor perfil..."
            : $"{SetupRecommendation.ProfileName} ({SetupRecommendation.ModeLabel.ToLowerInvariant()})";
    }

    public string GetRuntimeScopeSummary()
    {
        if (SetupRecommendation is null)
        {
            return "Lendo endpoint";
        }

        return SetupRecommendation.Runtime.IsLocal
            ? $"Local ({llamaClient.LlamaUrl})"
            : $"Remoto ({llamaClient.LlamaUrl})";
    }

    public CoronaColorSemantic GetRecommendationColor()
    {
        if (SetupRecommendation is null)
        {
            return CoronaColorSemantic.Neutral;
        }

        if (!SetupRecommendation.UsesGpu)
        {
            return CoronaColorSemantic.Neutral;
        }

        return SetupRecommendation.IsExperimental
            ? CoronaColorSemantic.Warning
            : CoronaColorSemantic.Success;
    }

    public CoronaColorSemantic GetConfidenceColor()
    {
        return SetupRecommendation?.ConfidenceLabel switch
        {
            "Alta" => CoronaColorSemantic.Success,
            "Media" => CoronaColorSemantic.Primary,
            _ => CoronaColorSemantic.Warning
        };
    }

    public bool IsRecommendedProfile(AccelerationProfileOption profile)
    {
        return SetupRecommendation?.ProfileKey == profile.Key;
    }

    public string GetAccelerationCardClass(AccelerationProfileOption profile)
    {
        return IsRecommendedProfile(profile)
            ? "nebula-acceleration-card is-recommended"
            : "nebula-acceleration-card";
    }

    public string GetAccelerationBadge(AccelerationProfileOption profile)
    {
        return IsRecommendedProfile(profile)
            ? "Recomendado"
            : profile.Badge;
    }

    public CoronaColorSemantic GetAccelerationBadgeColor(AccelerationProfileOption profile)
    {
        return IsRecommendedProfile(profile)
            ? GetRecommendationColor()
            : profile.Color;
    }

    public string GetComposerCardClass()
    {
        return Turns.Count == 0
            ? "nebula-composer-card"
            : "nebula-composer-card nebula-composer-card--sticky";
    }

    public static string GetReasoningText(ConversationTurn turn)
    {
        return string.IsNullOrWhiteSpace(turn.Reasoning)
            ? "O modelo nao retornou um bloco explicito de raciocinio neste turno."
            : turn.Reasoning;
    }

    public static string GetTurnModelLabel(ConversationEntryViewModel turn)
    {
        return turn.Result?.ModelName ?? turn.RequestedModel;
    }

    public static IReadOnlyList<ActionExecutionEvent> GetActionEvents(ConversationEntryViewModel turn)
    {
        return turn.Result?.ActionEvents ?? turn.StreamingActionEvents;
    }

    public static IReadOnlyList<CommandExecution> GetVisibleCommands(ConversationEntryViewModel turn)
    {
        return turn.Result?.Commands ?? turn.StreamingCommands;
    }

    public static CoronaColorSemantic GetActionStatusColor(ActionExecutionStatus status)
    {
        return status switch
        {
            ActionExecutionStatus.Completed => CoronaColorSemantic.Success,
            ActionExecutionStatus.Unsafe => CoronaColorSemantic.Danger,
            ActionExecutionStatus.Failed or ActionExecutionStatus.Cancelled => CoronaColorSemantic.Warning,
            ActionExecutionStatus.Executing or ActionExecutionStatus.Retrying => CoronaColorSemantic.Primary,
            _ => CoronaColorSemantic.Neutral
        };
    }

    public static string GetActionEventLabel(ActionExecutionEvent actionEvent)
    {
        return actionEvent.Kind.ToString();
    }

    public static string FormatBoolean(bool value) => value ? "sim" : "nao";

    public static bool ModelNamesMatch(string left, string right)
    {
        return NormalizeModelName(left).Equals(NormalizeModelName(right), StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();

        if (environmentModule is not null)
        {
            try
            {
                await environmentModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The browser circuit was already disconnected during disposal.
            }
            catch (InvalidOperationException)
            {
                // The JS module was not available anymore during disposal.
            }
        }
    }

    public void Dispose()
    {
        isDisposed = true;
        activeTurnCancellationSource?.Cancel();
        activeTurnCancellationSource?.Dispose();
        activeTurnCancellationSource = null;
    }

    private async Task CompleteTurnAsync(
        ConversationEntryViewModel turn,
        string normalizedPrompt,
        CancellationTokenSource turnCancellationSource)
    {
        try
        {
            var progress = new InlineProgress<ConversationTurn>(partialTurn =>
            {
                turn.StreamingClassification = partialTurn.Classification;
                turn.StreamingResponse = partialTurn.Response;
                turn.StreamingReasoning = partialTurn.Reasoning;
                turn.StreamingActionStatus = partialTurn.ActionStatus;
                turn.StreamingActionEvents = partialTurn.ActionEvents.ToList();
                turn.StreamingCommands = partialTurn.Commands.ToList();

                if (!isDisposed)
                {
                    NotifyChanged();
                }
            });

            turn.Result = await manager.ManageConversationAsync(
                new UserMessage(normalizedPrompt, turn.Mode),
                progress,
                turnCancellationSource.Token);
            turn.StreamingClassification = null;
            turn.StreamingResponse = null;
            turn.StreamingReasoning = null;
            turn.StreamingActionStatus = null;
            turn.StreamingActionEvents.Clear();
            turn.StreamingCommands.Clear();
        }
        catch (OperationCanceledException) when (turnCancellationSource.IsCancellationRequested && isDisposed)
        {
            // Disposal intentionally stops the active turn without updating UI state.
        }
        catch (OperationCanceledException) when (turnCancellationSource.IsCancellationRequested)
        {
            turn.StreamingClassification = null;
            turn.StreamingResponse = null;
            turn.StreamingReasoning = null;
            turn.StreamingActionStatus = null;

            var events = turn.StreamingActionEvents.ToList();
            events.Add(new ActionExecutionEvent
            {
                Kind = ActionExecutionEventKind.Cancelled,
                Status = ActionExecutionStatus.Cancelled,
                Step = Math.Max(1, events.LastOrDefault()?.Step ?? 1),
                Attempt = Math.Max(1, events.LastOrDefault()?.Attempt ?? 1),
                Title = "Action cancelled",
                Message = "Execucao cancelada pelo usuario."
            });

            turn.Result = new ConversationTurn
            {
                Prompt = normalizedPrompt,
                Mode = turn.Mode,
                ModelName = turn.RequestedModel,
                Classification = turn.Mode.ToString(),
                Response = "Execucao cancelada pelo usuario.",
                ActionStatus = ActionExecutionStatus.Cancelled,
                ActionEvents = events,
                Commands = turn.StreamingCommands.ToList(),
                IsCancelled = true
            };

            turn.StreamingActionEvents.Clear();
            turn.StreamingCommands.Clear();
        }
        catch (Exception ex)
        {
            turn.Error = ex.Message;
        }
        finally
        {
            IsSending = false;

            if (ReferenceEquals(activeTurnCancellationSource, turnCancellationSource))
            {
                activeTurnCancellationSource.Dispose();
                activeTurnCancellationSource = null;
            }

            if (!isDisposed)
            {
                NotifyChanged();
            }
        }
    }

    private void UpdateSetupRecommendation()
    {
        SetupRecommendation = runtimeSetupAdvisor.BuildRecommendation(EnvironmentProbe, llamaClient.LlamaUrl);
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private static string NormalizeModelName(string modelName)
    {
        var trimmed = modelName.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^7]
            : trimmed;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }
}

public sealed class ConversationEntryViewModel
{
    public string Prompt { get; set; } = string.Empty;

    public InteractionMode Mode { get; set; }

    public string RequestedModel { get; set; } = string.Empty;

    public ConversationTurn? Result { get; set; }

    public string? Error { get; set; }

    public string? StreamingClassification { get; set; }

    public string? StreamingResponse { get; set; }

    public string? StreamingReasoning { get; set; }

    public ActionExecutionStatus? StreamingActionStatus { get; set; }

    public List<ActionExecutionEvent> StreamingActionEvents { get; set; } = [];

    public List<CommandExecution> StreamingCommands { get; set; } = [];

    public bool HasStreamingContent =>
        !string.IsNullOrWhiteSpace(StreamingResponse) ||
        !string.IsNullOrWhiteSpace(StreamingReasoning) ||
        StreamingActionEvents.Count > 0;

    public bool ShowReasoning { get; set; } = true;
}

public sealed record SuggestedModelOption(string Name, string Description);

public sealed record AccelerationProfileOption(
    string Key,
    string Name,
    string Description,
    string Command,
    string Badge,
    CoronaColorSemantic Color);
