using PressTalk.Contracts.Session;
using PressTalk.Engine;
using Xunit;

namespace PressTalk.Engine.Tests;

public sealed class SessionStateMachineTests
{
    [Fact]
    public void Transition_HappyPath_ReachesCompleted()
    {
        var machine = new SessionStateMachine();

        machine.Transition(SessionTrigger.Press);
        machine.Transition(SessionTrigger.Release);
        machine.Transition(SessionTrigger.AsrComplete);
        machine.Transition(SessionTrigger.CommitComplete);

        Assert.Equal(SessionState.Completed, machine.CurrentState);
    }

    [Fact]
    public void Transition_InvalidTrigger_Throws()
    {
        var machine = new SessionStateMachine();

        Action action = () => _ = machine.Transition(SessionTrigger.Release);

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void Transition_ErrorThenReset_ReturnsIdle()
    {
        var machine = new SessionStateMachine();

        machine.Transition(SessionTrigger.Press);
        machine.Transition(SessionTrigger.Release);
        machine.Transition(SessionTrigger.Error);
        machine.Transition(SessionTrigger.Reset);

        Assert.Equal(SessionState.Idle, machine.CurrentState);
    }
}
