using Corona.Components.Enums;

using Microsoft.JSInterop;

using Nebula.Agent;
using Nebula.App.Shared.Setup;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;

using System.Text.Json;

namespace Nebula.App.Shared.State;

public sealed class NebulaWorkspaceState(
    IManager manager,
    ILlamaClient llamaClient,
    IRuntimeSetupAdvisor runtimeSetupAdvisor,
    IJSRuntime jsRuntime,
    NebulaRuntimeSettings runtimeSettings) : IDisposable, IAsyncDisposable
{
    private const string QuickSettingsStorageKey = "nebula.quick-settings.v1";

    private readonly List<ConversationEntryViewModel> turns = [];
    private readonly List<LlamaPullProgress> pullUpdates = [];

    private IJSObjectReference? environmentModule;
    private CancellationTokenSource? activeTurnCancellationSource;
    private bool settingsLoaded;
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

    public IReadOnlyList<ResponseLanguageOption> ResponseLanguages { get; } =
    [
        new("pt-BR", "Portugues (Brasil)"),
        new("en-US", "English (United States)"),
        new("es-ES", "Espanol"),
        new("fr-FR", "Francais"),
        new("de-DE", "Deutsch"),
        new("it-IT", "Italiano")
    ];

    public IReadOnlyList<WebResearchProviderOption> WebResearchProviders { get; } =
    [
        new("Free", "Automatico gratuito", "Documentacao direta, SearXNG e fallback para Bing HTML."),
        new("DirectDocumentation", "Documentacao direta", "Usa apenas fontes oficiais conhecidas pelo Nebula."),
        new("SearXng", "SearXNG", "Pesquisa gratuita em instancia local/self-hosted."),
        new("BingHtml", "Bing HTML", "Pesquisa publica sem chave de API."),
        new("Brave", "Brave Search", "Requer WebResearch:ApiKey configurada no servidor."),
        new("Disabled", "Desativado", "Impede pesquisa web e aprendizado por fontes externas.")
    ];

    public IReadOnlyList<ConversationEntryViewModel> Turns => turns;

    public IReadOnlyList<LlamaPullProgress> PullUpdates => pullUpdates;

    public NebulaRuntimeSettings RuntimeSettings { get; } = runtimeSettings;

    public QuickSettingsDraft QuickSettings { get; } = new()
    {
        MainModel = string.IsNullOrWhiteSpace(runtimeSettings.MainModel)
            ? llamaClient.SelectedModel
            : runtimeSettings.MainModel,
        LearningModel = string.IsNullOrWhiteSpace(runtimeSettings.LearningModel)
            ? llamaClient.SelectedModel
            : runtimeSettings.LearningModel,
        WebResearchProvider = runtimeSettings.WebResearchProvider,
        AccelerationProfile = runtimeSettings.AccelerationProfile,
        ResponseLanguageCode = runtimeSettings.ResponseLanguageCode,
        AutoApproveCommands = runtimeSettings.AutoApproveCommands
    };

    public LlamaRuntimeState? RuntimeState { get; private set; }

    public RuntimeSetupRecommendation? SetupRecommendation { get; private set; }

    public ClientEnvironmentProbe? EnvironmentProbe { get; private set; }

    public string ComposerText { get; set; } = string.Empty;

    public string LearningSourceFilePathsText { get; set; } = string.Empty;

    public string LearningSourceUrlsText { get; set; } = string.Empty;

    public InteractionMode SelectedInteractionMode { get; private set; } = InteractionMode.Chat;

    public string ModelInstallText { get; set; } = string.Empty;

    public string? ModelFeedback { get; private set; }

    public string? EnvironmentDetectionError { get; private set; }

    public string? SettingsFeedback { get; private set; }

    public bool IsSending { get; private set; }

    public bool IsRefreshingRuntime { get; private set; }

    public bool IsInstallingModel { get; private set; }

    public bool IsDetectingEnvironment { get; private set; }

    public bool IsLoadingSettings { get; private set; }

    public bool IsSavingSettings { get; private set; }

    public bool IsRuntimeOnline => RuntimeState?.IsAvailable == true;

    public bool CanSend => !IsSending && !string.IsNullOrWhiteSpace(ComposerText);

    public bool CanLearnFromSources =>
        !IsSending &&
        (!string.IsNullOrWhiteSpace(LearningSourceFilePathsText) ||
         !string.IsNullOrWhiteSpace(LearningSourceUrlsText));

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

    public async Task LearnFromSourcesAsync()
    {
        if (!CanLearnFromSources)
        {
            return;
        }

        var prompt = BuildLearningSourcesPrompt();
        var previousMode = SelectedInteractionMode;
        SelectedInteractionMode = InteractionMode.Agent;
        await SubmitPromptAsync(prompt);
        SelectedInteractionMode = previousMode;
        LearningSourceFilePathsText = string.Empty;
        LearningSourceUrlsText = string.Empty;
        NotifyChanged();
    }

    public async Task EnsureSettingsLoadedAsync()
    {
        if (settingsLoaded || IsLoadingSettings)
        {
            return;
        }

        IsLoadingSettings = true;
        NotifyChanged();

        try
        {
            var json = await jsRuntime.InvokeAsync<string?>(
                "localStorage.getItem",
                QuickSettingsStorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var snapshot = JsonSerializer.Deserialize<NebulaRuntimeSettingsSnapshot>(json);
                if (snapshot is not null)
                {
                    RuntimeSettings.Apply(snapshot);
                }
            }

            if (string.IsNullOrWhiteSpace(RuntimeSettings.MainModel))
            {
                RuntimeSettings.MainModel = llamaClient.SelectedModel;
            }

            if (string.IsNullOrWhiteSpace(RuntimeSettings.LearningModel))
            {
                RuntimeSettings.LearningModel = RuntimeSettings.MainModel;
            }

            SyncQuickSettingsDraft();
            await ApplyConfiguredMainModelAsync();
            settingsLoaded = true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JsonException)
        {
            SettingsFeedback =
                "Nao consegui carregar as preferencias salvas. Os valores padrao continuam ativos.";
        }
        finally
        {
            IsLoadingSettings = false;
            NotifyChanged();
        }
    }

    public async Task SaveQuickSettingsAsync()
    {
        if (IsSavingSettings)
        {
            return;
        }

        IsSavingSettings = true;
        SettingsFeedback = null;
        NotifyChanged();

        try
        {
            var language = ResponseLanguages.FirstOrDefault(option =>
                    option.Code.Equals(
                        QuickSettings.ResponseLanguageCode,
                        StringComparison.OrdinalIgnoreCase))
                ?? ResponseLanguages[0];
            var snapshot = new NebulaRuntimeSettingsSnapshot
            {
                MainModel = NormalizeModelSetting(
                    QuickSettings.MainModel,
                    llamaClient.SelectedModel),
                LearningModel = NormalizeModelSetting(
                    QuickSettings.LearningModel,
                    QuickSettings.MainModel),
                WebResearchProvider = QuickSettings.WebResearchProvider,
                AccelerationProfile = QuickSettings.AccelerationProfile,
                ResponseLanguageCode = language.Code,
                ResponseLanguageName = language.Name,
                AutoApproveCommands = QuickSettings.AutoApproveCommands
            };

            RuntimeSettings.Apply(snapshot);
            SyncQuickSettingsDraft();
            var modelApplied = await ApplyConfiguredMainModelAsync();
            var json = JsonSerializer.Serialize(RuntimeSettings.CreateSnapshot());
            await jsRuntime.InvokeVoidAsync(
                "localStorage.setItem",
                QuickSettingsStorageKey,
                json);

            settingsLoaded = true;
            SettingsFeedback = modelApplied
                ? "Configuracao salva e aplicada nesta sessao."
                : $"Configuracao salva. O modelo {RuntimeSettings.MainModel} precisa estar instalado para ser ativado.";
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            SettingsFeedback = $"Nao consegui salvar a configuracao: {ex.Message}";
        }
        finally
        {
            IsSavingSettings = false;
            NotifyChanged();
        }
    }

    public void UseDetectedAccelerationProfile()
    {
        QuickSettings.AccelerationProfile =
            SetupRecommendation?.ProfileKey ?? NebulaRuntimeSettings.DefaultAccelerationProfile;
        NotifyChanged();
    }

    public string GetConfiguredAccelerationCommand()
    {
        if (QuickSettings.AccelerationProfile.Equals(
                NebulaRuntimeSettings.DefaultAccelerationProfile,
                StringComparison.OrdinalIgnoreCase))
        {
            return SetupRecommendation?.Command ?? "Detectar automaticamente ao abrir Runtime";
        }

        return AccelerationProfiles.FirstOrDefault(profile =>
                profile.Key.Equals(
                    QuickSettings.AccelerationProfile,
                    StringComparison.OrdinalIgnoreCase))
            ?.Command ?? "docker compose up -d";
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

    public Task ApproveCommandAsync(CommandExecution command)
    {
        if (!CanApproveCommand(command))
        {
            return Task.CompletedTask;
        }

        var turn = new ConversationEntryViewModel
        {
            Prompt = $"Aprovar e executar: {command.Run}",
            Mode = InteractionMode.Agent,
            RequestedModel = ActiveModelName
        };

        turns.Add(turn);
        IsSending = true;
        SettingsFeedback = null;
        NotifyChanged();

        activeTurnCancellationSource?.Cancel();
        activeTurnCancellationSource?.Dispose();

        var turnCancellationSource = new CancellationTokenSource();
        activeTurnCancellationSource = turnCancellationSource;

        _ = CompleteApprovedCommandTurnAsync(turn, command, turnCancellationSource);
        return Task.CompletedTask;
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

    public bool CanApproveCommand(CommandExecution command)
    {
        return !IsSending &&
               command.SafetyDecision == CommandSafetyDecisionType.AskApproval &&
               !command.Executed &&
               !command.ApprovedByUser &&
               !command.AutoApproved &&
               command.OperationKind is (OperationKind.TerminalCommand or
                   OperationKind.ScriptExecution) &&
               !string.IsNullOrWhiteSpace(command.Run);
    }

    public string GetCommandApprovalSummary()
    {
        return RuntimeSettings.AutoApproveCommands
            ? "Auto-aprovacao ativa"
            : "Aprovacao manual";
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

    private async Task CompleteApprovedCommandTurnAsync(
        ConversationEntryViewModel turn,
        CommandExecution command,
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

            turn.Result = await manager.RunApprovedCommandAsync(
                command,
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

            turn.Result = new ConversationTurn
            {
                Prompt = turn.Prompt,
                Mode = InteractionMode.Agent,
                ModelName = turn.RequestedModel,
                Classification = InteractionMode.Agent.ToString(),
                Response = "Execucao cancelada pelo usuario.",
                ActionStatus = ActionExecutionStatus.Cancelled,
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

    private async Task<bool> ApplyConfiguredMainModelAsync()
    {
        if (string.IsNullOrWhiteSpace(RuntimeSettings.MainModel) ||
            ModelNamesMatch(llamaClient.SelectedModel, RuntimeSettings.MainModel))
        {
            return true;
        }

        try
        {
            var selected = await llamaClient.SelectModelAsync(RuntimeSettings.MainModel);
            if (selected)
            {
                RuntimeState = await llamaClient.GetRuntimeStateAsync(forceRefresh: true);
            }

            return selected;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SyncQuickSettingsDraft()
    {
        QuickSettings.MainModel = NormalizeModelSetting(
            RuntimeSettings.MainModel,
            llamaClient.SelectedModel);
        QuickSettings.LearningModel = NormalizeModelSetting(
            RuntimeSettings.LearningModel,
            QuickSettings.MainModel);
        QuickSettings.WebResearchProvider = RuntimeSettings.WebResearchProvider;
        QuickSettings.AccelerationProfile = RuntimeSettings.AccelerationProfile;
        QuickSettings.ResponseLanguageCode = RuntimeSettings.ResponseLanguageCode;
        QuickSettings.AutoApproveCommands = RuntimeSettings.AutoApproveCommands;
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

    private static string NormalizeModelSetting(string? modelName, string fallback)
    {
        return string.IsNullOrWhiteSpace(modelName)
            ? fallback.Trim()
            : modelName.Trim();
    }

    private string BuildLearningSourcesPrompt()
    {
        var objective = string.IsNullOrWhiteSpace(ComposerText)
            ? "Aprenda com as fontes adicionadas."
            : ComposerText.Trim();
        var files = NormalizeLines(LearningSourceFilePathsText);
        var urls = NormalizeLines(LearningSourceUrlsText);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(objective);
        if (files.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[learning_source_files]");
            foreach (var file in files)
            {
                builder.AppendLine(file);
            }

            builder.AppendLine("[/learning_source_files]");
        }

        if (urls.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[learning_source_sites]");
            foreach (var url in urls)
            {
                builder.AppendLine(url);
            }

            builder.AppendLine("[/learning_source_sites]");
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<string> NormalizeLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

public sealed class QuickSettingsDraft
{
    public string MainModel { get; set; } = string.Empty;

    public string LearningModel { get; set; } = string.Empty;

    public string WebResearchProvider { get; set; } =
        NebulaRuntimeSettings.DefaultWebResearchProvider;

    public string AccelerationProfile { get; set; } =
        NebulaRuntimeSettings.DefaultAccelerationProfile;

    public string ResponseLanguageCode { get; set; } =
        NebulaRuntimeSettings.DefaultLanguageCode;

    public bool AutoApproveCommands { get; set; }
}

public sealed record ResponseLanguageOption(string Code, string Name);

public sealed record WebResearchProviderOption(
    string Key,
    string Name,
    string Description);

public sealed record AccelerationProfileOption(
    string Key,
    string Name,
    string Description,
    string Command,
    string Badge,
    CoronaColorSemantic Color);
