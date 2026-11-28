using System.Text.Json.Serialization;

namespace ChGhost.Core.Models;

public sealed class AppSettings
{
    public int EngineThreads { get; set; } = 4;
    public int EngineHashMb { get; set; } = 64;
    public int TargetElo { get; set; } = 2600;
    public bool FullStrength { get; set; } = true;
    public int MoveTimeBlitzMs { get; set; } = 180;
    public int MoveTimeRapidMs { get; set; } = 550;
    public int ScanIntervalBlitzMs { get; set; } = 120;
    public int ScanIntervalRapidMs { get; set; } = 450;
    public int CommandTimeoutMs { get; set; } = 3500;
    public string RecognizerMode { get; set; } = "Auto";

    [JsonIgnore]
    public bool UseOnnxRecognizer
    {
        get => !string.Equals(RecognizerMode?.Trim(), "Template", StringComparison.OrdinalIgnoreCase);
        set => RecognizerMode = value ? "Onnx" : "Template";
    }
    public string OnnxModelPath { get; set; } = "models/pieces_48x48_tiny.onnx";
    public string EngineExecutablePath { get; set; } = "stockfish.exe";
    public int EngineIdleUnloadSeconds { get; set; } = 90;
    public bool ExcludeFromCapture { get; set; } = true;
    public bool StealthHumanize { get; set; } = true;
    public int MultiPvCount { get; set; } = 4;
    public int HumanMaxCpLoss { get; set; } = 45;
    public double HumanTemperature { get; set; } = 32.0;
    public bool ShowAdvisorArrows { get; set; } = true;
    public int AdvisorMaxGapCp { get; set; } = 65;
    public string AdvisorArrowColor1 { get; set; } = "#00E5FF";
    public string AdvisorArrowColor2 { get; set; } = "#E040FB";
    public double AdvisorArrowOpacity { get; set; } = 0.82;
    public bool HumanPacingEnabled { get; set; } = true;
    public int HumanDelayBlitzMinMs { get; set; } = 120;
    public int HumanDelayBlitzMaxMs { get; set; } = 950;
    public int HumanDelayRapidMinMs { get; set; } = 400;
    public int HumanDelayRapidMaxMs { get; set; } = 2600;

    [JsonIgnore]
    public int EffectiveEngineThreads => EngineThreads == 0
        ? Math.Clamp(Environment.ProcessorCount - 2, 1, 16)
        : Math.Clamp(EngineThreads, 1, 16);

    public static AppSettings CreateDefaults() => new()
    {
        EngineThreads = 4,
        EngineHashMb = 64,
        TargetElo = 2600,
        FullStrength = true,
        MoveTimeBlitzMs = 180,
        MoveTimeRapidMs = 550,
        ScanIntervalBlitzMs = 120,
        ScanIntervalRapidMs = 450,
        CommandTimeoutMs = 3500
        , RecognizerMode = "Auto"
        , OnnxModelPath = "models/pieces_48x48_tiny.onnx"
        , EngineExecutablePath = "stockfish.exe"
        , EngineIdleUnloadSeconds = 90
        , ExcludeFromCapture = true
        , StealthHumanize = true
        , MultiPvCount = 4
        , HumanMaxCpLoss = 45
        , HumanTemperature = 32.0
        , ShowAdvisorArrows = true
        , AdvisorMaxGapCp = 65
        , AdvisorArrowColor1 = "#00E5FF"
        , AdvisorArrowColor2 = "#E040FB"
        , AdvisorArrowOpacity = 0.82
        , HumanPacingEnabled = true
        , HumanDelayBlitzMinMs = 120
        , HumanDelayBlitzMaxMs = 950
        , HumanDelayRapidMinMs = 400
        , HumanDelayRapidMaxMs = 2600
    };

    public AppSettings Normalize()
    {
        EngineThreads = EngineThreads == 0 ? 0 : Math.Clamp(EngineThreads, 1, 16);
        EngineHashMb = Math.Clamp(EngineHashMb, 16, 4096);
        TargetElo = Math.Clamp(TargetElo, 1000, 3200);
        MoveTimeBlitzMs = Math.Clamp(MoveTimeBlitzMs, 10, 60_000);
        MoveTimeRapidMs = Math.Clamp(MoveTimeRapidMs, 10, 60_000);
        ScanIntervalBlitzMs = Math.Clamp(ScanIntervalBlitzMs, 50, 10_000);
        ScanIntervalRapidMs = Math.Clamp(ScanIntervalRapidMs, 50, 10_000);
        CommandTimeoutMs = Math.Clamp(CommandTimeoutMs, 250, 60_000);
        RecognizerMode = RecognizerMode?.Trim().ToLowerInvariant() switch
        {
            "onnx" => "Onnx",
            "template" => "Template",
            _ => "Auto"
        };
        OnnxModelPath = string.IsNullOrWhiteSpace(OnnxModelPath)
            ? "models/pieces_48x48_tiny.onnx"
            : OnnxModelPath.Trim();
        EngineExecutablePath = string.IsNullOrWhiteSpace(EngineExecutablePath)
            ? "stockfish.exe"
            : EngineExecutablePath.Trim();
        EngineIdleUnloadSeconds = Math.Clamp(EngineIdleUnloadSeconds, 0, 86_400);
        MultiPvCount = Math.Clamp(MultiPvCount, 1, 6);
        HumanMaxCpLoss = Math.Clamp(HumanMaxCpLoss, 0, 300);
        HumanTemperature = Math.Clamp(HumanTemperature, 8.0, 256.0);
        AdvisorMaxGapCp = Math.Clamp(AdvisorMaxGapCp, 0, 2_000);
        AdvisorArrowOpacity = Math.Clamp(AdvisorArrowOpacity, 0.2, 1.0);
        AdvisorArrowColor1 = string.IsNullOrWhiteSpace(AdvisorArrowColor1) ? "#00E5FF" : AdvisorArrowColor1.Trim();
        AdvisorArrowColor2 = string.IsNullOrWhiteSpace(AdvisorArrowColor2) ? "#E040FB" : AdvisorArrowColor2.Trim();
        HumanDelayBlitzMinMs = Math.Clamp(HumanDelayBlitzMinMs, 0, 60_000);
        HumanDelayBlitzMaxMs = Math.Clamp(HumanDelayBlitzMaxMs, HumanDelayBlitzMinMs, 60_000);
        HumanDelayRapidMinMs = Math.Clamp(HumanDelayRapidMinMs, 0, 60_000);
        HumanDelayRapidMaxMs = Math.Clamp(HumanDelayRapidMaxMs, HumanDelayRapidMinMs, 60_000);
        return this;
    }
}
