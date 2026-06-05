using Bunit;

using Nebula.Agent;
using Nebula.App.Shared.Pages;
using Nebula.App.Shared.Setup;
using Nebula.Llama.Client;

namespace Nebula.App.Test;

public sealed class HomePageTests : HomePageTestContext
{
    [Fact]
    public void send_async_must_show_pending_state_and_then_render_response_and_reasoning()
    {
        var completion = new TaskCompletionSource<ConversationTurn>();
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = (_, progress, _) =>
            {
                progress?.Report(new ConversationTurn
                {
                    Prompt = "Explique a arquitetura",
                    ModelName = "qwen3:8b",
                    Classification = ClassificationResult.Chat.ToString(),
                    Reasoning = "Raciocinio parcial do mock"
                });

                return completion.Task;
            }
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();

        component.Find("textarea").Input("Explique a arquitetura");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Preparando a resposta do agente", component.Markup);
            Assert.Contains("Explique a arquitetura", component.Markup);
            Assert.Contains("Raciocinio parcial do mock", component.Markup);
        });

        completion.SetResult(new ConversationTurn
        {
            Prompt = "Explique a arquitetura",
            ModelName = "qwen3:8b",
            Classification = ClassificationResult.Chat.ToString(),
            Response = "Resposta do mock",
            Reasoning = "Raciocinio do mock"
        });

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Resposta do mock", component.Markup);
            Assert.Contains("Raciocinio do mock", component.Markup);
            Assert.DoesNotContain("Preparando a resposta do agente", component.Markup);
        });
    }

    [Fact]
    public void send_async_must_render_error_panel_when_manager_fails()
    {
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = (_, _, _) => Task.FromException<ConversationTurn>(new InvalidOperationException("Falha simulada"))
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();

        component.Find("textarea").Input("Teste com erro");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Nao consegui concluir este turno", component.Markup);
            Assert.Contains("Falha simulada", component.Markup);
        });
    }

    [Fact]
    public void send_async_must_show_cancel_button_and_render_cancelled_turn()
    {
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = async (_, progress, cancellationToken) =>
            {
                progress?.Report(new ConversationTurn
                {
                    Prompt = "Execute uma acao lenta",
                    ModelName = "qwen3:8b",
                    Classification = ClassificationResult.Action.ToString(),
                    ActionStatus = ActionExecutionStatus.Executing,
                    ActionEvents =
                    [
                        new ActionExecutionEvent
                        {
                            Status = ActionExecutionStatus.Executing,
                            Attempt = 1,
                            Title = "Tool call",
                            Message = "Running slow command",
                            Command = "slowcmd"
                        }
                    ]
                });

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ConversationTurn();
            }
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();

        component.Find("textarea").Input("Execute uma acao lenta");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Tool call", component.Markup);
            Assert.Contains("slowcmd", component.Markup);
            Assert.Contains("Cancelar", component.Markup);
        });

        FindButton(component, "Cancelar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Execucao cancelada pelo usuario", component.Markup);
            Assert.DoesNotContain("Preparando a resposta do agente", component.Markup);
        });
    }

    private sealed class FakeManager : IManager
    {
        public Guid ActiveConversationId { get; private set; } = Guid.NewGuid();

        public Func<string, IProgress<ConversationTurn>?, CancellationToken, Task<ConversationTurn>> ManageConversationAsyncHandler { get; set; }
            = (prompt, _, _) => Task.FromResult(new ConversationTurn
            {
                Prompt = prompt,
                ModelName = "qwen3:8b",
                Classification = ClassificationResult.Chat.ToString(),
                Response = "Resposta padrao"
            });

        public Task<ConversationTurn> ManageConversationAsync(string prompt)
        {
            return ManageConversationAsync(prompt, progress: null, cancellationToken: default);
        }

        public Task<ConversationTurn> ManageConversationAsync(
            string prompt,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return ManageConversationAsyncHandler(prompt, progress, cancellationToken);
        }

        public Task<string> ManageResponse(string prompt)
        {
            return Task.FromResult(prompt);
        }

        public Guid StartNewConversation()
        {
            ActiveConversationId = Guid.NewGuid();
            return ActiveConversationId;
        }

        public Task<string> GenerateCommandSteps(string userRequest)
        {
            throw new NotSupportedException();
        }

        public Task<bool> VerifyCommandCorrectAsync(Command command)
        {
            throw new NotSupportedException();
        }

        public Task<bool> VerifyCommandSafetyAsync(Command command)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeLlamaClient : ILlamaClient
    {
        public string LlamaUrl { get; set; } = "http://localhost:11434/api/generate";

        public string SelectedModel { get; private set; } = "qwen3:8b";

        public Task<ClassificationResult> ClassifyPrompt(string prompt)
        {
            return Task.FromResult(ClassificationResult.Chat);
        }

        public Task<string> GetResponseAsync(string prompt)
        {
            return Task.FromResult(prompt);
        }

        public Task<string> GetResponseAsync(
            string prompt,
            IProgress<LlamaStreamUpdate>? progress,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LlamaStreamUpdate
            {
                Response = prompt,
                Reasoning = "Mock reasoning"
            });

            return Task.FromResult(prompt);
        }

        public Task<LlamaRuntimeState> GetRuntimeStateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlamaRuntimeState
            {
                GenerateUrl = LlamaUrl,
                ApiBaseUrl = "http://localhost:11434/api",
                SelectedModel = SelectedModel,
                IsAvailable = true,
                SelectedModelInstalled = true,
                InstalledModels =
                [
                    new LlamaModelInfo
                    {
                        Name = SelectedModel,
                        SizeBytes = 4L * 1024 * 1024 * 1024,
                        ModifiedAt = DateTimeOffset.UtcNow,
                        Details = new LlamaModelDetails
                        {
                            Family = "qwen",
                            ParameterSize = "8B",
                            QuantizationLevel = "Q4_K_M",
                            Format = "gguf"
                        }
                    }
                ]
            });
        }

        public Task<IReadOnlyList<LlamaModelInfo>> GetInstalledModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlamaModelInfo>>(
            [
                new LlamaModelInfo
                {
                    Name = SelectedModel
                }
            ]);
        }

        public Task<bool> SelectModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            SelectedModel = modelName;
            return Task.FromResult(true);
        }

        public Task<LlamaPullResult> PullModelAsync(string modelName, bool activateAfterInstall = false, IProgress<LlamaPullProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            SelectedModel = modelName;
            return Task.FromResult(new LlamaPullResult
            {
                ModelName = modelName,
                Success = true,
                Activated = activateAfterInstall,
                Message = "Mock pull concluido."
            });
        }
    }
}
