using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ghostflyby.Jupyter.Client;

public interface IJupyterKernelManager : IAsyncDisposable
{
    IJupyterClient Client { get; }

    /// <summary>Gets the operating system process id of the local child kernel, or null when the kernel is not local.</summary>
    int? ProcessId { get; }

    Task InterruptAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);

    Task TerminateAsync(CancellationToken cancellationToken = default);
}

public sealed record LocalJupyterKernelManagerOptions
{
    /// <summary>Gets the child kernel working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets whether the child starts without the host process environment.</summary>
    public bool ClearInheritedEnvironment { get; init; }

    /// <summary>Gets explicit environment values applied before kernelspec values.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string? RuntimeDirectory { get; init; }
}

/// <summary>Represents a local Jupyter kernel that failed before becoming ready.</summary>
public sealed class JupyterKernelStartupException : Exception
{
    internal JupyterKernelStartupException(
        string displayName,
        int? exitCode,
        bool timedOut,
        string standardError,
        Exception? innerException = null)
        : base(CreateMessage(displayName, exitCode, timedOut, standardError), innerException)
    {
        DisplayName = displayName;
        ExitCode = exitCode;
        TimedOut = timedOut;
        StandardError = standardError;
    }

    /// <summary>Gets the kernelspec display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the child exit code, or null when the child was still running at failure time.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets whether the configured startup timeout elapsed.</summary>
    public bool TimedOut { get; }

    /// <summary>Gets bounded and redacted child standard-error diagnostics.</summary>
    public string StandardError { get; }

    private static string CreateMessage(string displayName, int? exitCode, bool timedOut, string standardError)
    {
        var reason = timedOut
            ? "did not become ready before the startup timeout"
            : exitCode is { } code
                ? $"exited with code {code} before becoming ready"
                : "failed before becoming ready";
        return standardError.Length == 0
            ? $"Jupyter kernel '{displayName}' {reason}."
            : $"Jupyter kernel '{displayName}' {reason}. Standard error: {standardError}";
    }
}

public sealed partial class LocalJupyterKernelManager : IJupyterKernelManager
{
    private const int SigInt = 2;
    private readonly JupyterKernelSpec _kernelSpec;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly LocalJupyterKernelManagerOptions _options;
    private JupyterClient? _client;
    private string? _connectionFile;
    private int _disposeState;
    private KernelProcess? _process;

    private LocalJupyterKernelManager(JupyterKernelSpec kernelSpec, LocalJupyterKernelManagerOptions options)
    {
        this._kernelSpec = kernelSpec;
        this._options = options;
    }

    public IJupyterClient Client => _client
                                    ?? throw new InvalidOperationException("The Jupyter kernel is not running.");

