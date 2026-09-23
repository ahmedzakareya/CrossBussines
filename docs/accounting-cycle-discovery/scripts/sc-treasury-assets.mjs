// ACC-BNK-01, ACC-FA-01/02, ACC-PAY-01, ACC-FX-01 — treasury, fixed assets, payroll and FX.
//
// These are independent of the trade cycles and are executed after them so the ledger already
// carries the trade activity. Period close and year-end are deliberately NOT here: they are
// destructive to later tests and run on a separate checkpoint.
import { BASE, signIn, shot, saveShots, note } from './lib-ready.mjs'
import { formPost, stepRec } from './lib-exec.mjs'

const LANG = process.env.CB_LANG || 'en'
const DATE = '2026-09-21'
const CASH = 4     // 110101 Main Cash
const BANK = 5     // 110102 Bank - Current

export default async function run(page) {
  await page.setViewportSize({ width: 1600, height: 1000 })
  const log = { language: LANG, date: DATE, steps: [] }
  const S = (...a) => log.steps.push(stepRec(...a))

  await signIn(page)
  if (LANG !== 'en') {
    await page.goto(`${BASE}/Account/SetLanguage?culture=${LANG}&returnUrl=%2FAccounting%2FIndex`,
      { waitUntil: 'domcontentloaded' }).catch(() => {})
    await page.waitForTimeout(800)
  }

  // ------------------------------------------------ ACC-BNK-01 cash/bank transfer
  let r = await formPost(page, {
    tokenFrom: '/Accounting/Transfer', action: '/Accounting/DoTransfer',
    fields: { fromGlAccountId: BANK, toGlAccountId: CASH, amount: 2500, date: DATE,
              notes: 'ZZ-DISCOVERY BNK-01 bank to cash transfer' },
  })
  S(1, 'ACC-BNK-01 transfer 2,500.00 from Bank - Current to Main Cash',
    { from: '110102', to: '110101', amount: 2500 }, r)
  await shot(page, 'ACC-BNK-01-01_transfer', {
    lang: LANG, scenario: 'ACC-BNK-01', step: '1 cash/bank transfer posted', route: r.landed,
    beforeAfter: 'after transfer',
  })

  // ------------------------------------------------ ACC-FA-01 acquire and capitalise an asset
  r = await formPost(page, {
    tokenFrom: '/Accounting/FixedAssets', action: '/Accounting/CreateFixedAsset',
    fields: { name: 'ZZ-DISCOVERY أصل الاكتشاف', nameEn: 'ZZ-DISCOVERY Test Machine',
              acquisitionDate: DATE, cost: 24000, salvageValue: 0, usefulLifeMonths: 48,
              fundingAccountId: BANK, notes: 'ZZ-DISCOVERY FA-01 asset acquisition' },
  })
  S(2, 'ACC-FA-01 acquire an asset: cost 24,000.00, life 48 months, funded from the bank',
    { cost: 24000, usefulLifeMonths: 48, expectedMonthlyDepreciation: 500 }, r)
  await shot(page, 'ACC-FA-01-01_asset-created', {
    lang: LANG, scenario: 'ACC-FA-01', step: '1 asset acquired and capitalised', route: r.landed,
    beforeAfter: 'after capitalisation', expectText: 'ZZ-DISCOVERY',
  })

  // ------------------------------------------------ ACC-FA-02 depreciation run
  r = await formPost(page, {
    tokenFrom: '/Accounting/DepreciationRuns', action: '/Accounting/RunDepreciation',
    fields: { periodDate: DATE },
  })
  S(3, 'ACC-FA-02 run depreciation for the period containing ' + DATE, { periodDate: DATE }, r)
  await shot(page, 'ACC-FA-02-01_depreciation-run', {
    lang: LANG, scenario: 'ACC-FA-02', step: '2 depreciation posted', route: r.landed,
    beforeAfter: 'after the depreciation run',
  })

  // ------------------------------------------------ ACC-PAY-01 payroll posting
  r = await formPost(page, {
    tokenFrom: '/Accounting/Payroll', action: '/Accounting/PostPayroll',
    fields: { year: 2026, month: 9 },
  })
  S(4, 'ACC-PAY-01 post the September 2026 payroll', { year: 2026, month: 9 }, r)
  await shot(page, 'ACC-PAY-01-01_payroll-posted', {
    lang: LANG, scenario: 'ACC-PAY-01', step: '1 payroll posting attempted', route: r.landed,
    beforeAfter: 'after posting',
  })

  // ------------------------------------------------ ACC-FX-01 unrealised revaluation
  r = await formPost(page, {
    tokenFrom: '/Currency/Revaluation', action: '/Currency/PostRevaluation',
    fields: { asOf: DATE, rateType: 'Central' },
  })
  S(5, 'ACC-FX-01 post an unrealised foreign-currency revaluation as at ' + DATE,
    { asOf: DATE, rateType: 'Central' }, r)
  await shot(page, 'ACC-FX-01-01_revaluation', {
    lang: LANG, scenario: 'ACC-FX-01', step: '1 FX revaluation posted', route: r.landed,
    beforeAfter: 'after revaluation',
  })

  note(`sc-treasury-assets-${LANG}`, log)
  const s = saveShots(`treasury-assets-${LANG}`)
  return {
    lang: LANG,
    results: log.steps.map(x => `${x.n}. landed=${x.landed} banner=${(x.banner || '').slice(0, 80)}`),
    shots: s,
  }
}
