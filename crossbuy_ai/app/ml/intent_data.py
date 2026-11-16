"""Training data for the intent model — generated, because nobody has thousands of real questions yet.

WHY GENERATE RATHER THAN COLLECT. The assistant has existed for a day. There is no log of real
questions to learn from, and waiting for one means the matcher stays broken until enough people
have been failed by it. Generation lets the model learn the SHAPE of each question now; real
questions, once they exist, are worth more than any of this and should replace it.

WHAT MAKES GENERATED DATA WORTH ANYTHING. Not volume. A thousand copies of the same sentence teach
nothing. What teaches is VARIATION ALONG THE AXES THAT ACTUALLY VARY, and every axis below was
observed defeating the keyword matcher in one afternoon:

  * the question word          كام / كم / ايه / عايز اعرف / قوللي / none at all
  * the clitic                 و- ال- ب- ف- on the front, -ه -ها -هم on the back
  * word order                 Arabic puts the subject either side of the verb
  * the typo                   برجر داج for برجر دجاج — one keystroke
  * the punctuation            a trailing ؟ that nothing put a space before
  * the register               MSA, Egyptian colloquial, English, and English in Arabic script
  * the slot                   any item name in the catalogue, including ones with digits

THE EIGHTH CLASS IS THE IMPORTANT ONE. A seven-class classifier always picks one of seven; it
cannot say "that is not a question I answer". OUT_OF_SCOPE is trained on three kinds of negative:
small talk, questions about other parts of the business, and — the ones that matter —
BUSINESS QUESTIONS THAT LOOK LIKE OURS AND ARE NOT. "مين أحسن مورد" and "ايه الاصناف الراكدة" are
about inventory, use our vocabulary, and have no handler. A model that has never seen those will
confidently route them somewhere.
"""
from __future__ import annotations

import random

# The intent ids, which must match the .NET catalogue exactly.
STOCK = "inventory.item-stock"
VALUE = "inventory.item-value"
MOVES = "inventory.item-movements"
WAREHOUSES = "inventory.my-warehouses"
CASH = "accounting.cash-balance"
RECEIVABLES = "accounting.open-receivables"
ANOMALIES = "accounting.journal-anomalies"
SALES_QUERY = "sales.query"
DORMANT = "inventory.dormant-items"
CATEGORIES = "inventory.item-categories"
COLLECTION = "accounting.collection-days"
PURCHASES = "purchases.query"
STOCKQ = "inventory.stock-query"
POSQ = "pos.query"
HRQ = "hr.workforce"
EXPENSES = "accounting.expenses"
DOCTORS = "hospital.practitioners"
MEDS = "hospital.medications"
BILLING = "hospital.billing"
CLAIMS = "hospital.claims"
APPTS = "hospital.appointments"
DX = "hospital.diagnoses"
OUT_OF_SCOPE = "none"

# sales.top-items, sales.top-customers and sales.item-margin were retired into sales.query:
# the surface answers all three and also groups by month and measures quantity, which none of
# them could. Their vocabulary is kept below, under the one id.
INTENT_IDS = (STOCK, VALUE, MOVES, WAREHOUSES, CASH, RECEIVABLES, ANOMALIES, DORMANT,
              COLLECTION, SALES_QUERY, CATEGORIES, PURCHASES, STOCKQ, POSQ, HRQ,
              EXPENSES, DOCTORS, MEDS, BILLING, CLAIMS, APPTS, DX)

# Slot fillers. Real names from the catalogue plus shapes the catalogue contains — a name with a
# number in it, a Latin name inside an Arabic sentence, a two-word name, a code.
ITEMS = [
    "مياه معدنية", "مياه معدنية 600 مل", "برجر دجاج", "برجر لحم", "لابتوب Dell",
    "لاب توب", "عصير برتقال", "جبنة بيضاء", "زيت ذرة", "أرز مصري", "سكر",
    "شاي", "قهوة", "خبز", "دقيق", "HM-DEMO-001", "ITM-0001", "BRG-C",
    "Dell Laptop", "mineral water", "chicken burger", "orange juice",
]

# PEOPLE. The {n} slot, for questions that name a colleague.
#
# A question naming a person used to reach no surface at all: "مبيعات مياه معدنية" and "في موظف
# اسمه احمد زكريا" were both refused by the router, because every training example was the bare
# topic word and the model had never seen a topic word followed by a proper noun. The surface layer
# could answer both — it matches a named value against the data — so the capability existed and
# nothing could reach it.
#
# Deliberately NOT here: a bare "مين {n}" pattern. It would swallow "مين اللي كسر الباب امبارح",
# which must stay out of scope, and buying one phrasing at the cost of a refusal is a bad trade.
# VENDORS AND CUSTOMERS. The {v} slot. Named for the same reason {n} is: adding named-ITEM
# wordings to sales without named-VENDOR wordings to purchases moved the boundary, and "في مورد
# اسمه مورد الطاقة" went from answered to refused. A model is one thing; teaching half a pattern
# changes where the other half lands.
VENDORS = [
    "مورد الطاقة", "مورد الأدوات المكتبية", "شركة النقل", "شركة النيل", "مجموعة الدلتا",
    "الشركة المتحدة", "أسواق المستقبل", "مورد المواد الغذائية", "شركة التوريدات",
    "Power Supplier", "Stationery Supplier", "Transport Co", "Nile Co", "Delta Group",
    "United Co", "Future Markets",
]

PEOPLE = [
    "احمد زكريا", "احمد انس", "سارة علي", "خالد حسن", "ليلى كمال", "عمر فؤاد",
    "نور حسن", "سمير عادل", "هالة سعيد", "يوسف خالد", "محمد سيد", "منى فاروق",
    "ahmed zakareya", "sara ali", "khaled hassan", "omar fouad", "yousef khaled",
]

