# Tests

Unit tests for the Jellyfin-free decision logic — diffing, scheduling floors, cap
arithmetic, TMDB backoff and key precedence.

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj
```

This project is deliberately **not** in `Gelato.sln`. Both CI jobs operate on the
solution, and adding a test project there risks changing what CI builds and ships.

Anything that touches Jellyfin's request pipeline is not tested here — per
`CLAUDE.md`, that behaviour only manifests inside a running server and is verified
manually.
