# CrossBuy — خطة تنفيذ منصة الـ AI: Prompts متتابعة (Phase 0 + Phase 1)

> مرجع تنفيذي مشتق من [CrossBuy_AI_Platform_Analysis.md](CrossBuy_AI_Platform_Analysis.md).
> كل بلوك أدناه = **prompt مستقل** تديه لـ Claude Code بالترتيب. لا تشغّل خطوة قبل نجاح اللي قبلها.
>
> **القاعدة:** كل خطوة تنتهي بـ **تشغيل وتحقّق فعلي** قبل الانتقال. الـ AI يقترح فقط؛ الاعتماد
> النهائي بشري أو بقاعدة .NET ثابتة.

---

## القرارات الافتراضية المعتمدة (عدّلها لو لزم)

| # | القرار | الافتراضي المعتمد |
|---|--------|-------------------|
| 1 | مزوّد الـ LLM | **Claude API** (سحابي). on-prem يُؤجَّل كخيار Phase لاحق لو تطلّبت حساسية الرواتب. |
| 2 | Vector store | **Qdrant محلي** (ليس pgvector — قاعدتنا SQL Server). يُفعّل في Phase 3 فقط. |
| 3 | نطاق Phase 1 | المساعد المحاسبي + تصنيف الفواتير + مساعد الموظف (إجازات). |
| 4 | لغة المساعد | **عربي أولًا**، مع دعم AR/EN كامل في الردود. |
| 5 | الاعتماد التلقائي | **ممنوع auto-apply في المالية**. كل ناتج = Draft يمرّ على workflow بشري. |
| 6 | توجيه الموديل | Haiku للمهام القصيرة (تصنيف/تلخيص)؛ Opus/Sonnet للـ Copilot والاستخراج. |

> الميزانية الشهرية للـ LLM لسه غير محدّدة → نبدأ بحدود استخدام محافظة per-user/day + caching،
> ونضبطها بعد قياس أول أسبوع من `AiInteractions`.

---

## بنية المجلدات الجديدة (مرجع)

```
CrossBuy/
├── CrossBuy/                         (الـ .NET الموجود)
│   ├── Controllers/Api/AiController.cs        ← جديد (proxy + صلاحيات)
│   ├── BL/AiService.cs + IAiService.cs        ← جديد (نمط BL الموجود)
│   └── ViewModel/Ai/*.cs                       ← DTOs للـ AI
└── crossbuy_ai/                      ← جديد: خدمة بايثون منفصلة
    ├── app/
    │   ├── main.py                   (FastAPI app, :8000)
    │   ├── config.py                 (مفاتيح، إعدادات الموديل)
    │   ├── security.py               (shared-secret بين .NET و Python)
    │   ├── clients/anthropic.py      (Claude client + model routing)
    │   ├── schemas/                  (Pydantic models لكل ناتج)
    │   ├── routers/                  (assistant, classify, health ...)
    │   └── tools/                    (تعريف tools اللي بتنده .NET)
    ├── requirements.txt
    ├── .env.example
    └── README.md
```

---

# PHASE 0 — الأساس (البنية التحتية فقط، بدون قدرات للمستخدم)

## Prompt 0.1 — هيكلة خدمة بايثون + health check

```
أنشئ خدمة بايثون جديدة في crossbuy_ai/ على FastAPI تعمل على بورت 8000.
المطلوب في هذه الخطوة فقط (بنية تحتية، بدون أي قدرة AI بعد):
- requirements.txt: fastapi, uvicorn[standard], anthropic, pydantic v2, python-dotenv, httpx.
- app/main.py: تطبيق FastAPI مع CORS مغلق (داخلي فقط) و endpoint:
  GET /health → {"status":"ok","service":"crossbuy-ai"}.
- app/config.py: قراءة المتغيرات من .env (ANTHROPIC_API_KEY, AI_SHARED_SECRET,
  DOTNET_BASE_URL=http://localhost:5000, DEFAULT_MODEL, FAST_MODEL).
- app/security.py: dependency تتحقق من هيدر X-AI-Secret == AI_SHARED_SECRET على كل
  المسارات ما عدا /health. ترفض 401 لو غاب أو غلط.
- .env.example بكل المفاتيح (بدون قيم حقيقية) + README.md بخطوات التشغيل على ويندوز
  (python -m venv, تفعيله، pip install, uvicorn app.main:app --port 8000).
لا تكتب أي منطق AI بعد. شغّل الخدمة وأكّد أن /health يرجّع 200 و/classify (لو موجود) يرفض 401 بدون السر.
```

## Prompt 0.2 — اتصال Claude + توجيه الموديل (model routing)