# ---------------------------------------------------------------------------------------------
# Templates. {i} is the item slot, {n} the person slot. Each list mixes MSA, Egyptian colloquial and English, because
# the product is used in all three and a model trained on one register fails on the others.
# ---------------------------------------------------------------------------------------------
TEMPLATES: dict[str, list[str]] = {
    STOCK: [
        # PUSHED OUT BY THE HOSPITAL WORDINGS. "قطعة" and "عندي" drifted once medication orders
        # arrived; every added positive moves a boundary somewhere nobody is looking.
        "عندي كام قطعة من {i}", "عندي كام من {i}", "كام قطعة من {i}", "كام قطعة عندي من {i}",
        "فاضل كام قطعة من {i}", "how many pieces of {i} do I have", "how many units of {i}",
        "كام رصيد {i}", "كم رصيد {i}", "ايه رصيد {i}", "رصيد {i}", "رصيد {i} كام",
        "{i} عندنا كام", "عندنا كام {i}", "فيه كام {i}", "فيه {i} قد ايه",
        "عايز اعرف رصيد {i}", "محتاج اعرف كمية {i}", "قوللي رصيد {i}",
        "كمية {i} في المخزن", "كم الكمية المتاحة من {i}", "المتاح من {i}",
        "المخزون من {i}", "مخزون {i}", "رصيد الصنف {i}", "ارصدة {i}",
        "هو فاضل كام {i}", "باقي كام {i}", "لسه فيه {i}", "في {i} ولا خلص",
        "stock of {i}", "how much {i} do we have", "{i} stock level",
        "what is the stock of {i}", "quantity of {i}", "show me {i} balance",
        "on hand {i}", "{i} quantity in stock", "do we have {i}",
        "how much {i} is left", "what is left of {i}", "do we still have {i}", "remaining {i}", "{i} on hand", "available {i}", "is {i} in stock", "current stock for {i}", "inventory of {i}", "check stock for {i}", "how many units of {i}", "{i} balance please",
    ],
    VALUE: [
        "كام تكلفة {i}", "كم تكلفة {i}", "تكلفة {i}", "التكلفة بتاعة {i}",
        "متوسط تكلفة {i}", "سعر تكلفة {i}", "{i} بكام", "{i} تكلفته كام",
        "قيمة مخزون {i}", "قيمة المخزون من {i}", "كام قيمة {i}", "قيمة {i}",
        "عايز اعرف تكلفة {i}", "اعرض التكلفة ل{i}", "اعرض تكلفة {i}",
        # NAMING AN ITEM IS WHAT DECIDES THIS, not the verb. "اشترينا" now also belongs to
        # purchases, and "بكام اشترينا لابتوب Dell" went there - a general purchasing answer to
        # a question about one item. The buying verbs are taught HERE with the item slot, so
        # the model learns the distinction is the named item rather than deleting the word
        # from the other side.
        "بكام اشترينا {i}", "اشترينا {i} بكام", "بكام جابنا {i}", "{i} اشتريناه بكام",
        "دفعنا كام في {i}", "شريناه بكام {i}", "كام سعر شراء {i}", "سعر شراء {i}",
        "how much did we buy {i} for", "purchase price of {i}", "what did we pay for {i}",
        "التكلفة", "والتكلفة", "وكام التكلفة", "التكلفه كام",
        "cost of {i}", "what does {i} cost", "{i} unit cost", "average cost of {i}",
        "value of {i} stock", "inventory value of {i}", "how much is {i} worth",
        "what is {i} worth", "how much did we pay for {i}", "purchase cost of {i}", "cost price of {i}", "{i} valuation", "stock value for {i}", "what is the value of our {i}", "how much is our {i} inventory worth",
    ],
    MOVES: [
        "اخر حركات {i}", "آخر حركات {i}", "حركات {i}", "حركة {i}",
        "اخر حركة على {i}", "{i} اتحرك امتى", "امتى اتصرف {i}",
        "عايز اشوف حركات {i}", "كشف حركة {i}", "تحركات {i}",
        "الوارد والمنصرف من {i}", "منصرف {i}", "وارد {i}", "صرف {i}",
        "اخر حركاته", "حركاته", "وحركاته", "والحركات",
        "movements of {i}", "last movements for {i}", "{i} transactions",
        "show movements of {i}", "recent movements {i}", "issues and receipts for {i}",
        "transactions for {i}", "movement history of {i}", "what happened to {i}", "recent activity for {i}", "goods in and out for {i}", "issue history for {i}", "in and out movements of {i}",
    ],
    WAREHOUSES: [
        "ايه المخازن اللي اقدر اشوفها", "المخازن اللي اقدر اشوفها", "مخازني",
        "المخازن بتاعتي", "ايه مخازني", "اي المخازن المتاحة لي",
        "عايز اعرف المخازن بتاعتي", "قائمة المخازن", "المخازن المتاحة",
        "انا مسؤول عن انهي مخازن", "صلاحيتي على اي مخزن", "المستودعات بتاعتي",
        "my warehouses", "which warehouses can I see", "list my warehouses",
        "what warehouses do I have access to", "show my warehouses",
        "what stores can I access", "list the stores I can use", "which warehouses may I use", "stores available to me", "my stores", "warehouse access for me", "what warehouses am I permitted",
        # THE SINGULAR. Every template here used the plural "المخازن", and the owner asked
        # "قول المخزن اللي متسجله عندنا" — singular. It went to item-stock at 0.462, because
        # "المخزن" is ONE LETTER from "المخزون" and character n-grams cannot tell two core
        # business nouns apart on one letter. The typo tolerance that rescues "برجر داج" is the
        # same mechanism that confuses these, so the distinction has to come from examples.
        "قول المخزن اللي متسجل عندنا", "المخزن المتسجل عندنا", "المخزن المسجل",
        "ايه المخزن اللي عندنا", "انهي مخزن عندنا", "فيه كام مخزن",
        "عندنا كام مخزن", "اسماء المخازن", "المخازن المسجلة", "المخازن المتاحة عندنا",
        "المستودع", "المستودعات", "فروعنا ومخازننا", "قولي المخزن",
        "which warehouse do we have", "name the warehouses", "how many warehouses",
        "list the warehouse", "what stores are registered", "our registered warehouses",
    ],
    CASH: [
        "كام رصيد النقدية", "رصيد النقدية", "رصيد الخزنة", "الخزنة فيها كام",
        "كام في الخزينة", "فلوسنا كام", "عندنا كام فلوس", "كام الكاش",
        "رصيد البنك", "ارصدة البنوك", "النقدية والبنوك", "السيولة عندنا",
        "كام فلوس عندنا في البنك", "عايز اعرف الكاش", "الرصيد النقدي",
        "cash balance", "how much cash do we have", "bank balance",
        # "احنا معانا كام دلوقتي" - genuinely ambiguous between cash and stock value, and it used
        # to be REFUSED, which is the safe outcome for an ambiguous question. Adding category
        # vocabulary tipped it into answering item-stock instead: a refusal became a wrong
        # answer, which is the one direction a change must never move. The cash side has to
        # claim this phrasing explicitly.
        "احنا معانا كام", "معانا كام دلوقتي", "معانا كام فلوس", "كام معانا",
        "فلوسنا كام دلوقتي", "احنا عندنا كام دلوقتي", "المتاح عندنا كاش",
        "how much have we got", "what do we have right now", "how much money now",
        "show me the cash", "what is our liquidity", "treasury balance",
        "how much money do we have", "what is in the bank", "current cash position", "available funds", "money in the treasury", "our cash on hand", "funds available",
    ],
    RECEIVABLES: [
        "المستحق على العملاء", "كام المستحق على العملاء", "فلوسنا عند العملاء",
        "مين مدين لنا", "العملاء مدينين بكام", "ذمم العملاء", "الذمم المدينة",
        "المديونيات", "كام لينا عند العملاء", "المتأخرات على العملاء",
        "ارصدة العملاء المدينة", "عايز اعرف المستحقات",
        "العميل عليه كام", "عليهم كام فلوس", "مين عليه فلوس", "مين ما دفعش لحد دلوقتي",
        "الفلوس اللي لينا عند العملاء", "مديونية العملاء كام", "العملاء مدينين بكام",
        "اللي لينا عند الزباين", "فواتير لسه ما اتحصلتش", "مستحقات لم تحصل",
        # THE SAME QUESTION IN THE WORDS PEOPLE ACTUALLY USE. "الفواتير غير المدفوعة" was answered
        # by RANKING EVERY INVOICE BY REVENUE - the words "فاتورة" and a measure composed cleanly on
        # the sales surface, so a plausible table arrived in place of the receivables this asks for.
        "الفواتير غير المدفوعة", "الفواتير اللي لسه متدفعتش", "كام فاتورة لسه متدفعتش",
        "فواتير مش مدفوعة", "الفواتير المفتوحة", "فواتير مفتوحة", "كام فاتورة مفتوحة",
        "الفواتير المتأخرة", "فواتير متأخرة", "عدد الفواتير غير المحصلة",
        "unpaid invoices", "open invoices", "outstanding invoices", "how many unpaid invoices",
        "overdue invoices", "uncollected invoices",
        "who still owes us", "unpaid invoices from customers", "customer debt balance",
        "how much are customers behind", "outstanding balance per customer",
        "open receivables", "what do customers owe us", "accounts receivable",
        "outstanding customer balances", "who owes us money", "AR balance",
        "customer debts", "unpaid customer invoices", "who has not paid", "receivables balance", "money owed by customers", "outstanding invoices", "total due from customers",
    ],
    ANOMALIES: [
        "فيه قيود شاذة", "فيه قيود غريبة", "قيود شاذة", "القيود الشاذة",
        "ايه القيود المشبوهة", "قيود مشبوهة", "فيه حاجة غلط في القيود",
        "راجع القيود", "تحليل القيود", "قيود اليومية الغريبة",
        "فيه قيود مكررة", "قيود مكررة", "شوف القيود الشاذة",
        "journal anomalies", "unusual journal entries", "suspicious entries",
        "check the journal for anomalies", "duplicate journal entries",
        "any unusual entries in the ledger", "check the ledger for odd postings", "strange journal entries", "review the journal for problems", "anything wrong in the journal", "odd postings",
    ],
    CATEGORIES: [
        # A CATEGORY IS NOT AN ITEM. "فئة" and "صنف" are one short word apart to a character
        # n-gram, so the distinction has to be taught rather than inferred - the same lesson as
        # "المخزن" against "المخزون".
        "ايه فئات الاصناف اللي عندنا", "قول علي فئات الاصناف", "فئات الاصناف",
        "الفئات", "فئات", "ايه الفئات", "الفئات المسجلة", "فئات المنتجات",
        "التصنيفات", "تصنيفات الاصناف", "مجموعات الاصناف", "اقسام الاصناف",
        "عندنا كام فئة", "كام تصنيف عندنا", "اسماء الفئات",
        "item categories", "product categories", "what categories do we have",
        "list the categories", "our categories", "how many categories", "category list",
        "item groups", "product groups",
    ],
    PURCHASES: [
        # A NAMED VENDOR. The surface filters on it; the router only has to deliver the question.
        "في مورد اسمه {v}", "عندنا مورد اسمه {v}", "فيه مورد اسمه {v}",
        "اشترينا كام من {v}", "مشترياتنا من {v}", "صرفنا كام عند {v}",
        "فواتير {v}", "{v} وردلنا كام", "ايه مشترياتنا من {v}", "كام دفعنا لـ{v}",
        "is there a vendor called {v}", "purchases from {v}", "how much did we buy from {v}",
        "{v} invoices", "spend with {v}",
        "المشتريات مقارنة بالشهر اللي فات", "صرفنا اكتر ولا اقل عن الشهر اللي فات",
        "المشتريات زادت ولا قلت", "قارنلي المشتريات بالشهر الماضي",
        "purchases compared to last month", "did we spend more or less",
        "purchase spend month over month",
        # WHAT WE BOUGHT. "الاصناف المشتراه" is the exact wording that was refused, and it is here
        # with the spelling the owner used (هـ) as well as the formal one (ة) - a vocabulary that
        # knows only the correct spelling fails the question people actually type.
        "ايه الاصناف المشتراه", "هات الاصناف المشتراه", "الاصناف المشتراه", "الاصناف المشتراة",
        "ايه الاصناف اللي اشتريناها", "اشترينا ايه", "احنا اشترينا ايه", "ايه اللي اشتريناه",
        "اكتر صنف اشتريناه", "اعلى الاصناف شراء", "ترتيب الاصناف بالمشتريات",
        "المشتريات حسب الصنف", "المشتريات لكل صنف", "مشترياتنا",
        "what did we buy", "what have we purchased", "purchased items", "items we bought",
        "top purchased items", "purchases by item", "most bought items",
        # FROM WHOM
        "من مين اشترينا", "مين موردينا", "المشتريات حسب المورد", "اعلى الموردين",
        "اكبر مورد", "صرفنا كام على كل مورد", "المشتريات لكل مورد", "ترتيب الموردين بالمشتريات",
        "purchases by vendor", "spend by supplier", "top vendors", "which supplier did we buy from",
        "vendor spend", "supplier spend",
        # HOW MUCH, AND WHEN
        "اشترينا بكام", "صرفنا كام على المشتريات", "اجمالي المشتريات", "كام اجمالي الشراء",
        "المشتريات شهريا", "المشتريات لكل شهر", "مشتريات الشهر", "قيمة المشتريات",
        "كام كمية اشتريناها", "الكميات المشتراه",
        "total purchases", "purchase total", "monthly purchases", "purchases by month",
        "how much did we spend on purchases", "purchase quantity",
        # THE DOCUMENTS
        "اخر فاتورة شراء", "فواتير الشراء", "اخر فواتير الشراء", "اخر مشترياتنا",
        "last purchase invoice", "recent purchase invoices", "latest purchases",
        # BY CATEGORY
        "المشتريات حسب الفئة", "الشراء لكل فئة", "purchases by category",
    ],
    STOCKQ: [
        "حركة المخزون مقارنة بالشهر اللي فات", "الوارد زاد ولا قل",
        "المنصرف مقارنة بالشهر الماضي", "حركة المخازن الشهر ده مقابل اللي فات",
        "stock movements compared to last month", "did stock activity go up or down",
        "inbound month over month",
        # AGGREGATE STOCK MOVEMENT. The deciding feature against inventory.item-movements is that
        # these questions GROUP and that one NAMES AN ITEM. The two necessarily share vocabulary -
        # both are about حركة - so the distinction has to be taught by shape, not by words.
        "ايه الوارد لكل مخزن", "الوارد حسب المخزن", "المنصرف حسب المخزن",
        "الوارد والمنصرف لكل مخزن", "انهي مخزن استلم اكتر", "اكتر مخزن فيه حركة",
        "حركة المخازن", "حركات المخازن", "نشاط المخازن",
        "ايه اللي دخل المخازن", "ايه اللي خرج من المخازن", "كام دخل وكام خرج",
        "الوارد الشهر ده", "المنصرف الشهر ده", "حركة المخزون شهريا",
        "الوارد لكل شهر", "المنصرف لكل شهر", "حركة المخزون لكل شهر",
        "انواع الحركات", "الحركات حسب النوع", "ايه انواع حركات المخزون",
        "اكتر نوع حركة", "حركات حسب النوع",
        "اكتر صنف اتحرك", "الاصناف الاكثر حركة", "ترتيب الاصناف بالحركة",
        "الوارد حسب الصنف", "المنصرف حسب الصنف", "الوارد حسب الفئة",
        "عدد الحركات", "كام حركة عندنا", "تكلفة الحركات",
        "stock movements by warehouse", "stock in by warehouse", "stock out by warehouse",
        "which warehouse received the most", "warehouse activity", "stock activity by month",
        "movements by type", "movement types breakdown", "inbound by month", "outbound by month",
        "most active items by movement", "which items moved the most", "movements by category",
        "how many movements", "stock movement cost",
    ],
    MEDS: [
        # «طيب الادوية» — refused, like the doctors before it. Value-naming phrasings live HERE and
        # not in the handler's keywords: a keyword is also read as the surface's vocabulary, so one
        # that names a value ("المخدرات") makes that value invisible to the named-value filter.
        "الادوية", "ايه الادوية", "قائمة الادوية", "الاوامر الدوائية", "اوامر الدواء",
        "الادوية الاكثر وصفا", "اكتر دواء بيتوصف", "اكتر الادوية وصفا", "كام امر دوائي",
        "الادوية حسب طريقة الاعطاء", "الادوية حسب الحالة", "العلاج الموصوف", "الوصفات",
        "اوامر المخدرات", "الادوية المخدرة", "كام امر مخدر", "الادوية الساريه",
        "medications", "medicines", "what medicines", "drugs prescribed", "medication orders",
        "most prescribed medicines", "prescriptions", "controlled drug orders", "active orders",
    ],

    BILLING: [
        # THE MONEY SIDE OF THE HOSPITAL. Twenty-six billing tables carrying 458 rows had no
        # window at all, so the assistant could say how many doctors there are and not how much
        # the place billed. Value-naming phrasings live HERE and not in the handler keywords: a
        # keyword is also read as the surface vocabulary, so "الاشعة" as a keyword would make the
        # named-value filter blind to radiology and answer with the total for every category.
        "فواتير المستشفى", "فوترة المستشفى", "ايراد المستشفى", "ايرادات المستشفى",
        "كام فوترنا", "كام فوترت المستشفى", "المبلغ المفوتر", "اجمالي الفواتير",
        "كام فوترنا الشهر ده", "ايراد المستشفى الشهر ده", "فواتير المرضى", "الفوترة",
        "فواتير حسب القسم", "ايراد كل قسم", "الفواتير حسب التصنيف", "ايراد الاشعة",
        "فواتير الاشعة", "فواتير المعمل", "ايراد الكشف", "فواتير الصيدلية",
        "الفواتير المفتوحة", "الفواتير المقفولة", "كام بند اتفوتر", "عدد بنود الفواتير",
        "اكتر قسم فوترة", "اعلى قسم في الايراد", "فوترة الاجراءات", "ايراد العيادات",
        "hospital billing", "hospital revenue", "billed amount", "how much did we bill",
        "patient bills", "billing by department", "hospital charges", "revenue by category",
        "radiology billing", "lab billing", "pharmacy charges", "open bills", "closed bills",
        "how many billed lines", "which department billed most", "billed this month",
    ],

    CLAIMS: [
        # The insurer side of the hospital money. Separate intent from BILLING on purpose: they
        # are different grains and the costliest mistake here is answering one under the other.
        # Value-naming phrasings ("المرفوضة", "المعلقة", a payer name) live HERE, never in the
        # handler keywords, or the named-value filter goes blind to exactly the word asked about.
        "المطالبات", "مطالبات التامين", "حالة المطالبات", "ملف المطالبات",
        "المطالبات المرفوضة", "مطالبات مرفوضة", "كام مطالبة اترفضت", "اسباب الرفض",
        "المطالبات المعلقة", "مطالبات لسه ما اتبتش", "المطالبات المعتمدة", "اللي التامين وافق عليه",
        "المبلغ المعتمد", "المبلغ المطالب به", "قيمة المطالبات", "نسبة القبول",
        "المطالبات حسب جهة الدفع", "مطالبات كل شركة تامين", "اكبر جهة دفع",
        "المطالبات المعاد تقديمها", "المبالغ المستردة", "الاسترداد من التامين",
        "claims", "insurance claims", "claim status", "rejected claims", "claims rejected",
        "pending claims", "accepted claims", "claimed amount", "accepted amount",
        "claims by payer", "rejection reasons", "why were claims rejected", "recouped amount",
        "resubmitted claims", "acceptance rate", "which payer claims most",
    ],

    APPTS: [
        # «جيب مواعيد اليوم» went into the assistant and was refused with the catalogue offered
        # instead. 138 appointments in one table and no window onto them. Value-naming phrasings
        # live HERE and never in the handler keywords: "الملغاة" as a keyword would make the
        # filter blind to the very status being asked about.
        "المواعيد", "مواعيد اليوم", "جيب مواعيد اليوم", "مواعيد النهاردة", "كام موعد",
        "عدد المواعيد النهاردة", "جدول العيادة", "جدول المواعيد", "الحجوزات",
        "المواعيد حسب الطبيب", "مواعيد كل دكتور", "المواعيد حسب القسم", "المواعيد حسب الساعة",
        "المواعيد الملغاة", "المواعيد اللي اتلغت", "مين ماحضرش", "اللي محضروش",
        "كام حد ماجاش", "المواعيد المحجوزة", "اللي خلصوا كشف", "مواعيد المتابعة",
        "متوسط الانتظار", "المرضى بيستنوا قد ايه", "مدة الانتظار", "مدة الكشف",
        "اطول انتظار", "اي ساعة فيها مواعيد اكتر",
        # A VISIT IS AN APPOINTMENT THAT HAPPENED, AND IT BELONGS HERE.
        #
        # «كام زيارة عندنا» was answered with the CASH BALANCE — 239,605 under a question about
        # visits. "كام ... عندنا" is the shape of «كام فلوس عندنا», "زيارة" was a word the model
        # had never seen, and nothing in the sentence pulled the other way. Found the day the
        # diagnoses surface shipped, because that surface makes the question likely: the moment a
        # reader is told 85 diagnoses, the next thing they ask is how many visits produced them.
        #
        # They are taught HERE and not on the diagnoses intent, because the diagnoses surface's
        # grain is the diagnosis: it would answer 85 for 68 visits, which is close enough to be
        # believed. This surface counts the appointments themselves and shows their status.
        "كام زيارة", "كام زيارة عندنا", "عدد الزيارات", "الزيارات", "زيارات الشهر ده",
        "كام كشف اتعمل", "عدد الكشوف", "الكشوف", "كام كشف", "زيارات العيادة",
        "how many visits", "number of visits", "visits", "visits this month",
        "how many consultations", "consultations",
        "appointments", "todays appointments", "how many appointments", "clinic schedule",
        "bookings today", "appointments by doctor", "appointments by department",
        "cancelled appointments", "no shows", "how many did not turn up", "booked appointments",
        "average wait", "waiting time", "how long do patients wait", "consultation time",
        "busiest hour in clinic", "follow up visits",
    ],

    DX: [
        # WHAT THE HOSPITAL IS TREATING. 85 diagnoses on 68 consultations, and until now no
        # question could reach them: «ايه اكتر الامراض» landed on the appointments surface, which
        # knows when people came and nothing about why.
        #
        # NOTHING HERE ASKS FOR A COUNT OF VISITS OR OF NOTES. The surface's grain is the
        # diagnosis, so «كام زيارة» answered from it would be 85 for 68 visits — close enough to
        # be believed and wrong. Those wordings stay with APPTS, and the handler's caveat says so
        # when one slips through.
        "ايه اكتر التشخيصات", "اكتر التشخيصات عندنا", "ايه اكتر الامراض", "اكتر الامراض عندنا",
        "التشخيصات", "تشخيصات المرضى", "ايه الامراض اللي بنعالجها", "احنا بنعالج ايه",
        "ايه اكتر مرض بيتكرر", "توزيع الامراض", "الحالات المرضية عندنا",
        "التشخيصات حسب القسم", "الامراض حسب القسم", "التشخيصات حسب الطبيب",
        "تشخيصات كل دكتور", "الامراض حسب الشهر", "التشخيصات حسب الشهر", "تشخيصات الشهر ده",
        "كام حالة سكري", "حالات السكري", "كام حالة ضغط", "حالات الضغط", "كام حالة ربو",
        "تشخيصات الاطفال", "تشخيصات قسم الباطنة", "امراض قسم العيون",
        "التشخيصات الرئيسية", "التشخيصات الثانوية", "كام تشخيص اتسجل",
        "اكواد التشخيص المستخدمة", "اكتر كود تشخيص", "كام كود تشخيص استخدمناه",
        "diagnoses", "the diagnoses", "commonest diagnoses", "top diagnoses",
        "most common conditions", "what are we treating", "what conditions do we see most",
        "case mix", "our case mix", "diagnoses by department", "diagnoses by doctor",
        "diagnoses by month", "how many diabetes cases", "how many hypertension cases",
        "asthma cases", "principal diagnoses", "secondary diagnoses",
        "diagnosis codes used", "icd codes used", "how many diagnoses were recorded",
        "distribution of conditions",
    ],

    DOCTORS: [
        # THE HOSPITAL'S FIRST INTENT. The owner asked «جيب قائمة الاطباء» and was offered the
        # revenue per item instead — six surfaces existed and none of them was the hospital.
        #
        # {n} is reused for a named doctor: the surface matches a name against its own rows, so the
        # router only has to deliver the question to the right place.
        "جيب قائمة الاطباء", "قائمة الاطباء", "الاطباء", "مين الاطباء عندنا", "كام طبيب عندنا",
        "عدد الاطباء", "كام دكتور عندنا", "قائمة الدكاترة", "الدكاترة اللي عندنا",
        "الاطباء حسب القسم", "كام طبيب في كل قسم", "الاطباء حسب التخصص", "توزيع الاطباء",
        "جيب قائمة العيادات", "قائمة العيادات", "العيادات", "الاطباء حسب العيادة",
        "كام عيادة عندنا", "مين في كل عيادة", "list the clinics", "the clinics",
        "doctors by clinic", "how many clinics",
        "الاطباء النشطين", "مين الاستشاريين", "الاخصائيين عندنا", "الاطباء الزائرين",
        "الاطباء المتفرغين", "الاطباء حسب الدرجة", "كام استشاري", "اطباء الباطنة",
        "في دكتور اسمه {n}", "عندنا طبيب اسمه {n}", "دور على الدكتور {n}", "الدكتور {n}",
        "list the doctors", "the doctors", "how many doctors", "doctors by department",
        "our practitioners", "how many practitioners", "medical staff", "clinical staff",
        "who are our consultants", "visiting doctors", "active doctors",
        "is there a doctor called {n}", "find the doctor {n}",
    ],

    EXPENSES: [
        # SPENDING, from the posted ledger. Built because the question was being ANSWERED WRONGLY,
        # not because it was refused: "المصروفات حسب النوع" reached stock movements and returned
        # movement types, since "حسب النوع" is vocabulary there and "المصروفات" was nowhere.
        "ايه المصروفات", "المصروفات", "المصاريف", "ايه المصاريف", "المصروفات حسب الحساب",
        "المصروفات حسب النوع", "المصروفات حسب البند", "بنود المصروفات", "تحليل المصروفات",
        "احنا بنصرف على ايه", "بنصرف على ايه", "اكبر بند مصروف", "اكتر حاجة بنصرف عليها",
        "المصروفات حسب مركز التكلفة", "المصروفات لكل مركز تكلفة", "المصروفات الشهرية",
        "مصروفات الشهر", "صرفنا كام الشهر ده", "ايه اكبر المصروفات", "المصروفات حسب الشهر",
        "expenses", "what are our expenses", "expense breakdown", "expenses by account",
        "what do we spend on", "spending by account", "operating expenses",
        "biggest expense", "monthly expenses", "expenses by cost centre", "where the money goes",
    ],

    HRQ: [
        # THE ROSTER. Note what is NOT here: nothing about salary, payslips, attendance or hours.
        # The surface cannot answer those - pay is recorded for 1 employee of 21 and attendance for
        # 3 - so teaching the model to route such questions here would turn an honest refusal into
        # a confident answer about three people.
        "كام موظف عندنا", "عدد الموظفين", "احنا كام موظف", "كام واحد شغال عندنا",
        "الموظفين حسب الوظيفة", "كام موظف في كل وظيفة", "ايه المسميات الوظيفية",
        "الوظائف اللي عندنا", "فريق العمل", "حجم الفريق", "مين شغال عندنا",
        "كام موظف في كل فرع", "الموظفين حسب الفرع", "توزيع الموظفين على الفروع",
        "كام موظف نشط", "الموظفين النشطين", "مين على راس العمل", "كام حد ساب الشغل",
        "الموظفين حسب نوع التوظيف", "كام دوام كامل", "كام دوام جزئي",
        "الموظفين حسب سنة الالتحاق", "مين اتعين السنة دي",
        "كام رجالة وكام ستات", "الموظفين حسب النوع",
        "how many employees", "headcount", "staff count", "team size",
        "employees by job title", "employees by role", "what job titles do we have",
        "employees by branch", "staff per branch", "how many active employees",
        "who works here", "the roster", "how many full time", "employees by gender",
        "employees by year joined",
        # A NAMED COLLEAGUE. Each of these says "employee" somewhere on purpose - the word is what
        # separates asking after a person on the roster from asking after a person in general, and
        # the surface answers either way: it names them, or it says nobody by that name.
        "في موظف اسمه {n}", "عندنا موظف اسمه {n}", "فيه موظف اسمه {n}",
        "بسأل عن {n} موظف", "{n} موظف عندنا", "{n} موظف ولا لا", "هل {n} موظف عندنا",
        "ابحث عن موظف اسمه {n}", "دور على الموظف {n}", "بيانات الموظف {n}",
        "is there an employee called {n}", "employee named {n}", "do we have an employee {n}",
        "find the employee {n}", "look up employee {n}",
    ],
    POSQ: [
        # THE COUNTER. What distinguishes these from sales.query is the PLACE and the TIME: a till
        # question is about a branch, an hour, a service type or an order count. "المبيعات" alone
        # still belongs to sales.query, which reads invoices; these read the till.
        "اي ساعة اكتر زحمة", "ساعات الذروة", "وقت الذروة", "الزحمة بتكون امتى",
        "المبيعات حسب الساعة", "البيع حسب الساعة", "اي وقت بنبيع اكتر", "انهي ساعة بنبيع فيها اكتر",
        "اي فرع بيبيع اكتر", "المبيعات حسب الفرع", "مبيعات الفروع", "ترتيب الفروع بالمبيعات",
        "انهي محل اكتر", "مبيعات كل فرع",
        "كام طلب النهاردة", "عدد الطلبات", "كام اوردر", "عدد الاوردرات", "عدد الطلبات حسب الفرع",
        "تيك اواي ولا صالة", "المبيعات حسب نوع الطلب", "كام دليفري", "نسبة التيك اواي",
        "مبيعات الكاشير", "اي كاشير باع اكتر", "المبيعات حسب الكاشير",
        "مبيعات نقطة البيع", "ايراد نقطة البيع", "كام بعنا في الكاشير",
        "اكتر صنف بيتباع في المحل", "الاصناف الاكثر مبيعا في الفرع",
        "busiest hour", "which hour is busiest", "peak hours", "sales by hour",
        "which branch sells most", "sales by branch", "branch ranking",
        "how many orders", "order count by branch", "average order value",
        "takeaway versus dine in", "sales by order type", "how much delivery",
        "sales by cashier", "which cashier sold most",
        "point of sale sales", "till takings", "counter revenue",
    ],
    SALES_QUERY: [
        # COMPARISON WORDINGS. Added to the existing intent on purpose: "المبيعات الشهر ده" and
        # "المبيعات مقارنة بالشهر اللي فات" are the same question of the same surface, and the
        # comparison is read off the words by the composer. Making it a separate intent would have
        # created a thirteenth place for the router to go wrong for no gain.
        "المبيعات مقارنة بالشهر اللي فات", "الايراد مقارنة بالشهر الماضي",
        "مبيعاتنا زادت ولا قلت", "الايراد زاد ولا قل", "المبيعات طلعت ولا نزلت",
        "ايه الفرق في الايراد عن الشهر اللي فات", "نسبة التغير في المبيعات",
        "المبيعات الشهر ده مقابل اللي فات", "قارنلي المبيعات بالشهر اللي فات",
        "revenue compared to last month", "sales versus last month",
        "did sales go up or down", "sales month over month", "revenue growth",
        "change in revenue from last month",
        # what sells
        "ايه اعلى الاصناف مبيعا", "اعلى الاصناف مبيعا", "اكتر صنف بيتباع", "اكتر حاجة بتتباع",
        "ايه اكتر صنف بيتباع", "الاصناف الاكثر مبيعا", "اكثر الاصناف مبيعا", "افضل الاصناف مبيعا",
        "ايه اللي بيتباع كتير", "ايه اللي ماشي", "اعلى مبيعات", "ترتيب الاصناف بالمبيعات",
        "ايه البضاعه اللي بتتباع", "الاصناف اللي بتمشي اكتر", "اقل الاصناف مبيعا", "اقل الاصناف ايرادا",
        # TAUGHT AFTER A RETRAIN MOVED THE BOUNDARY. Adding the appointments intent flipped
        # "ايه البضاعة اللي بتتباع اكتر من غيرها" to dormant-items - the exact opposite meaning -
        # while the shorter "ايه البضاعه اللي بتتباع" stayed right. The tail was doing the work,
        # so both halves of the contrast are stated rather than left to the margin.
        # "SHOW ME THE LAST THREE INVOICES" is a sales question and the retrain began refusing
        # it. A refusal is cheaper than a wrong answer and is still a loss: the surface can list
        # invoices, so declining to is a capability quietly given up at the margin.
        "وريني اخر ثلاث فواتير", "اخر ثلاث فواتير", "وريني اخر الفواتير", "اخر الفواتير",
        "وريني اخر كام فاتورة", "اخر فواتير المبيعات", "اخر خمس فواتير",
        "ايه البضاعه اللي بتتباع اكتر من غيرها", "البضاعه اللي بتتباع اكتر من غيرها",
        "ايه الاصناف اللي بتتباع اكتر من غيرها", "الحاجات اللي بتتباع اكتر من غيرها",
        "best selling items", "top selling products", "what sells the most",
        "which items sell best", "top items by sales", "our best sellers", "rank items by sales",
        "lowest selling items", "worst selling products",
        # who buys
        "مين اعلى العملاء", "اعلى العملاء", "اكبر العملاء", "اهم العملاء", "افضل العملاء",
        "مين اكتر عميل بيشتري", "مين بيشتري اكتر", "ترتيب العملاء", "العملاء بالمبيعات",
        "اكبر عميل عندنا", "مين اكبر زبون", "اعلى عشرة عملاء", "المبيعات حسب العميل",
        "top customers", "biggest customers", "who buys the most", "rank customers by sales",
        "revenue per customer", "sales by customer",
        # what we make
        "ايه هامش الربح", "هامش الربح", "هامش ربح {i}", "كام هامش الربح على {i}",
        # PROFIT. The surface has carried cost and margin all along; only these wordings were
        # missing, so "الارباح كام" came back as revenue by item - a confident figure answering a
        # different question. "هامش الربح" already worked, which is how it went unnoticed.
        "الارباح كام", "الارباح", "ايه الارباح", "كام ربحنا", "ربحنا كام", "اجمالي الربح",
        "الربح الاجمالي", "صافي الربح من المبيعات", "ربح الاصناف", "الربح حسب الصنف",
        "الربح حسب العميل", "الربح حسب الشهر", "ايه اكتر صنف بيربح", "اكتر صنف مربح",
        "profit", "how much profit", "gross profit", "total profit", "profit by item",
        "profit by customer", "profit by month", "most profitable item",
        # PUSHED OUT BY THE TIME WORDINGS ABOVE. Both of these answered before that change and
        # were refused after it, which is the whole reason the training report is never the last
        # word: the boundary moves where you were not looking.
        # A BARE TOPIC WORD, WITH AND WITHOUT ITS PARTICLES. "بالمبيعات" answered until the hospital
        # intents arrived and then routed nowhere — the generator adds a prefix to the first word
        # only sometimes, and "sometimes" stopped being enough once there were more intents to lose
        # to. Written out, so the shortest possible question keeps working.
        "المبيعات", "بالمبيعات", "والمبيعات", "مبيعات", "الايراد", "بالايراد", "والايراد",
        "ايراد", "الدخل", "بالدخل", "sales", "the sales", "revenue", "the revenue",
        "which goods brought in the most money", "which items brought in the most money",
        "what goods made the most money", "which products earn the most",
        "مين العملاء اللي بيصرفوا اكتر", "اكتر العملاء انفاقا", "اكبر العملاء",
        "مين اكبر عميل عندنا", "العملاء حسب الانفاق", "ترتيب العملاء بالمبيعات",
        "which clients spend the most with us", "which customers spend the most",
        "our biggest customers", "customers by spend", "top clients",
        "كام قطعه راحت من كل صنف", "كام قطعة اتباعت من كل صنف", "الكميات المباعة لكل صنف",
        "عدد القطع المباعة لكل صنف", "كام وحدة بعنا من كل صنف",
        "quantity sold per item", "units sold by item", "how many pieces of each item",
        # A NAMED DAY. The surface turns these into a date filter; the router has to deliver them.
        # "الشهر اللي فات" is deliberately absent - it belongs to the comparison wordings above,
        # which answer a better question about it than a plain filter would.
        "مبيعات امبارح", "المبيعات امبارح", "مبيعات امبارح كام", "بعنا كام امبارح",
        "ايراد امبارح", "مبيعات النهاردة", "المبيعات النهاردة", "بعنا كام النهاردة",
        "مبيعات اول امبارح", "مبيعات الاسبوع ده", "ايراد الاسبوع ده", "مبيعات الشهر ده",
        "الايراد الشهر ده", "مبيعات اخر اسبوع",
        "yesterday sales", "sales yesterday", "revenue yesterday", "sales today",
        "today revenue", "sales this week", "revenue this month", "sales this month",
        # A NAMED CUSTOMER. {v} is the party slot and it serves both sides on purpose: the SAME
        # company is a customer here and a supplier in the purchases block, and what separates the
        # two questions is the word beside the name, not the name. Teaching only the purchases half
        # is what sent "Nile Co sales" to a refusal.
        "مبيعات {v}", "ايراد {v}", "الايراد من {v}", "بعنا كام لـ{v}", "فواتير {v} كام",
        "{v} اشترى كام", "مبيعاتنا لـ{v}", "عميل اسمه {v} اشترى كام", "في عميل اسمه {v}",
        "{v} sales", "sales to {v}", "revenue from {v}", "how much did we sell to {v}",
        "is there a customer called {v}",
        # A NAMED ITEM. The surface filters on it; the router simply has to get the question here.
        "مبيعات {i}", "المبيعات بتاعة {i}", "ايراد {i}", "الايراد من {i}", "{i} باع كام",
        "بعنا كام من {i}", "كام بعنا من {i}", "مبيعات {i} كام", "ايه مبيعات {i}",
        "{i} عمل كام", "فلوس {i}", "sales of {i}", "revenue for {i}", "how much {i} did we sell",
        "{i} revenue", "{i} sales",
        "الهامش", "ربح الاصناف", "ارباح الاصناف", "ايه اللي بيكسب", "الاصناف الرابحة",
        "اكتر صنف بيكسب", "نسبة الربح", "ربحية الاصناف", "الهامش لكل عميل", "الهامش حسب الصنف",
        "gross margin", "margin by item", "profit margin", "margin per customer",
        "which items are profitable", "most profitable items", "profit by product",
        # the shapes the retired handlers could not do at all
        "ايه الايراد لكل شهر", "الايراد الشهري", "المبيعات شهريا", "المبيعات لكل شهر",
        "ايه الايراد لكل صنف", "الايراد حسب الصنف", "بعنا كام", "بعنا كام اجمالا",
        "اجمالي المبيعات", "كام مبيعاتنا", "كام كمية اتباعت", "الكمية المباعه لكل صنف",
        "كام قطعه اتباعت", "التكلفه لكل صنف", "اعلى الاصناف تكلفه",
        "revenue by month", "monthly sales", "revenue by item", "total sales",
        # MONEY SHAPES. "which X the most" is a sentence sales and stock movement share;
        # the deciding word is money versus movement, so the money side is taught here
        # rather than the movement side being made to give the shape up.
        "انهي بضاعة جابت فلوس اكتر", "اي صنف دخل فلوس اكتر", "ايه اللي جاب ايراد اكتر",
        "مين العملاء اللي بيصرفوا اكتر", "انهي عميل دفع اكتر", "مين بيجيبلنا فلوس اكتر",
        "which products earned the most", "what earned the most money",
        "which customers spend the most money", "who pays us the most",
        "top earning products", "biggest spenders",
        "المبيعات حسب الفئة", "الايراد لكل فئة", "الهامش حسب الفئة",
        "اعلى فئة مبيعا", "اي فئة بتبيع اكتر", "مبيعات كل تصنيف",
        "revenue by category", "sales by category", "margin by category",
        "which category sells most", "top categories by revenue",
        # RECORDS, NOT TOTALS. "اخر فاتورة بيع" was refused outright because the surface could only
        # aggregate. It can now return bounded, ordered rows, and the vocabulary has to reach it.
        "اخر فاتورة بيع", "اخر فاتورة", "احدث فاتورة", "اخر الفواتير",
        "اخر خمس فواتير", "اخر عملية بيع", "اخر بيعة", "احدث المبيعات",
        "الفواتير الاخيرة", "ايه اخر فاتورة اتعملت", "اخر حاجة اتباعت",
        "last sales invoice", "latest invoice", "most recent invoice", "recent invoices",
        "the last five invoices", "newest sale", "last sale", "show recent sales",
        # REVENUE WORDING, restored. Folding the three intents dropped it, and
        # "which goods brought in the most money" went straight back to cash-balance - the money a
        # product brings in and the money in the bank share a vocabulary, so this side has to
        # claim its half of it explicitly.
        "which items generate the most revenue", "top items by revenue",
        "what brings in the most sales", "items ranked by money earned",
        "biggest revenue items", "which products earn the most",
        "الاصناف اللي بتجيب اعلى ايراد", "ايه اللي بيجيب فلوس اكتر",
        "how much did we sell", "quantity sold by item", "cost by item", "sales analysis",
    ],
    DORMANT: [
        "الاصناف الراكدة", "اصناف راكدة", "البضاعة الراكدة",
        "المخزون الراكد", "مخزون ميت", "بضاعة ميتة", "اصناف واقفة", "ايه اللي مش بيتحرك",
        "الاصناف اللي مش بتتباع", "اصناف بطيئة الحركة", "ايه اللي واقف في المخزن",
        "ايه البضاعة اللي مش ماشية", "فلوس واقفة في المخزن", "ايه اللي ساكن من زمان",
        "dead stock", "slow moving items", "slow movers", "dormant items",
        "what is not moving", "idle stock", "non moving inventory", "stagnant stock",
        "which items have not sold", "stock sitting still",
        # NEGATION IS THE WHOLE DISTINCTION, and it needed teaching once inventory.stock-query
        # arrived. "stock moved this month" and "stock has NOT moved" share every word except the
        # negation, and character n-grams weigh a short negation very lightly against a long shared
        # stem. "which stock has not moved in months" went to stock-query until these went in.
        # Shapes, not the benchmark's own wording, which is held out.
        "اصناف مش اتحركت من شهور", "بضاعة مامشتش من شهور", "اصناف ماتحركتش",
        "ايه اللي مااتحركش من زمان", "المخزون اللي مااتحركش", "اصناف من غير حركة",
        "stock that has not moved for months", "items with no movement for months",
        "inventory that never moved", "goods with no movement at all",
        "which products stopped moving", "items without any movement lately",
    ],
    COLLECTION: [
        "العملاء بياخدوا كام يوم عشان يدفعوا", "ايام التحصيل", "متوسط ايام التحصيل",
        "مدة التحصيل", "العملاء بيدفعوا امتى", "سرعة التحصيل", "بيدفعوا بعد كام يوم",
        "فترة السداد", "مدة السداد", "العملاء بيتأخروا في الدفع", "التحصيل بياخد قد ايه",
        "كام يوم للتحصيل", "متوسط فترة التحصيل",
        "dso", "days sales outstanding", "collection days", "how long to get paid",
        "average collection period", "how fast do customers pay", "time to collect",
        "payment speed", "how quickly are invoices settled",
    ],
}

