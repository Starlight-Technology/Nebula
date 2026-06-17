using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Learning;
using Nebula.Llama.Client;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test;

public sealed class LlamaKnowledgeExtractorTest
{
    [Fact]
    public async Task extract_async_must_accept_domain_and_kind_as_enum_names()
    {
        const string sourceUrl =
            "https://learn.microsoft.com/powershell/module/microsoft.powershell.management/get-childitem";
        var llamaClient = new Mock<ILlamaClient>();
        llamaClient
            .Setup(value => value.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                $$"""
                {
                  "items": [
                    {
                      "sourceUrl": "{{sourceUrl}}",
                      "evidenceSummary": "Get-ChildItem lists child items.",
                      "confidence": 0.95,
                      "domain": "PowerShell",
                      "kind": "PowerShell Cmdlet",
                      "title": "Get-ChildItem",
                      "content": "Lists files and directories.",
                      "summary": "Lists child items.",
                      "examples": ["Get-ChildItem -Path C:\\Temp"],
                      "warnings": [],
                      "facts": ["Get-ChildItem lists child items."],
                      "normalizedCommand": "Get-ChildItem",
                      "language": "PowerShell",
                      "executableLocally": true
                    },
                    "unexpected trailing item"
                  ]
                }
                """);
        var extractor = new LlamaKnowledgeExtractor(
            llamaClient.Object,
            new JsonExtractor());
        var sources = new[]
        {
            new ResearchResult(
                "Get-ChildItem",
                sourceUrl,
                "Get-ChildItem lists files and directories.",
                "Microsoft",
                DateTimeOffset.UtcNow,
                1)
        };

        var result = await extractor.ExtractAsync(
            "PowerShell Get-ChildItem",
            KnowledgeDomain.PowerShell,
            sources,
            CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(KnowledgeDomain.PowerShell, item.Domain);
        Assert.Equal(KnowledgeItemKind.Command, item.Kind);
        Assert.Equal(sourceUrl, item.SourceUrl);
    }

    [Fact]
    public async Task extract_async_must_return_multiple_training_ready_items()
    {
        const string sourceUrl = "file:///D:/docs/comandos-cmd.txt";
        var llamaClient = new Mock<ILlamaClient>();
        llamaClient
            .Setup(value => value.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                $$"""
                {
                  "items": [
                    {
                      "sourceUrl": "{{sourceUrl}}",
                      "evidenceSummary": "Arp shows ARP cache entries.",
                      "confidence": 0.93,
                      "domain": "WindowsCommands",
                      "kind": "Command",
                      "title": "CMD: Arp",
                      "content": "O comando arp exibe ou altera entradas no cache ARP.",
                      "summary": "arp exibe ou altera entradas no cache ARP.",
                      "examples": ["arp -a"],
                      "warnings": [],
                      "facts": ["arp exibe entradas ARP."],
                      "normalizedCommand": "arp",
                      "language": "cmd",
                      "executableLocally": true
                    },
                    {
                      "sourceUrl": "{{sourceUrl}}",
                      "evidenceSummary": "Assoc changes extension associations.",
                      "confidence": 0.91,
                      "domain": "WindowsCommands",
                      "kind": "Command",
                      "title": "CMD: Assoc",
                      "content": "O comando assoc exibe ou altera associacoes de extensao.",
                      "summary": "assoc exibe ou altera associacoes de extensao.",
                      "examples": ["assoc .txt"],
                      "warnings": [],
                      "facts": ["assoc trabalha com associacoes de extensao."],
                      "normalizedCommand": "assoc",
                      "language": "cmd",
                      "executableLocally": true
                    }
                  ]
                }
                """);
        var extractor = new LlamaKnowledgeExtractor(
            llamaClient.Object,
            new JsonExtractor());

        var result = await extractor.ExtractAsync(
            "Aprenda comandos CMD.",
            KnowledgeDomain.WindowsCommands,
            [
                new ResearchResult(
                    "comandos-cmd.txt",
                    sourceUrl,
                    "Arp O comando arp exibe ou altera entradas no cache ARP.\nAssoc O comando assoc exibe ou altera associacoes de extensao.",
                    null,
                    DateTimeOffset.UtcNow,
                    1)
            ],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, item =>
        {
            Assert.Equal(KnowledgeDomain.WindowsCommands, item.Domain);
            Assert.Equal(KnowledgeItemKind.Command, item.Kind);
            Assert.Contains("llm-extracted", item.Tags);
            Assert.False(string.IsNullOrWhiteSpace(item.NormalizedCommand));
        });
    }

    [Fact]
    public async Task extract_async_must_use_deterministic_fallback_when_llm_fails()
    {
        const string sourceUrl = "file:///D:/docs/comandos-cmd.txt";
        var llamaClient = new Mock<ILlamaClient>();
        llamaClient
            .Setup(value => value.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("not json");
        var extractor = new LlamaKnowledgeExtractor(
            llamaClient.Object,
            new JsonExtractor(),
            fallbackExtractor: new KnowledgeExtractor());

        var result = await extractor.ExtractAsync(
            "Aprenda comandos CMD.",
            KnowledgeDomain.WindowsCommands,
            [
                new ResearchResult(
                    "comandos-cmd.txt",
                    sourceUrl,
                    "Arp O comando arp e usado para exibir ou alterar entradas no cache ARP.\nAssoc O comando assoc e usado para exibir ou alterar associacoes de extensao.",
                    null,
                    DateTimeOffset.UtcNow,
                    1)
            ],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.NormalizedCommand == "arp");
        Assert.Contains(result, item => item.NormalizedCommand == "assoc");
    }
}
