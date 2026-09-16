namespace Ghostflyby.Jupyter.Kernel;

/// <summary>
///     Optional kernel-application lifecycle notification.
/// </summary>
/// <remarks>
///     <para>
///         Implement this alongside <see cref="IJupyterKernelApplication" /> when the application
///         holds resources that must be released, or wants to observe that the kernel is stopping.
///         It mirrors the classic <c>do_shutdown(restart)</c> hook.
///     </para>
///     <para>
///         This is an optional capability interface rather than a member of
///         <see cref="IJupyterKernelApplication" /> so that a simple stateless kernel never has to
///         implement shutdown machinery it does not need.
///     </para>
///     <para>
///         Called exactly once, after the shell, control, and router loops have stopped and before
///         the host disposes its transport. It runs for every stop path — an explicit
///         <see cref="JupyterKernelHost.StopAsync" />, a protocol <c>shutdown_request</c>, or a host
///         disposal — and an exception thrown here does not mask the host's own terminal cause.
///     </para>
///     <para>
///         The host does <em>not</em> call <see cref="IAsyncDisposable.DisposeAsync" /> on the
///         application: the caller that constructed it owns it. Disposing the host and disposing the
///         application are separate, deliberately.
///     </para>
/// </remarks>
public interface IJupyterKernelLifecycle
{
    /// <summary>
    ///     Notifies the application that the kernel host is stopping.
    /// </summary>
    /// <param name="restart">
    ///     <see langword="true" /> when the stop was requested by a <c>shutdown_request</c> whose
    ///     <c>restart</c> flag was set; <see langword="false" /> for every other stop path.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the application to finish stopping.</param>
    /// <returns>A task that completes when the application has finished stopping.</returns>
    ValueTask OnShutdownAsync(bool restart, CancellationToken cancellationToken = default);
}
