namespace PressTalk.Asr;

public sealed class FunAsrRuntimeOptions
{
    public string PythonExecutable { get; init; } = "python";

    public string ScriptPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "funasr_runtime.py");

    public string StreamingModel { get; init; } = "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online";

    public string Device { get; init; } = "cpu";

    public bool EnableInt8Quantization { get; init; }

    public string SpeakerModel { get; init; } = "iic/speech_campplus_sv_zh-cn_16k-common";

    public string RealtimePunctuationModel { get; init; } =
        "iic/punc_ct-transformer_zh-cn-common-vad_realtime-vocab272727";

    public string FinalPunctuationModel { get; init; } =
        "iic/punc_ct-transformer_zh-cn-common-vocab272727-pytorch";

    public string TranscriptionMode { get; init; } = "fast";

    public int StrideSamples { get; init; } = 9600;

    public int EndpointSilenceMs { get; init; } = 420;

    public double EndpointRmsThreshold { get; init; } = 0.0065;

    public int CommandTimeoutMs { get; init; } = 180000;
}
