using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Commands;
using Nebula.Core.Learning;
using Nebula.Llama.Client;

namespace Nebula.Agent.Test.Learning;

public sealed class OutputVerificationServiceTest
{
    private const string DefaultPrompt = "listar arquivos do D";
    private const string DefaultCommand = "Get-ChildItem D:";
    private const string DefaultOutput = "    Directory: D:\\\n\nMode                 LastWriteTime         Length Name\n----                 -------------         ------ ----\nd-----        2026-06-20     14:00                Testes";
    private const string DefaultWorkingDir = "C:\\Users\\test";

    private readonly Mock<ILlamaClient> llamaMock = new();
    private readonly Mock<IJsonExtractor> extractorMock = new();
    private readonly Mock<ICommandIntentParser> parserMock = new();
    private readonly Mock<ICommandResolver> resolverMock = new();
    private readonly Mock<IRuntimeCommandEnvironmentDetector> envMock = new();
    private readonly Mock<ILogger> loggerMock = new();

    private OutputVerificationService CreateService()
    {
        return new OutputVerificationService(
            llamaMock.Object,
            extractorMock.Object,
            parserMock.Object,
            resolverMock.Object,
            envMock.Object,
            loggerMock.Object);
    }

    private void SetupLlamaResponse(string jsonContent)
    {
        var parsed = ModelResponse.Parse(jsonContent);
        extractorMock
            .Setup(e => e.ExtractJsonObject(It.IsAny<string>()))
            .Returns(parsed.Response);
        llamaMock
            .Setup(l => l.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jsonContent);
    }

    [Fact]
    public async Task match_verdict_when_llm_returns_match()
    {
        SetupLlamaResponse("""
            {"verdict":"Match","reason":"Output shows directory listing of D: as requested.","correctedCommand":""}
            """);
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, DefaultOutput, DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Match, result.Verdict);
        Assert.Contains("D:", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task mismatch_verdict_when_llm_returns_mismatch()
    {
        SetupLlamaResponse("""
            {"verdict":"Mismatch","reason":"Output shows C: drive instead of the requested D: drive.","correctedCommand":"Get-ChildItem D:\\"}
            """);
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, DefaultOutput, DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Mismatch, result.Verdict);
        Assert.Contains("C:", result.Reason);
        Assert.Equal("Get-ChildItem D:\\", result.CorrectedCommand);
    }

    [Fact]
    public async Task uncertain_verdict_when_llm_returns_uncertain()
    {
        SetupLlamaResponse("""
            {"verdict":"Uncertain","reason":"Output is incomplete.","correctedCommand":""}
            """);
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, DefaultOutput, DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Uncertain, result.Verdict);
    }

    [Fact]
    public async Task uncertain_when_output_is_empty()
    {
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, "", DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Uncertain, result.Verdict);
    }

    [Fact]
    public async Task uncertain_when_llm_throws()
    {
        llamaMock
            .Setup(l => l.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Ollama unavailable"));
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, DefaultOutput, DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Uncertain, result.Verdict);
        Assert.Contains("Ollama unavailable", result.Reason);
    }

    [Fact]
    public async Task mismatch_without_corrected_command()
    {
        SetupLlamaResponse("""
            {"verdict":"Mismatch","reason":"Wrong directory listed.","correctedCommand":null}
            """);
        var service = CreateService();

        var result = await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, DefaultOutput, DefaultWorkingDir);

        Assert.Equal(OutputVerdict.Mismatch, result.Verdict);
        Assert.Null(result.CorrectedCommand);
    }

    [Fact]
    public async Task long_output_is_truncated_before_sending_to_llm()
    {
        var longOutput = new string('A', 5000);
        string? capturedPrompt = null;
        llamaMock
            .Setup(l => l.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                capturedPrompt = (string)llamaMock.Invocations
                    .First(i => i.Method.Name == nameof(ILlamaClient.GetResponseAsync))
                    .Arguments[0];
                return """{"verdict":"Match","reason":"OK.","correctedCommand":""}""";
            });
        extractorMock
            .Setup(e => e.ExtractJsonObject(It.IsAny<string>()))
            .Returns((string s) => s);

        var service = CreateService();

        await service.VerifyAsync(
            DefaultPrompt, DefaultCommand, longOutput, DefaultWorkingDir);

        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain(new string('A', 4000), capturedPrompt);
    }
}
