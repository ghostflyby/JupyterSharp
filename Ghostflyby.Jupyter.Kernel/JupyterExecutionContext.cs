using System.Collections.ObjectModel;
using System.Text.Json;

namespace Ghostflyby.Jupyter.Kernel;

public sealed class JupyterExecutionContext
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyMetadata =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>());

    private readonly Func<bool, CancellationToken, ValueTask> _clearOutput;
    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> _display;

    private readonly Func<MimeBundle, JupyterDisplayId?, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
        ValueTask<JupyterDisplayId>> _displayTracked;

    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, ValueTask> _publishError;

    private readonly Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask>
        _publishResult;

    private readonly Func<string, bool, CancellationToken, Task<string>> _requestInput;

    private readonly Func<JupyterDisplayId, MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
        ValueTask> _updateDisplay;

    private readonly Func<string, string, CancellationToken, ValueTask> _writeStream;

    internal JupyterExecutionContext(
        JupyterMessageId requestId,
        int executionCount,
        Func<string, string, CancellationToken, ValueTask> writeStream,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> display,
        Func<MimeBundle, JupyterDisplayId?, IReadOnlyDictionary<string, JsonElement>, CancellationToken,
            ValueTask<JupyterDisplayId>> displayTracked,
        Func<JupyterDisplayId, MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask>
            updateDisplay,
        Func<bool, CancellationToken, ValueTask> clearOutput,
        Func<MimeBundle, IReadOnlyDictionary<string, JsonElement>, CancellationToken, ValueTask> publishResult,
        Func<string, string, IReadOnlyList<string>, CancellationToken, ValueTask> publishError,
        Func<string, bool, CancellationToken, Task<string>> requestInput)
    {
        RequestId = requestId;
        ExecutionCount = executionCount;
        this._writeStream = writeStream;
        this._display = display;
        this._displayTracked = displayTracked;
        this._updateDisplay = updateDisplay;
        this._clearOutput = clearOutput;
        this._publishResult = publishResult;
        this._publishError = publishError;
        this._requestInput = requestInput;
    }

    public JupyterMessageId RequestId { get; }

    public int ExecutionCount { get; }

    public ValueTask WriteStdoutAsync(string text, CancellationToken cancellationToken = default)
    {
        return _writeStream("stdout", text, cancellationToken);
    }

    public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken = default)
    {
        return _writeStream("stderr", text, cancellationToken);
    }

    public ValueTask DisplayAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return _display(data, metadata ?? EmptyMetadata, cancellationToken);
    }

    public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
        MimeBundle data,
        JupyterDisplayId? displayId = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return _displayTracked(data, displayId, metadata ?? EmptyMetadata, cancellationToken);
    }

    public ValueTask UpdateDisplayAsync(
        JupyterDisplayId displayId,
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return _updateDisplay(displayId, data, metadata ?? EmptyMetadata, cancellationToken);
    }

    public ValueTask ClearOutputAsync(
        bool wait = false,
        CancellationToken cancellationToken = default)
    {
        return _clearOutput(wait, cancellationToken);
    }

    public ValueTask PublishResultAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return _publishResult(data, metadata ?? EmptyMetadata, cancellationToken);
    }

    /// <summary>Publishes a Jupyter execution error without failing the enclosing application execution.</summary>
    public ValueTask PublishErrorAsync(
        string name,
        string value,
        IReadOnlyList<string> traceback,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(traceback);
        return _publishError(name, value, traceback, cancellationToken);
    }

    public Task<string> RequestInputAsync(
        string prompt,
        bool password = false,
        CancellationToken cancellationToken = default)
    {
        return _requestInput(prompt, password, cancellationToken);
    }
}
