using Ghostflyby.Jupyter;

namespace Ghostflyby.Jupyter.Client.Transport;

/// <summary>
///     Names one of the five Jupyter protocol channels. Public because the channel a message
///     arrived on is part of the client's observable model (<see cref="JupyterUnhandledMessage" />),
///     not an implementation detail of any particular transport.
/// </summary>
public enum JupyterTransportChannel
{
    Shell,
    Control,
    Iopub,
    Stdin
}

internal sealed record JupyterTransportMessage(
    JupyterTransportChannel Channel,
    JupyterWireMessage WireMessage)
{
    public JupyterMessage Message => WireMessage.Message;
}

public sealed class JupyterBackpressureException(string message) : Exception(message);
