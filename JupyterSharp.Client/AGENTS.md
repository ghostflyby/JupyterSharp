# JupyterSharp.Client instructions

## Ownership

This project is a reusable client for arbitrary Jupyter kernels. It contains three internal layers:

- `Transport`: ZmqSharp sockets, wire serialization, bounded queues, asynchronous ownership, terminal state, and disposal.
- `Protocol`: request correlation, pending operations, execution aggregation, stdin, late output, and event fan-out.
- public facade and local manager: stable .NET APIs, kernelspec parsing, child-process startup, interrupt, restart, and
  cleanup.

It must remain usable without `JupyterSharp.Kernel` and without any host application.

## Forbidden dependencies

- Reference only `JupyterSharp` for Jupyter contracts.
- Never reference `JupyterSharp.Kernel` or host-product concepts.
- ZmqSharp types and raw frames must not escape `Transport`.
- Transport must not own request/reply semantics, execution aggregation, or notebook UI reduction.

## The transport seam

`IJupyterTransport` is internal on purpose. Read this before proposing to publicize it.

- It is the **channel-level** seam: how the five Jupyter channels are carried. It is an in-process
  seam — the test transport, and any future in-process kernel embedding — not a third-party
  transport extension point.
- The byte-pipe layer below it is ZmqSharp's `IZTransport`, which is where the endpoint family
  (tcp / ipc / arbitrary stream) is chosen. Changing this seam cannot change the byte pipe.
- The channel topology above it is fixed by the Jupyter protocol (four channels plus heartbeat), so
  no implementation of this interface can produce a peer-to-peer alternative to ZMQ.
- The frontend-to-`jupyter_server` WebSocket transport is **not** another implementation of this
  interface. It is a different client that also needs a REST plane (start / interrupt / restart /
  shutdown) and token auth, and whose peer is a server rather than a kernel.

A public extension point that nobody consumes rots: other ecosystems have reserved transport traits
and interfaces that ended up with a single empty impl and no callers. Do not repeat that here. If
in-process embedding is ever built, open this seam on both sides (client *and* kernel) as one
designed feature, with a consumer to exercise it.

## Unknown fields and missing fields

- `JupyterMessage.Content` is authoritative and lossless: it holds the original `JsonElement`, and
  serialization writes it verbatim, so kernel-specific extension fields survive a receive/forward
  round trip.
- The typed DTOs are a **convenience projection, not the contract**. Projecting through
  `GetContent<T>` and re-serializing drops fields the DTO does not know about. When forwarding a
  message you did not originate, forward `Content`, not a re-projected DTO.
- Do not add `[JsonExtensionData]` to protocol DTOs speculatively. No observed kernel extends the
  top level of a comm or reply content object; add it when a real fixture requires it.
- Missing fields do **not** fail deserialization. System.Text.Json binds a missing constructor
  argument to `default`, so a non-nullable `string` property ends up holding `null`. "Deserialized
  successfully" therefore does not mean "content is complete". Where a peer is known to omit a
  field, give the parameter an explicit default (see `JupyterKernelInfo`) rather than relying on
  the signature.

## Transport lifecycle

- One transport owner creates and disposes shell, control, stdin, IOPub, and heartbeat sockets and observes every
  long-lived asynchronous pump.
- Shell and stdin share an identity; control has its own identity.
- External producers communicate through bounded command queues. Queue saturation terminates the connection with a typed
  backpressure failure; never drop protocol messages.
- Startup cancellation, owner-thread failure, disconnect, backpressure, and concurrent disposal converge on one terminal
  cause and promptly fail pending sends and pings.
- Dispose related ZmqSharp resources together only after owned pumps have stopped. Received `ZMessage` values are owned
  by the receiver and must be disposed after their frames have been copied across the transport boundary.

## Protocol and API constraints

- Match replies by parent message ID, channel, and expected reply type. Never correlate by execution count.
- An execution completes only after its reply and parented idle both arrive, in either order.
- Preserve `input_request` headers for `input_reply`. Late parented output remains observable as late output.
- Unknown messages become controlled events or errors and never crash the receive loop.
- Local cancellation removes local waiting; interrupt is an explicit control/manager operation.
- Request/reply APIs use `Task<T>`. Execution output is single-consumer `IAsyncEnumerable<T>`; event subscriptions are
  independent streams. Do not expose writable `Channel<T>` values.
- Asynchronous connection starts through a factory, not constructor side effects.
- `LocalJupyterKernelManager` owns process, temporary connection file, client, and cleanup. Shutdown uses one total
  timeout budget and kills the process tree when graceful shutdown cannot finish.