    public int? ProcessId => _process?.Id;

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (string.Equals(_kernelSpec.InterruptMode, "message", StringComparison.OrdinalIgnoreCase))
            {
                await RequireClient().InterruptAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            SendInterruptSignal(RequireProcess());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(true, false, cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(false, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(false, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(false, false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    public static async Task<LocalJupyterKernelManager> StartAsync(
        JupyterKernelSpec kernelSpec,
        LocalJupyterKernelManagerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var manager = new LocalJupyterKernelManager(kernelSpec, options ?? new LocalJupyterKernelManagerOptions());
        try
        {
            await manager.StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var connectionInfo = JupyterConnectionInfo.CreateLocalTcp();
        var runtimeDirectory = _options.RuntimeDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(runtimeDirectory);
        _connectionFile = Path.Combine(runtimeDirectory, $"jupyter-sharp-kernel-{Guid.NewGuid():N}.json");
        await connectionInfo.WriteFileAsync(_connectionFile, cancellationToken).ConfigureAwait(false);

        _process = StartProcess(_connectionFile);
        _client = await JupyterClient.ConnectAsync(connectionInfo, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(_options.StartupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var readiness = _client.WaitForReadyAsync(startup.Token);
        var first = await Task.WhenAny(readiness, _process.Exit).ConfigureAwait(false);
        if (ReferenceEquals(first, _process.Exit))
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await startup.CancelAsync().ConfigureAwait(false);
            await ObserveStartupReadinessAsync(readiness, startup.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw _process.CreateStartupException(_kernelSpec.DisplayName);
        }

        try
        {
            await readiness.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw _process.CreateStartupException(_kernelSpec.DisplayName, true, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_process.HasExited) await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            throw _process.CreateStartupException(_kernelSpec.DisplayName, innerException: exception);
        }

        if (_process.HasExited)
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw _process.CreateStartupException(_kernelSpec.DisplayName);
        }
    }

    private async Task StopCoreAsync(
        bool restart,
        bool force,
        CancellationToken cancellationToken)
    {
        var currentClient = _client;
        var currentProcess = _process;
        _client = null;
        _process = null;
        Exception? failure = null;
        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            if (!force && currentClient is not null && currentProcess is { HasExited: false })
                await currentClient.ShutdownAsync(restart, shutdown.Token).ConfigureAwait(false);

            if (!force && currentProcess is { HasExited: false })
                await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (currentProcess is not null)
                try
                {
                    if (!currentProcess.HasExited) currentProcess.Kill(true);

                    await currentProcess.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (currentProcess.HasExited)
                {
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

            if (currentClient is not null)
                try
                {
                    await currentClient.DisposeAsync().AsTask().WaitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

            currentProcess?.Dispose();
            try
            {
                DeleteConnectionFile();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is OperationCanceledException && timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
            return;

        if (failure is not null && Volatile.Read(ref _disposeState) == 0) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private KernelProcess StartProcess(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _kernelSpec.Argv[0],
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _options.WorkingDirectory ?? string.Empty
        };

        if (_options.ClearInheritedEnvironment) startInfo.Environment.Clear();

        foreach (var environment in _options.Environment) startInfo.Environment[environment.Key] = environment.Value;

        foreach (var argument in _kernelSpec.Argv.Skip(1))
            startInfo.ArgumentList.Add(argument.Replace("{connection_file}", path, StringComparison.Ordinal));

        foreach (var environment in _kernelSpec.Environment) startInfo.Environment[environment.Key] = environment.Value;

        return KernelProcess.Start(
            startInfo,
            _kernelSpec.DisplayName,
            [
                path,
                .. startInfo.Environment.Values.OfType<string>(),
                .. startInfo.ArgumentList
            ]);
    }

    private static void SendInterruptSignal(Process process)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Signal-based Jupyter kernel interrupt is not supported on Windows.");

        if (Kill(process.Id, SigInt) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private JupyterClient RequireClient()
    {
        return _client
               ?? throw new InvalidOperationException(
                   "The Jupyter kernel is not running.");
    }

    private Process RequireProcess()
    {
        return _process?.Value
               ?? throw new InvalidOperationException("The Jupyter kernel is not running.");
    }

    private static async Task ObserveStartupReadinessAsync(Task readiness, CancellationToken cancellationToken)
    {
        try
        {
            await readiness.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Process exit is the terminal startup cause; this observes a concurrent transport failure.
        }
    }

    private void DeleteConnectionFile()
    {
        if (_connectionFile is not null && File.Exists(_connectionFile)) File.Delete(_connectionFile);

        _connectionFile = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private sealed class KernelProcess : IDisposable
    {
        private const int MaxDiagnosticCharacters = 8 * 1024;
        private const int MaxDiagnosticLineCharacters = 1024;
        private readonly StringBuilder _diagnostics = new();
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly string[] _redactions;

        private KernelProcess(ProcessStartInfo startInfo, IEnumerable<string> redactions)
        {
            Value = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            Value.Exited += OnExited;
            Value.OutputDataReceived += IgnoreOutput;
            Value.ErrorDataReceived += OnErrorDataReceived;
            this._redactions = redactions
                .Where(static value => value.Length >= 8)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderByDescending(static value => value.Length)
                .ToArray();
        }

        internal Process Value { get; }

        internal int Id => Value.Id;

        internal bool HasExited => Value.HasExited;

        internal Task<int> Exit => _exit.Task;

        public void Dispose()
        {
            Value.Exited -= OnExited;
            Value.OutputDataReceived -= IgnoreOutput;
            Value.ErrorDataReceived -= OnErrorDataReceived;
            Value.Dispose();
        }

        internal static KernelProcess Start(
            ProcessStartInfo startInfo,
            string displayName,
            IEnumerable<string> redactions)
        {
            var owner = new KernelProcess(startInfo, redactions);
            try
            {
                if (!owner.Value.Start())
                    throw new InvalidOperationException($"Could not start Jupyter kernel '{displayName}'.");

                owner.Value.BeginOutputReadLine();
                owner.Value.BeginErrorReadLine();
                return owner;
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        internal void Kill(bool entireProcessTree)
        {
            Value.Kill(entireProcessTree);
        }

        internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await Value.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _exit.TrySetResult(Value.ExitCode);
            await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal JupyterKernelStartupException CreateStartupException(
            string displayName,
            bool timedOut = false,
            Exception? innerException = null)
        {
            int? exitCode = HasExited ? Value.ExitCode : null;
            string standardError;
            lock (_gate)
            {
                standardError = _diagnostics.ToString();
            }

            return new JupyterKernelStartupException(
                displayName,
                exitCode,
                timedOut,
                standardError,
                innerException);
        }

        private static void IgnoreOutput(object sender, DataReceivedEventArgs args)
        {
        }

        private void OnExited(object? sender, EventArgs args)
        {
            try
            {
                _exit.TrySetResult(Value.ExitCode);
            }
            catch (InvalidOperationException)
            {
                _exit.TrySetResult(-1);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { Length: > 0 } line) return;

            foreach (var redaction in _redactions)
                line = line.Replace(
                    redaction,
                    "[redacted]",
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            var cleaned = string.Concat(line.Select(static character => char.IsControl(character) ? '?' : character));
            if (cleaned.Length > MaxDiagnosticLineCharacters) cleaned = cleaned[..MaxDiagnosticLineCharacters];

            lock (_gate)
            {
                if (_diagnostics.Length >= MaxDiagnosticCharacters) return;

                if (_diagnostics.Length > 0) _diagnostics.Append(' ');
                var remaining = MaxDiagnosticCharacters - _diagnostics.Length;
                _diagnostics.Append(cleaned.AsSpan(0, Math.Min(cleaned.Length, remaining)));
            }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
