import { chromium, expect } from '@playwright/test';

const stage=process.argv[2];
const base=process.env.DANMU_TEST_BASE;
if(!/^http:\/\/127\.0\.0\.1:\d+\/test-token$/.test(base??'')) throw new Error('A loopback fixture is required.');
let browser;
for(let attempt=0;attempt<40;attempt++) {
  try { browser=await chromium.connectOverCDP('http://127.0.0.1:9231'); break; }
  catch { await new Promise(resolve=>setTimeout(resolve,250)); }
}
if(!browser) throw new Error('The boundary test desktop did not start.');
try {
  let page;
  for(let attempt=0;attempt<30;attempt++) {
    page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith('https://danmu.local/'));
    if(page) break;
    await new Promise(resolve=>setTimeout(resolve,100));
  }
  if(!page) throw new Error('The local app document is unavailable.');
  const errors=[]; page.on('pageerror',error=>errors.push(error.message));
  let sequence=0;
  const send=(type,payload={},id=`boundary-${stage}-${++sequence}`)=>page.evaluate(({type,payload,id})=>new Promise((resolve,reject)=>{
    const bridge=window.chrome.webview;
    const timeout=setTimeout(()=>{bridge.removeEventListener('message',receive);reject(new Error('Boundary reply timed out.'));},8000);
    function receive(event) {
      if(event.data?.type!=='response' || event.data.id!==id) return;
      clearTimeout(timeout); bridge.removeEventListener('message',receive); resolve(event.data);
    }
    bridge.addEventListener('message',receive); bridge.postMessage({v:1,id,type,payload});
  }),{type,payload,id});
  if(stage==='close') {
    await page.evaluate(()=>window.chrome.webview.postMessage({v:1,id:'boundary-close',type:'close',payload:{}}));
  } else {
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    const initial=(await send('hello')).data.settings;
    if(stage==='prepare') {
      await page.evaluate(()=>new Promise((resolve,reject)=>{
        const bridge=window.chrome.webview;
        const timeout=setTimeout(()=>{bridge.removeEventListener('message',receive);reject(new Error('Player probe did not publish an initial snapshot.'));},8000);
        function receive(event) {
          if(event.data?.type==='state' && event.data.data.player.sampleMonotonicMs>0) {clearTimeout(timeout);bridge.removeEventListener('message',receive);resolve();}
        }
        bridge.addEventListener('message',receive);
      }));
      expect((await send('update-settings',{settings:initial})).ok).toBe(true);
      console.log('PASS: isolated desktop/React ready; test preferences exist.');
    } else if(stage==='locked') {
      const failed=await send('update-settings',{settings:{...initial,fontSize:initial.fontSize===40?28:40}});
      expect(failed.ok).toBe(false); expect(failed.error).toContain('本次设置未应用');
      expect((await send('hello')).data.settings).toEqual(initial);
      const originalLabel=new Map([[.25,'1/4'],[.5,'1/2'],[.75,'3/4'],[1,'全屏']]).get(initial.areaFraction);
      const target=initial.areaFraction===.25?'1/2':'1/4';
      await page.getByRole('button',{name:target,exact:true}).click();
      await expect(page.getByRole('alert')).toContainText('本次设置未应用');
      await expect(page.getByRole('button',{name:originalLabel,exact:true})).toHaveAttribute('aria-pressed','true');
      console.log('PASS: actual locked-file save failure leaves native state unchanged and rolls React controls back.');
    } else if(stage==='verify') {
      const good=send('search',{apiBase:base,value:'slow-valid'});
      const bad=await send('comments',{apiBase:base+'/other',value:{invalid:true},expectedMediaRevision:0});
      expect(bad.ok).toBe(false);
      const missingContext=await send('comments',{apiBase:base+'/other',value:'old-client-episode'});
      expect(missingContext.ok).toBe(false); expect(missingContext.code).toBe('media-context-required');
      const futureContext=await send('comments',{apiBase:base+'/other',value:'future-episode',expectedMediaRevision:Number.MAX_SAFE_INTEGER});
      expect(futureContext.ok).toBe(false); expect(futureContext.code).toBe('media-changed');
      expect((await good).ok).toBe(true);
      expect((await send('hello')).data.apiBase===base).toBe(true);
      const cancelled=send('search',{apiBase:base,value:'slow-cancel'});
      expect((await send('cancel')).ok).toBe(true);
      expect((await cancelled).ok).toBe(false);
      const old=send('search',{apiBase:base,value:'slow-old'});
      const latest=send('search',{apiBase:base,value:'latest'});
      expect((await old).ok).toBe(false); expect((await latest).ok).toBe(true);
      const failure=await send('search',{apiBase:base,value:'api-failure'});
      expect(failure.ok).toBe(false); expect(failure.error.includes(base)).toBe(false);
      for(const type of ['unknown-command','manual-clock','update-settings']) {
        const payload=type==='manual-clock'?{positionMs:-1,playing:true}:type==='update-settings'?{settings:{...initial,fontSize:999}}:{};
        expect((await send(type,payload)).ok).toBe(false);
      }
      await page.evaluate(()=>{for(const message of [null,[],{v:2,id:'bad-version',type:'hello',payload:{}}])window.chrome.webview.postMessage(message);});
      expect((await send('hello')).ok).toBe(true);
      expect((await send('update-settings',{settings:initial})).ok).toBe(true);
      console.log('PASS: real bridge payload validation, cancel, stale success, errors, malformed roots and settings recovery.');
      await page.addInitScript(()=>{
        window.__boundaryReplies=[];
        window.chrome.webview.addEventListener('message',event=>{if(event.data?.type==='response')window.__boundaryReplies.push(event.data);});
      });
      await page.evaluate(({base})=>window.chrome.webview.postMessage({v:1,id:'reload-collision',type:'search',payload:{apiBase:base,value:'slow-reload'}}),{base});
      await page.reload();
      await expect(page.getByText('桌面宿主已连接')).toBeVisible();
      expect((await send('search',{apiBase:base,value:'current-document'},'reload-collision')).ok).toBe(true);
      const collision=await page.evaluate(()=>window.__boundaryReplies.filter(reply=>reply.id==='reload-collision'));
      expect(collision).toHaveLength(1); expect(collision[0].ok).toBe(true);
      console.log('PASS: actual same-origin WebView2 reload cannot deliver the old error into the new request.');
      const currentUrl=page.url();
      await page.getByRole('link',{name:'爆米花弹幕工作台'}).click();
      expect((await send('hello')).ok).toBe(true);
      try { await page.goto('https://outside.invalid/'); } catch { /* The native navigation gate rejects it. */ }
      expect(page.url().startsWith('https://danmu.local/')).toBe(true);
      expect((await send('hello')).ok).toBe(true);
      const count=browser.contexts().flatMap(c=>c.pages()).length;
      await page.evaluate(()=>window.open('https://outside.invalid/'));
      expect(browser.contexts().flatMap(c=>c.pages()).length).toBe(count);
      expect(currentUrl.startsWith('https://danmu.local/')).toBe(true);
      expect(errors).toEqual([]);
      await page.screenshot({path:'../artifacts/desktop-boundary.png',fullPage:true});
      console.log('PASS: fragment navigation remains usable; external navigation/popups denied; page errors zero.');
    } else throw new Error('Unknown boundary stage.');
  }
} finally { await browser.close(); }
