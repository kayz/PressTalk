namespace PressTalk.Contracts.Session;

public enum SessionState
{
    Idle = 0,
    Recording = 1,
    Recognizing = 2,
    Committing = 3,
    Completed = 4,
    Failed = 5
}

