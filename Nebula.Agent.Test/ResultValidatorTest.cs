using Moq;
using Nebula.Agent.Application;
using Nebula.Core.Operations;
using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public sealed class ResultValidatorTest
{
    [Fact]
    public async Task validate_async_must_parse_string_operation_kind_in_corrected_action()
    {
        const string response = """
            {
              "verdict": "Mismatch",
              "reason": "The output path is incorrect.",
              "correctedAction": {
                "objective": "Write the file to the requested path.",
                "operationKind": "FileWrite",
                "command": "",
                "content": "corrected content",
                "targetPath": "/tmp/result.txt",
                "requiresSafetyReview": true
              }
            }
            """;
        var llamaClientMock = new Mock<ILlamaClient>();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var validator = new ResultValidator(
            llamaClientMock.Object,
            new JsonExtractor(),
            new ConsoleLogger());

        var result = await validator.ValidateAsync(
            "Create a file at /tmp/result.txt",
            new AgentToolAction(),
            "File written at /tmp/wrong.txt",
            "/tmp");

        Assert.Equal(ValidationVerdict.Mismatch, result.Verdict);
        Assert.NotNull(result.CorrectedAction);
        Assert.Equal(OperationKind.FileWrite, result.CorrectedAction.OperationKind);
        Assert.Equal("/tmp/result.txt", result.CorrectedAction.TargetPath);
    }
}
