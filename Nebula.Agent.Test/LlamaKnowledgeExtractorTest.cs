using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Learning;
using Nebula.Llama.Client;

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
}
