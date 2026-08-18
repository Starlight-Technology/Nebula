using Corona.Components.Enums;

using Microsoft.JSInterop;

using Nebula.Agent;
using Nebula.Agent.Data;
using Nebula.App.Shared.Setup;
using Nebula.Core.Agent;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;
using Nebula.Llama.Client;

using System.Text.Json;

namespace Nebula.App.Shared.State;

public sealed class NebulaWorkspaceState(
    IManager manager,
    ILlamaClient llamaClient,
    IRuntimeSetupAdvisor runtimeSetupAdvisor,
    IJSRuntime jsRuntime,
    NebulaRuntimeSettings runtimeSettings,
    IConversationMemoryRepository? conversationMemoryRepository = null,
    IOllamaUpdateService? ollamaUpdateService = null,
    IProjectDoctorService? projectDoctorService = null,
    IAgentRunStore? agentRunStore = null,
    ICommandRepository? commandRepository = null,
    ICommandAllowlistService? commandAllowlistService = null,
    IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService = null,
    IPolicySimulator? policySimulator = null) : IDisposable, IAsyncDisposable
{
    private const string QuickSettingsStorageKey = "nebula.quick-settings.v1";

    private const int ConversationHistoryLimit = 50;
    private const int ConversationHistoryMessageLimit = 400;

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

    public IReadOnlyList<ConversationSummary> ConversationHistory { get; private set; } = [];

    public bool IsHistoryLoading { get; private set; }

    public string? HistoryFeedback { get; private set; }

    public bool HasConversationHistory => ConversationHistory.Count > 0;

    public Guid ActiveConversationId => manager.ActiveConversationId;

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
        WorkspaceRoot = runtimeSettings.WorkspaceRoot,
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

    public string? OllamaServerVersion { get; private set; }

    public bool IsUpdatingOllama { get; private set; }

    public IReadOnlyList<string> OllamaUpdateOutput { get; private set; } = [];

    public ProjectDiagnosticReport? ProjectDiagnostic { get; private set; }

    public bool IsRunningProjectDoctor { get; private set; }

    public string? ProjectDoctorFeedback { get; private set; }

    public IReadOnlyList<AgentRun> AgentRuns { get; private set; } = [];

    public AgentRun? SelectedAgentRun { get; private set; }

    public bool IsLoadingAgentRuns { get; private set; }

    public string? AgentRunsFeedback { get; private set; }

    public string AllowlistCommandText { get; set; } = string.Empty;

    public IReadOnlyList<string> WorkspaceAllowlistCommands { get; private set; } = [];

    public bool IsLoadingAllowlist { get; private set; }

    public string? AllowlistFeedback { get; private set; }

    public string WorkspaceCategoryText { get; set; } = string.Empty;

    public IReadOnlyList<string> WorkspaceAutoApproveCategories { get; private set; } = [];

    public bool IsLoadingWorkspaceCategories { get; private set; }

    public string? WorkspaceCategoriesFeedback { get; private set; }

    public string PolicySimulatorText { get; set; } = string.Empty;

    public PolicySimulationResult? PolicySimulation { get; private set; }

    public bool IsSimulatingPolicy { get; private set; }

    public string? PolicySimulationError { get; private set; }

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

    /// <summary>
    /// The effective reference workspace folder the agent works on. Resolves
    /// the configured workspace root (creating the folder when missing) and
    /// falls back to a fresh empty workspace when nothing is configured.
    /// </summary>
    public ReferenceWorkspace ResolvedWorkspace =>
        ReferenceWorkspace.Resolve(QuickSettings.WorkspaceRoot);

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
                WorkspaceRoot = QuickSettings.WorkspaceRoot?.Trim() ?? string.Empty,
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
            RequestedModel = ActiveModelName,
            ConversationId = ActiveConversationId
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
            RequestedModel = ActiveModelName,
            ConversationId = ActiveConversationId
        };

        turns.Add(turn);
        IsSending = true;
        SettingsFeedback = null;
        NotifyChanged();

        activeTurnCancellationSource?.Cancel();
        activeTurnCancellationSource?.Dispose();

        var turnCancellationSource = new CancellationTokenSource();
        activeTurnCancellationSource = turnCancellationSource;

        var scope = SelectedApprovalScope;
        _ = CompleteApprovedCommandTurnAsync(
            turn,
            command,
            scope,
            turnCancellationSource);
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

    public async Task RefreshOllamaVersionAsync()
    {
        try
        {
            OllamaServerVersion = await llamaClient.GetServerVersionAsync();
        }
        catch
        {
            OllamaServerVersion = null;
        }

        NotifyChanged();
    }

    public async Task UpdateOllamaServerAsync()
    {
        if (IsUpdatingOllama || ollamaUpdateService is null)
        {
            return;
        }

        IsUpdatingOllama = true;
        ModelFeedback = "Atualizando o container do Ollama... Isso pode demorar alguns minutos.";
        NotifyChanged();

        try
        {
            var result = await ollamaUpdateService.UpdateServerAsync();
            OllamaServerVersion = result.NewVersion ?? OllamaServerVersion;
            OllamaUpdateOutput = result.OutputLines;
            ModelFeedback = result.Message;
        }
        catch (OperationCanceledException)
        {
            ModelFeedback = "Atualizacao do Ollama cancelada.";
        }
        finally
        {
            IsUpdatingOllama = false;
            NotifyChanged();
        }
    }

    public async Task RunProjectDoctorAsync()
    {
        if (IsRunningProjectDoctor || projectDoctorService is null)
        {
            return;
        }

        IsRunningProjectDoctor = true;
        ProjectDoctorFeedback = null;
        NotifyChanged();

        try
        {
            ProjectDiagnostic = await projectDoctorService.RunAsync();
            ProjectDoctorFeedback = ProjectDiagnostic.AllHealthy
                ? "Ambiente de desenvolvimento saudavel."
                : $"{ProjectDiagnostic.ProblemCount} item(ns) com problema. Veja as sugestoes abaixo.";
        }
        catch (OperationCanceledException)
        {
            ProjectDoctorFeedback = "Diagnostico cancelado.";
        }
        finally
        {
            IsRunningProjectDoctor = false;
            NotifyChanged();
        }
    }

    public async Task LoadAgentRunsAsync()
    {
        if (IsLoadingAgentRuns || agentRunStore is null)
        {
            return;
        }

        IsLoadingAgentRuns = true;
        AgentRunsFeedback = null;
        NotifyChanged();

        try
        {
            AgentRuns = await agentRunStore.GetRunsAsync(limit: 25);
            AgentRunsFeedback = AgentRuns.Count == 0
                ? "Nenhuma execucao de agente registrada ainda."
                : null;
        }
        catch (Exception ex)
        {
            AgentRunsFeedback = $"Nao consegui carregar o historico: {ex.Message}";
        }
        finally
        {
            IsLoadingAgentRuns = false;
            NotifyChanged();
        }
    }

    public async Task OpenAgentRunAsync(Guid runId)
    {
        if (agentRunStore is null)
        {
            return;
        }

        SelectedAgentRun = await agentRunStore.GetRunAsync(runId);
        NotifyChanged();
    }

    public Task CloseAgentRunAsync()
    {
        SelectedAgentRun = null;
        NotifyChanged();
        return Task.CompletedTask;
    }

    public async Task LoadAllowlistAsync()
    {
        if (IsLoadingAllowlist || commandAllowlistService is null)
        {
            return;
        }

        IsLoadingAllowlist = true;
        AllowlistFeedback = null;
        NotifyChanged();

        try
        {
            var entries = await commandAllowlistService.ListAsync(
                ResolvedWorkspace.Root);
            WorkspaceAllowlistCommands = entries
                .Select(entry => entry.Value)
                .ToList();
            AllowlistFeedback = WorkspaceAllowlistCommands.Count == 0
                ? "Nenhum comando na allowlist deste workspace ainda."
                : null;
        }
        catch (Exception ex)
        {
            AllowlistFeedback = $"Nao consegui carregar a allowlist: {ex.Message}";
        }
        finally
        {
            IsLoadingAllowlist = false;
            NotifyChanged();
        }
    }

    public async Task AddAllowlistCommandAsync()
    {
        if (commandAllowlistService is null ||
            string.IsNullOrWhiteSpace(AllowlistCommandText))
        {
            return;
        }

        var command = AllowlistCommandText.Trim();
        AllowlistCommandText = string.Empty;
        AllowlistFeedback = null;
        NotifyChanged();

        await commandAllowlistService.AddAsync(
            ResolvedWorkspace.Root,
            command,
            evidence: "Added by user in Settings.");
        AllowlistFeedback =
            $"Comando adicionado a allowlist deste workspace: {command}";
        await LoadAllowlistAsync();
    }

    public async Task LoadWorkspaceCategoriesAsync()
    {
        if (IsLoadingWorkspaceCategories || workspaceCategoryPolicyService is null)
        {
            return;
        }

        IsLoadingWorkspaceCategories = true;
        WorkspaceCategoriesFeedback = null;
        NotifyChanged();

        try
        {
            var entries = await workspaceCategoryPolicyService.ListAsync(
                ResolvedWorkspace.Root);
            WorkspaceAutoApproveCategories = entries
                .Select(entry => entry.Value)
                .ToList();
            WorkspaceCategoriesFeedback =
                WorkspaceAutoApproveCategories.Count == 0
                    ? "Nenhuma categoria auto-aprovada para este workspace ainda."
                    : null;
        }
        catch (Exception ex)
        {
            WorkspaceCategoriesFeedback =
                $"Nao consegui carregar as categorias: {ex.Message}";
        }
        finally
        {
            IsLoadingWorkspaceCategories = false;
            NotifyChanged();
        }
    }

    public async Task AddWorkspaceCategoryAsync()
    {
        if (workspaceCategoryPolicyService is null ||
            string.IsNullOrWhiteSpace(WorkspaceCategoryText))
        {
            return;
        }

        var category = WorkspaceCategoryText.Trim();
        WorkspaceCategoryText = string.Empty;
        WorkspaceCategoriesFeedback = null;
        NotifyChanged();

        await workspaceCategoryPolicyService.AddAsync(
            ResolvedWorkspace.Root,
            category,
            evidence: "Added by user in Settings.");
        WorkspaceCategoriesFeedback =
            $"Categoria auto-aprovada adicionada a este workspace: {category}";
        await LoadWorkspaceCategoriesAsync();
    }

    public async Task RunPolicySimulationAsync()
    {
        if (policySimulator is null ||
            IsSimulatingPolicy ||
            string.IsNullOrWhiteSpace(PolicySimulatorText))
        {
            return;
        }

        var text = PolicySimulatorText.Trim();
        IsSimulatingPolicy = true;
        PolicySimulation = null;
        PolicySimulationError = null;
        NotifyChanged();

        try
        {
            PolicySimulation = await policySimulator.SimulateAsync(
                text,
                workingDirectory: ResolvedWorkspace.Root);
        }
        catch (Exception ex)
        {
            PolicySimulationError = $"Falha na simulacao: {ex.Message}";
        }
        finally
        {
            IsSimulatingPolicy = false;
            NotifyChanged();
        }
    }

    public IReadOnlyList<StoredCommand> ApprovedCommands { get; private set; } = [];

    public bool IsLoadingApprovedCommands { get; private set; }

    public string? ApprovedCommandsFeedback { get; private set; }

    public async Task LoadApprovedCommandsAsync()
    {
        if (IsLoadingApprovedCommands || commandRepository is null)
        {
            return;
        }

        IsLoadingApprovedCommands = true;
        ApprovedCommandsFeedback = null;
        NotifyChanged();

        try
        {
            var commands = await commandRepository.GetApprovedCommandsAsync(skip: 0, take: 100);
            ApprovedCommands = commands.ToList();
            ApprovedCommandsFeedback = ApprovedCommands.Count == 0
                ? "Nenhum comando aprovado (manual ou automatico) registrado ainda."
                : null;
        }
        catch (Exception ex)
        {
            ApprovedCommandsFeedback = $"Nao consegui carregar a auditoria: {ex.Message}";
        }
        finally
        {
            IsLoadingApprovedCommands = false;
            NotifyChanged();
        }
    }

    public Task ResumeTaskAsync(AgentRun run)
    {
        if (IsSending)
        {
            return Task.CompletedTask;
        }

        if (run.ConversationId != Guid.Empty)
        {
            manager.SelectConversation(run.ConversationId);
        }

        var turn = new ConversationEntryViewModel
        {
            Prompt = $"Retomar tarefa: {run.Prompt}",
            Mode = InteractionMode.Agent,
            RequestedModel = ActiveModelName,
            ConversationId = run.ConversationId
        };

        turns.Add(turn);
        IsSending = true;
        NotifyChanged();

        activeTurnCancellationSource?.Cancel();
        activeTurnCancellationSource?.Dispose();

        var turnCancellationSource = new CancellationTokenSource();
        activeTurnCancellationSource = turnCancellationSource;

        _ = CompleteResumeTurnAsync(turn, run, turnCancellationSource);
        return Task.CompletedTask;
    }

    public static string GetAgentRunStatusLabel(AgentRun run)
    {
        if (run.IsCancelled)
        {
            return "Cancelado";
        }

        return run.Status switch
        {
            nameof(ActionExecutionStatus.Completed) => "Concluido",
            nameof(ActionExecutionStatus.Failed) => "Falhou",
            nameof(ActionExecutionStatus.Unsafe) => "Inseguro",
            nameof(ActionExecutionStatus.AwaitingApproval) => "Aguardando aprovacao",
            _ => run.Status
        };
    }

    public static CoronaColorSemantic GetAgentRunStatusColor(AgentRun run)
    {
        if (run.IsCancelled)
        {
            return CoronaColorSemantic.Neutral;
        }

        return run.Status switch
        {
            nameof(ActionExecutionStatus.Completed) => CoronaColorSemantic.Success,
            nameof(ActionExecutionStatus.Failed) => CoronaColorSemantic.Warning,
            nameof(ActionExecutionStatus.Unsafe) => CoronaColorSemantic.Danger,
            _ => CoronaColorSemantic.Neutral
        };
    }

    public static string FormatRunDuration(AgentRun run)
    {
        var start = run.StartedAt;
        var end = run.FinishedAt ?? DateTimeOffset.UtcNow;
        var duration = end - start;
        return duration.TotalSeconds < 60
            ? $"{Math.Max(0, (int)duration.TotalSeconds)}s"
            : $"{Math.Max(0, (int)duration.TotalMinutes)}min {Math.Max(0, duration.Seconds)}s";
    }

    public static bool IsResumableRun(AgentRun? run)
    {
        if (run is null)
        {
            return false;
        }

        return run.FinishedAt is null &&
               run.Status != nameof(ActionExecutionStatus.Completed) &&
               run.Status != nameof(ActionExecutionStatus.Failed) &&
               run.Status != nameof(ActionExecutionStatus.Unsafe);
    }

    public ConversationEntryViewModel? ActiveMissionTurn
    {
        get
        {
            if (Turns.Count == 0 || Turns[^1].Mode != InteractionMode.Agent)
            {
                return null;
            }

            return Turns[^1];
        }
    }

    public static string GetTurnStatusLabel(ActionExecutionStatus? status)
    {
        if (status is null)
        {
            return "Sem execucao";
        }

        return status switch
        {
            ActionExecutionStatus.Started => "Iniciado",
            ActionExecutionStatus.Validating => "Validando",
            ActionExecutionStatus.Planning => "Planejando",
            ActionExecutionStatus.Executing => "Executando",
            ActionExecutionStatus.Retrying => "Reexecutando",
            ActionExecutionStatus.Completed => "Concluido",
            ActionExecutionStatus.Failed => "Falhou",
            ActionExecutionStatus.Unsafe => "Inseguro",
            ActionExecutionStatus.AwaitingApproval => "Aguardando aprovacao",
            ActionExecutionStatus.Cancelled => "Cancelado",
            ActionExecutionStatus.Observing => "Observando",
            ActionExecutionStatus.Correcting => "Corrigindo",
            ActionExecutionStatus.Blocked => "Bloqueado",
            _ => status.Value.ToString()
        };
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

    public async Task UpdateInstalledModelAsync(string modelName)
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
        ModelFeedback = $"Verificando atualizacoes de {normalizedModel}...";
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
                activateAfterInstall: false,
                progress: reporter);

            if (result.Success)
            {
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
        HistoryFeedback = null;
        NotifyChanged();
        return Task.CompletedTask;
    }

    public async Task LoadConversationHistoryAsync()
    {
        if (IsHistoryLoading || conversationMemoryRepository is null)
        {
            return;
        }

        IsHistoryLoading = true;
        HistoryFeedback = null;
        NotifyChanged();

        try
        {
            var history = await conversationMemoryRepository.GetRecentConversationsAsync(
                ConversationHistoryLimit);

            ConversationHistory = history;
            if (ConversationHistory.Count == 0)
            {
                HistoryFeedback = "Nenhuma conversa persistida ainda.";
            }
        }
        catch (Exception ex)
        {
            HistoryFeedback = $"Nao foi possivel carregar o historico: {ex.Message}";
        }
        finally
        {
            IsHistoryLoading = false;
            if (!isDisposed)
            {
                NotifyChanged();
            }
        }
    }

    public async Task OpenConversationAsync(Guid conversationId)
    {
        if (IsSending || conversationMemoryRepository is null)
        {
            return;
        }

        HistoryFeedback = null;
        NotifyChanged();

        try
        {
            var messages = await conversationMemoryRepository.GetRecentMessagesAsync(
                conversationId,
                ConversationHistoryMessageLimit);

            turns.Clear();
            RebuildTurnsFromMessages(conversationId, messages);
            manager.SelectConversation(conversationId);
            ComposerText = string.Empty;
            SettingsFeedback = null;

            if (turns.Count == 0)
            {
                HistoryFeedback = "Conversa aberta, mas nenhuma mensagem foi encontrada.";
            }
        }
        catch (Exception ex)
        {
            HistoryFeedback = $"Nao foi possivel abrir a conversa: {ex.Message}";
        }
        finally
        {
            NotifyChanged();
        }
    }

    private void RebuildTurnsFromMessages(Guid conversationId, IReadOnlyList<ConversationMessage> messages)
    {
        ConversationEntryViewModel? pendingTurn = null;

        foreach (var message in messages)
        {
            if (message.Role == ConversationRoles.User)
            {
                pendingTurn = new ConversationEntryViewModel
                {
                    Prompt = message.Content,
                    Mode = InteractionMode.Chat,
                    RequestedModel = ActiveModelName,
                    ConversationId = conversationId
                };
                turns.Add(pendingTurn);
            }
            else if (message.Role == ConversationRoles.Assistant && pendingTurn is not null)
            {
                pendingTurn.Result = new ConversationTurn
                {
                    ConversationId = conversationId,
                    RequestId = Guid.NewGuid(),
                    Prompt = pendingTurn.Prompt,
                    Mode = pendingTurn.Mode,
                    ModelName = pendingTurn.RequestedModel,
                    Classification = InteractionMode.Chat.ToString(),
                    Response = message.Content
                };
                pendingTurn = null;
            }
        }
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

    public ApprovalScope SelectedApprovalScope { get; set; } = ApprovalScope.Once;

    public bool CanApproveCommand(CommandExecution command)
    {
        return !IsSending &&
               command.SafetyDecision == CommandSafetyDecisionType.AskApproval &&
               !command.Executed &&
               !command.ApprovedByUser &&
               !command.AutoApproved &&
               command.OperationKind is (
                   OperationKind.TerminalCommand or
                   OperationKind.ScriptExecution or
                   OperationKind.FileRead or
                   OperationKind.FileWrite or
                   OperationKind.ScriptContent or
                   OperationKind.PlannedPatch or
                   OperationKind.ProjectScaffold) &&
               !string.IsNullOrWhiteSpace(command.Run);
    }

    public static string GetApprovalScopeLabel(ApprovalScope scope) =>
        scope switch
        {
            ApprovalScope.Conversation => "Aprovar nesta conversa",
            ApprovalScope.Workspace => "Aprovar neste workspace",
            ApprovalScope.Category => "Auto-aprovar categoria",
            _ => "Aprovar uma vez"
        };

    public static ApprovalScope ParseApprovalScope(string? value) =>
        Enum.TryParse<ApprovalScope>(value, out var scope)
            ? scope
            : ApprovalScope.Once;

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

    public static string FormatConversationDate(ConversationSummary conversation)
    {
        var local = conversation.UpdatedAt.ToLocalTime();
        var now = DateTime.Now;

        if (local.Date == now.Date)
        {
            return $"Hoje, {local:HH\\:mm}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"Ontem, {local:HH\\:mm}";
        }

        if (local.Year == now.Year)
        {
            return local.ToString("dd/MM");
        }

        return local.ToString("dd/MM/yyyy");
    }

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
                turn.StreamingCurrentPlan = partialTurn.CurrentPlan;

                if (!isDisposed)
                {
                    NotifyChanged();
                }
            });

            turn.Result = await manager.ManageConversationAsync(
                new UserMessage(
                    normalizedPrompt,
                    turn.Mode,
                    WorkspaceRoot: QuickSettings.WorkspaceRoot),
                progress,
                turnCancellationSource.Token);
            turn.StreamingClassification = null;
            turn.StreamingResponse = null;
            turn.StreamingReasoning = null;
            turn.StreamingActionStatus = null;
            turn.StreamingActionEvents.Clear();
            turn.StreamingCommands.Clear();
            turn.StreamingCurrentPlan = null;
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
                _ = RefreshConversationHistoryAfterTurnAsync();
            }
        }
    }

    private async Task RefreshConversationHistoryAfterTurnAsync()
    {
        if (conversationMemoryRepository is null)
        {
            return;
        }

        try
        {
            ConversationHistory = await conversationMemoryRepository.GetRecentConversationsAsync(
                ConversationHistoryLimit);
        }
        catch
        {
            // History refresh is best-effort; the active turn is already complete.
        }
        finally
        {
            if (!isDisposed)
            {
                NotifyChanged();
            }
        }
    }

    private async Task CompleteApprovedCommandTurnAsync(
        ConversationEntryViewModel turn,
        CommandExecution command,
        ApprovalScope scope,
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
                scope,
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

    private async Task CompleteResumeTurnAsync(
        ConversationEntryViewModel turn,
        AgentRun run,
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
                turn.StreamingCurrentPlan = partialTurn.CurrentPlan;

                if (!isDisposed)
                {
                    NotifyChanged();
                }
            });

            turn.Result = await manager.ResumeTaskAsync(
                run,
                progress,
                turnCancellationSource.Token);
            turn.StreamingClassification = null;
            turn.StreamingResponse = null;
            turn.StreamingReasoning = null;
            turn.StreamingActionStatus = null;
            turn.StreamingActionEvents.Clear();
            turn.StreamingCommands.Clear();
            turn.StreamingCurrentPlan = null;
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
            turn.StreamingCurrentPlan = null;
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
                _ = RefreshConversationHistoryAfterTurnAsync();
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
        QuickSettings.WorkspaceRoot = RuntimeSettings.WorkspaceRoot;
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
    public Guid ConversationId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public InteractionMode Mode { get; set; }

    public string RequestedModel { get; set; } = string.Empty;

    public ConversationTurn? Result { get; set; }

    public string? Error { get; set; }

    public string? StreamingClassification { get; set; }

    public string? StreamingResponse { get; set; }

    public string? StreamingReasoning { get; set; }

    public ActionExecutionStatus? StreamingActionStatus { get; set; }

    public string? StreamingCurrentPlan { get; set; }

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

    public string WorkspaceRoot { get; set; } = string.Empty;

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
