using Moq;

using Nebula.Agent.Application;
using Nebula.Agent.Domain;

namespace Nebula.Agent.Test;

public sealed class PlansTest
{
    [Fact]
    public void apply_plan_must_merge_new_steps()
    {
        using var workspace = new TempTestWorkspace();
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build" },
            new AgentPlanStep { Id = 2, Description = "Test", DependsOn = [1] }
        ]);

        Assert.Equal(2, session.Plan.Count);
        Assert.Equal("pending", session.Plan[1].Status);
        Assert.Equal([1], session.Plan[1].DependsOn);
    }

    [Fact]
    public void apply_plan_must_update_status_of_existing_step()
    {
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build", Status = "pending" }
        ]);
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build", Status = "inProgress" }
        ]);

        Assert.Single(session.Plan);
        Assert.Equal("inProgress", session.Plan[0].Status);
    }

    [Fact]
    public void apply_plan_must_not_revert_completed_steps()
    {
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build", Status = "completed" }
        ]);
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build", Status = "pending" }
        ]);

        Assert.Equal("completed", session.Plan[0].Status);
    }

    [Fact]
    public void complete_step_must_mark_matching_plan_step_completed()
    {
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Compilar o projeto" }
        ]);

        session.CompleteStep("Compilar o projeto", "build ok");

        Assert.Equal("completed", session.Plan[0].Status);
    }

    [Fact]
    public void current_plan_must_render_structured_plan_when_present()
    {
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Build" },
            new AgentPlanStep { Id = 2, Description = "Test", DependsOn = [1] }
        ]);

        var plan = session.CurrentPlan;

        Assert.Contains("#1 [pending] [risk=low] Build", plan);
        Assert.Contains("#2 [pending] [risk=low] (depends on 1) Test", plan);
    }

    [Fact]
    public void current_plan_must_render_checkpoint_and_risk_markers()
    {
        var session = CreateSession();
        session.ApplyPlan([
            new AgentPlanStep { Id = 1, Description = "Scaffold", IsCheckpoint = true, Risk = "high" },
            new AgentPlanStep { Id = 2, Description = "Test", Risk = "medium" }
        ]);

        var plan = session.CurrentPlan;

        Assert.Contains("#1 [pending] [checkpoint] [risk=high] Scaffold", plan);
        Assert.Contains("#2 [pending] [risk=medium] Test", plan);
    }

    [Fact]
    public void current_plan_must_render_text_fallback_when_no_plan()
    {
        var session = CreateSession();
        session.CompleteStep("Goal", "done");

        Assert.Contains("completed. Observation: done", session.CurrentPlan);
    }

    [Fact]
    public void emit_stream_output_must_fuse_consecutive_lines_of_same_command()
    {
        var session = CreateSession();
        session.EmitStreamOutput("first", isError: false, command: "echo a");
        session.EmitStreamOutput("second", isError: false, command: "echo a");

        var events = session.Snapshot(ActionExecutionStatus.Executing, "running").ActionEvents;
        var stream = events.Where(value => value.Kind == ActionExecutionEventKind.StreamOutput).ToList();

        Assert.Single(stream);
        Assert.Contains("first", stream[0].ToolResponse);
        Assert.Contains("second", stream[0].ToolResponse);
    }

    [Fact]
    public void emit_stream_output_must_not_fuse_different_commands()
    {
        var session = CreateSession();
        session.EmitStreamOutput("one", isError: false, command: "echo a");
        session.EmitStreamOutput("two", isError: false, command: "echo b");

        var events = session.Snapshot(ActionExecutionStatus.Executing, "running").ActionEvents;
        var stream = events.Where(value => value.Kind == ActionExecutionEventKind.StreamOutput).ToList();

        Assert.Equal(2, stream.Count);
    }

    [Fact]
    public void apply_architecture_comparison_must_store_options_and_emit_event()
    {
        var session = CreateSession();

        session.ApplyArchitectureComparison([
            new ArchitectureOption
            {
                Name = "Monolith",
                Pros = "Simple",
                Cons = "Scales poorly",
                Recommendation = "Start simple",
                Risk = "low"
            },
            new ArchitectureOption
            {
                Name = "Microservices",
                Pros = "Scales",
                Cons = "Complex",
                Recommendation = "Overkill now",
                Risk = "high"
            }
        ]);

        Assert.Equal(2, session.ArchitectureOptions.Count);
        var snapshot = session.Snapshot(ActionExecutionStatus.Planning, "planning");
        Assert.Contains(snapshot.ActionEvents, value =>
            value.Kind == ActionExecutionEventKind.ArchitectureComparison
            && value.Message.Contains("Monolith"));
        Assert.Equal(2, snapshot.ArchitectureOptions.Count);
    }

    [Fact]
    public void apply_architecture_comparison_must_ignore_empty()
    {
        var session = CreateSession();

        session.ApplyArchitectureComparison(null);
        session.ApplyArchitectureComparison([]);

        Assert.Empty(session.ArchitectureOptions);
    }

    private static AgentActionSession CreateSession()
    {
        return new AgentActionSession(
            new AgentActionRunRequest { Prompt = "create a project", ConversationId = Guid.NewGuid() },
            new Progress<ConversationTurn>(_ => { }),
            new Mock<ILogger>().Object,
            "test-model",
            defaultMaxSteps: 10,
            defaultMaxRetriesPerStep: 2);
    }
}