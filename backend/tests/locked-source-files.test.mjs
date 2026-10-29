import assert from 'node:assert/strict';
import test from 'node:test';
import { lockedSourceFiles } from '../prepare-lib.mjs';
const sha='a'.repeat(64), integrity='sha512-'+Buffer.alloc(64).toString('base64');
const entry={name:'lib',version:'1.0.0',sourceCommit:'b'.repeat(40),sourceFile:'source-lib.zip',sourceUrl:'https://codeload.github.com/a/b/zip/c',sourceSha256:sha,tarballFile:'npm-lib.tgz',tarballUrl:'https://registry.npmjs.org/lib/-/lib.tgz',tarballSha256:sha,integrity};
const lock={packages:{'':{},'node_modules/lib':{version:'1.0.0',integrity}}};
test('corresponding source inputs cover the exact lock with safe immutable names and hashes', () => {
 assert.equal(lockedSourceFiles(lock,{schemaVersion:1,packages:[entry]}).length,2);
 assert.throws(()=>lockedSourceFiles(lock,{schemaVersion:1,packages:[]}),/coverage/i);
 assert.throws(()=>lockedSourceFiles(lock,{schemaVersion:1,packages:[{...entry,sourceFile:'../escape.zip'}]}),/archive/i);
 assert.throws(()=>lockedSourceFiles(lock,{schemaVersion:1,packages:[{...entry,sourceSha256:'bad'}]}),/archive/i);
 assert.throws(()=>lockedSourceFiles(lock,{schemaVersion:1,packages:[{...entry,version:'other'}]}),/version/i);
});
