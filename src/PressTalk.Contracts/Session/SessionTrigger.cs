namespace PressTalk.Contracts.Session;

public enum SessionTrigger
{
    Press = 0,
    Release = 1,
    StartStreaming = 2,
    StreamingChunk = 3,
    StopStreaming = 4,
    AsrComplete = 5,
    CommitComplete = 6,
    Error = 7,
    Reset = 8
}