```
في crossbuy_ai/app/clients/anthropic.py أنشئ غلاف (wrapper) لعميل Anthropic:
- دالة complete(messages, system, model_tier, tools=None) ترجّع الرد + عدّاد التوكنز.
- model_tier: "fast" → FAST_MODEL، "smart" → DEFAULT_MODEL (من config).
- استخدم أحدث موديلات Claude (راجع سكِل claude-api للـ model ids والتسعير الصحيحين — لا تخمّن).
- فعّل prompt caching للـ system prompt الثابت.
- أضف معالجة أخطاء + إعادة محاولة (retry) عند rate limit.
أضف endpoint اختبار داخلي POST /diag/echo (محمي بالسر) يرسل رسالة بسيطة بالعربي للموديل
ويرجّع الرد + التوكنز، لإثبات أن الاتصال شغّال. شغّل وأكّد رد عربي سليم.
```

## Prompt 0.3 — جداول الـ AI في SQL Server (يدوي)

```
الـ migrations مكسورة (Trap #1) — نفّذ عبر sqlcmd يدويًا على CrossBuyDB2.
أنشئ ملف SQL واحد ينشئ الجداول: AiInteractions, AiSuggestions, AiKnowledgeChunks
بالظبط كما في CrossBuy_AI_Platform_Analysis.md سكشن 9 (PascalCase، أعمدة تدقيق،
أسماء أعمدة = أسماء الـ properties المستقبلية في EF).
نفّذه: sqlcmd -S localhost -d CrossBuyDB2 -E -i "<path>\ai_tables.sql"
ثم تحقّق بـ SELECT أن الجداول اتعملت. لا تنشئ entities في الـ .NET بعد.
```

## Prompt 0.4 — AiController proxy + IAiService في الـ .NET

```
في مشروع الـ .NET أضف طبقة proxy للخدمة البايثونية على نمط الـ BL services الموجود:
- BL/IAiService.cs + BL/AiService.cs: يستخدم HttpClient (named client "AiService")
  لمناداة http://localhost:8000، يحقن هيدر X-AI-Secret من appsettings.
- Controllers/Api/AiController.cs تحت /api/ai، يتطلب JWT (مثل باقي الـ API):
  POST /api/ai/diag → ينده /diag/echo في بايثون (للاختبار فقط، نشيله لاحقًا).
- سجّل الـ HttpClient و IAiService في Program.cs (DI) + إعدادات AiService:BaseUrl,
  AiService:Secret في appsettings.
- مهم: الصلاحيات والهوية تُؤخذ من الـ JWT في الـ .NET، وتُمرّر للخدمة كسياق موثوق —
  لا تثق بأي هوية قادمة من نص المستخدم.
ابنِ وشغّل (Trap #2: ابنِ إلى /c/temp/cb_run وشغّل على :5000) وأكّد أن
POST /api/ai/diag (بتوكن صالح) يرجّع رد الموديل عبر السلسلة .NET→Python.
```

> **نهاية Phase 0:** عندك خط أنابيب موثّق وآمن .NET ↔ Python ↔ Claude + جداول تتبّع. صفر قدرات
> مرئية للمستخدم بعد — وده مقصود.

---

# PHASE 1 — أول ثلاث قدرات (أعلى قيمة)

## القدرة 1 — المساعد المحاسبي (قيود بالعربي)

### Prompt 1.1 — أدوات القراءة (read-only tools) في .NET
```
أضف في AiController/AiService endpoints قراءة فقط تستخدمها الأداة لاحقًا، كلها تحترم
صلاحيات وشركة المستخدم من الـ JWT:
- GET /api/ai/accounts/search?q=  → حسابات Postable مطابقة (Id, Code, Name, NameEn).
- GET /api/ai/costcenters/search?q= → مراكز التكلفة (المرتبطة بـ Hierarchicals).
- GET /api/ai/cash-accounts → الخزائن/البنوك مع حساب الـ GL المرتبط.
أرجِع نتائج محدودة (top 10) وبالعملتين. اختبرها يدويًا بتوكن صالح.
```

### Prompt 1.2 — tool-calling في بايثون لبناء قيد Draft
```
في crossbuy_ai أضف router /assistant/journal:
- عرّف tools (Anthropic Tools API): search_accounts, search_cost_centers, get_cash_accounts
  — كل واحدة تنده الـ .NET endpoint المقابل (عبر httpx + السر + تمرير توكن المستخدم).
- schema الناتج (Pydantic): ProposedJournalEntry { date, description(ar/en),
  lines: [{accountId, debit, credit, costCenterId?, description}] }.
- النموذج يفهم جملة عربية مثل "اعمل قيد إيجار 5000 على فرع القاهرة" → ينده الأدوات →
  يبني قيدًا متوازنًا مقترحًا. استخدم model_tier="smart".
- تحقّق صارم بـ Pydantic؛ لو Σمدين ≠ Σدائن أعد المحاولة مرة واحدة ثم أرجِع خطأ واضح.
- لا تُنشئ القيد — أرجِع الاقتراح فقط. سجّل التفاعل في AiInteractions (عبر .NET).
```

