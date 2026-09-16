# Ghostflyby.Jupyter.Tests instructions

## Ownership

This project owns all coverage for `Ghostflyby.Jupyter`, `Ghostflyby.Jupyter.Client`, and `Ghostflyby.Jupyter.Kernel`. It references no
host application. There is no other test project in this repository: when a change spans layers, its test belongs here.

## Test placement

- Wire protocol tests belong with the `Ghostflyby.Jupyter` coverage group.
- Transport and protocol-correlation tests belong with the `Ghostflyby.Jupyter.Client` group.
- Dispatch, ordering, and lifecycle tests belong with the `Ghostflyby.Jupyter.Kernel` group.
- Cross-layer behavior uses the self-hosted pairing: a real `JupyterKernelHost` plus a real `JupyterClient` over
  loopback TCP, with a test application implementing the kernel application interfaces.

## Coverage ownership

- Wire protocol: frame layout, signatures, wire names, source-generated DTO round trips, unknown fields, buffers,
  connection validation, cursors, MIME, display IDs, and malformed messages.
- Client transport: socket ownership, five channels, identities, heartbeat, queue failure, startup cancellation,
  disconnect, and concurrent disposal.
- Client protocol: parent/channel/type correlation, reply-idle permutations, stdin parents, ordered and late output,
  concurrent requests, cancellation, and terminal propagation.
- Kernel: busy/reply/idle order, shell serialization, responsive control and heartbeat, interrupt, shutdown, silent,
  stdin, language services, display/update/clear, and exception completion.

## xUnit and assertions

- Use xUnit v3 and flow `TestContext.Current.CancellationToken` into cancellable APIs.
- Every asynchronous test carries a declarative xUnit `Timeout` (`[Fact(Timeout = 30_000)]` / `[Theory(Timeout = ...)]`).
  Do not rely on an internal deadline alone; the attribute is the outer safety net.
- The internal deadline must be strictly smaller than the declared xUnit `Timeout`. Default pairing: 20s internal
  deadline under 30s Timeout; heavier integration 50s under 60s. When raising an internal deadline, raise the Timeout
  to match.
- For asynchronous exception assertions, invoke through FluentAssertions `Awaiting(...)`.
- Do not create a temporary `async` delegate solely for `delegate.Should().ThrowAsync(...)`.
- When `Awaiting` overloads are ambiguous, return an explicit `Task`/`ValueTask`, or use a named helper with a concrete
  return type.
- Assert protocol stages and typed failures, not incidental exception text unless the text is the public contract.

## Determinism

- Never synchronize by polling or by relying on incidental timing. This is a hard rule:
  - No busy-wait loops, no `Thread.Sleep`, no `SpinWait`, no wall-clock polling.
  - No fixed sleeps used as synchronization. A `Task.Delay` is only acceptable as a genuine timeout with an explicit
    purpose, never to wait for an event.
- Every wait must be signal-driven and awaitable:
  - `TaskCompletionSource` (always `TaskCreationOptions.RunContinuationsAsynchronously`) awaited via `.WaitAsync(...)`.
  - Bounded/unbounded `Channel<T>` readers (`await foreach (... .WithCancellation(token))`).
  - `task.WaitAsync(deadline)` for terminal conditions; `Task.WhenAll`/`WhenAny` for concurrency.
- Do not use fixed TCP ports. Allocate loopback ports dynamically.
- Socket integration tests share process-level state, so they join the non-parallel collection defined by
  `JupyterSocketIntegrationCollection`.
- Integration failures should identify the failed stage: bind, connect, heartbeat, send, reply, output, shutdown, or
  cleanup.
