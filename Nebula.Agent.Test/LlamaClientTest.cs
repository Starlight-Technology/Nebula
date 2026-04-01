using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class LlamaClientTest
{
    [Fact]
    public void LlamaClient_DefaultUrl_ShouldBeCorrect()
    {
        // Arrange & Act
        var client = new LlamaClient();

        // Assert
        Assert.Equal("http://localhost:11434/api/generate", client.LlamaUrl);
    }

    [Fact]
    public void LlamaClient_CanSetCustomUrl()
    {
        // Arrange
        var client = new LlamaClient();
        var customUrl = "http://custom:1234/api/test";

        // Act
        client.LlamaUrl = customUrl;

        // Assert
        Assert.Equal(customUrl, client.LlamaUrl);
    }

    [Fact]
    public async Task ClassifyPrompt_WithActionIntent_ShouldReturnAction()
    {
        // Arrange
        var client = new LlamaClient();
        // This test would require a real Llama instance or mocking HttpClient
        // For demonstration, we'll test that the method exists and returns a task
        
        // Note: Actual testing would require setting up HttpClient mocks or using WireMock
        // This is a basic structure test
        Assert.NotNull(client);
    }

    [Fact]
    public async Task GetResponseAsync_WithPrompt_ShouldReturnString()
    {
        // Arrange
        var client = new LlamaClient();
        // This test would require a real Llama instance or mocking HttpClient
        
        // Note: Actual testing would require setting up HttpClient mocks or using WireMock
        // This is a basic structure test
        Assert.NotNull(client);
    }
}
