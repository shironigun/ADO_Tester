// Playwright global-setup: logs in to the app under test and saves storageState for reuse by
// UI specs (execution) and the Playwright agents (script generation). Skips re-login when a
// fresh storageState.json already exists (< 30 minutes). Exits gracefully if env vars are not set.
import { chromium } from '@playwright/test'
import fs from 'fs'
import path from 'path'

const appUrl      = process.env.AUTOMATION_APP_URL      ?? ''
const appUsername = process.env.AUTOMATION_APP_USERNAME ?? ''
const appPassword = process.env.AUTOMATION_APP_PASSWORD ?? ''
const MAX_AGE_MS  = 30 * 60 * 1000

export default async function globalSetup(): Promise<void> {
  if (!appUrl || !appUsername || !appPassword) {
    console.warn(
      '[auth.setup] AUTOMATION_APP_URL / _USERNAME / _PASSWORD not set — skipping login.',
    )
    return
  }

  const storageStatePath = path.join(__dirname, 'storageState.json')
  try {
    const st = fs.statSync(storageStatePath)
    if (Date.now() - st.mtimeMs < MAX_AGE_MS) {
      console.log(`[auth.setup] reusing fresh storageState (${storageStatePath})`)
      return
    }
  } catch {
    // missing / unreadable — mint a new one
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

  await page.context().storageState({ path: storageStatePath })
  console.log(`[auth.setup] storageState saved to ${storageStatePath}`)

  await browser.close()
}
