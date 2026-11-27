---
name: world-sync
description: World-space anchoring for VRChat avatar props, built on Freeze To World constraints
---

# world-sync

Unity 2022.3 project at the repository root (the "main project", which is the
dev harness) with redistributable VPM packages as directories under
`Packages/`. The VRChat SDK arrives through the vendored bootstrapper on first
editor open and never enters git. Code is AGPL-3.0-or-later with the
VRChat/Unity linking exception in `LICENSE_ADDENDUM`.

## Repo Map

- `Assets/` — the development harness project's own content; nothing shipped lives here.
- `Packages/net.wirenook.world-sync/` — the world-anchor editor API and the anchor pin prefab; see README for what it does and does not guarantee. An `AnchorBus` can carry a value for each slot (`values:` in `Configure`, read as `bus.Value(slot)`): eight synced bits for all of them, sent in each slot's turn and ahead of it when the wearer changes one. A size or a heading of something set down belongs there, not in a synced parameter of its own; `AnchorBusCheck` covers it wherever the controller has the channel's `TxValue`.
- `Packages/com.vrchat.core.bootstrap/` — VRChat's resolver bootstrapper, vendored byte-for-byte. Don't touch.
- `Packages/vpm-manifest.json` — the main project's VPM dependencies (by default the VRChat Avatars SDK, lilToon, and Modular Avatar; NDMF and `com.vrchat.base` are locked as their dependencies). MA and NDMF don't compile without the SDK, because it ships the `System.Collections.Immutable` and Harmony they use. Edit it with a VPM client (ALCOM / `vrc-get`), not by hand.
- `Packages/.gitignore` — allowlist inversion keeping resolver-installed packages out of git. Load-bearing.
- `ProjectSettings/` — pinned Unity project configuration (`ProjectVersion.txt` names the editor build).
- `scripts/` — the checks and builds; CI runs these same scripts, nothing else.
- `.github/workflows/ci-cd.yml` — lint-and-test on push/PR, package publish on release.
- `.githooks/pre-commit` — the fast checks; opt in with `git config core.hooksPath .githooks`.
- `Website/index.html` — placeholder package page; unthemed, unpublished, awaiting a future pass.
- `CREDITS.md` — intake ledger for everything third-party.

This repository is developed inside [cass-avatar](https://github.com/CassidyPrather/cass-avatar), which mounts it and its sibling packages as submodules under `modules/` and opens them all in one Unity project. Generated prefabs and animators are rebuilt from code (`Tools > Wirenook > ...`), never edited by hand.

## Commands

```bash
scripts/check-meta.sh          # every visible file has a committed .meta
scripts/check-manifests.py     # package.json sanity across Packages/
scripts/format.sh --check      # dotnet-format over the CI-visible C#
scripts/test.sh                # headless NUnit run of Runtime/Core + Tests
scripts/build-packages.sh      # VPM .zip + .unitypackage per package -> dist/
```

## The SDK Boundary

- The VRChat SDK and Unity are proprietary; the repository must stay clonable,
  checkable, and testable without either installed.
- `Packages/*/Runtime/Core/` is engine-free C#. `scripts/test.sh` compiles it
  with plain dotnet against NUnit alone, so a `using UnityEngine` in Core is a
  CI failure, not a style nit. Engine and SDK glue lives outside Core and is
  exercised in the editor instead.
- Tests use classic NUnit asserts only: the same files must pass `dotnet test`
  and the editor's Test Runner, and the classic API is the overlap.
- Never weaken the `Packages/.gitignore` allowlist; it is what makes committing
  non-redistributable material structurally impossible.

## House Rules

- Every asset gets a `CREDITS.md` line at intake — source, author, license, URL. CC0 first.
- Nothing proprietary and nothing licence-incompatible enters this repository; generate or draw it instead.
- Unity editor stays on 2022.3.22f1, the version VRChat supports. Runtime Unity packages in `Packages/manifest.json` (Timeline, TextMeshPro, AI Navigation, uGUI) stay at the versions the VRChat client ships. Only editor-only tooling (IDE integrations, Test Framework) tracks the latest.
- Version lives in `package.json`; a release publishes every package whose version matches the tag.
- The linking exception in `LICENSE_ADDENDUM` is load-bearing legal text. Don't reword it casually.
