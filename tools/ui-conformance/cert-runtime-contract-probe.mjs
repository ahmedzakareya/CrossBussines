// Proves the LIVE runtime reports the certification contract the gate depends on.
import { chromium } from 'playwright';
const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const b = await chromium.launch();
const p = await (await b.newContext()).newPage();
await p.goto(`${BASE}/Account/Login`, {waitUntil:'domcontentloaded'});
await p.fill('input[name="UserName"]', process.env.UI_USER ?? 'Admin');
await p.fill('input[type="password"]', process.env.UI_PASS ?? 'Admin@123');
await Promise.all([p.waitForNavigation({waitUntil:'domcontentloaded'}).catch(()=>{}), p.click('button[type="submit"], input[type="submit"]')]);
const s = await p.evaluate(async (base) => {
  const r = await fetch(`${base}/BusinessEventMonitor/Runtime`, {credentials:'same-origin'});
  return r.ok ? await r.json() : {error:'HTTP'+r.status};
}, BASE);
console.log('certificationMode           =', s.certificationMode);
console.log('backgroundWritersSuppressed =', s.backgroundWritersSuppressed);
console.log('workerRole                  =', s.workerRole);
const leaked = Object.entries(s).filter(([k,v]) => typeof v === 'string' && /Server=|Password=|Trusted_Connection|User Id=/i.test(v));
console.log('secret-shaped values in payload:', leaked.length);
console.log('payload keys:', Object.keys(s).join(', '));
await b.close();
