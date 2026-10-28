import { chromium, expect } from '@playwright/test';
let browser;
for (let i=0;i<30;i++) {
  try { browser=await chromium.connectOverCDP('http://127.0.0.1:9229'); break; }
  catch { await new Promise(resolve=>setTimeout(resolve,500)); }
}
if (!browser) throw new Error('Native WebView2 debug endpoint unavailable.');
let page;
try {
  for (let i=0;i<30;i++) {
    page=browser.contexts().flatMap(context=>context.pages()).find(page=>page.url().startsWith('https://danmu.local/'));
    if (page) break;
    await new Promise(resolve=>setTimeout(resolve,500));
  }
  if (!page) throw new Error('Local application page not found.');
  const errors=[]; page.on('pageerror',error=>errors.push(error.message));
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  await page.getByLabel('正在看什么？').fill('三体');
  await page.getByRole('button',{name:'搜索弹幕来源'}).click();
  await page.getByRole('button',{name:/^三体\(2023\).*tencent/}).click({timeout:100000});
  await page.getByRole('button',{name:'载入分集'}).click();
  await expect(page.getByLabel('当前分集')).toBeVisible({timeout:100000});
  await page.getByRole('button',{name:'加载这一集的弹幕'}).click();
  await page.waitForFunction(()=>Boolean(document.querySelector('.source-notice.error')) || /\d[\d,]* 条弹幕已准备/.test(document.querySelector('.app-footer')?.textContent??''),{},{timeout:35000});
  if (await page.locator('.source-notice.error').count()) throw new Error(await page.locator('.source-notice.error').innerText());
  await expect(page.locator('.app-footer')).toContainText(/\d[\d,]* 条弹幕已准备/,{timeout:185000});
  await page.getByRole('button',{name:'3/4',exact:true}).click();
  await expect(page.getByText('75% 显示区域')).toBeVisible();
  await page.screenshot({path:'../artifacts/desktop-live.png',fullPage:true});
  expect(errors).toEqual([]);
  console.log('PASS: real PS7/WebView2/React -> configured API search -> episodes -> comments -> native settings');
  console.log(await page.locator('.app-footer').innerText());
} finally {
  if(page && !page.isClosed()) {
    try { await page.evaluate(()=>window.chrome.webview.postMessage({v:1,id:'test-close',type:'close',payload:{}})); }
    catch { /* The app may already have closed after a startup failure. */ }
  }
  await browser.close();
}
