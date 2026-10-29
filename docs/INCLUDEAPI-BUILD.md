# Building the local includeAPI candidate

This is an additional beta.3 variant, not a replacement for published beta.2.
The approved boundary is in `INCLUDEAPI-CONTRACT.md`. Follow the existing native
toolchain setup before these steps; do not install global runtimes for the app.

## Inputs and preparation

- `native-toolchain.json` pins the workspace .NET SDK, runtime, WebView2 SDK,
  projection assemblies, Microsoft bootstrapper and Inno compiler.
- `backend/toolchain.json` pins the official Windows x64 Node executable and
  license, complete upstream commit archive, package manifest and npm lock hash.
- `backend/package-lock.json` records exact production npm tarball integrity.
- `backend/entry.mjs` is the complete loopback adapter source. It imports the
  unchanged upstream worker, not its public-facing Node server.

Run on Windows x64 with build-time Node/npm available:

```text
node scripts/prepare-includeapi.mjs
node --test backend/tests/*.test.mjs
node backend/tests/real-worker-health.mjs
```

Preparation verifies downloads, keeps the full upstream source and dependency
contents, installs locked production packages without lifecycle scripts or npm
shell wrappers, and emits `.deps/includeapi/payload-inventory.json`. It does not
start a Windows service or change firewall settings. Real-worker health tests
do not request any provider. Runtime entrypoint, manifest, lock and provenance
must match the current source, not a stale preparation from a previous build.

Run the native regression harness, UI check and browser tests as described in
the project, plus `node --test tests/*.test.mjs`. Audit both UI and production
backend dependencies with npm's native audit command. Do not equate an empty
advisory result with a security proof.

## Build and artifact verification

Commit the reviewed sources before the final build:

```text
node scripts/build-release.mjs --version 0.1.0-beta.3 --include-api
node scripts/test-release.mjs --version 0.1.0-beta.3 --include-api --evidence artifacts/includeapi/final-validation
```

Use a new evidence directory for each run. The installer and portable ZIP share
one manifest-hashed payload. Normal builds retain the 210 MiB limit; this variant
has an explicit 400 MiB limit and exact API file inventory. PowerShell, batch
wrappers, private credentials and private WebView2 remain prohibited. The
variant's 16,384-file verification ceiling does not apply to ordinary manifests.

Additional actual-UI QA may be run against the built payload with
`scripts/test-includeapi-runtime.mjs`; it must use a new isolated profile and
keep the real player closed to avoid automatic provider requests. Never use a
test to modify an existing user's application installation or credentials.

No command here publishes, pushes, tags or merges anything. Local candidates
may use `--allow-dirty` for debugging, but dirty artifacts cannot pass final
release QA and must not be presented as reproducible release assets.

## Publication gate

Keep the full source, lockfiles, modifications, notices and build instructions
available with any later distribution, and resolve the outstanding licensing
questions before a public release. The upstream root AGPL-3.0 versus package
ISC inconsistency and brotli's missing standalone MIT text are retained in
`backend/NOTICES.md`; copying original metadata is not a legal determination.
The present task produces a local test candidate only.
