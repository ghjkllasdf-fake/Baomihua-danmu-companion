# Spec: Native Beta Installer and Portable Distribution

Status: implemented and locally validated; see docs/BETA-VERIFICATION.md. No remote publication.
Date: 2026-10-29, Asia/Shanghai.

## Objective and Baseline

Deliver both a Windows x64 EXE installer with a selectable destination and an
extract-and-run portable ZIP. Neither edition may depend on PowerShell.

The verified latest public release is `v0.1.0-preview.15`, commit
`8838254e1dd9858b74884c69d9d72974cdc6620e`. Use this publication checkout,
not the older preview.10 sources in the enclosing migration directory.
The proposed new version is `0.1.0-beta.1`; existing releases remain unchanged.

## Assumptions for Review

- Preserve the current React interface, tray behavior, matching rules, renderer,
  API encryption, and optional startup behavior rather than redesigning them.
- "No PowerShell" covers the shipped payload and every application-owned process,
  including sampling, startup registration, verification, installation and removal.
  Historical development tests may remain in source, outside the shipped payload.
- Both editions include a self-contained .NET desktop runtime and a private
  WebView2 Fixed Version runtime. Users do not install either runtime separately.
- Target Windows x64. Report the exact tested Windows versions; do not extend
  local test results to untested operating systems, ARM64 or network shares.
- The installer defaults to a per-user location and always displays destination
  selection, including upgrades. A chosen path must be writable and suitable for
  the private browser runtime. Permission failures must be explicit.
- Portable means no installation or prerequisite installer is needed. It still
  uses a documented per-user data directory so read-only application directories,
  edition switching and upgrades do not destroy settings.
- Preserve the existing fixed per-user encrypted API store. Do not silently copy
  credentials into the portable folder or imply they transfer to another account.
- Produce and test local artifacts first. No GitHub push, tag or publication is
  included in this request, and no certificate purchase is authorized.

## Runtime Contract

1. Replace Host.ps1's dynamic compilation/bootstrap with a compiled Windows GUI
   entry point. Build the C# sources ahead of distribution; no Add-Type at runtime.
2. Replace ProbeWorker.ps1 and pwsh.exe invocation with a compiled worker process.
   Preserve the MTA sampling boundary, parent admission gate, framed transport,
   timeouts, Job ownership, reaping and cancellation; never collapse sampling
   onto the UI thread merely to simplify packaging.
3. Keep the existing STA UI and WPF overlay boundaries. Preserve WinRT/media
   projection loading, WebView2 origin restrictions, disabled password/form
   saving, bridge validation, and absence of ordinary-use debug ports.
4. Startup shortcuts launch the new EXE directly. Default startup remains off.
   Only remove or update shortcuts positively identified as belonging to this app.
5. Resolve immutable assets from the executable directory, not the current
   working directory. Store writable preferences, browser data and diagnostics
   under the documented current-user data root. Preserve the existing API path
   and DPAPI CurrentUser behavior. Do not scan unrelated directories for settings.
6. Explicitly select the shipped private WebView2 runtime. Honor required sandbox
   permissions without disabling the sandbox or changing global security policy.
   Fixed Version runtime updates become part of future application releases.
7. Retain all unchanged preview.15 behavior. Prior unverified latency, long-running
   playback, DPI and other acceptance items remain unverified, not silently green.

## Deliverables

- `DanmuCompanion-0.1.0-beta.1-win-x64-setup.exe`.
- `DanmuCompanion-0.1.0-beta.1-win-x64-portable.zip`.
- `SHA256SUMS.txt`, covering final installer and portable ZIP bytes.
- A manifest of payload hashes, source identity and pinned dependency provenance.
- Updated Chinese installation/portable/upgrade/removal instructions, MIT license,
  applicable third-party notices, known limitations and unsigned-status disclosure.
- Verification evidence for the actual final installer and extracted ZIP.

The two editions share the same application/runtime payload. Installer bookkeeping
and portable instructions may differ; program behavior and version must not.
Do not include developer SDKs, PowerShell, source/test scripts, real API values,
browser profiles, personal data or unrelated workspace files in either payload.

## Installer and Portable Behavior

- Build the installer with Inno Setup; explicitly keep the directory page enabled.
- Accept a custom local path, including Chinese characters and spaces. Verify the
  installed files and launch from that actual path, not just the default location.
- Provide a Start menu entry and an optional desktop shortcut, both targeting EXE.
- Application startup registration remains a separate explicit opt-in.
- Upgrading preserves settings and the encrypted API without logging their values.
- Removal deletes only installation-owned files/shortcuts. Preserve user data by
  default and document its location and explicit removal procedure. Do not remove
  files belonging to a portable copy or an unrelated old installation.
