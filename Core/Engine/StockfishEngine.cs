using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;
using ChGhost.Core.Native;

namespace ChGhost.Core.Engine;

public sealed class StockfishEngine : IChessEngine
{
    private readonly string _executablePath;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private Process? _process;
    private ChildProcessJob? _job;
    private Channel<string>? _output;
    private Task? _outputReader;
    private bool _disposed;
    private int _currentMultiPv = 1;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private readonly object _stdinLock = new();
    private volatile bool _isSearching;
    private StreamWriter? _stdin;

    public StockfishEngine(string executablePath, AppSettings settings, IAppLogger logger)
    {
        _executablePath = executablePath;
        _settings = settings.Normalize();
        _logger = logger;
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false })
                return;
            await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Unable to ensure Stockfish is running.", ex);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task NewGameAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _lastUsedUtc = DateTime.UtcNow;
            await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("ucinewgame", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("isready", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "readyok", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Stockfish failed while starting a new game.", ex);
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<string> GetBestMoveAsync(string fen, int moveTimeMs, CancellationToken cancellationToken)
    {
        ValidateFen(fen);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            (string bestMove, _) = await SearchAsync(fen, moveTimeMs, multiPv: 1, cancellationToken).ConfigureAwait(false);
            return bestMove;
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Stockfish failed while calculating a move.", ex);
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<IReadOnlyList<CandidateMove>> GetTopMovesAsync(
        string fen,
        int moveTimeMs,
        int multiPv,
        CancellationToken cancellationToken = default)
    {
        ValidateFen(fen);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            (_, IReadOnlyList<CandidateMove> candidates) = await SearchAsync(
                fen,
                moveTimeMs,
                Math.Clamp(multiPv, 1, 6),
                cancellationToken).ConfigureAwait(false);
            return candidates;
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Stockfish failed while calculating top moves.", ex);
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task UnloadIfIdleAsync(TimeSpan maxIdle)
    {
        if (maxIdle <= TimeSpan.Zero)
            return;

        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Process? process = _process;
            if (process == null || process.HasExited || DateTime.UtcNow - _lastUsedUtc < maxIdle)
                return;

            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            _logger.Info("Stockfish unloaded due to idle timeout.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _lastUsedUtc = DateTime.UtcNow;
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task EnsureRunningCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;
        if (_process != null || _job != null)
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
        await StartProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executablePath))
            throw new FileNotFoundException("Stockfish executable was not found.", _executablePath);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        ChildProcessJob? job = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Stockfish process did not start.");
            job = new ChildProcessJob();
            job.Assign(process);
            _process = process;
            _job = job;
            _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            _outputReader = ReadOutputAsync(process, _output.Writer);
            _stdin = process.StandardInput;
            await WriteCommandAsync("uci", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "uciok", cancellationToken).ConfigureAwait(false);

            await WriteCommandAsync($"setoption name Threads value {_settings.EffectiveEngineThreads}", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync($"setoption name Hash value {_settings.EngineHashMb}", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("setoption name MultiPV value 1", cancellationToken).ConfigureAwait(false);
            _currentMultiPv = 1;
            await WriteCommandAsync("setoption name Move Overhead value 15", cancellationToken).ConfigureAwait(false);
            if (_settings.FullStrength)
            {
                await WriteCommandAsync("setoption name UCI_LimitStrength value false", cancellationToken).ConfigureAwait(false);
                await WriteCommandAsync("setoption name Skill Level value 20", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteCommandAsync("setoption name UCI_LimitStrength value true", cancellationToken).ConfigureAwait(false);
                await WriteCommandAsync($"setoption name UCI_Elo value {_settings.TargetElo}", cancellationToken).ConfigureAwait(false);
            }

            await WriteCommandAsync("ucinewgame", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("isready", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "readyok", cancellationToken).ConfigureAwait(false);
            _lastUsedUtc = DateTime.UtcNow;
            _logger.Info($"Stockfish started with {_settings.EffectiveEngineThreads} thread(s), {_settings.EngineHashMb} MB hash.");
        }
        catch
        {
            if (_process == process)
                await StopProcessAsync(graceful: true).ConfigureAwait(false);
            else
            {
                job?.Dispose();
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception cleanupException) when (cleanupException is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.Error("Unable to clean up Stockfish after startup failure.", cleanupException);
                }
                finally
                {
                    process.Dispose();
                }
            }
            throw;
        }
    }

    private async Task<(string BestMove, IReadOnlyList<CandidateMove> Candidates)> SearchAsync(
        string fen,
        int moveTimeMs,
        int multiPv,
        CancellationToken cancellationToken)
    {
        _lastUsedUtc = DateTime.UtcNow;
        await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
        await WriteCommandAsync("isready", cancellationToken).ConfigureAwait(false);
        await WaitForLineAsync(line => line == "readyok", cancellationToken).ConfigureAwait(false);
        DrainOutput();

        int targetMultiPv = Math.Clamp(multiPv, 1, 6);
        if (targetMultiPv != _currentMultiPv)
        {
            await WriteCommandAsync($"setoption name MultiPV value {targetMultiPv}", cancellationToken).ConfigureAwait(false);
            _currentMultiPv = targetMultiPv;
        }

        ValidateFen(fen);
        await WriteCommandAsync($"position fen {fen}", cancellationToken).ConfigureAwait(false);

        var pvCandidates = new Dictionary<int, CandidateMove>();
        bool receivedBestMove = false;
        _isSearching = true;
        using var stopReg = cancellationToken.Register(TrySendStopUnlocked);
        string response;
        try
        {
            WriteCommandLocked($"go movetime {Math.Clamp(moveTimeMs, 1, 60_000)}");
            response = await WaitForLineAsync(
                    line => line.StartsWith("bestmove ", StringComparison.Ordinal),
                    cancellationToken,
                    line =>
                    {
                        if (line.StartsWith("bestmove ", StringComparison.Ordinal))
                            receivedBestMove = true;
                        if (!TryParseInfoPvLine(line, out CandidateMove candidate))
                            return;
                        if (!pvCandidates.TryGetValue(candidate.MultiPvIndex, out CandidateMove? existing)
                            || candidate.Depth >= existing.Depth)
                        {
                            pvCandidates[candidate.MultiPvIndex] = candidate;
                        }
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!receivedBestMove)
            {
                TrySendStopUnlocked();
                bool drained = await TryDrainBestMoveAfterStopAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (!drained)
                {
                    _logger.Warn("Stockfish не завершил поиск после stop за 500 мс — перезапуск процесса.");
                    KillProcessNoLock();
                }
            }
            throw;
        }
        finally
        {
            _isSearching = false;
        }

        string[] parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidDataException($"Invalid Stockfish response: '{response}'.");
        string bestMove = parts[1];
        if (bestMove == "(none)")
            return (bestMove, Array.Empty<CandidateMove>());
        if (!IsValidUciMove(bestMove))
            throw new InvalidDataException($"Invalid Stockfish bestmove: '{response}'.");

        IReadOnlyList<CandidateMove> candidates = OrderCandidatesWithAuthoritativeBestMove(bestMove, pvCandidates.Values);
        return (bestMove, candidates);
    }

    internal static IReadOnlyList<CandidateMove> OrderCandidatesWithAuthoritativeBestMove(
        string bestMove,
        IEnumerable<CandidateMove> parsedCandidates)
    {
        if (string.IsNullOrWhiteSpace(bestMove) || bestMove == "(none)")
            return Array.Empty<CandidateMove>();

        List<CandidateMove> all = parsedCandidates?.ToList() ?? new List<CandidateMove>();

        CandidateMove? matchedBest = all
            .Where(c => c.Uci.Equals(bestMove, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Depth)
            .ThenByDescending(c => c.EffectiveCp)
            .ThenBy(c => c.MultiPvIndex)
            .FirstOrDefault();

        CandidateMove primary = matchedBest is not null
            ? matchedBest with { MultiPvIndex = 1 }
            : new CandidateMove(bestMove, 0, false, 0, 1, 1);

        var ordered = new List<CandidateMove>(Math.Max(1, all.Count)) { primary };
        int nextMultiPv = 2;
        foreach (CandidateMove alt in all
            .Where(c => !c.Uci.Equals(bestMove, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.EffectiveCp)
            .ThenByDescending(c => c.Depth)
            .ThenBy(c => c.MultiPvIndex))
        {
            ordered.Add(alt with { MultiPvIndex = nextMultiPv++ });
        }

        return ordered;
    }

    private async Task ReadOutputAsync(Process process, ChannelWriter<string> writer)
    {
        try
        {
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;
                if (line.StartsWith("info string CRITICAL ERROR", StringComparison.Ordinal))
                    _logger.Error(line);
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
            writer.TryComplete(process.HasExited
                ? new InvalidOperationException($"Stockfish exited with code {process.ExitCode}.")
                : null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.Error("Error while reading Stockfish output.", ex);
            writer.TryComplete(ex);
        }
    }

    private void WriteCommandLocked(string command)
    {
        lock (_stdinLock)
        {
            if (_process is { HasExited: false } && _stdin is not null)
            {
                _stdin.WriteLine(command);
                _stdin.Flush();
            }
        }
    }

    private Task WriteCommandAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCommandLocked(command);
        return Task.CompletedTask;
    }

    private void TrySendStopUnlocked()
    {
        if (!_isSearching) return;
        try
        {
            WriteCommandLocked("stop");
        }
        catch
        {
            // Игнорируем, если процесс уже завершился
        }
    }

    private async Task<bool> TryDrainBestMoveAfterStopAsync(TimeSpan timeout)
    {
        using var drainCts = new CancellationTokenSource(timeout);
        try
        {
            await WaitForLineAsync(
                line => line.StartsWith("bestmove ", StringComparison.Ordinal),
                drainCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void KillProcessNoLock()
    {
        Process? process = _process;
        ChildProcessJob? job = _job;
        _process = null;
        _job = null;
        _outputReader = null;
        _output = null;
        _stdin = null;
        _isSearching = false;

        if (process == null)
        {
            job?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(250);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Error("Error while killing Stockfish after failed stop.", ex);
        }
        finally
        {
            job?.Dispose();
            process.Dispose();
        }
    }

    private void DrainOutput()
    {
        ChannelReader<string>? reader = _output?.Reader;
        if (reader == null)
            return;
        while (reader.TryRead(out _))
        {
        }
    }

    internal static void ValidateFen(string fen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fen);
        if (fen.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("FEN must not contain newline or NUL characters.", nameof(fen));

        string[] fields = fen.Split(' ');
        if (fields.Length != 6 || fields.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("FEN must contain exactly 6 non-empty space-separated fields.", nameof(fen));
    }

    private static bool IsValidUciMove(string move)
    {
        if (move.Length is not (4 or 5))
            return false;
        for (int i = 0; i < 4; i += 2)
        {
            if (move[i] is < 'a' or > 'h' || move[i + 1] is < '1' or > '8')
                return false;
        }
        if (move.Length == 5)
        {
            char promotion = char.ToLowerInvariant(move[4]);
            if (promotion is not ('q' or 'r' or 'b' or 'n'))
                return false;
        }
        return true;
    }

    public static bool TryParseInfoPvLine(string line, out CandidateMove candidate)
    {
        candidate = null!;
        if (string.IsNullOrWhiteSpace(line)
            || !line.StartsWith("info ", StringComparison.Ordinal)
            || line.Contains(" lowerbound ", StringComparison.Ordinal)
            || line.Contains(" upperbound ", StringComparison.Ordinal))
        {
            return false;
        }

        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int depth = -1;
        int multiPv = 1;
        int scoreCp = 0;
        int mateIn = 0;
        bool isMate = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "depth" && i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out int parsedDepth))
                depth = parsedDepth;
            else if (tokens[i] == "multipv" && i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out int parsedMultiPv))
                multiPv = Math.Max(1, parsedMultiPv);
            else if (tokens[i] == "score" && i + 2 < tokens.Length)
            {
                if (tokens[i + 1] == "cp" && int.TryParse(tokens[i + 2], out int parsedCp))
                    scoreCp = parsedCp;
                else if (tokens[i + 1] == "mate" && int.TryParse(tokens[i + 2], out int parsedMate))
                {
                    isMate = true;
                    mateIn = parsedMate;
                    scoreCp = 0;
                }
            }
        }

        int pvIndex = Array.IndexOf(tokens, "pv");
        if (depth < 0 || pvIndex < 0 || pvIndex + 1 >= tokens.Length)
            return false;

        string uci = tokens[pvIndex + 1];
        if (!IsValidUciMove(uci))
            return false;

        candidate = new CandidateMove(uci, scoreCp, isMate, mateIn, depth, multiPv);
        return true;
    }

    private async Task<string> WaitForLineAsync(
        Func<string, bool> predicate,
        CancellationToken cancellationToken,
        Action<string>? observe = null)
    {
        ChannelReader<string> reader = _output?.Reader ?? throw new InvalidOperationException("Stockfish output is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.CommandTimeoutMs);
        try
        {
            while (true)
            {
                string line = await reader.ReadAsync(timeout.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(_settings.CommandTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                observe?.Invoke(line);
                if (predicate(line))
                    return line;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stockfish did not respond within {_settings.CommandTimeoutMs} ms.");
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("Stockfish closed its output stream.", ex);
        }
    }

    private async Task StopProcessAsync(bool graceful)
    {
        Process? process = _process;
        ChildProcessJob? job = _job;
        Task? outputReader = _outputReader;
        _process = null;
        _job = null;
        _outputReader = null;
        _output = null;
        _stdin = null;
        _isSearching = false;

        if (process == null)
        {
            job?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited && graceful)
            {
                lock (_stdinLock)
                {
                    process.StandardInput.WriteLine("quit");
                    process.StandardInput.Flush();
                }
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.Warn("Stockfish did not exit after quit; terminating the process.");
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            _logger.Error("Error while stopping Stockfish.", ex);
        }
        finally
        {
            job?.Dispose();
            process.Dispose();
            if (outputReader != null)
            {
                try
                {
                    await outputReader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    _logger.Error("Timed out while closing the Stockfish output reader.", ex);
                }
            }
        }
    }

    private static bool IsEngineFailure(Exception exception) =>
        exception is TimeoutException or IOException or InvalidOperationException or InvalidDataException
            or System.ComponentModel.Win32Exception or ChannelClosedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
