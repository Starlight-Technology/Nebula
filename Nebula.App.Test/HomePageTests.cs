using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent;
using Nebula.App.Shared.Pages;
using Nebula.App.Shared.Setup;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
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
                    Mode = InteractionMode.Chat,
                    ModelName = "qwen3:8b",
                    Classification = InteractionMode.Chat.ToString(),
                    Response = "Resposta parcial do mock",
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
            Assert.Contains("Preparando resposta de conversa", component.Markup);
            Assert.Contains("Explique a arquitetura", component.Markup);
            Assert.Contains("Raciocinio parcial do mock", component.Markup);
            AssertReasoningBeforeResponse(component);
        });

        completion.SetResult(new ConversationTurn
        {
            Prompt = "Explique a arquitetura",
            Mode = InteractionMode.Chat,
            ModelName = "qwen3:8b",
            Classification = InteractionMode.Chat.ToString(),
            Response = "Resposta do mock",
            Reasoning = "Raciocinio do mock"
        });

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Resposta do mock", component.Markup);
            Assert.Contains("Raciocinio do mock", component.Markup);
            Assert.DoesNotContain("Preparando resposta de conversa", component.Markup);
            AssertReasoningBeforeResponse(component);
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
                    Mode = InteractionMode.Agent,
                    ModelName = "qwen3:8b",
                    Classification = InteractionMode.Agent.ToString(),
                    ActionStatus = ActionExecutionStatus.Executing,
                    ActionEvents =
                    [
                        new ActionExecutionEvent
                        {
                            Kind = ActionExecutionEventKind.ActionStarted,
                            Status = ActionExecutionStatus.Executing,
                            Step = 1,
                            Attempt = 1,
                            Title = "Action started",
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

        FindModeButton(component, "Agente").Click();
        component.Find("textarea").Input("Execute uma acao lenta");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Action started", component.Markup);
            Assert.Contains("slowcmd", component.Markup);
            Assert.Contains("Cancelar", component.Markup);
        });

        FindButton(component, "Cancelar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Execucao cancelada pelo usuario", component.Markup);
            Assert.DoesNotContain("Executando tarefa do agente", component.Markup);
        });
    }

    [Fact]
    public void mode_selector_must_send_selected_mode_to_backend_and_keep_session_choice()
    {
        UserMessage? receivedMessage = null;
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = (message, _, _) =>
            {
                receivedMessage = message;
                return Task.FromResult(new ConversationTurn
                {
                    Prompt = message.Content,
                    Mode = message.Mode,
                    ModelName = "qwen3:8b",
                    Classification = message.Mode.ToString(),
                    Response = "Executado"
                });
            }
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();
        FindModeButton(component, "Agente").Click();
        component.Find("textarea").Input("Crie um arquivo teste.txt");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.NotNull(receivedMessage);
            Assert.Equal("Crie um arquivo teste.txt", receivedMessage!.Content);
            Assert.Equal(InteractionMode.Agent, receivedMessage.Mode);
            Assert.Contains(
                "nebula-mode-selector__button is-active",
                FindModeButton(component, "Agente").ClassName);
        });
    }

    [Fact]
    public void learn_sources_button_must_send_files_and_sites_to_agent_mode()
    {
        UserMessage? receivedMessage = null;
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = (message, _, _) =>
            {
                receivedMessage = message;
                return Task.FromResult(new ConversationTurn
                {
                    Prompt = message.Content,
                    Mode = message.Mode,
                    ModelName = "qwen3:8b",
                    Classification = message.Mode.ToString(),
                    Response = "Aprendi fontes adicionadas."
                });
            }
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();
        component.Find(".nebula-composer__input")
            .Input("Aprenda com estes materiais");
        component.FindAll(".nebula-learning-sources__input")[0]
            .Input(@"C:\docs\manual.txt");
        component.FindAll(".nebula-learning-sources__input")[1]
            .Input("https://example.test/learn");
        FindButton(component, "Aprender fontes").Click();

        component.WaitForAssertion(() =>
        {
            Assert.NotNull(receivedMessage);
            Assert.Equal(InteractionMode.Agent, receivedMessage!.Mode);
            Assert.Contains(
                "[learning_source_files]",
                receivedMessage.Content);
            Assert.Contains(
                @"C:\docs\manual.txt",
                receivedMessage.Content);
            Assert.Contains(
                "[learning_source_sites]",
                receivedMessage.Content);
            Assert.Contains(
                "https://example.test/learn",
                receivedMessage.Content);
        });
    }

    [Fact]
    public void approve_command_button_must_execute_the_pending_command()
    {
        CommandExecution? approvedCommand = null;
        var pendingCommand = new CommandExecution
        {
            Id = 1,
            Attempt = 1,
            Objective = "Install package",
            Run = "pip install requests",
            OperationKind = OperationKind.TerminalCommand,
            SafetyDecision = CommandSafetyDecisionType.AskApproval,
            Notes = "decision=AskApproval"
        };
        var manager = new FakeManager
        {
            ManageConversationAsyncHandler = (message, _, _) =>
                Task.FromResult(new ConversationTurn
                {
                    Prompt = message.Content,
                    Mode = InteractionMode.Agent,
                    ModelName = "qwen3:8b",
                    Classification = InteractionMode.Agent.ToString(),
                    Response = "O passo 1 requer confirmacao explicita.",
                    ActionStatus = ActionExecutionStatus.AwaitingApproval,
                    Commands = [pendingCommand]
                }),
            RunApprovedCommandAsyncHandler = (command, _, _) =>
            {
                approvedCommand = command;
                return Task.FromResult(new ConversationTurn
                {
                    Prompt = $"Aprovar e executar: {command.Run}",
                    Mode = InteractionMode.Agent,
                    ModelName = "qwen3:8b",
                    Classification = InteractionMode.Agent.ToString(),
                    Response = "Comando aprovado executado.",
                    ActionStatus = ActionExecutionStatus.Completed,
                    Commands =
                    [
                        new CommandExecution
                        {
                            Id = 1,
                            Attempt = 1,
                            Objective = command.Objective,
                            Run = command.Run,
                            OperationKind = command.OperationKind,
                            SafetyDecision = CommandSafetyDecisionType.AskApproval,
                            ApprovedByUser = true,
                            Executed = true,
                            ExitCode = 0,
                            Output = "ok"
                        }
                    ]
                });
            }
        };

        RegisterPageServices(manager, new FakeLlamaClient());

        var component = Render<Chat>();
        FindModeButton(component, "Agente").Click();
        component.Find("textarea").Input("Instale requests");
        FindButton(component, "Enviar").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Executar aprovado", component.Markup);
            Assert.Contains("Aguardando aprovacao", component.Markup);
            Assert.Contains("Policy: AskApproval", component.Markup);
        });

        FindButton(component, "Executar aprovado").Click();

        component.WaitForAssertion(() =>
        {
            Assert.NotNull(approvedCommand);
            Assert.Equal("pip install requests", approvedCommand!.Run);
            Assert.Contains("Comando aprovado executado", component.Markup);
            Assert.Contains("Aprovacao: manual", component.Markup);
        });
    }

    [Fact]
    public void quick_settings_must_save_language_provider_and_gpu_preferences()
    {
        RegisterPageServices(new FakeManager(), new FakeLlamaClient());

        var component = Render<Nebula.App.Shared.Pages.Settings>();
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Preferencias do Nebula", component.Markup);
            Assert.Contains("Modelo de aprendizado", component.Markup);
        });

        component.Find("input[value='BingHtml']").Change("BingHtml");
        component.Find("input[type='checkbox']").Change(true);
        component.FindAll(".nebula-settings-field select")[2].Change("nvidia");
        component.FindAll(".nebula-settings-field select")[3].Change("es-ES");
        component.FindAll("button")
            .Single(button => button.TextContent.Contains(
                "Salvar configuracao",
                StringComparison.OrdinalIgnoreCase))
            .Click();

        component.WaitForAssertion(() =>
        {
            var settings = Services.GetRequiredService<NebulaRuntimeSettings>();
            Assert.Equal("BingHtml", settings.WebResearchProvider);
            Assert.Equal("nvidia", settings.AccelerationProfile);
            Assert.Equal("es-ES", settings.ResponseLanguageCode);
            Assert.Equal("Espanol", settings.ResponseLanguageName);
            Assert.True(settings.AutoApproveCommands);
            Assert.Contains(
                JSInterop.Invocations,
                invocation => invocation.Identifier == "localStorage.setItem");
            Assert.Contains("Configuracao salva e aplicada", component.Markup);
        });
    }

    private static AngleSharp.Dom.IElement FindModeButton(
        IRenderedComponent<Chat> component,
        string label)
    {
        return component.FindAll(".nebula-mode-selector__button")
            .Single(button => button.TextContent.Trim().Equals(
                label,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertReasoningBeforeResponse(
        IRenderedComponent<Chat> component)
    {
        var responseStack = component.Find(".nebula-response-stack");
        Assert.True(responseStack.Children.Length >= 2);
        Assert.Equal("DETAILS", responseStack.Children[0].TagName);
        Assert.Equal("SECTION", responseStack.Children[1].TagName);
    }

    private sealed class FakeManager : IManager
    {
        public Guid ActiveConversationId { get; private set; } = Guid.NewGuid();

        public Func<UserMessage, IProgress<ConversationTurn>?, CancellationToken, Task<ConversationTurn>> ManageConversationAsyncHandler { get; set; }
            = (message, _, _) => Task.FromResult(new ConversationTurn
            {
                Prompt = message.Content,
                Mode = message.Mode,
                ModelName = "qwen3:8b",
                Classification = message.Mode.ToString(),
                Response = "Resposta padrao"
            });

        public Func<CommandExecution, IProgress<ConversationTurn>?, CancellationToken, Task<ConversationTurn>> RunApprovedCommandAsyncHandler { get; set; }
            = (command, _, _) => Task.FromResult(new ConversationTurn
            {
                Prompt = $"Aprovar e executar: {command.Run}",
                Mode = InteractionMode.Agent,
                ModelName = "qwen3:8b",
                Classification = InteractionMode.Agent.ToString(),
                Response = "Comando aprovado executado.",
                ActionStatus = ActionExecutionStatus.Completed
            });

        public Task<ConversationTurn> ManageConversationAsync(UserMessage message)
        {
            return ManageConversationAsync(message, progress: null, cancellationToken: default);
        }

        public Task<ConversationTurn> ManageConversationAsync(
            UserMessage message,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return ManageConversationAsyncHandler(message, progress, cancellationToken);
        }

        public Task<ConversationTurn> RunApprovedCommandAsync(
            CommandExecution command,
            IProgress<ConversationTurn>? progress,
            CancellationToken cancellationToken = default)
        {
            return RunApprovedCommandAsyncHandler(command, progress, cancellationToken);
        }

        public Task<string> ManageResponse(UserMessage message)
        {
            return Task.FromResult(message.Content);
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
