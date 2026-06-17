using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Nebula.Core.Learning;
using Nebula.Core.Safety;

namespace Nebula.Services.Learning;

public sealed class LearningOrchestrator : ILearningOrchestrator
{
    private const string WebNotConfiguredWarning =
        "Web research provider is not configured. Using local/manual learning providers.";

    private readonly IReadOnlyList<IResearchProvider> providers;
    private readonly IKnowledgeExtractor extractor;
    private readonly IKnowledgeClassifier classifier;
    private readonly IKnowledgeRiskClassifier riskClassifier;
    private readonly IKnowledgeStore store;
    private readonly IKnowledgeScoreEngine scoreEngine;
    private readonly ILearningSourceReader sourceReader;
    private readonly Action<string>? log;

    public LearningOrchestrator(
        IEnumerable<IResearchProvider> providers,
        IKnowledgeExtractor extractor,
        IKnowledgeClassifier classifier,
        IKnowledgeRiskClassifier riskClassifier,
        IKnowledgeStore store,
        IKnowledgeScoreEngine scoreEngine,
        ILearningSourceReader? sourceReader = null,
        Action<string>? log = null)
    {
        this.providers = providers.ToList();
        this.extractor = extractor;
        this.classifier = classifier;
        this.riskClassifier = riskClassifier;
        this.store = store;
        this.scoreEngine = scoreEngine;
        this.sourceReader = sourceReader ?? new LearningSourceReader();
        this.log = log;
    }

    /// <summary>
    /// Learns structured knowledge from user text, local seeds, fake providers, and optional web providers.
    /// </summary>
    public async Task<LearningResult> LearnAsync(
        LearningOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Objective);

        var documents = new List<LearningSourceDocument>();
        var providerDiagnostics = new List<LearningProviderDiagnostic>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var webProviders = providers
            .Where(provider => provider.SourceType == LearningSourceType.WebResearch)
            .ToList();

        if (options.IncludeWebResearch &&
            (webProviders.Count == 0 || webProviders.All(provider => !provider.IsConfigured)))
        {
            warnings.Add(WebNotConfiguredWarning);
            if (webProviders.Count == 0)
            {
                providerDiagnostics.Add(new LearningProviderDiagnostic(
                    "WebResearchProvider",
                    LearningSourceType.WebResearch,
                    false,
                    0,
                    "Provider is not registered."));
            }
        }

        if (!string.IsNullOrWhiteSpace(options.UserProvidedText))
        {
            documents.Add(new LearningSourceDocument(
                options.Objective,
                options.UserProvidedText.Trim(),
                null,
                "UserProvidedText",
                DateTimeOffset.UtcNow,
                LearningSourceType.UserProvidedText));
            providerDiagnostics.Add(new LearningProviderDiagnostic(
                "UserProvidedText",
                LearningSourceType.UserProvidedText,
                true,
                1,
                "User text was supplied with the learning request."));
        }

        documents.AddRange(options.AdditionalDocuments);

        if (options.SourceFilePaths.Count > 0)
        {
            var fileDocuments = await sourceReader.ReadFilesAsync(
                options.SourceFilePaths,
                cancellationToken);
            documents.AddRange(fileDocuments);
            providerDiagnostics.Add(new LearningProviderDiagnostic(
                "ExplicitLocalFiles",
                LearningSourceType.LocalFile,
                true,
                fileDocuments.Count,
                $"{options.SourceFilePaths.Count} file path(s) requested."));
        }

