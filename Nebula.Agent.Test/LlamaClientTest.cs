using System.Net;
using System.Text;

using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class LlamaClientTest
{
    [Fact]
    public async Task get_response_async_must_capture_thinking_and_response_from_stream_chunks()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"response":"","thinking":"Analisando","done":false}
                {"response":"","thinking":" o pedido","done":false}
                {"response":"Resposta final","done":false}
                {"response":"","done":true}
                """,
                Encoding.UTF8,
                "application/json")
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "mock-model");
        LlamaStreamUpdate? partialUpdate = null;
        var progress = new Progress<LlamaStreamUpdate>(update => partialUpdate = update);

        var response = await client.GetResponseAsync("Explique o projeto", progress, CancellationToken.None);

        Assert.Equal("<think>Analisando o pedido</think>Resposta final", response);
        Assert.NotNull(partialUpdate);
        Assert.Equal("Resposta final", partialUpdate!.Response);
        Assert.Equal("Analisando o pedido", partialUpdate.Reasoning);
    }

    [Fact]
    public async Task classify_prompt_must_send_system_prompt_and_disable_thinking()
    {
        string? capturedPayload = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"chat","done":true}""", Encoding.UTF8, "application/json")
            };
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "mock-model");

        var result = await client.ClassifyPrompt("Hello");

        Assert.Equal(ClassificationResult.Chat, result);
        Assert.NotNull(capturedPayload);
        Assert.Contains(@"""think"":false", capturedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""system"":", capturedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""prompt"":""Hello""", capturedPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_response_async_must_disable_thinking_for_command_planner_prompt()
    {
        string? capturedPayload = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"{}","done":true}""", Encoding.UTF8, "application/json")
            };
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "mock-model");

        await client.GetResponseAsync("You are a command planner.\nGenerate JSON.");

        Assert.NotNull(capturedPayload);
        Assert.Contains(@"""think"":false", capturedPayload, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
