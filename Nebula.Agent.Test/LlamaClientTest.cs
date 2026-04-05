using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class LlamaClientTest
{
    [Fact]
    public void llama_client_must_return_default_url_when_created()
    {
        // Arrange & Act
        var client = new LlamaClient();

        // Assert
        Assert.Equal("http://localhost:11434/api/generate", client.LlamaUrl);
    }

    [Fact]
    public void llama_client_must_allow_custom_url_when_url_is_updated()
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
    public void llama_client_must_create_instance_when_initialized()
    {
        var client = new LlamaClient();

        Assert.NotNull(client);
    }
}
