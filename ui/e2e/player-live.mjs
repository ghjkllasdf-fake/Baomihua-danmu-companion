import { chromium, expect } from '@playwright/test';

const stage=process.argv[2]??'inspect';
const browser=await chromium.connectOverCDP('http://127.0.0.1:9230');
try {
  const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith('https://danmu.local/'));
  if(!page) throw new Error('Native local page is unavailable.');
  if(stage==='load') {
    await page.reload();
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    await page.getByRole('button',{name:'使用播放器片名',exact:false}).click();
    await expect(page.locator('.result-item').first()).toBeVisible({timeout:90000});
    console.log('Actual source candidates:',await page.locator('.result-item').allTextContents());
    await page.locator('.result-item').first().click();
    await page.getByRole('button',{name:'载入分集'}).click();
    await expect(page.getByLabel('当前分集')).toBeVisible({timeout:90000});
    console.log('Actual selected episode:',await page.locator('#episode option:checked').innerText());
    await page.getByRole('button',{name:'加载这一集的弹幕'}).click();
    await expect(page.locator('.app-footer')).toContainText(/\d[\d,]* 条弹幕已准备/,{timeout:185000});
    await page.screenshot({path:'../artifacts/player-console.png',fullPage:true});
  }
  console.log(await page.locator('.playback-strip').innerText());
  console.log(await page.locator('.app-footer').innerText());
  console.log('Verified player snapshot:',await page.evaluate(()=>new Promise(resolve=>{
    const receive=e=>{if(e.data?.type==='state'){window.chrome.webview.removeEventListener('message',receive);resolve(e.data.data.player);}};
    window.chrome.webview.addEventListener('message',receive);
  })));
  if(stage==='close') await page.evaluate(()=>window.chrome.webview.postMessage({v:1,id:'live-close',type:'close',payload:{}}));
} finally { await browser.close(); }