        if (options.SourceUrls.Count > 0)
        {
            var siteDocuments = await sourceReader.ReadSitesAsync(
                options.SourceUrls,
                cancellationToken);
            documents.AddRange(siteDocuments);
            providerDiagnostics.Add(new LearningProviderDiagnostic(
                "ExplicitSites",
                LearningSourceType.WebResearch,
                true,
                siteDocuments.Count,
                $"{options.SourceUrls.Count} URL(s) requested."));
        }

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldUseProvider(provider, options))
            {
                continue;
            }

            if (!provider.IsConfigured)
            {
                if (provider.SourceType == LearningSourceType.WebResearch &&
                    !warnings.Contains(WebNotConfiguredWarning, StringComparer.Ordinal))
                {
                    warnings.Add(WebNotConfiguredWarning);
                }

                providerDiagnostics.Add(new LearningProviderDiagnostic(
                    provider.Name,
                    provider.SourceType,
                    false,
                    0,
                    "Provider is disabled or not configured."));
                continue;
            }

            try
            {
                var providerDocuments = await provider.SearchAsync(
                    options.Objective,
                    options.Domain,
                    cancellationToken);
                documents.AddRange(providerDocuments);
                providerDiagnostics.Add(new LearningProviderDiagnostic(
                    provider.Name,
                    provider.SourceType,
                    true,
                    providerDocuments.Count,
                    null));
            }
            catch (InvalidOperationException ex)
            {
                if (provider.SourceType == LearningSourceType.WebResearch)
                {
                    warnings.Add(WebNotConfiguredWarning);
                }
                else
                {
                    errors.Add(ex.Message);
                }

                providerDiagnostics.Add(new LearningProviderDiagnostic(
                    provider.Name,
                    provider.SourceType,
                    provider.IsConfigured,
                    0,
                    ex.Message));
            }
        }

        documents = documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Content))
            .DistinctBy(SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (documents.Count == 0)
        {
            const string noSourcesMessage =
                "Nenhuma fonte local, manual, fake ou web retornou documentos.";
            errors.Add(noSourcesMessage);
            return EmptyResult(
                success: false,
                noSourcesMessage,
                warnings,
                errors,
                providerDiagnostics);
        }

        var research = documents.Select(ToResearchResult).ToList();
        var drafts = await extractor.ExtractAsync(
            options.Objective,
            options.Domain,
            research,
            cancellationToken);
        LogDiagnostic("KnowledgeExtractor", drafts.Count, "items extracted");

        if (drafts.Count == 0)
        {
            var noItemsMessage =
                $"KnowledgeExtractor: 0 items extracted from {documents.Count} documents.";
            errors.Add(noItemsMessage);
            return EmptyResult(
                success: false,
                noItemsMessage,
                warnings,
                errors,
                providerDiagnostics,
                documents.Count);
        }

        var documentsByKey = documents.ToDictionary(
            SourceKey,
            StringComparer.OrdinalIgnoreCase);
        var items = new List<KnowledgeItem>();
        var sources = new List<KnowledgeSource>();
        var experiments = new List<KnowledgeExperiment>();
        var facts = new List<KnowledgeFact>();
        var evidence = new List<KnowledgeEvidence>();
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var dangerous = 0;

        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documentsByKey.TryGetValue(draft.SourceUrl, out var document))
            {
                skipped++;
                continue;
            }

            var classification = await classifier.ClassifyAsync(
                draft,
                cancellationToken);
            var risk = riskClassifier.Classify(draft);
            var item = CreateItem(
                options.Objective,
                draft,
                document,
                classification,
                risk);
            var existing = await FindExistingAsync(item.Hash, cancellationToken);
            if (existing is null)
            {
                created++;
            }
            else
            {
                updated++;
                item.Id = existing.Item.Id;
                item.CreatedAt = existing.Item.CreatedAt;
                item.ObservationCount = existing.Item.ObservationCount + 1;
            }

            if (item.IsDangerousInstruction ||
                item.RiskLevel == KnowledgeRiskLevel.Dangerous)
            {
                dangerous++;
            }

            var source = CreateSource(item, document);
            var itemFacts = CreateFacts(item, draft, document);
            var experiment = CreateSourceOnlyExperiment(item, document);
            item.VerificationScore = 0.55;
            item.IsValidated = experiment.Success;
            item.ValidationNotes = "Validated as source-backed knowledge; no command was executed.";
            item.FinalScore = scoreEngine.Calculate(item);

            await store.SaveAsync(
                item,
                [source],
                itemFacts,
                experiment,
                cancellationToken);

            items.Add(item);
            sources.Add(source);
            experiments.Add(experiment);
            facts.AddRange(itemFacts);
            evidence.Add(new KnowledgeEvidence(
                document.Title,
                document.SourceUri,
                document.ProviderName,
                document.RetrievedAt,
                TextExcerpt(document.Content),
                item.ConfidenceScore));
        }

        LogDiagnostic("Repository", created, "created");
        LogDiagnostic("Repository", updated, "updated");
        var message = BuildMessage(
            items.Count,
            created,
            updated,
            dangerous,
            warnings);

        return new LearningResult(
            items.Count > 0,
            message,
            created,
            updated,
            skipped,
            dangerous,
            documents.Count,
            items,
            sources,
            experiments,
            facts,
            evidence,
            warnings,
            errors,
            providerDiagnostics);
    }

    private static bool ShouldUseProvider(
        IResearchProvider provider,
        LearningOptions options) =>
        provider.SourceType switch
        {
            LearningSourceType.ManualSeed => options.IncludeManualSeeds,
            LearningSourceType.WebResearch => options.IncludeWebResearch,
            _ => true
        };

    private async Task<KnowledgeLookupResult?> FindExistingAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        if (store is IKnowledgeRepository repository)
        {
            return await repository.FindByHashAsync(hash, cancellationToken);
        }

        var details = await store.FindDetailsAsync(
            hash,
            minimumScore: 0,
            cancellationToken);
        return details.FirstOrDefault(result =>
            result.Item.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static KnowledgeItem CreateItem(
        string topic,
        KnowledgeItemDraft draft,
        LearningSourceDocument document,
        KnowledgeClassification classification,
        KnowledgeRiskAssessment risk)
    {
        var domain = classification.Domain == KnowledgeDomain.General
            ? draft.Domain
            : classification.Domain;
        var title = string.IsNullOrWhiteSpace(draft.Title)
            ? document.Title
            : draft.Title.Trim();
        var summary = string.IsNullOrWhiteSpace(draft.Summary)
            ? TextExcerpt(draft.Content)
            : draft.Summary.Trim();
        var content = string.IsNullOrWhiteSpace(draft.Content)
            ? document.Content
            : draft.Content.Trim();
        var tags = draft.Tags
            .Concat(risk.Tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var confidence = draft.Confidence > 0
            ? Math.Clamp(draft.Confidence, 0, 1)
            : risk.ConfidenceScore;

        return new KnowledgeItem
        {
            Domain = domain,
            Kind = classification.Kind,
            Topic = topic,
            Title = title,
            Content = content,
            Summary = summary,
            Examples = string.Join(Environment.NewLine, draft.Examples),
            Warnings = string.Join(
                Environment.NewLine,
                draft.Warnings.Concat(risk.Reasons).Distinct()),
            Tags = string.Join(",", tags),
            NormalizedCommand = draft.NormalizedCommand,
            Language = draft.Language,
            SourceUrl = SourceKey(document),
            SourceType = document.SourceType,
            SourceName = document.ProviderName,
            RiskLevel = risk.RiskLevel,
            ConfidenceScore = confidence,
            SourceScore = SourceScore(document.SourceType),
            ClassificationConfidence = confidence,
            SafetyScore = SafetyScore(risk.RiskLevel),
            Hash = KnowledgeHash.Create(domain, title, summary, content),
            LastSeenAt = DateTimeOffset.UtcNow,
            ObservationCount = 1,
            IsExecutableAdvice = risk.IsExecutableAdvice,
            IsDangerousInstruction = risk.IsDangerousInstruction,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static KnowledgeSource CreateSource(
        KnowledgeItem item,
        LearningSourceDocument document) =>
        new()
        {
            KnowledgeItemId = item.Id,
            Url = SourceKey(document),
            Title = document.Title,
            Publisher = document.ProviderName,
            ProviderName = document.ProviderName,
            SourceType = document.SourceType,
            ExtractedContent = TextExcerpt(document.Content, 4000),
            RetrievedAt = document.RetrievedAt,
            TrustScore = SourceScore(document.SourceType)
        };

    private static IReadOnlyList<KnowledgeFact> CreateFacts(
        KnowledgeItem item,
        KnowledgeItemDraft draft,
        LearningSourceDocument document)
    {
        var factValues = draft.Facts.Count == 0
            ? [item.Summary]
            : draft.Facts;
        return factValues
            .Where(fact => !string.IsNullOrWhiteSpace(fact))
            .Select(fact => fact.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(fact => new KnowledgeFact
            {
                KnowledgeItemId = item.Id,
                Fact = fact,
                Confidence = item.ConfidenceScore,
                SourceUrl = SourceKey(document)
            })
            .ToList();
    }

    private static KnowledgeExperiment CreateSourceOnlyExperiment(
        KnowledgeItem item,
        LearningSourceDocument document) =>
        new()
        {
            KnowledgeItemId = item.Id,
            VerificationKind = VerificationKind.SourceOnly,
            Success = true,
            StdOut = "Source-only learning evidence recorded.",
            EvidenceHash = KnowledgeHash.Create(
                item.Domain,
                document.Title,
                document.ProviderName,
                document.Content)
        };

    private static ResearchResult ToResearchResult(LearningSourceDocument document) =>
        new(
            document.Title,
            SourceKey(document),
            document.Content,
            document.ProviderName,
            document.RetrievedAt,
            SourceScore(document.SourceType));

    private static string SourceKey(LearningSourceDocument document) =>
        string.IsNullOrWhiteSpace(document.SourceUri)
            ? $"nebula://{document.SourceType}/{KnowledgeHash.Slug(document.Title)}"
            : document.SourceUri;

    private static double SourceScore(LearningSourceType sourceType) =>
        sourceType switch
        {
            LearningSourceType.ManualSeed => 0.95,
            LearningSourceType.UserProvidedText => 0.85,
            LearningSourceType.FakeResearch => 0.80,
            LearningSourceType.WebResearch => 0.70,
            LearningSourceType.LocalFile => 0.90,
            LearningSourceType.ExistingKnowledgeBase => 0.90,
            _ => 0.50
        };

    private static double SafetyScore(KnowledgeRiskLevel riskLevel) =>
        riskLevel switch
        {
            KnowledgeRiskLevel.Safe => 1,
            KnowledgeRiskLevel.LowRisk => 0.90,
            KnowledgeRiskLevel.MediumRisk => 0.60,
            KnowledgeRiskLevel.HighRisk => 0.25,
            KnowledgeRiskLevel.Dangerous => 0.05,
            _ => 0.40
        };

    private static string TextExcerpt(string value, int maxLength = 240)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        return text.Length <= maxLength
            ? text
            : text[..maxLength].TrimEnd() + "...";
    }

    private static LearningResult EmptyResult(
        bool success,
        string message,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        IReadOnlyList<LearningProviderDiagnostic> diagnostics,
        int documentsFound = 0) =>
        new(
            success,
            message,
            0,
            0,
            0,
            0,
            documentsFound,
            [],
            [],
            [],
            [],
            [],
            warnings,
            errors,
            diagnostics);

    private static string BuildMessage(
        int itemCount,
        int created,
        int updated,
        int dangerous,
        IReadOnlyList<string> warnings)
    {
        var sourceText = warnings.Any(value =>
            value.Contains("Web research provider", StringComparison.OrdinalIgnoreCase))
            ? "usando fontes locais/manuais"
            : "usando fontes configuradas";
        return
            $"Aprendi {itemCount} itens {sourceText}. " +
            $"Criados: {created}. Atualizados: {updated}. " +
            $"Itens perigosos identificados: {dangerous}. " +
            "Conhecimento salvo com evidencias e score.";
    }

    private void LogDiagnostic(string component, int count, string message) =>
        log?.Invoke($"[AGENT] {component}: {count} {message}");
}

public sealed class ManualSeedResearchProvider : IResearchProvider
{
    private static readonly IReadOnlyList<LearningSourceDocument> Documents =
    [
        Manual(
            "Boas praticas de seguranca para comandos shell",
            "Comandos shell devem ser avaliados antes da execucao. Comandos que removem arquivos recursivamente, apagam diretorios de usuario, alteram registro, modificam permissoes de sistema, desligam ou reiniciam a maquina, criam usuarios, instalam pacotes sem aprovacao, executam scripts baixados diretamente da internet ou escrevem fora de uma sandbox devem ser tratados como perigosos ou exigir aprovacao explicita. Comandos simples de leitura, como echo, pwd, dir, ls em diretorios controlados, dotnet --info, python --version e leitura de arquivos dentro de uma sandbox podem ser considerados de baixo risco.",
            "shell-security"),
        Manual(
            "Execucao segura em sandbox",
            "Operacoes automaticas de criacao, edicao e leitura de arquivos devem ocorrer preferencialmente dentro de uma pasta sandbox controlada. Caminhos absolutos fora da sandbox devem exigir aprovacao ou bloqueio. O agente deve registrar diretorio de execucao, comando proposto, decisao de seguranca, stdout, stderr e exit code.",
            "sandbox"),
        Manual(
            "Riscos de scripts remotos",
            "Comandos como curl URL | sh, wget URL | bash, iwr URL | iex e qualquer forma de baixar e executar codigo remoto diretamente sao perigosos. O agente deve bloquear ou exigir revisao manual. O conteudo deve ser baixado, inspecionado e validado antes de qualquer execucao.",
            "remote-scripts"),
        Manual(
            "Python Launcher no Windows",
            "No Windows, o comando py pode funcionar mesmo quando python nao esta no PATH. Para verificar a versao, tente python --version, py --version e python3 --version. Se python falhar mas py funcionar, scripts Python podem ser executados com py script.py.",
            "python-launcher-windows"),
        Manual(
            ".NET CLI basico",
            "dotnet --info mostra informacoes do SDK instalado. dotnet new console cria um projeto console. dotnet run executa o projeto atual. A criacao de projetos de teste deve ocorrer dentro da sandbox.",
            "dotnet-cli")
    ];

    public string Name => nameof(ManualSeedResearchProvider);

    public LearningSourceType SourceType => LearningSourceType.ManualSeed;

    public bool IsConfigured => true;

    /// <summary>
    /// Returns local seed documents relevant to the requested learning objective.
    /// </summary>
    public Task<IReadOnlyList<LearningSourceDocument>> SearchAsync(
        string objective,
        KnowledgeDomain domain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = objective.ToLowerInvariant();
        IReadOnlyList<LearningSourceDocument> result = Documents
            .Where(document => IsRelevant(document, normalized, domain))
            .ToList();
        return Task.FromResult(result);
    }

    private static bool IsRelevant(
        LearningSourceDocument document,
        string objective,
        KnowledgeDomain domain)
    {
        if (domain == KnowledgeDomain.ShellSecurity &&
            document.Content.Contains("comandos", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = $"{document.Title} {document.Content}".ToLowerInvariant();
        return objective.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Any(text.Contains);
    }

    private static LearningSourceDocument Manual(
        string title,
        string content,
        string slug) =>
        new(
            title,
            content,
            $"nebula://manual-seed/{slug}",
            nameof(ManualSeedResearchProvider),
            DateTimeOffset.UtcNow,
            LearningSourceType.ManualSeed);
}

public sealed class FakeResearchProvider : IResearchProvider
{
    private readonly IReadOnlyList<LearningSourceDocument> documents;

    public FakeResearchProvider(params LearningSourceDocument[] documents)
    {
        this.documents = documents;
    }

    public string Name => nameof(FakeResearchProvider);

    public LearningSourceType SourceType => LearningSourceType.FakeResearch;

    public bool IsConfigured => true;

    /// <summary>
    /// Returns deterministic fake documents for automated learning tests.
    /// </summary>
    public Task<IReadOnlyList<LearningSourceDocument>> SearchAsync(
        string objective,
        KnowledgeDomain domain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(documents);
    }
}

public sealed class WebResearchProvider : IResearchProvider
{
    private readonly IWebResearchService webResearchService;

    public WebResearchProvider(
        IWebResearchService webResearchService,
        bool isConfigured = true)
    {
        this.webResearchService = webResearchService;
        IsConfigured = isConfigured;
    }

    public string Name => nameof(WebResearchProvider);

    public LearningSourceType SourceType => LearningSourceType.WebResearch;

    public bool IsConfigured { get; }

    /// <summary>
    /// Converts web research results into source documents for the offline-first orchestrator.
    /// </summary>
    public async Task<IReadOnlyList<LearningSourceDocument>> SearchAsync(
        string objective,
        KnowledgeDomain domain,
        CancellationToken cancellationToken = default)
    {
        var results = await webResearchService.SearchAsync(
            objective,
            domain,
            cancellationToken);
        return results
            .Select(result => new LearningSourceDocument(
                result.Title,
                result.Snippet,
                result.Url,
                result.Publisher ?? nameof(WebResearchProvider),
                result.RetrievedAt,
                LearningSourceType.WebResearch))
            .ToList();
    }
}

public sealed class KnowledgeExtractor : IKnowledgeExtractor
{
    private static readonly Regex CommandReferenceRowRegex = new(
        @"^\s*(?<command>[A-Za-z][A-Za-z0-9.+_-]{1,40})\s+(?<description>O\s+comando\b.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts deterministic structured knowledge items from source text.
    /// </summary>
    public Task<IReadOnlyList<KnowledgeItemDraft>> ExtractAsync(
        string topic,
        KnowledgeDomain domain,
        IReadOnlyList<ResearchResult> sources,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drafts = sources.SelectMany(source =>
            ExtractFromSource(topic, domain, source)).ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeItemDraft>>(drafts);
    }

    private static IEnumerable<KnowledgeItemDraft> ExtractFromSource(
        string topic,
        KnowledgeDomain domain,
        ResearchResult source)
    {
        var commandRows = ExtractCommandReferenceRows(source).ToList();
        if (commandRows.Count > 0)
        {
            foreach (var draft in commandRows)
            {
                yield return draft;
            }

            yield break;
        }

        var text = $"{source.Title} {source.Snippet}".ToLowerInvariant();
        if (ContainsAny(text, "curl", "wget", "iwr", "remote", "remoto") &&
            ContainsAny(text, "| sh", "| bash", "| iex", "script"))
        {
            yield return Draft(
                source,
                KnowledgeDomain.ShellSecurity,
                "Evitar execucao direta de scripts remotos",
                "Executar scripts baixados diretamente da internet e perigoso.",
                source.Snippet,
                ["shell", "security", "remote-script", "dangerous"],
                ["Scripts remotos devem ser baixados, inspecionados e validados antes de qualquer execucao."],
                normalizedCommand: ExtractCommand(source.Snippet));
        }

        if (ContainsAny(text, "rm -rf", "del /s", "remove-item", "shutdown", "reboot", "format", "reg delete", "net user", "useradd"))
        {
            yield return Draft(
                source,
                KnowledgeDomain.ShellSecurity,
                "Bloquear comandos destrutivos ou administrativos",
                "Comandos destrutivos, administrativos ou de sistema exigem bloqueio ou aprovacao explicita.",
                source.Snippet,
                ["shell", "security", "dangerous-command"],
                ["Regras deterministicas de seguranca sempre vencem conhecimento aprendido."],
                normalizedCommand: ExtractCommand(source.Snippet));
        }

        if (ContainsAny(text, "sandbox", "diretorio controlado", "pasta sandbox"))
        {
            yield return Draft(
                source,
                KnowledgeDomain.ShellSecurity,
                "Executar operacoes automaticas dentro da sandbox",
                "Criacao, edicao e leitura automaticas devem ocorrer em uma sandbox controlada.",
                source.Snippet,
                ["shell", "security", "sandbox"],
                ["Caminhos absolutos fora da sandbox devem exigir aprovacao ou bloqueio."]);
        }

        if (ContainsAny(text, "echo", "pwd", "dotnet --info", "python --version", "py --version", "node --version"))
        {
            yield return Draft(
                source,
                domain == KnowledgeDomain.General
                    ? InferDomain(text)
                    : domain,
                "Comandos simples de leitura sao baixo risco",
                "Comandos simples de leitura ou versao podem ser baixo risco quando executados em contexto controlado.",
                source.Snippet,
                ["shell", "safe-command", "low-risk"],
                []);
        }

        if (ContainsAny(text, "python launcher", " py ", "py --version", "python nao esta no path", "python no path"))
        {
            yield return Draft(
                source,
                KnowledgeDomain.Python,
                "Python Launcher pode substituir python no Windows",
                "No Windows, py pode funcionar quando python nao esta no PATH; verifique com py --version.",
                source.Snippet,
                ["python", "windows", "launcher", "py"],
                []);
        }

        if (ContainsAny(text, "dotnet --info", "dotnet new", "dotnet run"))
        {
            yield return Draft(
                source,
                KnowledgeDomain.DotNet,
                ".NET CLI basico em sandbox",
                "dotnet --info inspeciona o SDK e projetos devem ser criados dentro da sandbox.",
                source.Snippet,
                ["dotnet", "cli", "sandbox"],
                []);
        }

        if (!ContainsAny(text, "curl", "wget", "iwr", "rm -rf", "sandbox", "python", "dotnet", "echo"))
        {
            yield return Draft(
                source,
                domain,
                source.Title,
                TextExcerpt(source.Snippet),
                source.Snippet,
                ["general"],
                []);
        }
    }

    private static KnowledgeItemDraft Draft(
        ResearchResult source,
        KnowledgeDomain domain,
        string title,
        string summary,
        string content,
        List<string> tags,
        List<string> warnings,
        string? normalizedCommand = null) =>
        new()
        {
            SourceUrl = source.Url,
            EvidenceSummary = TextExcerpt(content),
            Confidence = 0.90,
            Domain = domain,
            Kind = string.IsNullOrWhiteSpace(normalizedCommand)
                ? KnowledgeItemKind.Concept
                : KnowledgeItemKind.Command,
            Title = title,
            Content = content,
            Summary = summary,
            Tags = tags,
            Warnings = warnings,
            Facts = [summary],
            NormalizedCommand = normalizedCommand,
            ExecutableLocally = false
        };

    private static IEnumerable<KnowledgeItemDraft> ExtractCommandReferenceRows(
        ResearchResult source)
    {
        var lines = source.Snippet.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var match = CommandReferenceRowRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var command = match.Groups["command"].Value.Trim();
            var description = match.Groups["description"].Value.Trim();
            if (command.Equals("Comando", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var normalizedCommand = command.ToLowerInvariant();
            var summary = TextExcerpt(description);
            yield return new KnowledgeItemDraft
            {
                SourceUrl = source.Url,
                EvidenceSummary = summary,
                Confidence = 0.88,
                Domain = KnowledgeDomain.WindowsCommands,
                Kind = KnowledgeItemKind.Command,
                Title = $"CMD: {command}",
                Content = description,
                Summary = summary,
                Tags =
                [
                    "cmd",
                    "windows",
                    "command-reference",
                    normalizedCommand
                ],
                Facts =
                [
                    $"{command}: {summary}"
                ],
                NormalizedCommand = normalizedCommand,
                Language = "cmd",
                ExecutableLocally = true
            };
        }
    }

    private static KnowledgeDomain InferDomain(string text)
    {
        if (text.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgeDomain.Python;
        }

        if (text.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgeDomain.DotNet;
        }

        return KnowledgeDomain.ShellSecurity;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractCommand(string text)
    {
        var match = Regex.Match(
            text,
            @"(?:rm\s+-rf\s+\S+|del\s+/s\s+\S+|remove-item[^\.;]+|curl\s+\S+\s*\|\s*sh|wget\s+\S+\s*\|\s*bash|iwr\s+\S+\s*\|\s*iex)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string TextExcerpt(string value)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        return text.Length <= 180 ? text : text[..180].TrimEnd() + "...";
    }
}

public sealed class KnowledgeRiskClassifier : IKnowledgeRiskClassifier
{
    private static readonly Regex DangerousRegex = new(
        @"rm\s+-rf|del\s+/s|remove-item\s+.*-recurse|format\b|shutdown\b|reboot\b|diskpart\b|reg\s+delete|net\s+user|sudo\s+useradd|chmod\s+-r|chown\s+-r|curl\s+\S+\s*\|\s*sh|wget\s+\S+\s*\|\s*bash|iwr\s+\S+\s*\|\s*iex|administrador|root|fora\s+da\s+sandbox|outside\s+the\s+sandbox",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LowRiskRegex = new(
        @"\becho\b|\bpwd\b|\bcd\b|\bls\b|\bdir\b|dotnet\s+--info|python\s+--version|py\s+--version|node\s+--version|(?:python|py|python3)\s+\S+\.py|sandbox",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies extracted knowledge so dangerous instructions are stored as warnings, not recommendations.
    /// </summary>
    public KnowledgeRiskAssessment Classify(KnowledgeItemDraft draft)
    {
        var text = $"{draft.Title} {draft.Summary} {draft.Content} {draft.NormalizedCommand}";
        if (DangerousRegex.IsMatch(text))
        {
            return new KnowledgeRiskAssessment(
                KnowledgeRiskLevel.Dangerous,
                0.95,
                false,
                true,
                [.. draft.Tags, "dangerous"],
                ["Knowledge describes a dangerous command or execution pattern."]);
        }

        if (LowRiskRegex.IsMatch(text))
        {
            return new KnowledgeRiskAssessment(
                KnowledgeRiskLevel.LowRisk,
                0.85,
                true,
                false,
                [.. draft.Tags, "low-risk"],
                ["Knowledge describes read-only or sandbox-scoped behavior."]);
        }

        return new KnowledgeRiskAssessment(
            KnowledgeRiskLevel.Unknown,
            0.60,
            false,
            false,
            draft.Tags,
            ["No deterministic knowledge-risk rule matched."]);
    }
}

internal static class KnowledgeHash
{
    public static string Create(
        KnowledgeDomain domain,
        string title,
        string summary,
        string content)
    {
        var normalized = string.Join(
            '|',
            domain,
            Normalize(title),
            Normalize(summary),
            Normalize(content));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string Slug(string value)
    {
        var normalized = Regex.Replace(
            value.ToLowerInvariant(),
            @"[^a-z0-9]+",
            "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized)
            ? "document"
            : normalized;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
}