# ---------------------------------------------------------------------------------------------
# NEGATIVES. The third group is the one that earns its place: business questions that use our
# vocabulary and have no handler. Without them a model routes "مين أحسن مورد" to item-stock with
# confidence, which is exactly what the keyword matcher did.
# ---------------------------------------------------------------------------------------------
NEGATIVES = [
    # small talk and nonsense
    "ازيك", "صباح الخير", "شكرا", "مين انت", "ما هو الطقس اليوم", "الجو عامل ايه",
    "اكتب لي قصيدة", "احكيلي نكتة", "كام الساعة", "فين اقرب مطعم",
    "hello", "how are you", "thanks", "who are you", "what is the weather",
    "write me a poem", "tell me a joke", "what time is it",
    "asdf", "...", "؟؟؟", "123456", "test", "تجربة",
    # other parts of the business, with no handler here.
    #
    # "كام موظف عندنا" and "how many employees do we have" were REMOVED from this list on
    # 2026-11-10: hr.workforce now answers them. A question that stays in the negatives after its
    # handler ships trains the model to refuse what the product does - the same defect, inverted,
    # as leaving a retired intent's keywords behind.
    #
    # Pay and attendance STAY here. hr.workforce deliberately carries no salary data, and
    # attendance is recorded for 3 employees out of 21, so both remain unanswerable and must keep
    # being refused.
    "مرتبات الشهر", "اجازات الموظفين", "عايز كشف حضور",
    "فاتورة كهرباء", "مشاريع الشهر ده", "عقود الموردين", "طلبات الشراء",
    "امر تشغيل جديد", "الصيانة الدورية", "تقرير المبيعات السنوي",
    "payroll for this month", "open purchase orders",
    # PAY AND ATTENDANCE, IN THE SHAPES THAT ACTUALLY LEAKED.
    #
    # Two got through once hr.workforce arrived, and neither is in the benchmark - they were found
    # by asking the model questions I knew the answer to rather than by waiting for the test:
    #   "كام ساعة اشتغل محمد"  -> pos.query @0.657, because pos owns the word "ساعة" (hour of day)
    #   "متوسط الرواتب"        -> accounting.collection-days @0.297
    # An hour-of-day question and an hours-worked question share a word and nothing else, and a
    # salary average is not a collection period. Both must refuse: attendance is recorded for 3
    # employees of 21 and a salary for 1, so there is no honest answer to give.
    "كام ساعة اشتغل", "كام ساعة اشتغلوا", "ساعات العمل", "ساعات الدوام",
    "مين تأخر", "التأخيرات", "الغياب", "كام يوم غاب", "سجل الحضور", "الحضور والانصراف",
    "متوسط الرواتب", "متوسط المرتبات", "إجمالي الرواتب", "كام بندفع مرتبات",
    "راتب محمد", "مرتب المدير", "الرواتب حسب الوظيفة", "تكلفة الموظفين",
    "اجازات مين", "رصيد الاجازات", "مين في اجازة",
    "how many hours did he work", "hours worked", "working hours per employee",
    "who was late", "absence record", "attendance sheet", "attendance report",
    "average salary", "total payroll", "salary by job title", "staff cost",
    "what does the manager earn", "who is on leave", "leave balance",

    "sales report", "maintenance schedule", "project budget",
    # LOOK-ALIKES: inventory and finance words, no handler
    # RATING A SUPPLIER. No such judgement is recorded anywhere in this product, so every one of
    # these must be refused. One variant was not enough: after the named-item templates were added
    # to sales, "مين أحسن مورد عندنا" leaked to the query intent at 0.5005 - a hair over the bar,
    # and a must-refuse question answered is the expensive failure this whole suite exists to stop.
    # The negative class is capped at one intent's worth of examples, so the lever is VARIETY.
    "مين أحسن مورد عندنا", "مين احسن مورد", "ايه افضل مورد", "افضل مورد عندنا",
    "مين اسوأ مورد", "رتبلي الموردين بالجودة", "تقييم الموردين", "تقييم المورد",
    "مين المورد الاحسن في الالتزام", "انهي مورد اوثق", "مين المورد اللي بيتأخر",
    "جودة الموردين", "اداء الموردين", "مين افضل عميل من ناحية الالتزام",
    # PRICE MOVEMENT OVER TIME. Nothing records what a supplier charged BEFORE, so "who raised
    # prices" has no basis - and it leaked to the query intent at 0.5292 once expense and profit
    # wordings widened that side. Every added positive moves a boundary somewhere unwatched.
    "مين المورد اللي رفع اسعاره", "مين رفع السعر", "الموردين اللي رفعوا اسعارهم",
    "تغير اسعار الموردين", "الاسعار زادت قد ايه", "تطور الاسعار", "تاريخ اسعار الشراء",
    "مقارنة اسعار الموردين", "مين اغلى مورد", "ارخص مورد",
    "which supplier raised prices the most", "who raised their prices", "price increases",
    "supplier price changes", "price history", "compare supplier prices", "cheapest supplier",

    # A REORDER POINT NOBODY RECORDED. The Items table has no reorder level - MinMarginPct is its
    # only threshold column - and demand cannot supply one either: 28 active stock days out of 312.
    # Any answer would rest on a number I chose, so these are refused. Counted before deciding.
    "ايه الاصناف اللي قربت تخلص", "الاصناف اللي قربت تخلص", "ايه الاصناف اللي محتاجة طلب",
    "الاصناف تحت حد الطلب", "حد اعادة الطلب", "نقطة اعادة الطلب", "ايه اللي لازم اطلبه",
    "الاصناف الناقصة", "اصناف قربت تنتهي", "اطلبلي اللي ناقص",
    "which items should I reorder", "items below reorder level", "low stock items",
    "what needs reordering", "items running out", "reorder point",

    # FETCHING ONE DOCUMENT. The surfaces group and total; they do not hand over a numbered
    # invoice, and Report Studio is where a document comes from. After named-item wordings were
    # added to sales, "هات فاتورة رقم 12" began composing a ranking BY invoice - a plausible table
    # in place of the single document that was asked for.
    "هات فاتورة رقم 12", "عايز فاتورة رقم 500", "اطبعلي فاتورة رقم 7",
    "هات لي الفاتورة رقم 1024", "افتح فاتورة رقم 9", "وريني فاتورة رقم 33",
    "اطبع اذن صرف رقم 12", "هات امر شغل رقم 4", "افتح قيد رقم 88",
    "get invoice number 12", "print invoice 500", "open invoice 9", "show invoice no 33",
    "best supplier", "worst supplier", "rate our suppliers", "supplier rating",
    "supplier quality", "supplier performance", "which vendor is most reliable",
"ايه الاصناف اللي قربت تخلص", "امتى اطلب من المورد",
    "توقع المبيعات الشهر الجاي", "ايه اسعار المنافسين",     "كام صنف تحت الحد الادنى", "الاصناف منتهية الصلاحية",
    "who is our best supplier",     "forecast next month sales", "which items should I reorder",     "what we owe to suppliers", "accounts payable", "supplier balances",
    # Measured as unanswerable here: 2 sales orders, 6 purchase orders, 4 payments with no
    # allocations, no budget table, and 28 active movement days in 312. A model that learns to
    # refuse these is refusing for the right reason.
    "ايه نسبة تلبية الطلبات", "ايه نسبة تلبية الطلبات عندنا", "نسبة تلبية الطلبات عندنا",
    "الموازنة مقابل الفعلي", "ايه الاصناف اللي هتخلص الاسبوع الجاي",
    "تباين اسعار الموردين", "اعمار ديون الموردين", "نسبة التسليم في الموعد",
    "fill rate", "budget vs actual", "which items will stock out next week",
    "supplier price variance", "on time delivery rate", "payables ageing",
    # ---------------------------------------------------------------------------------------
    # EVERYDAY QUESTIONS WEARING BUSINESS CLOTHES.
    #
    # Each of these carries a question word (مين/فين/ليه/امتى/كام) or a time word (امبارح/
    # النهاردة/الشهر ده) that the positives also use, on a subject the business has no handler
    # for and never will. They exist so those words stop being evidence on their own.
    #
    # The door question is here because it was a real failure, but it is one of many on purpose:
    # a single wording would have fixed a sentence rather than a shape.
    # ---------------------------------------------------------------------------------------
    "مين اللي كسر الباب امبارح", "مين اللي فتح الشباك", "مين جه الصبح", "مين اللي قفل النور",
    "فين المفتاح", "فين العربية", "فين الريموت", "فين الشنطة بتاعتي",
    "ليه الدنيا حر النهاردة", "ليه النت فاصل", "ليه الاسانسير واقف", "ليه الباب مقفول",
    "امتى هتمطر", "امتى الاجازة", "امتى يرجع الولد من المدرسة", "امتى الماتش",
    "كام الساعة دلوقتي", "كام واحد جه امبارح", "كام يوم فاضل على الاجازة",
    "ايه اخبارك النهاردة", "ايه اللي حصل امبارح", "ايه الاكل النهاردة",
    "العربية عطلت امبارح", "التكييف مش شغال", "الكهربا قطعت الشهر ده",
    "who broke the door yesterday", "who left the light on", "where are my keys",
    "why is the internet down", "when will it rain", "what happened yesterday",
    "how many people came yesterday", "the car broke down yesterday",
    # ---------------------------------------------------------------------------------------
    # "MAKE ME A ..." - a request to PRODUCE something, not to read a figure.
    #
    # A verb of production in front of a business noun flips the meaning entirely: "sales this
    # month" is a question this can answer; "make me a report of sales this month" is a document
    # request, and producing documents goes through Report Studio. Only the NOUN PHRASE was in
    # the negatives before, so the verb carried no weight and the request came back as a table.
    # ---------------------------------------------------------------------------------------
    "اعملي تقرير مبيعات ربع سنوي", "اعمل تقرير بالمخزون", "جهزلي كشف حساب العميل",
    "حضرلي تقرير شهري", "اطبعلي فاتورة", "اطبعلي كشف المخزون", "صدرلي الملف اكسيل",
    "ابعتلي التقرير على الميل", "عايز تقرير pdf", "حملي كشف الحركة",
    "اعملي رسم بياني للمبيعات", "صمملي تقرير جديد", "ضيف عمود في التقرير",
    "make me an annual sales report", "generate a stock report", "print the invoice",
    "export this to excel", "email me the report", "build a new report",
    "create a chart of sales", "download the statement",
]

