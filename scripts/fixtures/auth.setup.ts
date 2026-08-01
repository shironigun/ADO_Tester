// Playwright global-setup: logs in to the app under test and saves storageState for reuse by
// UI specs (execution) and the Playwright agents (script generation). Run once before a UI phase;
// re-run when auth expires. Exits gracefully if env vars are not set (non-UI runs stay unaffected).
import { chromium } from '@playwright/test'
import path from 'path'

const appUrl      = process.env.AUTOMATION_APP_URL      ?? ''
const appUsername = process.env.AUTOMATION_APP_USERNAME ?? ''
const appPassword = process.env.AUTOMATION_APP_PASSWORD ?? ''

export default async function globalSetup(): Promise<void> {
  if (!appUrl || !appUsername || !appPassword) {
    console.warn(
      '[auth.setup] AUTOMATION_APP_URL / _USERNAME / _PASSWORD not set — skipping login.',
    )
    return
  }

  const browser = await chromium.launch()
  const page    = await browser.newPage()

  await page.goto(appUrl)
  await page.getByLabel(/username|email/i).fill(appUsername)
  await page.getByLabel(/password/i).fill(appPassword)
  await page.getByRole('button', { name: /sign in|log in/i }).click()
  await page.waitForURL(u => !u.pathname.includes('/login'), { timeout: 15_000 })

  // Post-login modal: "Would you like to set up email login?" — dismiss or the
  // session stays stuck on the dashboard under the overlay.
  const notNow = page.getByRole('button', { name: /not now/i })
  try {
    await notNow.waitFor({ state: 'visible', timeout: 8_000 })
    await notNow.click()
  } catch {
    // Modal not shown for this account / already dismissed.
  }

  const storageStatePath = path.join(__dirname, 'storageState.json')
  await page.context().storageState({ path: storageStatePath })
  console.log(`[auth.setup] storageState saved to ${storageStatePath}`)

  await browser.close()
}