- A running instance must not be overwritten silently. Request normal exit or
  report it; never terminate unrelated user processes by executable name.
- The portable ZIP starts by double-clicking its EXE after full extraction, also
  from a moved/renamed folder. No BAT/PS1 launcher or setup step is required.
- Full offline UI/tray startup must work without system .NET/WebView2. Fetching
  real danmaku still requires the user's configured API and network connectivity.

## Proposed Structure and Commands

These are implementation interfaces, not existing or already-passing commands.
Keep the current compact C# style and existing native source names where practical.

```text
desktop/NativeHost/          compiled GUI entry point and project
desktop/NativeProbe/         compiled worker entry point and project
desktop/native/             reused application and sampling logic
tests/NativeDistribution/   native regression and process-boundary harness
installer/                  Inno Setup definition
scripts/build-release.mjs   pinned build/package orchestration, no PS dependency
scripts/test-release.mjs    final artifact checks and isolated smoke runs
artifacts/releases/         final deliverables and verification evidence
```

```text
npm.cmd --prefix ui ci --ignore-scripts
npm.cmd --prefix ui run lint
npm.cmd --prefix ui test
npm.cmd --prefix ui run build
npm.cmd --prefix ui run test:e2e
dotnet build desktop/NativeHost/NativeHost.csproj -c Release
dotnet build desktop/NativeProbe/NativeProbe.csproj -c Release
dotnet run --project tests/NativeDistribution/NativeDistribution.csproj -c Release
node scripts/build-release.mjs --version 0.1.0-beta.1
node scripts/test-release.mjs --version 0.1.0-beta.1
```

Publish both native programs for `win-x64` with `--self-contained true`.
Do not enable trimming or Native AOT while reflection/WinRT compatibility is
unproven. Pin the verified .NET SDK, runtime, native packages, browser runtime
and installer compiler; verify downloads before use. Prefer workspace-local
build tools instead of changing machine-wide SDKs or security settings.

## Verification and Success Criteria

- Retain existing assertions; add failing tests before changing runtime behavior.
  Port relevant existing C# fixtures to a compiled harness instead of treating
  legacy PowerShell-hosted tests alone as proof that the new executable works.
- Build and run native regressions plus UI lint/unit/build/browser checks. Report
  pre-existing/environment failures honestly; do not skip or weaken tests.
- Inspect the final package manifests and native dependency closure: no PS1/BAT
  launchers, pwsh.exe, powershell.exe or System.Management.Automation dependency.
- Extract the actual final ZIP to a fresh directory with Chinese characters and
  spaces; launch its EXE with an unrelated working directory and restricted PATH.
  Use runtime isolation to prevent fallback to installed .NET or WebView2.
- Observe the real UI/native handshake, tray opening and closing, settings
  persistence, API save/remove failure boundaries, sampling transport and normal
  exit. Record the test-owned descendant process tree: no PowerShell processes,
  no remaining owned worker/browser processes after exit.
- Test installer destination selection in the actual wizard, then install into a
  nondefault test-owned path. Verify installed payload hashes and EXE shortcuts;
  launch that installation, upgrade/reinstall preserving isolated data, and remove
  it. Verify installation-owned files and registration are removed while unrelated
  sentinel files and portable files are not deleted.
- Exercise missing/corrupt runtime/assets, unwritable data/path and interrupted
  operations: fail clearly rather than claiming connected/installed success.
- Audit secrets and third-party redistribution notices in final artifacts, then
  calculate final hashes after all edits. Do not claim test coverage from older
  payloads or from an installed development environment alone.
- Completion requires both requested final artifacts and evidence for custom-path
  installation, extract-and-run startup, and absence of runtime PowerShell.
  A successful compile, wrapper EXE or installer creation alone is insufficient.

## Scope and Review Gate

Always preserve existing user data, playback state and unrelated processes. Never
modify global execution policy, disable antivirus or browser sandboxing, publish
credentials, weaken acceptance assertions, or overwrite existing release assets.

The build machine currently has .NET 9 runtimes but no .NET SDK. Acquiring verified
workspace-local build tools is required implementation work, not a user blocker.
Private browser runtime increases the download size and needs ongoing security
updates; document those costs rather than silently dropping the offline payload.

Open questions: no missing core requirements. Preserve the existing per-user
encrypted API location while delivering both explicitly requested editions.

## Official References

- https://learn.microsoft.com/en-us/dotnet/core/deploying/
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version
- https://jrsoftware.org/ishelp/topic_setup_disabledirpage.htm
- https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm
