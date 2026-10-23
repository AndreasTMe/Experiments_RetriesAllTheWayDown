# Coding standards

How the C# in this repo is written. New code follows the code around it, and these rules write that down.

## Files and types

- One type per file, named after the type. A small type that only one class uses is nested in that class.
- File-scoped namespaces that match the folder (`namespace Runner.Commands;`).
- No `using` directives in the files: every project has a `GlobalUsings.cs`, and new namespaces go there.
- Types are `internal sealed` unless something outside the project needs them (ServiceDefaults is `public`).
- No primary constructors on classes or structs. Records are the exception: a record that is built from values is
  positional (`internal sealed record DeliveryRecord(string MessageId, int DeliveryCount, DateTimeOffset ReceivedAt);`),
  with no hand-written properties, constructor or `Deconstruct`. A record bound from configuration or JSON, with
  defaults, keeps `init` properties.

## Member order

Top to bottom:

1. Nested types
2. Constants
3. Fields: static before instance
4. Properties: static before instance
5. Constructors
6. Instance methods
7. Static methods

Within each group, order by accessibility: `public`, `internal`, `protected`, `private`.

## Naming

- Private instance fields: `_camelCase`. Private static readonly fields and constants: `PascalCase`.
- A field that only exists for `Interlocked` (`ref` access) may be a public lower-case field on a private nested class
  (`RunningTotal.value`).
- Async methods end in `Async`. A `CancellationToken` parameter is called `cancellationToken`; in short lambdas, `ct`.
- Local functions and lambdas use full words (`GetCounter`, `ComputePerMessage`), except one-letter lambda parameters in
  short LINQ chains.
- Metric and counter names are `snake_case` (`db_connection_failed`); JSON in summaries and results is `camelCase`.

## Layout

- Lines are at most 120 characters.
- Braces on their own lines, always, including single-statement `if`s and empty loop bodies.
- Arguments that don't fit on one line go one per line, indented once, with the closing parenthesis on the last
  argument's line.
- Consecutive declarations and assignments are aligned in columns: field types and names, `=` in a block of fields,
  property names in a block of auto-properties, and `=` in object and dictionary initializers.
- Object, collection and array initializers put their braces on their own lines, even with one entry.
- Expression-bodied members for one-liners (`=>` on the declaration line, the body on the next line if it doesn't fit).
- `var` for locals, unless the type isn't clear and matters.
- A blank line between members, and none at the end of a type.

## Comments

- Few comments. A comment says why something is done, or what a non-obvious value means, not what the next line does.
- A type or member that needs explaining gets a `//` comment right above it, not XML doc comments.
- Comments are full sentences, and wrap at 120 characters like the code.

## Behaviour

- No new packages without asking first. Pin package versions and container images to exact versions.
- Library defaults stay as they ship unless an `Experiment` switch changes them, and switches are off by default.
- Catch only what's expected, with a filter where it helps
  (`catch (Exception) when (!cancellationToken.IsCancellationRequested)`).
- Keep the experiment code boring. The faults should be the only interesting thing in it.
