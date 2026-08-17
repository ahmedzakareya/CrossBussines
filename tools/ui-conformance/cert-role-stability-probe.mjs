// Is workerRole STABLE under certification-mode suppression, or is it still moving?
import { chromium } from 'playwright';
const BASE='http://localhost:5268';
const b = await chromium.launch();
const p = await (await b.newContext()).newPage();
await p.goto(`${BASE}/Account/Login`, {waitUntil:'domcontentloaded'});
await p.fill('input[name="UserName"]','Admin'); await p.fill('input[type="password"]','Admin@123');
await Promise.all([p.waitForNavigation({waitUntil:'domcontentloaded'}).catch(()=>{}), p.click('button[type="submit"], input[type="submit"]')]);
const seen=[];
for (let i=0;i<10;i++){
  const r = await p.evaluate(async (base) => {
    try { const res = await fetch(`${base}/BusinessEventMonitor/Runtime`, {credentials:'same-origin'});
          if(!res.ok) return 'HTTP'+res.status;
          const j = await res.json(); return j.workerRole ?? 'MISSING'; }
    catch(e){ return 'ERR'; }
  }, BASE);
  seen.push(r);
  await p.waitForTimeout(3000);
}
console.log('workerRole probes over 30s:', JSON.stringify(seen));
console.log('distinct values:', JSON.stringify([...new Set(seen)]));
// and the page height, which is what the warm-up was really protecting
const h=[];
for (let i=0;i<3;i++){
  await p.goto(`${BASE}/BusinessEventMonitor`, {waitUntil:'networkidle'});
  h.push(await p.evaluate(()=>document.documentElement.scrollHeight));
}
console.log('BusinessEventMonitor heights across 3 loads:', JSON.stringify(h));
await b.close();
