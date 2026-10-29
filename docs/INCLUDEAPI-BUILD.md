# Building includeAPI Beta3

This is an additional v0.1.1-beta.3 variant, alongside the ordinary installer
and Portable. It does not replace old release assets or tags. The runtime
boundary is in `INCLUDEAPI-CONTRACT.md`; prepare the pinned native toolchain
as described in `DEVELOPMENT.md`. End users do not install global build tools.

## Inputs and preparation

- `native-toolchain.json` pins the workspace .NET SDK/runtime, WebView2 SDK,
  projection assemblies, Microsoft bootstrapper and Inno compiler.
- `backend/toolchain.json` pins the official Node executable/source, upstream
  archive, manifests, locks, patches and dependency-source metadata.
- `backend/dependency-sources.json` maps every production package to its exact
  npm archive and preferred editable source archive, with hashes and caveats.
- The original upstream AGPL root license and conflicting ISC package metadata
  are both retained. The local adapter and covered modifications are offered
  under AGPL-3.0-only; the separate original .NET/UI remains MIT.
- Exact patch 0001 uses Node's built-in Brotli decoder. Patch 0002 removes unused
  database dependency declarations from dan-any's package metadata, not its JS.
  The unused esbuild/server and database dependency graph is not installed.
  Source/API business defaults and the upstream limiter are not tuned away.

Run from the checkout on Windows x64 with build-time Node/npm and tar available:

```text
node backend/prepare-corresponding-source.mjs
node scripts/prepare-includeapi.mjs
node --test backend/tests/*.test.mjs
node backend/tests/real-worker-health.mjs
```

The first command prepares `.deps/includeapi/corresponding-source/` and verifies
it against the tracked source inventory. It refuses to overwrite an existing
source tree. A maintainer changing reviewed source inputs can explicitly use
`--update-inventory`, review the generated inventory and commit it before the
final build; this is not a way to bypass missing source or integrity failures.

Runtime preparation uses locked npm production packages without lifecycle
scripts or shell wrappers, applies exact hash-checked patches and emits
`.deps/includeapi/payload-inventory.json`. It does not install a Windows service
or change firewall rules. The offline real-worker health test makes no provider
requests. For library rebuilding/replacement, follow
`../backend/CORRESPONDING-SOURCE.md`; custom builds must be labeled as such and
cannot pass official-release gates.

Run the native regression harness, UI check and browser tests from the project,
plus `node --test tests/*.test.mjs`. Audit both UI and production backend
packages. A zero-advisory audit is not a proof of security. Real-player/provider
acceptance is separate from fixture tests and must use isolated user data;
never read or publish saved external API credentials, private paths or viewing
history.

## Final artifacts

Commit reviewed sources before the final build:

```text
node scripts/build-release.mjs --version 0.1.1-beta.3
node scripts/build-release.mjs --version 0.1.1-beta.3 --include-api
node scripts/build-corresponding-source.mjs --version 0.1.1-beta.3
node scripts/test-release.mjs --version 0.1.1-beta.3 --evidence artifacts/beta3/final-standard
node scripts/test-release.mjs --version 0.1.1-beta.3 --include-api --evidence artifacts/beta3/final-includeapi
```

Use a new evidence directory for each test run. Keep BaoMiHua closed for offline
artifact/lifecycle QA, then explicitly conduct actual movie acceptance on the
same final payload. The installer and Portable of each variant share one
manifest-hashed payload. Ordinary builds retain a 210 MiB budget; includeAPI
has a separate 400 MiB budget and exact API inventory. Runtime PowerShell,
batch wrappers, private credentials and private WebView2 remain prohibited.
The includeAPI 16,384-file ceiling does not apply to ordinary manifests.

The separate corresponding-source ZIP contains tracked project source,
original/modified upstream trees, exact patches, full pinned source archives,
npm input archives, notices and build controls. Nested source archives are
allowed only under their locked `archives/` paths. They may retain upstream
compiled test fixtures; this source asset is not described as binary-free and
is never copied into application runtime payloads. Runtime allowlists are not
relaxed by the source-archive rules. Complete inventories, hash checks, source
stability checks and no-overwrite guards bind the source archive to the same
clean commit as the EXE packages. Byte matching does not replace review of
source completeness or license obligations.

Additional actual source-picker/lifecycle QA is available in
`scripts/test-includeapi-runtime.mjs`, with a new isolated profile and the player
closed. Do not overwrite or uninstall an existing user's application for QA.
Local debugging may use `--allow-dirty`; a custom dependency is explicitly
marked custom/dirty and cannot be published as an official release.

## Publication

These build commands do not push, merge, tag or publish. Publication requires
separate user authorization after final-byte, real-source and license checks.
Use a draft prerelease, verify uploaded assets by downloaded bytes, then publish
with the concise version title and Changelog-only notes. Do not rewrite old
assets/tags or mislabel the Beta as a stable release. The corresponding-source
asset and checksums must remain available with the executable release.