# ---------------------------------------------------------------------------------------------
# Noise. Applied at generation time so the model sees the same sentence worn several ways.
# ---------------------------------------------------------------------------------------------
PREFIXES = ["", "", "", "و", "ال", "ب", "ف"]
SUFFIX_WORDS = ["", "", "", "؟", "?", " من فضلك", " لو سمحت", " please", " دلوقتي", " النهاردة"]
# A READ VERB IS NOT A PRINT VERB, and they are one letter apart.
#
# Adding "اطبعلي" (print me) to the document-request negatives also captured "اطلعلي" (show me) -
# ط-ب-ع against ط-ل-ع - and a plain stock question started being refused. Removing the print verb
# would have traded one defect for the other. Instead the READ verbs are taught here, on every
# intent, so the model sees both shapes in the places they actually belong and can tell them
# apart on context rather than on three shared characters.
LEADERS = ["", "", "", "عايز اعرف ", "محتاج ", "قوللي ", "ممكن ", "لو سمحت ",
           "اطلعلي ", "وريني ", "هاتلي ", "جيبلي ",
           "show me ", "tell me ", "I want to know ", "can you tell me "]

_AR = "ابتثجحخدذرزسشصضطظعغفقكلمنهوي"


def _typo(word: str, rng: random.Random) -> str:
    """One keystroke wrong — the shape that cost a hand-written rule."""
    if len(word) < 3:
        return word
    i = rng.randrange(len(word))
    kind = rng.random()
    if kind < 0.4:                       # drop a letter: دجاج -> داج
        return word[:i] + word[i + 1:]
    if kind < 0.7:                       # double a letter
        return word[:i] + word[i] + word[i:]
    if word[i] in _AR:                   # swap for another Arabic letter
        return word[:i] + rng.choice(_AR) + word[i + 1:]
    return word


