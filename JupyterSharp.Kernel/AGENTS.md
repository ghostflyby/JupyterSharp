# JupyterSharp.Kernel instructions

## Ownership

This project is the reusable server-side Jupyter host. Kernel authors implement application capabilities without
touching ZmqSharp or wire envelopes.

- `Transport` owns ROUTER shell/control/stdin, XPUB IOPub, REP heartbeat, framing, routing identities, bounded queues,
  asynchronous pumps, terminal state, and deterministic disposal.
- `JupyterKernelHost` owns request dispatch, execution count, state publication, active execution cancellation,
  interrupt, shutdown, and conversion between application outcomes and protocol replies.
- Application interfaces and `JupyterExecutionContext` expose domain behavior and ordered output operations only.

## Forbidden dependencies

- Reference only `JupyterSharp` for Jupyter contracts.
- Never reference `JupyterSharp.Client`, provider, or host-product concepts.
- ZmqSharp types and raw frames must not escape `Transport`.
- Application capability implementations must not create frames, access sockets, or manage routing identities.

## Lifecycle and ordering

- A single transport owner creates and disposes every socket, observes every long-lived asynchronous pump, and
  propagates one terminal cause. Dispose received `ZMessage` values after copying their frames across the boundary.
- Shell requests execute serially. Control handling remains independently responsive during long execution.
- Preserve `busy -> handler and parented output -> reply -> idle`; publish idle from `finally` after busy.
- Publish `status:starting` once per host lifecycle and `iopub_welcome` according to XPUB subscription semantics.
- Interrupt cancels the host-owned active execution token. Shutdown sends its reply before stopping the host.
- `silent` suppresses IOPub execution output while preserving application execution semantics.
- `JupyterExecutionContext` preserves call order on the IOPub wire and does not maintain frontend display state.
- Missing optional language providers return protocol-valid `NotSupported` or `unknown` responses as specified by the
  message type.
- Terminal transport transitions complete once and fail all dependent operations consistently.

## Application ownership

- The host does **not** dispose the kernel application. The caller that constructed it owns it, and disposing the host
  is a separate act from disposing the application.
- `IJupyterKernelLifecycle` is the only stop notification, and it is optional by design so a stateless kernel never
  implements shutdown machinery it does not need. It fires exactly once per host, after every loop has stopped (the
  application can no longer be asked to execute) and before the transport is disposed, on every stop path — explicit
  `StopAsync`, a protocol `shutdown_request`, or host disposal. A faulting hook is surfaced on `Completion` like a
  disposal fault rather than allowed to mask the host's own terminal cause.
- Optional capabilities stay optional interfaces. Do not fold them into `IJupyterKernelApplication`: that would force
  every simple kernel to implement machinery it does not use, and it would be a breaking change for existing kernels.
