using System.Net;
using System.Text;

using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class LlamaClientTest
{
    [Fact]
    public async Task get_response_async_must_capture_thinking_and_response_from_stream_chunks()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("mock-model");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
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
            };
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "mock-model");
        LlamaStreamUpdate? partialUpdate = null;
        var progress = new InlineProgress<LlamaStreamUpdate>(update => partialUpdate = update);

        var response = await client.GetResponseAsync("Explique o projeto", progress, CancellationToken.None);

        Assert.Equal("<think>Analisando o pedido</think>Resposta final", response);
        Assert.NotNull(partialUpdate);
        Assert.Equal("Resposta final", partialUpdate!.Response);
        Assert.Equal("Analisando o pedido", partialUpdate.Reasoning);
    }

    [Fact]
    public async Task get_response_async_must_disable_thinking_for_command_planner_prompt()
    {
        string? capturedPayload = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("mock-model");
            }

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
        Assert.Contains(@"""format"":""json""", capturedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""num_predict"":384", capturedPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Extract structured knowledge only from the supplied sources.\nReturn only JSON with this shape:")]
    [InlineData("You are Nebula's ReAct action controller.\nRespond ONLY with valid JSON and no markdown.")]
    public async Task get_response_async_must_use_json_mode_without_thinking_for_structured_prompts(string prompt)
    {
        string? capturedPayload = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("mock-model");
            }

            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"{}","done":true}""", Encoding.UTF8, "application/json")
            };
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "mock-model");

        await client.GetResponseAsync(prompt);

        Assert.NotNull(capturedPayload);
        Assert.Contains(@"""think"":false", capturedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""format"":""json""", capturedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""num_predict"":384", capturedPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_response_async_must_retry_without_thinking_when_model_rejects_thinking()
    {
        var capturedPayloads = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("llama3.2:1b");
            }

            capturedPayloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            if (capturedPayloads.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":"\"llama3.2:1b\" does not support thinking"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"Resposta sem thinking","done":true}""", Encoding.UTF8, "application/json")
            };
        });

        var client = new LlamaClient(new HttpClient(handler), defaultModel: "llama3.2:1b");

        var response = await client.GetResponseAsync("Explique o projeto");

        Assert.Equal("Resposta sem thinking", response);
        Assert.Equal(2, capturedPayloads.Count);
        Assert.Contains(@"""think"":true", capturedPayloads[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""think"":false", capturedPayloads[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_response_async_must_use_the_requested_model_override()
    {
        string? capturedPayload = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("chat-model", "learning-model");
            }

            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"response":"ok","done":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new LlamaClient(
            new HttpClient(handler),
            defaultModel: "chat-model");

        await client.GetResponseAsync(
            "Extraia conhecimento",
            "learning-model",
            progress: null,
            CancellationToken.None);

        Assert.NotNull(capturedPayload);
        Assert.Contains(
            @"""model"":""learning-model""",
            capturedPayload,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("chat-model", client.SelectedModel);
    }

    [Fact]
    public async Task get_response_async_must_fail_fast_when_model_load_timeout_expires()
    {
        var client = new LlamaClient(
            new HttpClient(new LoadTimeoutHttpMessageHandler()),
            defaultModel: "nemotron-3.5-lightning:latest",
            loadTimeoutSeconds: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync("Explique o projeto"));

        Assert.Contains("nao conseguiu carregar", error.Message);
        Assert.Contains("nemotron-3.5-lightning:latest", error.Message);
        Assert.Contains("memoria", error.Message);
    }

    [Fact]
    public async Task get_response_async_must_warm_up_model_when_it_is_not_loaded()
    {
        var payloads = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse();
            }

            payloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    payloads.Count == 1
                        ? """{"response":"ok","done":true}"""
                        : """{"response":"Resposta final","done":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new LlamaClient(new HttpClient(handler), defaultModel: "cold-model");

        var response = await client.GetResponseAsync("Explique o projeto");

        Assert.Equal("Resposta final", response);
        Assert.Equal(2, payloads.Count);
        Assert.Contains(@"""num_predict"":1", payloads[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""stream"":false", payloads[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"""prompt"":""Explique o projeto""", payloads[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_response_async_must_explain_ollama_model_load_failure()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (IsPsRequest(request))
            {
                return PsResponse("nemotron-3.5-lightning:latest");
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    """{"error":"timed out waiting for llama-server to start - "}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new LlamaClient(
            new HttpClient(handler),
            defaultModel: "nemotron-3.5-lightning:latest");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync("Explique o projeto"));

        Assert.Contains("nao conseguiu carregar", error.Message);
        Assert.Contains("nemotron-3.5-lightning:latest", error.Message);
        Assert.Contains("modelo menor", error.Message);
    }

    private static bool IsPsRequest(HttpRequestMessage request)
    {
        return request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath.EndsWith("/api/ps", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static HttpResponseMessage PsResponse(params string[] modelNames)
    {
        var models = string.Join(
            ",",
            modelNames.Select(name => $"{{\"name\":\"{name}\"}}"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"models\":[{models}]}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class LoadTimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (IsPsRequest(request))
            {
                return PsResponse();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
