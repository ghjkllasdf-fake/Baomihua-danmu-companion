import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const api = resolve(import.meta.dirname, '../../.deps/includeapi/payload/api');
const script = `
import assert from 'node:assert/strict';
import {brotliCompressSync, constants} from 'node:zlib';
const {decompressBrotli} = await import(process.argv[1]);
let count = 0;
for (const raw of [Buffer.alloc(0), Buffer.from('abc'), Buffer.from('<i><d>\\u5f39\\u5e55</d></i>'), Buffer.from(Array.from({length:256},(_,i)=>i)), Buffer.alloc(65536,65)]) {
  for (const quality of [0,4,11]) {
    const compressed=brotliCompressSync(raw,{params:{[constants.BROTLI_PARAM_QUALITY]:quality}});
    const padded=Buffer.concat([Buffer.from([1,2]),compressed,Buffer.from([3])]);
    for (const input of [new Uint8Array(compressed),new Uint8Array(padded.buffer,padded.byteOffset+2,compressed.length)]) {
      const output=decompressBrotli(input); assert.equal(output.constructor.name,'Uint8Array');
      assert.deepEqual(Buffer.from(output),raw); count++;
    }
  }
}
assert.throws(()=>decompressBrotli(null),TypeError);
assert.throws(()=>decompressBrotli(new Uint8Array([1,2,3,4])));
console.log('PASS '+count+' Node22 Brotli vectors and invalid inputs');
`;
const result = spawnSync(join(api, 'node.exe'), ['--input-type=module', '-e', script,
  pathToFileURL(join(api, 'upstream/danmu_api/utils/codec-util.js')).href], { encoding: 'utf8', windowsHide: true });
assert.equal(result.status, 0, result.stderr || result.error?.message);
process.stdout.write(result.stdout);
