# Corresponding source for includeAPI

This tree accompanies the executable release. `upstream/original/` is the
unchanged huangxd-/danmu_api commit 4d325e096bbbf0c1ff261a9bfa638f7c58784f60;
`upstream/modified/` is the exact source shipped by includeAPI. Original root
LICENSE is GNU AGPL version 3 while its original package.json says ISC; both
are retained. The modified worker and `project/backend/entry.mjs` are offered
under AGPL-3.0-only. The changes dated 2026-10-29 are described and executable
in `project/backend/patches/0001-node-brotli.json` and
`project/backend/patches/0002-dan-any-node-profile.json`. The separate original
.NET/UI application is offered under its own top-level MIT license.

`archives/` contains the exact upstream original ZIP, official Node 22.23.4
source tar.xz, and a pinned source-repository ZIP plus the original npm archive
for each of the 36 runtime packages. `dependencies/SOURCES.json` identifies
every version, commit, URL, SHA-256, npm SRI, and any source-selection caveat.
The npm archives are supplied for reinstallation; they are not treated as a
substitute for the preferred editable source repositories. The @dan-uni library
is LGPL-3.0-or-later and its original TypeScript, build controls and license
are in its source archive. Its packaged runtime manifest is changed only by
patch 0002. Its JS files are unmodified and replaceable with a compatible
modified library build without signing keys. The two noted Git submodules are
external conformance-test data, not needed to build or run the shipped worker;
their pinned commits and omission are explicitly recorded in SOURCES.json.

Read VERSION from the `<Version>` value in `project/desktop/NativeCommon.props`.
The corresponding-source archive must be taken from the same Release as the executable.

## Rebuild and install a modified copy

On Windows x64, unpack this source ZIP. The `project/` directory contains the
application source, adapter, pinned runtime manifest and lock, preparation and
installation code. The unmodified source can be checked and prepared by running
the following commands from `project/`:

    node backend/prepare-corresponding-source.mjs
    node scripts/prepare-includeapi.mjs
    node --test backend/tests/*.test.mjs
    node backend/tests/real-worker-health.mjs

These commands use build-time Node/npm and Windows `tar.exe`. The corresponding
source preparer first uses the included `third-party/archives/`, and otherwise
downloads only inputs matching the pinned SHA-256. The runtime preparer uses
npm ci with the production lock, disables package scripts and applies the two
verified patches. It does not require an application signing key. Full installer
build instructions and pinned .NET/Inno inputs are in
`project/docs/INCLUDEAPI-BUILD.md`. The application runtime itself does not
install Node/npm or restore packages.

## Replace the LGPL library

The installed application's manifest checks file hashes, so editing its
`node_modules` in place is not sufficient. To build a compatible replacement:

1. Extract `third-party/archives/source-dan-uni-dan-any-2.3.9-401205834f37.zip`
   to a short, user-writable path; on Windows use an extractor that handles the
   repository's `.github/copilot-instructions.md` symlink or treats it as an
   ordinary text file. Edit the preferred TypeScript files in `src/`.
2. With the source package's declared Bun 1.3.14 available for development,
   run `bun install --frozen-lockfile --ignore-scripts`, `bun run buf`, and `bun run build` in
   the extracted library root. These produce `dist/`. The source includes the
   existing `src/core/db/migrations.json`; this procedure does not regenerate
   database schema. On Windows the upstream `db:generate` helper currently
   converts a file URL with `.pathname`, producing an invalid `/C:/...` path;
   database schema changes require correcting that upstream helper separately.
3. If `project/` is from this ZIP rather than a Git checkout, initialize a local
   Git repository and make an initial commit; the application builder needs a
   local source identity. From `project/`, run:

       git init
       git add .
       git -c user.name=Local -c user.email=local@example.invalid commit -m "Local source"

   Then prepare and package the replacement:

       node scripts/prepare-includeapi.mjs --local-dan-any <built-library-root>
       node backend/tests/real-worker-health.mjs
       node scripts/build-release.mjs --version VERSION --include-api --allow-dirty --output <new-empty-output-directory>

The replacement must retain the package name, version, LGPL license, required
exports and the remaining locked runtime dependencies. The preparer drops the
three unused database dependency declarations and copies the modified dist,
LICENSE and README. It sets `customDanAny` and recalculates the entire payload
inventory. The candidate builder recalculates the application manifest and
marks the build custom/dirty; no original author signing key is needed. Install
or extract this candidate to a separate, user-owned location after closing the
app. If redistributing a modified library, provide its actual modified source
and required notices as well; the stock source inventory alone does not cover
your changes. A modification that adds dependencies requires editing and
revalidating the lock and build controls rather than using this simple option.

Original npm tarballs in `third-party/archives/` may be fed to an npm cache for an offline
build; network access to the pinned npm registry is used by the normal build
command. SHA-256 and npm integrity are pinned, but rebuilding with a different
npm or compiler version is not claimed to be bit-for-bit reproducible.
Licenses and notices accompany the individual upstream and dependency sources.
No warranty is provided, subject to the applicable licenses.
