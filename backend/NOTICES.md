# includeAPI source and third-party notices

The includeAPI adapter `entry.mjs` and the modified upstream worker in
`upstream/` are distributed as an AGPL-3.0-only covered program. The upstream
root LICENSE contains the GNU Affero General Public License version 3 and is
included in `upstream/LICENSE`. The unmodified upstream package.json says ISC;
that original metadata is preserved in `upstream/package.json`, not substituted
for the root license. The separate .NET host and UI retain their own MIT license
as described in the application's top-level LICENSE.

Upstream: huangxd-/danmu_api commit
4d325e096bbbf0c1ff261a9bfa638f7c58784f60. Modification on 2026-10-29:
`patches/0001-node-brotli.json` changes the codec to use Node's native Brotli
implementation in this Node-only build. The original source, exact patch,
modified source, dependency source archives, and build/install controls are in
the companion `DanmuCompanion-0.1.1-beta.3-corresponding-source.zip` release
asset. The runtime package.json and package-lock.json are the modified runtime
profile, not the upstream original. The former brotli npm package is not shipped.

`@dan-uni/dan-any` 2.3.9 is LGPL-3.0-or-later; its LICENSE is in its package.
That LGPL text incorporates GPL version 3, included separately as
`licenses/GPL-3.0.txt` (official GNU text, SHA-256
3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986;
https://www.gnu.org/licenses/gpl-3.0.txt).
`patches/0002-dan-any-node-profile.json` removes three unused database backend
dependency declarations from that package's runtime manifest, not its JS.
Its preferred TypeScript source, original npm archive, patch, build controls
and license are included in the corresponding-source asset. Users can replace
this library with a compatible modified build without a private key; follow the
source asset README. The esbuild-only upstream server/build path is not shipped
as a runtime dependency. Ordinary worker source still remains in `upstream/`.

Node.js 22.23.4 is distributed under `licenses/Node-LICENSE.txt`; its complete
versioned official source is in the corresponding-source asset. The locked
production npm package contents and original notices are in `node_modules/`;
the corresponding-source asset also contains their pinned source repositories,
original npm archives, and `dependency-sources.json`. Optional npm `.bin`
wrappers are not shipped. Additional unmodified license texts are
`licenses/Apache-2.0.txt`, `licenses/nodable-MIT.txt`, and
`licenses/redis-MIT.txt`; protobuf's BSD-3-Clause notice is retained in its
`dist/esm/wire/varint.js` and CommonJS counterpart. data-uri-to-buffer's MIT
text is in its README.md. `licenses/esbuild-MIT.txt` is retained as provenance
for the original upstream build tool, which is not part of this runtime.

Release source access: the corresponding-source ZIP is offered with this
version's releases at https://github.com/ghjkllasdf-fake/Baomihua-danmu-companion/releases/tag/v0.1.1-beta.3.
The included software comes without warranty, as stated in the applicable
licenses; recipients may convey and modify covered works under those licenses.
