using PressTalk.Contracts.Session;

namespace PressTalk.Engine;

public sealed class SessionStateMachine
{
    private readonly Action<string>? _log;

    public SessionStateMachine(Action<string>? log = null)
    {
        _log = log;
    }

    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    public SessionState Transition(SessionTrigger trigger)
    {
        var previous = CurrentState;
        CurrentState = (CurrentState, trigger) switch
        {
            (SessionState.Idle, SessionTrigger.Press) => SessionState.Recording,
            (SessionState.Idle, SessionTrigger.Reset) => SessionState.Idle,

            (SessionState.Recording, SessionTrigger.Release) => SessionState.Recognizing,
            (SessionState.Recording, SessionTrigger.Error) => SessionState.Failed,

            (SessionState.Recognizing, SessionTrigger.AsrComplete) => SessionState.Committing,
            (SessionState.Recognizing, SessionTrigger.Error) => SessionState.Failed,

            (SessionState.Committing, SessionTrigger.CommitComplete) => SessionState.Completed,
            (SessionState.Committing, SessionTrigger.Error) => SessionState.Failed,

            (SessionState.Completed, SessionTrigger.Reset) => SessionState.Idle,
            (SessionState.Failed, SessionTrigger.Reset) => SessionState.Idle,

            _ => throw new InvalidOperationException(
                $"Invalid transition: state={CurrentState}, trigger={trigger}")
        };

        _log?.Invoke($"[Engine.State] {previous} --({trigger})-> {CurrentState}");

        return CurrentState;
    }
}
