# Snook

Snook is being built as a local-first, data-private task and time system for
Windows, Linux, and Android. The new implementation will use C#/.NET and
Avalonia UI, with the same application contract available in embedded mode or
through an optional headless daemon.

## Status

Specification and repository foundation. Product implementation has not begun.

Start with the [Snook specification](.opencode/artifacts/specs/spec-2026-09-08-snook/README.md).

## Repository layout

- `Snook.slnx` — .NET solution; projects are added in the first delivery phase.
- `.opencode/artifacts/specs/` — product and engineering specifications.
- `refs/grouper-main.zip` — immutable legacy reference used for behavior and migration analysis.
- `docs/` — future user-facing and contributor documentation.

## Toolchain baseline

- .NET 10 LTS / C# 14
- Avalonia 12.x (exact package versions will be centrally pinned when projects are scaffolded)
- SQLite through `Microsoft.Data.Sqlite`

The Android workload is intentionally not installed or restored during this
specification phase.

## Legacy reference integrity

`refs/grouper-main.zip` SHA-256:

```text
2b26bb9d5fc8262614d50a00d82a7b66af2c050355da3994c2c8f2b24fac46d0
```

Do not develop against the extracted Python source or mutate an original
database in place. Legacy data is handled by a read-only importer defined in
the migration specification.

## Licensing

The legacy archive is AGPL-3.0. Snook is proprietary, closed-source software. All rights are reserved by the
licensor. The legacy archive remains AGPL-3.0 and is used only as a reference;
legacy implementation code must not be copied into Snook without an explicit
license review.