### Prompt 1.3 — ربط .NET: تحويل الاقتراح إلى JournalEntry Draft
```
في AiController: POST /api/ai/assistant/journal يستقبل جملة المستخدم، ينده بايثون،
يستلم ProposedJournalEntry، ثم:
- يمرّره على نفس posting/validation rules الموجودة (أو يخزّنه كـ Draft في JournalEntries
  بحالة Draft فقط — بدون ترحيل).
- يسجّل صفًا في AiSuggestions (TargetType=JournalEntry, Status=Pending).
- يرجّع للواجهة: الاقتراح + رابط المراجعة. ممنوع أي auto-post.
أكّد بمثال عربي حقيقي أن القيد الناتج متوازن ومحفوظ Draft، ولا يظهر في الأرصدة قبل اعتماد بشري.
```

### Prompt 1.4 — واجهة المساعد (ويب + موبايل)
```
ويب (Razor + Metronic): لوحة محادثة (drawer) بزر عائم "مساعد CrossBuy"، RTL عربي،
ترسل لـ /api/ai/assistant/journal وتعرض القيد المقترح في جدول قابل للتعديل + زر "اعتمد"
يدخل مسار الترحيل العادي. استخدم inline SVG للأيقونات (Trap: ki-translate/ki-globe غير موجودين).
موبايل (Flutter): شاشة شات RTL عبر Dio، نفس الـ endpoint، وبعد الاعتماد نفّذ AppNav.dataChanged.
اختبر المسار كاملًا من الجملة العربية حتى الاعتماد.
```

## القدرة 2 — تصنيف فواتير الموردين

### Prompt 2.1
```
أضف POST /classify/invoice في بايثون: يستقبل {description, vendorName?, amount} ويرجّع
{accountId, accountName, costCenterId?, confidence, reasoning} عبر:
- جلب PostingRules + حسابات المصروفات من .NET (endpoint قراءة جديد محترم للصلاحيات).
- model_tier="fast" (Haiku) + Pydantic schema صارم.
- يتعلّم من القرارات السابقة: مرّر آخر N تصنيفات مقبولة من AiInteractions كأمثلة (few-shot).
في .NET: POST /api/ai/classify/invoice + عرض الاقتراح inline في شاشة فاتورة المشتريات
كـ "اقتراح حساب" قابل للقبول/التعديل. سجّل القرار (Accepted/Edited/Rejected) في AiInteractions.
```

## القدرة 3 — مساعد الموظف (إجازات) بالعربي

### Prompt 3.1
```
أضف router /assistant/leave في بايثون بأدوات تنده الـ API الموجود (محترمة للصلاحيات):
- get_leave_balance (يستخدم /api/leave/... و LeaveDashboardService)، get_leave_types,
  get_workdays.
- يفهم: "كام يوم إجازة فاضلي؟" → يقرأ ويجاوب بالعربي. "قدّم إجازة من الأحد 3 أيام" →
  يبني LeaveRequest draft (يحسب أيام العمل عبر الـ API، لا يحسبها الـ LLM).
في .NET: POST /api/ai/assistant/leave. الإنشاء الفعلي يمرّ على LeaveWorkflowService.CreateAsync
الموجود (نفس التحقق: التداخل، الرصيد، أيام العمل) — الـ AI يجهّز فقط، الـ workflow يقرّر.
أضف الواجهة في portal الموظف + موبايل. اختبر سؤال رصيد + تقديم طلب ينجح ويظهر للموافق الصحيح.
```

> **نهاية Phase 1:** ثلاث قدرات حيّة End-to-End، كلها تقترح وتُحترم فيها الصلاحيات والـ workflows،
> وكلها متتبَّعة في `AiInteractions`. بعدها نقيس الاستخدام/الدقة/التكلفة قبل Phase 2 (ML: الشذوذ
> والتنبؤ).

---

## تحديثات لازمة على وثائق المشروع بعد كل Phase
- **دليل المستخدم** (`docs/CrossBuy_User_Manual.html`): إضافة قسم "مساعد CrossBuy" مع كل قدرة جديدة.
- **System Context** (`docs/CrossBuy_System_Context_for_AI.md`): توثيق خدمة بايثون :8000، الـ
  AiController، جداول الـ AI، وقاعدة "الـ AI يقترح فقط".
- **القائمة الموحّدة** (`Models/Menu/MainMenu.cs`): رابط واحد للمساعد يظهر بالصلاحية المناسبة.
