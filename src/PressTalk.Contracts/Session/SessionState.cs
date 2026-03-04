namespace PressTalk.Contracts.Session;

public enum SessionState
{
    Idle = 0,
    Recording = 1,
    Streaming = 2,
    Recognizing = 3,
    Committing = 4,
    Completed = 5,
    Failed = 6
}