def _roughen(text: str, rng: random.Random) -> str:
    """Wear the sentence the way a person would type it."""
    if rng.random() < 0.18:
        words = text.split()
        if words:
            j = rng.randrange(len(words))
            words[j] = _typo(words[j], rng)
            text = " ".join(words)
    if rng.random() < 0.10:
        text = text.replace(" ", "  ", 1)
    if rng.random() < 0.08:
        text = text.upper() if text.isascii() else text
    return text


def generate(per_intent: int = 900, seed: int = 20261108) -> tuple[list[str], list[str]]:
    """Return (texts, labels). Deterministic: the same seed gives the same corpus.

    Determinism matters more than it looks. A model retrained on a different corpus scores
    differently for reasons nobody can see, and "the accuracy moved" then has two possible causes
    instead of one.
    """
    rng = random.Random(seed)
    texts: list[str] = []
    labels: list[str] = []

    for intent, templates in TEMPLATES.items():
        for _ in range(per_intent):
            template = rng.choice(templates)
            item = rng.choice(ITEMS)
            if rng.random() < 0.25:
                item = " ".join(_typo(w, rng) if rng.random() < 0.5 else w for w in item.split())
            text = (template.replace("{i}", item)
                            .replace("{n}", rng.choice(PEOPLE))
                            .replace("{v}", rng.choice(VENDORS)))
            if rng.random() < 0.3:
                text = rng.choice(LEADERS) + text
            if rng.random() < 0.25:
                first, _, rest = text.partition(" ")
                text = rng.choice(PREFIXES) + first + (" " + rest if rest else "")
            text = text + rng.choice(SUFFIX_WORDS)
            texts.append(_roughen(text.strip(), rng))
            labels.append(intent)

    # The negative class gets the same weight as any one intent, so refusing is as learnable as
    # answering rather than a rare event the model can afford to ignore.
    for _ in range(per_intent):
        base = rng.choice(NEGATIVES)
        if rng.random() < 0.3:
            base = rng.choice(LEADERS) + base
        base = base + rng.choice(SUFFIX_WORDS)
        texts.append(_roughen(base.strip(), rng))
        labels.append(OUT_OF_SCOPE)

    return texts, labels
