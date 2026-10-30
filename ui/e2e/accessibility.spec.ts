import { test, expect, type Locator, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

async function tabTo(page: Page, target: Locator) {
  for (let count = 0; count < 80; count++) {
    if (await target.evaluate(element => element === document.activeElement)) {
      await expect(target).toHaveCSS('outline-style', 'solid');
      expect(await target.evaluate(element => parseFloat(getComputedStyle(element).outlineWidth))).toBeGreaterThanOrEqual(2);
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error('The control is not reachable by keyboard.');
}

test('display controls and disclosures are keyboard-operable with visible focus', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (['error', 'warning'].includes(message.type())) errors.push(message.text()); });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '爆米花弹幕面板' })).toHaveCount(1);
  const master = page.getByRole('switch', { name: '显示弹幕', exact: true });
  await tabTo(page, master);
  await page.keyboard.press('Space');
  await expect(master).not.toBeChecked();
  await expect(page.locator('.preview')).toHaveCount(0);
  await page.keyboard.press('Space');
  await expect(master).toBeChecked();

  const region = page.getByRole('button', { name: '1/4', exact: true });
  await tabTo(page, region);
  await page.keyboard.press('Enter');
  await expect(region).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: '1/2', exact: true })).toHaveAttribute('aria-pressed', 'false');
  for (const [label, value, spokenValue] of [['弹幕字号', '23', '23 px'], ['滚动速度', '0.9', '0.9×'], ['不透明度', '0.9', '90%'], ['文字描边', '0.5', '0.5 px']]) {
    const slider = page.getByRole('slider', { name: label, exact: true });
    await tabTo(page, slider);
    await page.keyboard.press('ArrowRight');
    await expect(slider).toHaveValue(value);
    await expect(slider).toHaveAttribute('aria-valuetext', spokenValue);
  }
  const scale = page.getByRole('switch', { name: /随视频窗口等比缩放/ });
  await tabTo(page, scale);
  await page.keyboard.press('Space');
  await expect(scale).toBeChecked();
  const density = page.getByRole('combobox', { name: '弹幕密度' });
  await tabTo(page, density);
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');
  await expect(density).toHaveValue('100');
  await tabTo(page, page.getByRole('button', { name: '更多设置', exact: true }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('button', { name: '返回设置', exact: true })).toBeVisible();
  const bottom = page.getByRole('checkbox', { name: '底部', exact: true });
  await tabTo(page, bottom);
  await page.keyboard.press('Space');
  await expect(bottom).not.toBeChecked();

  await tabTo(page, page.locator('.advanced > summary'));
  await page.keyboard.press('Enter');
  await expect(page.locator('.advanced')).toHaveAttribute('open', '');
  const keywords = page.getByRole('textbox', { name: '屏蔽关键词' });
  await tabTo(page, keywords);
  await page.keyboard.insertText('测试关键词');
  await expect(keywords).toHaveValue('测试关键词');
  const offset = page.getByRole('slider', { name: /弹幕时间偏移/ });
  await tabTo(page, offset);
  await page.keyboard.press('ArrowRight');
  await expect(offset).toHaveValue('250');
  await expect(offset).toHaveAttribute('aria-valuetext', '+0.25 s');
  await tabTo(page, page.locator('.manual-sync > summary'));
  await page.keyboard.press('Enter');
  const manual = page.getByRole('textbox', { name: '视频当前时间' });
  await tabTo(page, manual);
  await expect(manual).toBeEditable();
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations.filter(value => ['critical', 'serious'].includes(value.impact ?? '')).map(value => value.id)).toEqual([]);
  expect(errors).toEqual([]);
});
