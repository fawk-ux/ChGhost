using System.Text.Json;
using ChGhost.Core.Configuration;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;

namespace ChGhost.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void MissingSettingsFileIsCreatedWithDefaults()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var store = new SettingsStore(path, new TestLogger());

        AppSettings settings = store.Load();

        Assert.True(File.Exists(path));
        Assert.Equal(4, settings.EngineThreads);
        Assert.Equal(4, settings.EffectiveEngineThreads);
        Assert.Equal(64, settings.EngineHashMb);
        Assert.Equal(120, settings.ScanIntervalBlitzMs);
        Assert.Equal(2600, settings.TargetElo);
        Assert.Equal("Auto", settings.RecognizerMode);
        Assert.Equal("models/pieces_48x48_tiny.onnx", settings.OnnxModelPath);
    }

    [Fact]
    public void SettingsCanBeSavedAndLoaded()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var store = new SettingsStore(path, new TestLogger());
        var settings = AppSettings.CreateDefaults();
        settings.EngineThreads = 8;
        settings.EngineHashMb = 1024;
        settings.FullStrength = false;
        settings.MoveTimeRapidMs = 900;

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.Equal(8, loaded.EngineThreads);
        Assert.Equal(1024, loaded.EngineHashMb);
        Assert.False(loaded.FullStrength);
        Assert.Equal(900, loaded.MoveTimeRapidMs);
    }

    [Fact]
    public void InvalidJsonLogsAndRecoversToDefaults()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{broken");
        var logger = new TestLogger();

        AppSettings settings = new SettingsStore(path, logger).Load();

        Assert.Equal(64, settings.EngineHashMb);
        Assert.Equal(2600, settings.TargetElo);
        Assert.NotEmpty(logger.Errors);
        Assert.True(JsonDocument.Parse(File.ReadAllText(path)).RootElement.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public void InvalidResourceValuesAreClamped()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "EngineThreads": 500,
              "EngineHashMb": 1,
              "MoveTimeBlitzMs": -20,
              "MoveTimeRapidMs": 90000,
              "ScanIntervalBlitzMs": 1,
              "ScanIntervalRapidMs": 90000,
              "CommandTimeoutMs": 1
            }
            """);

        AppSettings settings = new SettingsStore(path, new TestLogger()).Load();

        Assert.Equal(16, settings.EngineThreads);
        Assert.Equal(16, settings.EngineHashMb);
        Assert.Equal(10, settings.MoveTimeBlitzMs);
        Assert.Equal(60_000, settings.MoveTimeRapidMs);
        Assert.Equal(50, settings.ScanIntervalBlitzMs);
        Assert.Equal(10_000, settings.ScanIntervalRapidMs);
        Assert.Equal(250, settings.CommandTimeoutMs);
    }

    [Theory]
    [InlineData(-3, 1, 1)]
    [InlineData(500, 16, 16)]
    public void EngineThreadsAreClampedOrResolved(int configured, int expectedMinimum, int expectedMaximum)
    {
        var settings = AppSettings.CreateDefaults();
        settings.EngineThreads = configured;

        settings.Normalize();

        Assert.InRange(settings.EffectiveEngineThreads, expectedMinimum, expectedMaximum);
    }

    [Fact]
    public void ZeroEngineThreadsResolvesFromProcessorCountWithinLimits()
    {
        var settings = AppSettings.CreateDefaults();
        settings.EngineThreads = 0;

        Assert.Equal(Math.Clamp(Environment.ProcessorCount - 2, 1, 16), settings.EffectiveEngineThreads);
        Assert.Equal(0, settings.EngineThreads);
    }

    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("ONNX", "Onnx")]
    [InlineData(" Template ", "Template")]
    [InlineData("unknown", "Auto")]
    public void RecognizerModeIsNormalizedCaseInsensitively(string configured, string expected)
    {
        var settings = AppSettings.CreateDefaults();
        settings.RecognizerMode = configured;

        settings.Normalize();

        Assert.Equal(expected, settings.RecognizerMode);
    }

    [Fact]
    public void OnnxModelPathFallsBackWhenBlank()
    {
        var settings = AppSettings.CreateDefaults();
        settings.OnnxModelPath = " ";

        settings.Normalize();

        Assert.Equal("models/pieces_48x48_tiny.onnx", settings.OnnxModelPath);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(5000, 4096)]
    public void EngineHashIsClampedToSafeMemoryRange(int configured, int expected)
    {
        var settings = AppSettings.CreateDefaults();
        settings.EngineHashMb = configured;

        settings.Normalize();

        Assert.Equal(expected, settings.EngineHashMb);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(2000, 300)]
    public void HumanMaxCpLossIsClampedToMaximum300(int configured, int expected)
    {
        var settings = AppSettings.CreateDefaults();
        settings.HumanMaxCpLoss = configured;

        settings.Normalize();

        Assert.InRange(settings.HumanMaxCpLoss, 0, 300);
        Assert.Equal(expected, settings.HumanMaxCpLoss);
    }

    [Theory]
    [InlineData("Auto", true)]
    [InlineData("Onnx", true)]
    [InlineData("Template", false)]
    public void UseOnnxRecognizerReflectsRecognizerMode(string recognizerMode, bool expected)
    {
        var settings = AppSettings.CreateDefaults();
        settings.RecognizerMode = recognizerMode;

        Assert.Equal(expected, settings.UseOnnxRecognizer);
    }

    [Fact]
    public void UseOnnxRecognizerTogglePersistsAcrossSaveAndLoad()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var store = new SettingsStore(path, new TestLogger());

        var settings = store.Load();
        Assert.True(settings.UseOnnxRecognizer);

        settings.UseOnnxRecognizer = false;
        store.Save(settings);

        AppSettings reloadedTemplate = store.Load();
        Assert.False(reloadedTemplate.UseOnnxRecognizer);
        Assert.Equal("Template", reloadedTemplate.RecognizerMode);

        reloadedTemplate.UseOnnxRecognizer = true;
        store.Save(reloadedTemplate);

        AppSettings reloadedOnnx = store.Load();
        Assert.True(reloadedOnnx.UseOnnxRecognizer);
        Assert.Equal("Onnx", reloadedOnnx.RecognizerMode);
    }

    private sealed class TestLogger : IAppLogger
    {
        public List<string> Errors { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
