using Moq;

using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public class ManagerTest
{
    private Mock<ILlamaClient> CreateMockLlamaClient()
    {
        return new Mock<ILlamaClient>();
    }

    private Mock<IShellExecutor> CreateMockExecutor()
    {
        return new Mock<IShellExecutor>();
    }

    private Mock<IJsonExtractor> CreateMockJsonExtractor()
    {
        return new Mock<IJsonExtractor>();
    }

    private Mock<ILogger> CreateMockLogger()
    {
        return new Mock<ILogger>();
    }

    #region ManageResponse Tests

    [Fact]
    public async Task ManageResponse_WithEmptyPrompt_ShouldReturnEmptyPromptMessage()
    {
        // Arrange
        var llamaClientMock = CreateMockLlamaClient();
        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse("");

        // Assert
        Assert.Equal("The prompt are empty, write something.", result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ManageResponse_WithWhitespacePrompt_ShouldReturnEmptyPromptMessage()
    {
        // Arrange
        var llamaClientMock = CreateMockLlamaClient();
        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse("   ");

        // Assert
        Assert.Equal("The prompt are empty, write something.", result);
    }

    [Fact]
    public async Task ManageResponse_WithChatClassification_ShouldCallHandleChat()
    {
        // Arrange
        var testPrompt = "Hello, how are you?";
        var expectedResponse = "I'm doing well, thanks for asking!";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Chat);
        llamaClientMock.Setup(x => x.GetResponseAsync(testPrompt))
            .ReturnsAsync(expectedResponse);

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        Assert.Equal(expectedResponse, result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(testPrompt), Times.Once);
        llamaClientMock.Verify(x => x.GetResponseAsync(testPrompt), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_WithActionClassification_ShouldProcessCommands()
    {
        // Arrange
        var testPrompt = "Create a file";
        var commandJson = """{"Steps": [{"Id": 1, "Objective": "Create file", "Run": "touch test.txt"}]}""";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson);

        var executorMock = CreateMockExecutor();
        executorMock.Setup(x => x.RunCommandAsync(It.IsAny<string>()))
            .ReturnsAsync("File created");

        var jsonExtractorMock = CreateMockJsonExtractor();
        jsonExtractorMock.Setup(x => x.ExtractJsonObject(commandJson))
            .Returns(commandJson);

        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        Assert.Equal("Commands executed", result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(testPrompt), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_WithUnknownClassification_ShouldReturnErrorMessage()
    {
        // Arrange
        var testPrompt = "Test prompt";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Unknown);

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        Assert.Contains("Unable to classify", result);
        llamaClientMock.Verify(x => x.ClassifyPrompt(testPrompt), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_WhenExceptionOccurs_ShouldReturnErrorMessage()
    {
        // Arrange
        var testPrompt = "Test prompt";
        var testException = new InvalidOperationException("Test error");

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ThrowsAsync(testException);

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        Assert.Contains("Error managing response:", result);
        loggerMock.Verify(x => x.LogError(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region GenerateCommandSteps Tests

    [Fact]
    public async Task GenerateCommandSteps_WithValidPrompt_ShouldReturnResponse()
    {
        // Arrange
        var userRequest = "Create a file";
        var expectedResponse = """{"Steps": [{"Id": 1, "Objective": "Create file", "Run": "touch test.txt"}]}""";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(expectedResponse);

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.GenerateCommandSteps(userRequest);

        // Assert
        Assert.Equal(expectedResponse, result);
        llamaClientMock.Verify(x => x.GetResponseAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GenerateCommandSteps_WithEmptyRequest_ShouldThrowArgumentException()
    {
        // Arrange
        var llamaClientMock = CreateMockLlamaClient();
        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GenerateCommandSteps(""));
    }

    [Fact]
    public async Task GenerateCommandSteps_WithNullRequest_ShouldThrowArgumentException()
    {
        // Arrange
        var llamaClientMock = CreateMockLlamaClient();
        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GenerateCommandSteps(null!));
    }

    [Fact]
    public async Task GenerateCommandSteps_WithWhitespaceRequest_ShouldThrowArgumentException()
    {
        // Arrange
        var llamaClientMock = CreateMockLlamaClient();
        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GenerateCommandSteps("   "));
    }

    #endregion

    #region VerifyCommandCorrectAsync Tests

    [Fact]
    public async Task VerifyCommandCorrectAsync_WithYesResponse_ShouldReturnTrue()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("Yes");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandCorrectAsync(command);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyCommandCorrectAsync_WithNoResponse_ShouldReturnFalse()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("No");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandCorrectAsync(command);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task VerifyCommandCorrectAsync_WithYesResponseWithWhitespace_ShouldReturnTrue()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("  Yes  ");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandCorrectAsync(command);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyCommandCorrectAsync_WithLowercaseYes_ShouldReturnTrue()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("yes");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandCorrectAsync(command);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region VerifyCommandSafetyAsync Tests

    [Fact]
    public async Task VerifyCommandSafetyAsync_WithYesResponse_ShouldReturnTrue()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("Yes");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandSafetyAsync(command);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyCommandSafetyAsync_WithNoResponse_ShouldReturnFalse()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("No");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandSafetyAsync(command);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task VerifyCommandSafetyAsync_WithInvalidResponse_ShouldReturnFalse()
    {
        // Arrange
        var command = new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" };

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync("Maybe");

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.VerifyCommandSafetyAsync(command);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Command Execution Tests

    [Fact]
    public async Task ManageResponse_WithValidActionAndSafeCommand_ShouldExecuteCommand()
    {
        // Arrange
        var testPrompt = "Create a file";
        var commandJson = """{"Steps": [{"Id": 1, "Objective": "Create file", "Run": "touch test.txt"}]}""";
        var commandOutput = "File created successfully";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("Yes") // Safe verification
            .ReturnsAsync("Yes"); // Correct verification

        var executorMock = CreateMockExecutor();
        executorMock.Setup(x => x.RunCommandAsync("touch test.txt"))
            .ReturnsAsync(commandOutput);

        var jsonExtractorMock = CreateMockJsonExtractor();
        jsonExtractorMock.Setup(x => x.ExtractJsonObject(commandJson))
            .Returns(commandJson);

        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        Assert.Equal("Commands executed", result);
        executorMock.Verify(x => x.RunCommandAsync("touch test.txt"), Times.Once);
        loggerMock.Verify(x => x.Log(commandOutput), Times.Once);
    }

    [Fact]
    public async Task ManageResponse_WithUnsafeCommand_ShouldNotExecute()
    {
        // Arrange
        var testPrompt = "Delete system files";
        var commandJson = """{"Steps": [{"Id": 1, "Objective": "Delete files", "Run": "rm -rf /"}]}""";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("No"); // Not safe

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        jsonExtractorMock.Setup(x => x.ExtractJsonObject(commandJson))
            .Returns(commandJson);

        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act
        var result = await manager.ManageResponse(testPrompt);

        // Assert
        // Command should not be executed since safety check failed
        executorMock.Verify(x => x.RunCommandAsync(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region JSON Extraction Error Handling Tests

    [Fact]
    public async Task ManageResponse_WithJsonExtractionError_ShouldHandleGracefully()
    {
        // Arrange
        var testPrompt = "Create a file";
        var invalidJson = "This is not JSON";

        var llamaClientMock = CreateMockLlamaClient();
        llamaClientMock.Setup(x => x.ClassifyPrompt(testPrompt))
            .ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(x => x.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(invalidJson);

        var executorMock = CreateMockExecutor();
        var jsonExtractorMock = CreateMockJsonExtractor();
        jsonExtractorMock.Setup(x => x.ExtractJsonObject(invalidJson))
            .Throws<ArgumentException>();

        var loggerMock = CreateMockLogger();

        var manager = new Manager(llamaClientMock.Object, executorMock.Object, jsonExtractorMock.Object, loggerMock.Object);

        // Act & Assert
        // Should handle the error gracefully
        _ = await manager.ManageResponse(testPrompt);
        loggerMock.Verify(x => x.LogError(It.IsAny<string>()), Times.Once);
    }

    #endregion
}
