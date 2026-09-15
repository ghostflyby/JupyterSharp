# AGENTS.md

Engineering constraints for JupyterSharp: a .NET implementation of the classic Jupyter messaging protocol, its
client, and its kernel host.

## Repository purpose

JupyterSharp is a standalone, reusable library set. It has no product consumer inside this repository and must never
acquire one: the executable that once hosted a Jupyter kernel was removed before extraction, and these libraries are
published to NuGet for arbitrary Jupyter frontends and kernel authors.

The three shipping packages:

| Package | Role |
|---|---|
| `JupyterSharp` | Transport-independent wire protocol: envelopes, HMAC, connection files, MIME, cursors, DTOs |
| `JupyterSharp.Client` | Client for arbitrary Jupyter kernels: ZeroMQ transport, correlation, executions, local process manager |
| `JupyterSharp.Kernel` | Server-side kernel host: ROUTER/XPUB/REP transport, dispatch, state publication, interrupt, shutdown |

`JupyterSharp.Tests` owns the coverage for all three and is never packed.

## Dependency direction

```text
JupyterSharp
    ^
    |-- JupyterSharp.Client
    `-- JupyterSharp.Kernel
```

`Client` and `Kernel` must never reference each other. A boundary problem is never solved with a reverse project
reference; put a small interface in the lower-level owning layer instead.

Only `Client` and `Kernel` carry the `ZmqSharp` dependency. `JupyterSharp` itself must stay free of any transport
implementation.

## Global invariants

Every change must preserve these invariants:

1. Protocol requests are correlated by message ID, channel, and expected reply type — never by execution count.
2. Completion follows protocol state (reply plus parented idle), never fixed delays or guessed ordering.
3. Causal parent IDs are retained on IOPub, and IOPub wire order is preserved.
4. Lifecycle frames lead output (`busy` before handler output), and terminal frames precede `idle`.
5. Control and heartbeat stay responsive during long shell execution.
6. Cancellation is cooperative through all layers and may escalate to owned child-process termination.
7. Raw ZeroMQ frames do not cross transport boundaries; `ZmqSharp` types and `ZMessage` values do not escape
   `Transport`.
8. Backpressure never silently drops protocol messages. Queue saturation terminates the affected connection with a
   typed failure.
9. Unknown protocol messages cannot crash long-running receive loops.
10. Disposal stops owned loops, closes sockets, completes streams, and fails pending operations exactly once.
11. Binary buffers stay binary and outside JSON; their order is preserved.
12. Cursor offsets are Unicode code-point offsets, converted through `JupyterCursorPosition`, never sliced directly.

Use structured concurrency. Every long-lived loop or socket needs an owner, cancellation source, completion task, and
deterministic disposal path. Observe background exceptions. Do not hold locks while awaiting transport sends, bounded
queues, or process exit. Multi-stage shutdown uses one total timeout budget.

## Compatibility boundaries

Target classic messaging protocol 5.5 and classic connection files. Readers tolerate unknown optional fields and
compatible older protocol announcements; writers avoid unsupported fields; unsupported capabilities return
protocol-valid errors or fallback statuses; CurveZMQ and unsupported signature schemes fail explicitly.

Unless explicitly requested, do not add history, comm, debug, subshell, Jupyter 5.6 registration, automatic
reconnect, remote provisioners, or another ZeroMQ implementation.

A public API or wire change is a breaking change for published packages. Version and document it.

## Public API and coding style

- Enable nullable reference types; warnings are errors.
- Prefer immutable records for protocol values, messages, and results, with explicit wire names.
- Prefer `Task` for request/reply and `IAsyncEnumerable<T>` for streams; use `ValueTask` only when justified.
- Accept `CancellationToken` on potentially blocking asynchronous operations.
- Keep constructors side-effect free; use asynchronous factories for asynchronous startup.
- Use `System.Threading.Lock` for gate/lock fields instead of `object`.
- Do not use the null-forgiving operator (`!`). Bind checked values with `is { } local` or restructure state so
  nullability is compiler-provable; document any unavoidable exception.
- Avoid global mutable state and service locators.
- Public APIs require XML documentation.
- Use explicit ownership for every disposable resource.
- Use source-generated serialization for protocol paths.
- Keep `JsonElement` as a compatibility or structured-data escape hatch, not the primary domain model.
- Do not expose writable `Channel<T>` values. Request/reply APIs use `Task<T>`; execution output is single-consumer
  `IAsyncEnumerable<T>`; event subscriptions are independent streams.
- Comment only non-obvious protocol or lifecycle assumptions.
- Documentation and code comments are written in English.

## Native AOT

The libraries declare `IsAotCompatible` and are consumed by AOT-published hosts, so trimming and AOT analyzers run
with warnings as errors. No runtime reflection or dynamic code generation: use source-generated `System.Text.Json`
metadata for protocol DTOs and avoid reflection serialization in hot paths.

## Verification

Standard acceptance:

```bash
dotnet test JupyterSharp.slnx
dotnet build JupyterSharp.slnx --no-restore -warnaserror
dotnet format JupyterSharp.slnx --verify-no-changes --no-restore
git diff --check
```

For transport, process lifetime, cancellation, or timing changes, run focused tests first. For wire-format changes,
update and inspect representative round-trip and malformed-input fixtures. Never report completion without naming
relevant checks that were skipped.

## Scoped instructions

| Scope | Local instructions |
|---|---|
| Wire protocol | `JupyterSharp/AGENTS.md` |
| Client | `JupyterSharp.Client/AGENTS.md` |
| Kernel host | `JupyterSharp.Kernel/AGENTS.md` |
| Coverage | `JupyterSharp.Tests/AGENTS.md` |
