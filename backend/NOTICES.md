# includeAPI source and third-party notices

The embedded service uses huangxd-/danmu_api at commit
4d325e096bbbf0c1ff261a9bfa638f7c58784f60. Its complete archived source,
original README and root LICENSE accompany this distribution in `upstream/`.
The root LICENSE is AGPL-3.0; the original package.json instead says ISC. This
inconsistency is retained and is not a relabeling or a legal determination.
The adapter source is `entry.mjs`; the production dependency manifest and lock
are `package.json` and `package-lock.json`. `runtime.json` and the project's
backend/toolchain.json record versions, URLs and integrity hashes. Source and
build instructions are available with this project; the payload is not a
substitute for legal review of redistribution obligations.

Node.js 22.23.4 is distributed under the terms in `licenses/Node-LICENSE.txt`.
Its official source is available at https://nodejs.org/dist/v22.23.4/.
All npm production package contents and their own notices are in
`node_modules/`. The package lock preserves exact npm tarball integrity and
version data. The optional npm executable wrappers in `.bin` are not shipped.
For packages omitting their license files from their npm distributions, the
following additional unmodified texts accompany the package: Apache-2.0.txt
(@bufbuild/protobuf, drizzle-orm, and Google-derived brotli source files),
esbuild-MIT.txt, nodable-MIT.txt, redis-MIT.txt. The protobuf BSD-3-Clause
notice is retained in @bufbuild/protobuf/dist/esm/wire/varint.js and its
CommonJS counterpart. data-uri-to-buffer's MIT text is in its README.md.
brotli's package metadata and README identify MIT and author Devon Govett,
but the published package and its source commit do not contain a separate MIT
license text or copyright notice; this ambiguity requires review before release.
