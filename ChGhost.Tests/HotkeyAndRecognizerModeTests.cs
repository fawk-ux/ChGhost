using ChGhost.Core.Native;

namespace ChGhost.Tests;

public class HotkeyAndRecognizerModeTests
{
    [Fact]
    public void ModNoRepeatConstantMatchesWin32Specification()
    {
        Assert.Equal(0x4000u, Win32Interop.ModNoRepeat);
        Assert.Equal(Win32Interop.ModNoRepeat, Win32Interop.MOD_NOREPEAT);
    }

    [Fact]
    public void HotkeyDefinitionsIncludeUniqueAltQAndAltM()
    {
        var hotkeys = MainWindow.GetHotkeyDefinitions();

        Assert.Equal(hotkeys.Length, hotkeys.Select(h => h.Id).Distinct().Count());
        Assert.Equal(hotkeys.Length, hotkeys.Select(h => h.Key).Distinct().Count());
        Assert.Contains(hotkeys, h => h.Id == 9 && h.Key == 0x51 && h.Name == "Alt+Q");
        Assert.Contains(hotkeys, h => h.Id == 18 && h.Key == 0x4D && h.Name == "Alt+M");
    }

    [Fact]
    public void BuildHotkeyRegistrationWarningIncludesAltF4HintWhenAltQFails()
    {
        Assert.Equal(string.Empty, MainWindow.BuildHotkeyRegistrationWarning(Array.Empty<string>()));

        string nonExitWarning = MainWindow.BuildHotkeyRegistrationWarning(new[] { "Alt+G" });
        Assert.Contains("Alt+G", nonExitWarning);
        Assert.DoesNotContain("Alt+F4", nonExitWarning);

        string exitWarning = MainWindow.BuildHotkeyRegistrationWarning(new[] { "Alt+G", "Alt+Q" });
        Assert.Contains("Alt+G", exitWarning);
        Assert.Contains("Alt+Q", exitWarning);
        Assert.Contains("Alt+F4", exitWarning);
    }

    [Fact]
    public void ResolveOnnxModelPathPrefersPrimaryThenFallbackThenReturnsMissingPrimary()
    {
        using var directory = new TemporaryDirectory();
        string modelsDir = Path.Combine(directory.Path, "models");
        Directory.CreateDirectory(modelsDir);

        string primaryPath = Path.Combine(modelsDir, "pieces_48x48_tiny.onnx");
        string fallbackPath = Path.Combine(modelsDir, "pieces_64x64.onnx");

        var missing = MainWindow.ResolveOnnxModelPath("models/pieces_48x48_tiny.onnx", directory.Path);
        Assert.Equal(primaryPath, missing.Path);
        Assert.False(missing.UsedFallback);

        File.WriteAllBytes(fallbackPath, [0x08, 0x01]);
        var fallback = MainWindow.ResolveOnnxModelPath("models/pieces_48x48_tiny.onnx", directory.Path);
        Assert.Equal(fallbackPath, fallback.Path);
        Assert.True(fallback.UsedFallback);

        File.WriteAllBytes(primaryPath, [0x08, 0x01]);
        var primary = MainWindow.ResolveOnnxModelPath("models/pieces_48x48_tiny.onnx", directory.Path);
        Assert.Equal(primaryPath, primary.Path);
        Assert.False(primary.UsedFallback);
    }

    [Theory]
    [InlineData(false, false, false, true, 2600, true, "ChGhost · Белые · Пауза · Рапид · ONNX")]
    [InlineData(true, true, true, false, 2100, false, "ChGhost · Чёрные · Анализ · Блиц · 2100 · Шаблоны")]
    public void FormatOverlayHeaderReflectsActiveRecognizerMode(
        bool playAsBlack,
        bool isRunning,
        bool isBlitz,
        bool fullStrength,
        int targetElo,
        bool usingOnnx,
        string expected)
    {
        string header = MainWindow.FormatOverlayHeader(playAsBlack, isRunning, isBlitz, fullStrength, targetElo, usingOnnx);
        Assert.Equal(expected, header);
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
