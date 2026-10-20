using Moq;

using Nebula.Agent.Application;
using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public sealed class TranslationServiceTest
{
    [Fact]
    public async Task translate_async_must_strip_thinking_blocks_from_llm_response()
    {
        var llamaClient = new Mock<ILlamaClient>();
        llamaClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<think>Analisando o pedido.</think>Listando os arquivos.");
        var logger = new Mock<ILogger>();
        var service = new TranslationService(llamaClient.Object, logger.Object);

        var translated = await service.TranslateAsync(
            "Listing the files.",
            "Portuguese (Brazil)");

        Assert.Equal("Listando os arquivos.", translated);
    }
}
