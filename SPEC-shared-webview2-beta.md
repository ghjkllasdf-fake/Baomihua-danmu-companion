# Shared WebView2 Beta Distribution

Date: 2026-10-29. Version: 0.1.0-beta.2.

## Authorized Outcome

Build an installer with a selectable local destination and a Portable ZIP, then
publish both to this repository's GitHub Release as a prerelease. Keep beta.1's
existing local offline artifacts unchanged. Publish a release branch/tag; do not
force-push, replace old assets, or silently change the remote main branch.

The requested approximately 200 MiB means installed/extracted application files,
not a padded download size. Preserve self-contained .NET 10.0.14; use installed
stable Evergreen WebView2 instead of bundling its 673 MiB private copy. Both app
and worker remain compiled EXEs, with no PowerShell dependency.

## Acceptance Contract

- Application payload is at most 210 MiB and contains no private browser runtime.
- Clear inherited browser/profile/debug/channel overrides; select stable Runtime,
  not Edge browser or preview channels. Keep isolated per-user browser data.
- A missing/unsupported Runtime gives an actionable native error without opening
  a browser, installing software, or accessing the network during automated tests.
- Include Microsoft's signed Evergreen bootstrapper; installer runs it only when
  required, handles failure without installing the application, and rechecks the
  prerequisite. Portable users can explicitly run the same EXE if required.
- Do not remove the user's shared Runtime on uninstall. Never modify its ACLs.
- Detect full-offline beta.1 layouts and require uninstall before replacing an
  installed copy, or a new empty directory for a separate Portable copy,
  rather than silently retain the old private browser and misleading size claims.
- Preserve existing startup ownership, DPAPI, profile isolation, destination and
  reparse/collision protections; no weakened/deleted assertions or test skips.
- Refuse a different destination while an AppId install is registered. Separate
  Evergreen browser cache from beta.1's fixed-browser cache to avoid downgrade reuse.
- Keep runtime/installer/bootstrapper versions, hashes, signatures and licenses.
- Test actual final installer and moved ZIP, matching payload hashes, native/React
  handshake, installed stable browser process path and observed descendant exit.
- Run native, Inno, packaging/privacy and existing UI tests, plus independent review.
- Build from clean committed source, then verify exact final bytes before upload.
- Create a draft prerelease, upload only reviewed final assets/notes/checksums,
  verify remote bytes/digests/tag commit, then publish with prerelease=true.

## Evidence And Limitations

Private evidence: artifacts/shared-beta/. Final files: artifacts/releases/0.1.0-beta.2/.
Only Windows 11 x64 is available for runtime acceptance. Preserve existing Win10,
ARM64, multi-DPI, long real-player and strict-latency limitations. Missing Runtime
behavior uses isolated/injected tests; never uninstall or sabotage the real shared
Runtime just to manufacture that condition. This release adds no API telemetry.

## Official Contracts

- https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution
- https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environmentoptions.releasechannels
- https://docs.github.com/rest/releases/releases
- https://docs.github.com/rest/releases/assets
