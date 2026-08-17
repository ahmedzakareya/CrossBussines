// Measures the semantic BUTTON families at the visual-authority level.
//
// Two things are measured, because they answer different questions:
//   (a) the REAL buttons rendered on /Tasks/MatchSuggestions — what the user actually sees,
//       with the CSS rule and token that produced the colour;
//   (b) every semantic family, probed by injecting one button of each class, reading the
//       Bootstrap button custom properties (--bs-btn-color/-bg, hover, active, disabled).
//       That covers states the page does not currently render, without guessing.
import { chromium } from 'playwright';

const BASE = process.env.UI_BASE_URL ?? 'http://localhost:5268';
const USER = process.env.UI_USER ?? 'Admin';
const PASS = process.env.UI_PASS ?? 'Admin@123';

const FAMILIES = ['btn-primary', 'btn-light-primary', 'btn-success', 'btn-light-success',
                  'btn-warning', 'btn-light-warning', 'btn-danger', 'btn-light-danger',
                  'btn-info', 'btn-light-info'];

const VIEWPORTS = [[1440, 900, '1440'], [992, 800, '992'], [390, 844, '390']];

const browser = await chromium.launch();
const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Account/Login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="UserName"]', USER);
await page.fill('input[type="password"]', PASS);
await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
                   page.click('button[type="submit"], input[type="submit"]')]);

const HELPERS = `
const srgb=(c)=>{c/=255;return c<=0.03928?c/12.92:Math.pow((c+0.055)/1.055,2.4);};
const parse=(s)=>(String(s).match(/[\\d.]+/g)||[]).map(Number);
const lum=([r,g,b])=>0.2126*srgb(r)+0.7152*srgb(g)+0.0722*srgb(b);
const ratio=(a,b)=>{const[x,y]=[lum(a),lum(b)].sort((m,n)=>n-m);return (x+0.05)/(y+0.05);};
const hex=(v)=>'#'+v.slice(0,3).map(n=>Math.round(n).toString(16).padStart(2,'0')).join('').toUpperCase();
const opaque=(s)=>{const p=parse(s);return (p.length>=4&&p[3]===0)?null:p;};
const bgOf=(el)=>{let n=el;while(n&&n!==document.documentElement){const c=opaque(getComputedStyle(n).backgroundColor);if(c)return c;n=n.parentElement;}return[255,255,255];};
`;

// ---------- (a) real buttons on the page ----------
for (const [w, h, label] of VIEWPORTS) {
    for (const culture of ['en', 'ar']) {
        await page.setViewportSize({ width: w, height: h });
        await page.goto(`${BASE}/Account/SetLanguage?culture=${culture}&returnUrl=%2F`, { waitUntil: 'domcontentloaded' }).catch(() => {});
        await page.goto(`${BASE}/Tasks/MatchSuggestions`, { waitUntil: 'networkidle' }).catch(() => {});
        await page.waitForTimeout(700);
        const rows = await page.evaluate(new Function(`${HELPERS}
            const out=[];
            for (const el of document.querySelectorAll('.btn')) {
                const r=el.getBoundingClientRect(); if(!r.width||!r.height) continue;
                const cs=getComputedStyle(el);
                const fg=parse(cs.color), bg=opaque(cs.backgroundColor)||bgOf(el);
                const rules=[];
                for(const sh of document.styleSheets){let rs;try{rs=sh.cssRules;}catch(e){continue;}
                  for(const rl of rs){ if(!rl.selectorText||!rl.style) continue;
                    if(!rl.style.color && !rl.style.backgroundColor && !rl.style.getPropertyValue('--bs-btn-color')) continue;
                    try{ if(el.matches(rl.selectorText)) rules.push((sh.href||'inline').split('/').pop()+' :: '+rl.selectorText.slice(0,60)); }catch(e){} } }
                out.push({ cls:[...el.classList].join('.'), text:(el.textContent||'').trim().slice(0,26),
                  fg:hex(fg), bg:hex(bg), border:cs.borderColor, font:cs.fontSize+'/'+cs.fontWeight,
                  ratio:+ratio(fg,bg).toFixed(2),
                  tokFg:cs.getPropertyValue('--bs-btn-color').trim(), tokBg:cs.getPropertyValue('--bs-btn-bg').trim(),
                  rules:rules.slice(-2) });
            }
            return out;`));
        console.log(`\n=== REAL BUTTONS  /Tasks/MatchSuggestions  ${culture} @${label} ===`);
        const seen = new Set();
        for (const b of rows) {
            const k = b.cls + b.fg + b.bg; if (seen.has(k)) continue; seen.add(k);
            const verdict = b.ratio >= 4.5 ? 'PASS' : 'FAIL';
            console.log(`   [${verdict}] ${b.ratio.toFixed(2)}  .${b.cls}  "${b.text}"`);
            console.log(`           fg=${b.fg} bg=${b.bg} border=${b.border} font=${b.font}`);
            console.log(`           tokens: --bs-btn-color=${b.tokFg} --bs-btn-bg=${b.tokBg}`);
            for (const r of b.rules) console.log(`           rule: ${r}`);
        }
    }
}

// ---------- (b) family sweep across states ----------
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${BASE}/Inventory/Index`, { waitUntil: 'networkidle' }).catch(() => {});
const sweep = await page.evaluate(new Function('FAM', `${HELPERS}
    const host=document.createElement('div');
    host.style.cssText='position:fixed;left:-9999px;top:0;background:#ffffff;';
    document.body.appendChild(host);
    const out=[];
    for(const fam of FAM){
        const b=document.createElement('button'); b.className='btn btn-sm '+fam; b.textContent='Probe';
        host.appendChild(b);
        const cs=getComputedStyle(b);
        const rd=(n,fb)=>{const v=cs.getPropertyValue(n).trim();return v||fb;};
        const pair=(f,g)=>{const F=parse(f),G=parse(g); if(!F.length||!G.length) return null;
            return {fg:hex(F),bg:hex(G),r:+ratio(F,G).toFixed(2)};};
        const rendered=pair(cs.color, opaque(cs.backgroundColor)?cs.backgroundColor:'rgb(255,255,255)');
        out.push({fam, rendered,
            normal  : pair(rd('--bs-btn-color',cs.color),          rd('--bs-btn-bg','rgb(255,255,255)')),
            hover   : pair(rd('--bs-btn-hover-color',''),          rd('--bs-btn-hover-bg','')),
            active  : pair(rd('--bs-btn-active-color',''),         rd('--bs-btn-active-bg','')),
            disabled: pair(rd('--bs-btn-disabled-color',''),       rd('--bs-btn-disabled-bg','')),
        });
        host.removeChild(b);
    }
    document.body.removeChild(host);
    return out;`), FAMILIES);

console.log('\n\n=========== SEMANTIC BUTTON FAMILY SWEEP (on #ffffff ground) ===========');
console.log('   family              rendered            normal              hover               active              disabled');
for (const s of sweep) {
    const f = (p) => p ? `${p.fg}/${p.bg} ${p.r.toFixed(2).padStart(5)}` : '        --        ';
    const flag = s.rendered && s.rendered.r < 4.5 ? ' <== FAIL' : '';
    console.log(`   ${s.fam.padEnd(19)} ${f(s.rendered)}  ${f(s.normal)}  ${f(s.hover)}  ${f(s.active)}  ${f(s.disabled)}${flag}`);
}
await browser.close();
