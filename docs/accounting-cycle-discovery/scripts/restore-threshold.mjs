import { BASE, signIn } from './lib-ready.mjs'
import { formPost } from './lib-exec.mjs'
export default async function run(page) {
  await signIn(page)
  const r = await formPost(page, {
    tokenFrom: '/Accounting/AccountingRoles', action: '/Accounting/SaveAccSettings',
    fields: { approvalThreshold: 0 },
  })
  return { restored: r.landed }
}
