namespace PressTalk.Asr;

public sealed class QwenRuntimeOptions
{
    public string PythonExecutable { get; init; } = "python";

    public string ScriptPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "qwen_runtime.py");

    public string AsrFinalModel { get; init; } = "Qwen/Qwen3-ASR-0.6B";

    public string AsrPreviewModel { get; init; } = "Qwen/Qwen3-ASR-0.6B";

    public string LlmModel { get; init; } = "Qwen/Qwen3-0.6B";

    public string Device { get; init; } = "cpu";

    public int CommandTimeoutMs { get; init; } = 180000;

    public bool EnableSemanticLlm { get; init; } = true;
}
