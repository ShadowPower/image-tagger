# Test infrastructure and quality gates

## Categories (`[Trait("Category", ...)]`)

| Category | Runs where | Filter |
|---|---|---|
| *(none / Unit / Integration)* | everywhere, no model needed | default |
| `RealModel` | machines with the LFS model pulled | `--filter "Category=RealModel"` |
| `WindowsRuntime` | real Windows only | `--filter "Category=WindowsRuntime"` |
| `MacRuntime` | real macOS only | `--filter "Category=MacRuntime"` |
| `Visual` | headless renderer screenshots | `--filter "Category=Visual"` |

Commit-level gate (default local run): everything except
`RealModel`, `WindowsRuntime`, `MacRuntime`, `Visual`:

```
dotnet test --filter "Category!=RealModel&Category!=WindowsRuntime&Category!=MacRuntime&Category!=Visual"
```

## Failure semantics

- Red test, no trait -> code error.
- Skipped `WindowsRuntime`/`MacRuntime` -> missing real-machine capability (message says which).
- Skipped `RealModel`/LFS gate -> missing LFS asset; run `git lfs install && git lfs pull`.
  A pointer file is never fed to a decoder (`LfsAssets.RequireRealFile` guards this).

## Quality gates (plain tests, always on)

- `AbsolutePathGateTests`: no drive-letter paths, no home-directory paths, no
  local username in committed sources. The scanner itself contains only regex
  rules; synthetic samples live in the excluded gate test file.
- `LfsAssetGateTests`: real model present (else skip), pointer detection works.
- `ModelPackManifestGateTests`: `Assets/Models/*/manifest.json` must validate
  against the embedded Core schema (activates when workflow B lands manifests).

## Deterministic helpers

- `Determinism.WaitForAsync` / `PumpUntilAsync` / `CancelAfter`: bounded
  polling with descriptive timeouts; `Dispatcher.UIThread.RunJobs()` pumping.
- `HeadlessTestApp` + `[AvaloniaFact]`: headless Avalonia environment
  (no real window server, deterministic frame pumping).
- `TempDirectory` / `TestAssets`: disposable scratch dirs; fixture copies so
  shared `TestData` is never mutated.
