# SportsModels.Basketball — C# coding practices

These are agreed project defaults; apply proportionately to task complexity. Avoid needless abstractions.

## Architecture
- Use pragmatic onion architecture, with per-function projects or layers: **Contracts, Application, Domain, Interfaces, Definitions, Infrastructure, Autofac**. An onion may initially be one project with these folders.
- When exposing an onion via NuGet, extract `Xxx.Yyy.MyOnion.Contracts` and `Xxx.Yyy.MyOnion.Autofac` as separate projects/packages.
- **Contracts:** public service interfaces and public input/output records; favor `get; init;`, few dependencies; communication across onions.
- **Application:** orchestrates the use case; constructs domain objects, invokes domain behaviour and calls interfaces for I/O. Do not couple Application directly to Infrastructure. Use factories for complex domain construction; converters for straightforward type transformations.
- **Domain:** encapsulates business rules and invariants; intent-revealing mutations. Use pragmatic encapsulation: if domain state has only one external use case, prefer private fields and purpose-specific public query methods over broad public properties. If multiple legitimate consumers require state, expose appropriately. Convert to contracts via `ToContracts()` where appropriate.
- **Interfaces:** internal I/O abstractions. **Definitions:** internal shared types. **Infrastructure:** concrete I/O. **Autofac:** DI registrations; in a split onion, Autofac is the only layer referencing Infrastructure.

## Dependency injection and validation
- Autofac with constructor injection. Register multiple implementations and inject `IEnumerable<T>` or `T[]`; select via `IsFor(...)` and `.Single(...)` where exactly one match is required (fail for zero/multiple).
- `SingleInstance()` for shared immutable datasets where suitable; ensure thread safety.
- Validate public onion-entry inputs with reusable rules behind `IInputValidation` where worthwhile; throw appropriate exceptions for invalid data. Avoid repeated validation of guaranteed invariants.

## Testing and execution
- `.Tests.Unit`: NUnit fast, deterministic automated tests; must pass in ordinary builds.
- `.Tests.QualityAssurance`: NUnit heavier data/regression/performance/pricing tests; not part of routine automated builds.
- `.Tools`: NUnit project for executable tooling, not necessarily tests.
- Optional `.Host`: manual or scheduled execution entry point.
- Test transport errors, invalid inputs, and reruns as appropriate. Keep heavy domain QA separate from lightweight transport validation.

## Style
- PascalCase `NbaModel`-style naming in C# projects and types as applicable to this repository.
- Favor clear descriptive names and high-level methods readable as roughly five or six coherent steps, not a rigid count. Extract meaningful independently testable logic, not trivial one-line wrappers.
- Prefer `var` for locals, primary constructors where appropriate.
- Blank lines between statements by default, except closely related constructor assignments; no blank line immediately after an opening brace.
- Make assumptions and caveats visible. Avoid overengineering, speculative abstractions and unnecessary rewrites.

## Design process
For substantive basketball/model logic, explicitly explore and agree the conceptual/domain object model before implementation. For straightforward API retrieval/plumbing, proceed from an approved task without unnecessary domain-design ceremony.

## Task #2 boundaries
Raw retrospective NBA play-by-play retrieval and byte-preserving JSON storage only. Do not add basketball-specific parsing, canonical contracts, cleaning, live data, databases or daily scheduling.
