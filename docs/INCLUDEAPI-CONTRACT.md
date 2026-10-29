# includeAPI implementation contract

Approved in conversation on 2026-10-29: local EXE installer, app-owned loopback
API, automatic connection, source-only controls, at most three randomly sampled
candidates per matching round, no complicated retry/scheduling system. This is
an additional local candidate, not permission to replace or publish old releases.

## Runtime boundary

- An includeAPI payload contains `api/runtime.json`, `api/node.exe`,
  `api/entry.mjs`, pinned upstream source at `api/upstream/danmu_api/`, and
  production dependencies at `api/node_modules/`. No global Node, npm, PS,
  Docker, Windows service, firewall changes or privileged install.
- Upstream: huangxd-/danmu_api commit
  `4d325e096bbbf0c1ff261a9bfa638f7c58784f60`. Preserve upstream business defaults,
  including its limiter. Runtime adapter does NOT import upstream server.js:
  only the worker API. No upstream proxy/admin/UI/listen-all interfaces.
- Parent launches its packaged Node hidden, assigns its own kill-on-close Job,
  THEN supplies one bounded JSON line on stdin:
  `{protocol:1,token:<64 lowercase hex>,sources:<source ids>}`.
- Child reads configuration before importing upstream. Bind ONLY 127.0.0.1,
  port 0. stdout is reserved for one readiness line:
  `{protocol:1,port:<integer>,owner:<same token>}`. Never log the token elsewhere.
- Parent verifies authenticated GET `/<token>/health`: response
  `{success:true,owner:<same token>,upstreamCommit:<pinned commit>}`.
  Main API base is `http://127.0.0.1:<port>/<token>`; it is ephemeral, not saved
  over the user's existing external API credential.
- Allow only authenticated GET health, `/api/v2/search/anime?keyword=...`,
  `/api/v2/bangumi/<numeric id>`, `/api/v2/comment/<numeric id>?format=json&duration=true`.
  Reject Origin-bearing browser requests, unexpected Host, methods, routes,
  proxy/admin/debug/local-upload endpoints and unbounded input/output. Never
  forward X-Forwarded-For from a caller; worker client IP is always loopback.
- Working directory is app-owned profile/embedded-api, not program files.
  Inherited Node options/proxies/cookies/API settings are not accepted. No
  startup prefetch or network access before an actual search/comment request.
- stdin EOF shuts the child down. Parent disposal kills ONLY its own process
  tree if graceful exit times out. Source changes restart that owned child.

## Sources and UI boundary

Supported source ids: `douban,360,tencent,youku,iqiyi,imgo,bilibili,migu,renren,hanjutv,sohu,leshi,xigua,maiduidui,aiyifan,hongguo,bahamut,animeko`.
Default: `douban,360,renren,hanjutv`. Require 1-18 unique known ids; order matters.
Sources requiring extra accounts/URLs/local upload (tmdb,vod,custom,dandan,local)
are not offered by this minimal build. Anonymous source availability is not
guaranteed. This does not ship an offline danmu database.

Native capability: `embedded-api-v1`. Hello retains existing compatible fields
and adds optional `localApi` with the following shape:
`{enabled:true,state:'starting'|'ready'|'failed',message:string,sources:string[],availableSources:{id:string,label:string}[]}`.
State broadcasts may also include `localApi`. No port/token/path in this view.

New RPCs, gated on that capability:
- `get-local-api`, payload `{}` -> the localApi view.
- `set-local-sources`, payload `{sources:string[]}` -> updated localApi view.
  Explicitly saving the current sources also retries a failed child startup.

External-API mode keeps its current UI and saved credentials. Embedded mode
replaces API address entry with status and a compact accessible source picker
(enable, move up/down, restore defaults, apply). Do not expose general env/admin.
Source saves use native profile JSON; old API credentials are not modified.

## Sampling and delivery

Only embedded mode enables sampling. Each round tries at most three candidate
sources including a previously bound source attempt; sample remaining candidates
without replacement. Select the most comments among those tried, NOT globally.
Reuse the same-work binding as before. 429 is an actionable failure, not a reason
to try every other source or start a retry queue. External mode retains baseline.

Candidate version: 0.1.0-beta.3, variant includeAPI, separate installer AppId,
default directory and Start-menu name. Existing beta.2 bytes/tags stay unchanged.
Retain source notices, AGPL text and reproducible upstream/dependency provenance;
do not relabel upstream as MIT (upstream package metadata says ISC while its
root LICENSE is AGPL-3.0). Final size and actual Windows runtime QA are measured.
