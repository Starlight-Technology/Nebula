using Moq;

using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public class ManagerTest
{
    [Fact]
    public async Task ManageResponse_Should_Send_Prompt_To_Llamma()
    {
        var expected = "Mocked response";
        var testPrompt = "Test prompt";
        var llamaClientMock = new Mock<ILlamaClient>();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(expected);

        var executorMock = new Mock<IShellExecutor>();
        executorMock.Setup(executor => executor.RunCommandAsync(It.IsAny<string>()))
            .ReturnsAsync("Mocked command output");

        Manager manager = new(llamaClientMock.Object, executorMock.Object);

        var result = await manager.ManageResponse("Test prompt");

        Assert.Contains(expected, result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(testPrompt), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_Should_Handle_Empty_Response()
    {
        var expected = "";
        var testPrompt = "Test prompt";
        var llamaClientMock = new Mock<ILlamaClient>();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(expected);
        var executorMock = new Mock<IShellExecutor>();
        executorMock.Setup(executor => executor.RunCommandAsync(It.IsAny<string>()))
            .ReturnsAsync("Mocked command output");

        Manager manager = new(llamaClientMock.Object, executorMock.Object);
        var result = await manager.ManageResponse("Test prompt");
        Assert.Contains(expected, result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(testPrompt), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_Should_Handle_Empty_Prompt()
    {
        var expected = "The prompt are empty, write something.";
        var testPrompt = "";
        var llamaClientMock = new Mock<ILlamaClient>();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(expected);
        var executorMock = new Mock<IShellExecutor>();
        executorMock.Setup(executor => executor.RunCommandAsync(It.IsAny<string>()))
            .ReturnsAsync("Mocked command output");

        Manager manager = new(llamaClientMock.Object, executorMock.Object);
        var result = await manager.ManageResponse("");
        Assert.Contains(expected, result);
        llamaClientMock.Verify(x => x.GetResponseAsync(testPrompt), Times.Never);
    }

    [Fact]
    public async Task ManageResponse_Should_Handle_Prompt_When_Chat()
    {
        var expected = "Hello, i'm ok, and you?";
        var testPrompt = "Hello, how are you?";
        var llamaClientMock = new Mock<ILlamaClient>();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(expected);
        var executorMock = new Mock<IShellExecutor>();
        executorMock.Setup(executor => executor.RunCommandAsync(It.IsAny<string>()))
            .ReturnsAsync("Mocked command output");

        Manager manager = new(llamaClientMock.Object, executorMock.Object);
        var result = await manager.ManageResponse(testPrompt);
        Assert.Contains(expected, result);
        llamaClientMock.Verify(x => x.GetResponseAsync(testPrompt), Times.Once);
    }
}
