namespace PressTalk.Asr;

public sealed class FunAsrRuntimeOptions
{
    public string PythonExecutable { get; init; } = "python";

    public string ScriptPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "funasr_runtime.py");

    public string StreamingModel { get; init; } = "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online";

    public string Device { get; init; } = "cpu";

    public bool EnableInt8Quantization { get; init; }

    public string SpeakerModel { get; init; } = "iic/speech_campplus_sv_zh-cn_16k-common";

    public int CommandTimeoutMs { get; init; } = 180000;
}
