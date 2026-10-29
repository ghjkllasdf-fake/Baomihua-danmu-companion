import assert from 'node:assert/strict';
import test from 'node:test';
import { prepareDanAnyManifest } from '../prepare-lib.mjs';
const expected={name:'@dan-uni/dan-any',version:'2.3.9',license:'LGPL-3.0-or-later',dependencies:{other:'^1.0.0'}};
test('a compatible modified LGPL library can be rebuilt with the same pure runtime graph',()=>{
 const custom={...expected,dependencies:{...expected.dependencies,'@electric-sql/pglite':'^0.5.4','@electric-sql/pglite-tools':'^0.4.4','drizzle-orm':'^1.0.0'}};
 assert.deepEqual(prepareDanAnyManifest(custom,expected).dependencies,expected.dependencies);
 assert.equal(custom.dependencies['@electric-sql/pglite'],'^0.5.4');
 assert.throws(()=>prepareDanAnyManifest({...custom,license:'MIT'},expected),/license/i);
 assert.throws(()=>prepareDanAnyManifest({...custom,dependencies:{...custom.dependencies,newdep:'1.0.0'}},expected),/dependencies/i);
 assert.throws(()=>prepareDanAnyManifest({...custom,version:'9.0.0'},expected),/version/i);
});
