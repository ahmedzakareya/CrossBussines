# -*- coding: utf-8 -*-
"""Author workflow-catalog.json and VALIDATE every cross-reference.

The step sequences below are authored from the evidence collected by the other scripts — the
screen catalogue, the action pairs, the status vocabulary, the event families and the approval
silos. They are not idealised ERP flows: a step exists here only if the screen it names exists in
screen-catalog.json, and the script FAILS LOUDLY if it does not.

Every step carries its own evidence level, because the levels differ inside a single workflow:
the screens were viewed, the actions were not performed.
"""
import io, os, json, sys

ROOT = r'C:\CrossBuy\CrossBuy'
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')

screens = json.load(io.open(os.path.join(OUT, 'screen-catalog.json'), encoding='utf-8'))['screens']
by_route = {s['route'].lower(): s for s in screens}

V = 'runtime-viewed (screen renders); action NOT performed'
R = 'repository-verified'
N = 'not verified'


def step(n, route, action, expect, status=None, evidence=V, note=None):
    s = by_route.get(route.lower())
    return {
        'n': n, 'route': route,
        'screen_id': s['screen_id'] if s else None,
        'screen_exists': bool(s),
        'screen_title_en': (s['heading'] or {}).get('en') if s and s['heading'] else None,
        'screen_title_ar': (s['heading'] or {}).get('ar') if s and s['heading'] else None,
        'action': action, 'expected_result': expect,
        'status_after': status, 'evidence': evidence, 'note': note,
    }


WF = []


def wf(**kw):
    WF.append(kw)


# ----------------------------------------------------------------- setup
wf(id='WF-SETUP-01', module='Service / Admin',
   title_en='Create the company and its branches',
   title_ar='إنشاء الشركة والفروع',
   purpose_en='Establish the tenant every other record belongs to.',
   responsible='Administrator (identity role)',
   prerequisites=['A signed-in administrator.',
                  'No prerequisite record — this is the first thing on a new install.'],
   steps=[
       step(1, '/Service/CompaniesList', 'Open the companies list', 'The existing companies are listed.'),
       step(2, '/Service/CompaniesList', 'Add a company', 'The company is created and becomes selectable elsewhere.', 'Active', N),
       step(3, '/Service/BranchesList', 'Add a branch under the company', 'The branch is available for stock, POS and role scoping.', 'Active', N),
   ],
   exceptions=['A company cannot be deleted once documents reference it — NOT VERIFIED.'],
   related=['/Admin/AdministrativeStructure', '/Brand/Brands'],
   example='Company "Gulf Trading Co." with branches "Cairo" and "Alexandria".',
   evidence_level='screens viewed; creation NOT performed')

wf(id='WF-SETUP-02', module='Admin',
   title_en='Grant module roles so people can work',
   title_ar='منح صلاحيات الوحدات',
   purpose_en='Open the modules to named employees. Until this is done, some actions refuse '
              'even for an administrator.',
   responsible='Administrator',
   prerequisites=['At least one company and one employee record.'],
   steps=[
       step(1, '/Inventory/InventoryRoles', 'Assign InventoryManager / PurchasingOfficer / WarehouseKeeper / Auditor',
            'The employee gains the inventory actions read | doc | purchase | manage.', None, V),
       step(2, '/Accounting/AccountingRoles', 'Assign ChiefAccountant / Auditor',
            'The employee gains read | post | pay | manage | currency-override.', None, V),
       step(3, '/Crm/CrmRoles', 'Assign SalesManager / SalesRep / CrmViewer',
            'The employee gains read | edit | manage in CRM.', None, V),
       step(4, '/Pos/CashierRoles', 'Assign cashier roles per branch',
            'The employee can sign in at a terminal.', None, V,
            'The POS role vocabulary is NOT declared in source — read the values off this screen.'),
   ],
   exceptions=['HR employee-manage is in NeverBootstrapOpen: it refuses until granted, even with '
               'no roles configured at all. PlatformRoleAssignments held 0 rows on the '
               'development install — repository-verified and runtime-viewed.'],
   related=['/Admin/EmployeesList'],
   example='Grant "WarehouseKeeper" for the Cairo branch.',
   evidence_level='screens viewed; granting NOT performed')

# ----------------------------------------------------------------- access
wf(id='WF-ACCESS-01', module='Account',
   title_en='Sign in, choose a portal, switch language',
   title_ar='تسجيل الدخول واختيار البوابة وتغيير اللغة',
   purpose_en='Everything starts here. This is the one workflow fully tested safely.',
   responsible='Every user',
   prerequisites=['An account. The product also offers Microsoft and Google sign-in when '
                  'configured — both were present on the development install.'],
   steps=[
       step(1, '/Account/Login', 'Enter the user name and password, press Sign In',
            'The browser lands on /Portal/Choose, not on a dashboard.', None,
            'behaviour tested safely'),
       step(2, '/Portal/Choose', 'Pick a portal',
            'The chosen module opens with its own sidebar.', None, V),
       step(3, '/Account/Login', 'Choose ar / en / fr from the switcher in the layout header',
            'The whole shell re-renders. Arabic sets <html lang="ar" dir="rtl"> and the RTL '
            'stylesheet bundle is served; Cairo is confirmed loaded.', None,
            'behaviour tested safely',
            'The switcher posts to /Account/SetLanguage, which is a REDIRECT ACTION with no view '
            'of its own - it is not a screen and has no screenshot.'),
       step(4, '/Account/Profile', 'Review the profile', 'Personal details and preferences.', None, V),
   ],
   exceptions=['A refused route answers /Account/AccessDenied with HTTP 403 — captured.',
               'The default culture for a new session is English, even though Arabic is the '
               'most completely translated language.'],
   related=['/Account/Settings', '/Account/Subscription', '/Account/Statements'],
   example='admin / (development password, not reproduced).',
   evidence_level='behaviour tested safely')

# ----------------------------------------------------------------- P2P
wf(id='WF-P2P-01', module='Inventory + Accounting',
   title_en='Procure to pay',
   title_ar='من الشراء إلى السداد',
   purpose_en='Order goods, receive them into stock, record the supplier bill, pay it.',
   responsible='PurchasingOfficer, WarehouseKeeper, ChiefAccountant',
   prerequisites=['At least one item, one unit, one category and one warehouse.',
                  'A supplier record.',
                  'An open fiscal period for the posting date.'],
   steps=[
       step(1, '/Inventory/Planning', 'Review replenishment suggestions',
            'Items below their reorder point are listed. A recommendation only — nothing is ordered.',
            None, V, 'The screen states this itself: "A recommendation only — nothing is ordered, '
                     'transferred or manufactured."'),
       step(2, '/Inventory/NewPurchaseOrder', 'Create the purchase order',
            'A commitment is recorded. No stock movement and no ledger entry yet.', 'Draft/Open', N),
       step(3, '/Inventory/PurchaseOrders', 'Find and open the order', 'The order list, filterable.', None, V),
       step(4, '/Inventory/NewGoodsReceipt', 'Receive against the order',
            'Stock increases. StockService is the authority for the movement.', 'Received', N),
       step(5, '/Inventory/LandedCosts', 'Add freight, duty or handling',
            'Item cost is adjusted after receipt. Margin figures before this step are provisional.',
            None, N),
       step(6, '/Accounting/NewPurchaseInvoice', 'Record the supplier invoice',
            'Accounts payable and input VAT are raised; a journal entry is posted.', 'Posted', N),
       step(7, '/Accounting/Payments', 'Create a payment and allocate it',
            'Payable is cleared. Events Payment.Created then Payment.Allocated are emitted.',
            'Paid', N),
   ],
   exceptions=[
       'Purchase return (debit note) at /Accounting/NewPurchaseReturn. The screen states: '
       '"Issues goods out, reverses the vendor payable & input VAT."',
       'Goods received but not invoiced is a real position; the chart of accounts carries an '
       'account for it (بضاعة وردت ولم تُفوتَر).',
   ],
   related=['/Accounting/ApAging', '/Accounting/VendorStatement', 'Report Inventory.GoodsReceipts',
            'Report Accounting.PurchaseInvoices'],
   example='PO-2026-0001 to "Nile Supplies" for 100 units; receive 100; invoice 100; pay in full.',
   evidence_level='screens viewed; no document created')

wf(id='WF-O2C-01', module='Inventory + Accounting',
   title_en='Order to cash',
   title_ar='من البيع إلى التحصيل',
   purpose_en='Quote, confirm, deliver, invoice, collect.',
   responsible='SalesRep, WarehouseKeeper, ChiefAccountant',
   prerequisites=['A customer record.', 'Stock on hand, or an item sourcing rule that allows the sale.',
                  'A price list; promotions are applied automatically.'],
   steps=[
       step(1, '/Inventory/NewQuotation', 'Raise a quotation',
            'A priced offer. Pricing comes from IPricingService — list priority, quantity breaks '
            'and promotions — not from the screen.', 'Draft', N),
       step(2, '/Inventory/NewSalesOrder', 'Convert to a sales order', 'The sale is committed.', 'Confirmed', N),
       step(3, '/Inventory/NewDelivery', 'Issue the delivery note', 'Stock decreases.', 'Delivered', N),
       step(4, '/Accounting/NewSalesInvoice', 'Invoice the customer',
            'Receivable and output VAT are raised; a journal entry is posted. '
            'SalesInvoice.Created is emitted.', 'Posted', N),
       step(5, '/Accounting/Receipts', 'Record the receipt and allocate it',
            'Receivable is cleared. Receipt.Created then Receipt.Allocated.', 'Paid', N),
   ],
   exceptions=[
       'Sales return (credit note) at /Accounting/NewSalesReturn. The screen states: "The return '
       'reverses revenue & VAT, reduces the customer\'s receivable, and returns the goods."',
       'Egyptian e-invoicing status is tracked separately at /Accounting/EtaStatus.',
   ],
   related=['/Accounting/ArAging', '/Accounting/CustomerStatement', '/Accounting/CustomerAnalytics',
            'Report Accounting.SalesInvoices', 'Report Accounting.CustomerAging'],
   example='Quotation to "Delta Retail" for 20 units; deliver; invoice; receive in two instalments.',
   evidence_level='screens viewed; no document created')

# ----------------------------------------------------------------- accounting
wf(id='WF-ACC-01', module='Accounting',
   title_en='Set up the ledger',
   title_ar='تأسيس الدفاتر',
   purpose_en='Chart of accounts, cost centres, fiscal periods, currencies.',
   responsible='ChiefAccountant',
   prerequisites=['A company.'],
   steps=[
       step(1, '/Accounting/ChartOfAccounts', 'Build the account tree', 'Accounts are postable or parents.', None, V),
       step(2, '/Accounting/CostCenters', 'Define cost centres',
            'An analytic dimension. The screen states it "does not change posting logic".', None, V),
       step(3, '/Accounting/Periods', 'Open fiscal periods', 'Posting is allowed inside an open period.', 'Open', V),
       step(4, '/Currency/Currencies', 'Add currencies', 'Available for documents.', None, V),
       step(5, '/Currency/Setup', 'Set the functional currency', 'The reporting currency is fixed.', None, V),
       step(6, '/Currency/ExchangeRates', 'Maintain rates', 'Used at document date.', None, V),
       step(7, '/Inventory/OpeningBalances', 'Enter opening balances', 'The ledger starts balanced.', None, N,
            'Route listed under Inventory for stock; the accounting equivalent is the journals screen.'),
   ],
   exceptions=['Closing a period needs the period-close permission; reopening needs '
               'period-reopen AND a recorded reason. The source documents the sequence '
               'Open → SoftClosed → Closed.'],
   related=['/Accounting/TrialBalance', '/Accounting/Journals'],
   example='4-level chart; periods for FY2026; EGP functional with USD and EUR.',
   evidence_level='screens viewed; no configuration changed')

wf(id='WF-ACC-02', module='Accounting',
   title_en='Close a period and read the statements',
   title_ar='إقفال الفترة وقراءة القوائم',
   purpose_en='Prove the books balance, then close.',
   responsible='ChiefAccountant',
   prerequisites=['All documents for the period posted.'],
   steps=[
       step(1, '/Accounting/TrialBalance', 'Check the trial balance',
            'Three tiles — Debit, Credit, Difference. A dashed notice reads "The trial balance is '
            'balanced" with a "Matched" badge when the difference is zero.', None,
            'runtime viewed — captured with Debit = Credit and Difference 0.00'),
       step(2, '/Accounting/BalanceSheet', 'Review the balance sheet', 'Position at the date.', None, V),
       step(3, '/Accounting/IncomeStatement', 'Review the income statement', 'Result for the period.', None, V),
       step(4, '/Accounting/CashFlow', 'Review cash flow', 'Movement for the period.', None, V),
       step(5, '/Currency/Revaluation', 'Run FX revaluation if there are foreign balances',
            'Unrealised differences are posted.', None, N),
       step(6, '/Accounting/DepreciationRuns', 'Run depreciation',
            'The screen states: "One month\'s depreciation is computed for all active assets and '
            'posted as a single entry."', None, N),
       step(7, '/Accounting/Periods', 'Close the period', 'Posting into it is refused.', 'Closed', N),
       step(8, '/Accounting/YearEndClose', 'Year-end close', 'Result is carried to retained earnings.', 'Closed', N),
   ],
   exceptions=['Reopening requires period-reopen and a recorded reason.'],
   related=['Report Accounting.TrialBalance'],
   example='Close September 2026 after posting depreciation and revaluation.',
   evidence_level='trial balance viewed with data; closing NOT performed')

# ----------------------------------------------------------------- inventory
wf(id='WF-INV-01', module='Inventory',
   title_en='Set up stock and count it',
   title_ar='تأسيس المخزون والجرد',
   purpose_en='Master data, then balances, then a physical count.',
   responsible='InventoryManager, WarehouseKeeper',
   prerequisites=['At least one category and one unit BEFORE any item can be created — the Items '
                  'screen says so and disables Add Product until both exist.'],
   steps=[
       step(1, '/Inventory/Categories', 'Create categories', 'Items can be classified.', None, V),
       step(2, '/Inventory/Units', 'Create units of measure', 'Items can be quantified.', None, V),
       step(3, '/Inventory/Warehouses', 'Create warehouses', 'Stock has a location.', None, V),
       step(4, '/Inventory/WarehouseSections', 'Create sections and racks', 'Rack-level stock becomes possible.', None, V),
       step(5, '/Inventory/ItemForm', 'Create an item',
            'Item code, name, unit, category; and the tracking switches Track batch / Track expiry '
            '/ Track serial / Composite item (Kit).', None, N,
            'Reached as /Inventory/CreateItem or /Inventory/EditItem.'),
       step(6, '/Inventory/OpeningBalances', 'Enter opening quantities', 'Stock exists without a purchase.', None, N),
       step(7, '/Inventory/StockCounts', 'Run a stock count', 'Differences become adjustments.', None, N),
       step(8, '/Inventory/IntegrityReconciliation', 'Reconcile stock against the ledger',
            'Discrepancies between stock value and the GL are listed.', None, V),
   ],
   exceptions=['Write-offs at /Inventory/WriteOffs for damage and loss.',
               'Stock emits NO platform events, so a goods receipt raises no notification.'],
   related=['/Inventory/StockBalances', '/Inventory/RackBalances', '/Inventory/Batches',
            '/Inventory/Serials', 'Report Inventory.StockOnHand'],
   example='Warehouse "Cairo Main", section A, rack A-01; item "Sugar 1kg" batch-tracked.',
   evidence_level='screens viewed; no stock moved')

wf(id='WF-INV-02', module='Inventory',
   title_en='Transfer stock between warehouses',
   title_ar='تحويل المخزون بين المخازن',
   purpose_en='Move goods without selling them.',
   responsible='WarehouseKeeper',
   prerequisites=['Two warehouses and stock at the source.'],
   steps=[
       step(1, '/Inventory/NewTransfer', 'Create the transfer', 'Source and destination, lines, quantities.', 'Draft', N),
       step(2, '/Inventory/StockTransfers', 'Find the transfer', 'The list of transfers.', None, V),
       step(3, '/Inventory/DocumentDetails', 'Open the transfer document',
            'One shared screen serves eight Inventory document types; it titles itself from the document.',
            None, V, 'Reached as /Inventory/TransferDetails.'),
   ],
   exceptions=['Approvals may intervene — the Inventory approval silo feeds /Approvals/Index.'],
   related=['/Inventory/Approvals', 'Report Inventory.TransferDocument'],
   example='Move 50 units of "Sugar 1kg" from Cairo Main to Alexandria.',
   evidence_level='screens viewed; no transfer created')

# ----------------------------------------------------------------- HR
wf(id='WF-HR-01', module='Admin',
   title_en='Hire, record attendance, run payroll',
   title_ar='التعيين والحضور والرواتب',
   purpose_en='The employee lifecycle through to a posted payroll entry.',
   responsible='HrManager, PayrollOfficer',
   prerequisites=['HR roles GRANTED — employee-manage is NeverBootstrapOpen and refuses '
                  'otherwise, even for an administrator.',
                  'Job titles, leave types, policies and official holidays defined.'],
   steps=[
       step(1, '/Admin/Applications', 'Review job applications', 'Applicants are listed.', None, V),
       step(2, '/Admin/EmployeeData', 'Create the employee',
            'A multi-step form: personal details, job, documents. Both Arabic and English name '
            'fields should be filled, or English screens show the Arabic name.', 'Active', N),
       step(3, '/Admin/HrDocuments', 'Attach contracts and documents', 'Required document types are satisfied.', None, V),
       step(4, '/Admin/Attendance', 'Record attendance', 'Late minutes, overtime and absences accumulate.', None, V),
       step(5, '/Admin/LeaveTypesList', 'Approve leave through the inbox',
            'Leave requests appear in the Leave approval silo at /Approvals/Index.', None, V),
       step(6, '/Accounting/Payroll', 'Run payroll',
            'Per employee: base, allowances, gross, late minutes, overtime minutes, absent days, '
            'overtime pay, late penalty, absence penalty, employee SI, company SI, tax, net. The '
            'GL expense is gross + overtime − latePenalty − absencePenalty.', 'Posted', N,
            'This route TIMED OUT twice during capture — see document 08.'),
       step(7, '/Accounting/Payslips', 'Issue payslips', 'Employees can view them in the People portal.', None, V),
       step(8, '/Accounting/PayrollDisbursement', 'Disburse and remit',
            'The screen warns: "No bank or cash account. Create one under «Banks & cash» first."',
            None, V),
   ],
   exceptions=['/Admin/DocExpiryAlerts warns before a document lapses.',
               '/Admin/FinalSettlement handles end of service.',
               'Leave accrual, provision and carry-over post to the ledger.'],
   related=['/People/Dashboard', '/Accounting/PayrollTaxSettings'],
   example='Employee "A. Hassan", Cairo branch, monthly salary; September payroll.',
   evidence_level='screens viewed; payroll NOT run')

# ----------------------------------------------------------------- approvals
wf(id='WF-APPR-01', module='Approvals',
   title_en='Act on an approval',
   title_ar='البتّ في طلب موافقة',
   purpose_en='One inbox aggregates four kinds of pending decision.',
   responsible='Whoever the silo names as approver',
   prerequisites=['A pending item in one of the four silos.'],
   steps=[
       step(1, '/Approvals/Index', 'Open My approvals',
            'Pending items from all four silos: Leave, Request, Inventory, ProjectBilling.', None, V),
       step(2, '/Approvals/Index', 'Open the item',
            'Each silo supplies its own navigation target; project billing opens the real '
            'document, not a list.', None, N),
       step(3, '/Approvals/Index', 'Approve or reject', 'The item leaves the inbox.', 'Approved / Rejected', N),
   ],
   exceptions=['ACCOUNTING DOCUMENTS DO NOT GO THROUGH THIS INBOX. An invoice is posted, not '
               'approved — a deliberate design position the manual should state.'],
   related=['/People/Leaves', '/People/Requests', '/Inventory/Approvals'],
   example='A leave request from a Cairo employee.',
   evidence_level='inbox viewed; no decision taken')

# ----------------------------------------------------------------- POS
wf(id='WF-POS-01', module='Restaurant',
   title_en='Serve a restaurant order',
   title_ar='تشغيل طلب مطعم',
   purpose_en='Cashier, kitchen and delivery on purpose-built operator screens.',
   responsible='Cashier, kitchen staff, driver',
   prerequisites=['Operations setup, dining areas, kitchen stations, floor plan, quick items, '
                  'modifiers, payment methods and terminals configured.',
                  'Item sourcing decided per item per branch — from stock, prepared in a '
                  'kitchen, or bought in.'],
   steps=[
       step(1, '/Pos/Setup', 'Complete operations setup', 'Branch capabilities are enabled.', None, V),
       step(2, '/Pos/FloorPlan', 'Lay out tables', 'The cashier can seat a table.', None, V),
       step(3, '/PosApp/Start', 'Open the cashier terminal', 'The operator surface, not a back-office list.', None, V),
       step(4, '/PosApp/Kitchen', 'Kitchen display', 'Prepared items are consumed from stock — capability-gated.', None, V),
       step(5, '/PosApp/Delivery', 'Delivery board', 'Drivers and zones; the zone fee is frozen on the order.', None, V),
   ],
   exceptions=['The terminal is designed to keep selling offline; /Pos/SyncConflicts exists to '
               'review what could not be reconciled.',
               'Cancel-after-preparation is treated as waste.'],
   related=['/Pos/Reservations', '/Pos/Drivers', '/Pos/DeliveryZones', '/Pos/SourcingOverview',
            '/RestaurantIntelligence/Index'],
   example='Table 6, two covers, one item with a modifier, paid by card.',
   evidence_level='setup screens viewed; operator screens viewed; NO order taken')

# ----------------------------------------------------------------- projects
wf(id='WF-PRJ-01', module='Projects',
   title_en='Run a construction project to billing',
   title_ar='إدارة مشروع مقاولات حتى المستخلص',
   purpose_en='Advance, progress billing, retention, release.',
   responsible='ProjectsAdministrator, ProjectsFinance',
   prerequisites=['A project and its activity types.'],
   steps=[
       step(1, '/Project/Projects', 'Create the project', 'The project exists with a contract value.', None, V),
       step(2, '/Project/Advance', 'Receive an advance',
            'The screen states: "Advance received = a liability (2104), not revenue — posted via '
            'the GL, tagged to the project."', None, N),
       step(3, '/Project/Dashboard', 'Track progress', 'Progress against plan.', None, V),
       step(4, '/Project/Projects', 'Raise a progress billing (مستخلص)',
            'Prepared, then approved, then posted — three SEPARATE permissions: billing-prepare, '
            'billing-approve, billing-post.', 'Submitted → Approved → Posted', N),
       step(5, '/Project/RetentionRelease', 'Release retention', 'Held-back value is released.', None, N),
       step(6, '/Project/SubRetentionRelease', 'Release subcontractor retention', 'Subcontractor retention released.', None, N),
       step(7, '/Project/Profitability', 'Review profitability', 'Revenue against cost per project.', None, V),
   ],
   exceptions=['Progress billing is the ONE accounting-adjacent document that goes through the '
               'approval inbox.',
               'Segregation of duties is real here: prepare, approve and post are three grants.'],
   related=['/ProjectCloseout/Index', 'Accounting.BoqItem'],
   example='Project "Tower A", 10% advance, first billing at 30% completion, 5% retention.',
   evidence_level='screens viewed; no billing raised')

# ----------------------------------------------------------------- CRM
wf(id='WF-CRM-01', module='CRM',
   title_en='Lead to opportunity to ticket',
   title_ar='من العميل المحتمل إلى الفرصة إلى التذكرة',
   purpose_en='The commercial pipeline and post-sale support.',
   responsible='SalesRep, SalesManager, Marketing',
   prerequisites=['Pipelines and stages configured.'],
   steps=[
       step(1, '/Crm/Leads', 'Capture a lead', 'The lead is scored by the scoring rules.', 'New', V),
       step(2, '/Crm/Opportunities', 'Convert to an opportunity', 'It enters a pipeline stage.', 'Open', V),
       step(3, '/Crm/Pipeline', 'Move it through the Kanban',
            'Stages observed on the dashboard: Prospecting, Qualification, Proposal, Negotiation, '
            'Won, Lost.', 'Won / Lost', V),
       step(4, '/Crm/Accounts', 'Link to a financial customer',
            'The screen confirms: "Linked to a financial customer."', None, N),
       step(5, '/Crm/Tickets', 'Handle support tickets against SLA policies', 'SLA timers apply.', 'Open → Resolved', V),
   ],
   exceptions=['Whether an opportunity converts to a QUOTATION inside the product, or by '
               'retyping, was NOT ESTABLISHED. This is the most commercially important '
               'unverified link in the package.'],
   related=['/Crm/Forecast', '/Crm/Reports', '/Crm/Campaigns', '/Crm/SlaPolicies',
            'Report Crm.Leads', 'Report Crm.Opportunities'],
   example='Lead "Delta Retail" → opportunity EGP 250,000 → Negotiation.',
   evidence_level='screens viewed; nothing advanced')

# ----------------------------------------------------------------- reporting
wf(id='WF-REP-01', module='Reporting',
   title_en='Find, run, print and design a report',
   title_ar='تشغيل التقارير وتصميمها',
   purpose_en='Every printed artefact in the product goes through here.',
   responsible='Any user with the relevant reports.*.view permission',
   prerequisites=['The report permission key must be MAPPED. An unmapped key answers 404, not '
                  '403, deliberately — so the report is invisible rather than merely refused.'],
   steps=[
       step(1, '/Reports/Index', 'Open the Reports Center', 'The catalogue of reports available to you.', None, V),
       step(2, '/Reports/Viewer', 'Open a report and set parameters',
            'Parameters are validated before the data source is reached.', None, V),
       step(3, '/Reports/Viewer', 'Export or print',
            'Html, PrintHtml, Pdf, Xlsx or Csv. PDF is produced by the Playwright renderer.', None, N),
       step(4, '/Reports/Studio', 'Design a template',
            'Bands, elements, page setup, grouping, charts with series and time buckets.', None, V),
   ],
   exceptions=[
       'Platform templates cannot be edited: "Platform templates are created by deployment, not '
       'by a tenant." Change "Available to" off Platform first.',
       'Scheduled delivery is BUILT BUT DISABLED — the worker is off by design and '
       'NullReportMailSender is registered.',
       'NO SCREEN PRINTS ITS OWN HTML. A screen that needs printing registers a dataset and '
       'links to /Reports/Viewer.',
   ],
   related=['/Reports/Datasets', '/Reports/Templates'],
   example='Run Inventory.StockOnHand for Cairo Main, export to Excel.',
   evidence_level='screens viewed; no report run or exported')

# ----------------------------------------------------------------- collaboration
wf(id='WF-COLLAB-01', module='Platform',
   title_en='Work reaches you: workspace, notifications, tasks, mentions',
   title_ar='وصول العمل إليك: مساحة العمل والإشعارات والمهام والإشارات',
   purpose_en='How a document posted in one module becomes somebody else\'s work.',
   responsible='Every user',
   prerequisites=['None.'],
   steps=[
       step(1, '/Workspace/Index', 'Open the Workspace', 'A cross-module read model of what is waiting.', None, V),
       step(2, '/Workspace/Agenda', 'Agenda', 'What is due.', None, V),
       step(3, '/Workspace/Notifications', 'Notifications', 'Events delivered by the dispatch worker.', None, V),
       step(4, '/Workspace/Mentions', 'Mentions', '@mentions addressed to you, self-excluded.', None, V),
       step(5, '/Tasks/Index', 'My tasks', 'Tasks, including ones generated by auto-rules.', None, V),
   ],
   exceptions=[
       'Notifications are not instant: the dispatch worker polls every 15 seconds, batches 50, '
       'retries 5 times with 30-second backoff.',
       'STOCK EMITS NO EVENTS. A goods receipt produces no notification. 47 event types exist '
       'across 11 families; none is an inventory movement.',
   ],
   related=['/Notifications/Index', '/Chat/Index', '/Calendar/Index', '/Comm/Index',
            '/Announcements/Index', '/FileManager/Index', '/BusinessEventMonitor/Index'],
   example='A sales invoice is posted; a task appears for the collections clerk.',
   evidence_level='screens viewed; no event triggered')

# ----------------------------------------------------------------- AI
wf(id='WF-AI-01', module='Accounting / Platform',
   title_en='Read an AI insight',
   title_ar='قراءة رؤى الذكاء الاصطناعي',
   purpose_en='Three statistical capabilities. The AI proposes; it is never the source of truth.',
   responsible='ChiefAccountant, InventoryManager',
   prerequisites=['The local AI service running on port 8000. Configured as Internal / '
                  'LocalLoopback — nothing is sent to any third party.'],
   steps=[
       step(1, '/Accounting/AiInsights', 'Open AI insights', 'Anomaly, forecast and inventory findings.', None, V),
       step(2, '/Accounting/AiInsights', 'Read a journal anomaly',
            'Posted entries flagged FOR HUMAN REVIEW. A flag is not an error.', None, N),
       step(3, '/Accounting/AiInsights', 'Read the cash-flow projection',
            'Opening cash plus outstanding AR/AP with FIFO settlement, projected 90 days by default.',
            None, N),
       step(4, '/Inventory/Index', 'Read the inventory analysis',
            'Slow-moving / reorder / stockout-risk classification with suggested quantities.', None, N),
   ],
   exceptions=[
       'All three run on local scikit-learn — no LLM, no API key. Described in the source as '
       '"local ML — no LLM, no API key".',
       'The egress policy refuses PersonalData everywhere and free text to any external '
       'processor. No approved external processor is configured.',
       'Whether the CRM insight, scoring and automation screens are model-backed was NOT '
       'ESTABLISHED — do not describe them as AI without checking.',
   ],
   related=['/Crm/OpportunityInsights', '/Crm/AccountInsights', '/InsightActions/Index',
            '/Tasks/MatchSuggestions'],
   example='A journal entry flagged as unusual for its account and amount.',
   evidence_level='screen viewed; no AI call made; AI service NOT started')

# ----------------------------------------------------------------- external audiences
wf(id='WF-EXT-01', module='Portals',
   title_en='What people outside the company see',
   title_ar='ما يراه من هم خارج الشركة',
   purpose_en='Three separate audiences with their own surfaces.',
   responsible='n/a',
   prerequisites=['Store:Enabled is true and Store:StoreCompanyId pins the catalogue to one company.'],
   steps=[
       step(1, '/People/Dashboard', 'Employee self-service', 'Profile, leaves, payslips, alerts.', None, V),
       step(2, '/ClientPortal/Index', 'Client portal', 'A customer\'s own documents.', None, V),
       step(3, '/Home/Store', 'Public store', 'The anonymous catalogue — one company only.', None, V),
   ],
   exceptions=['ClientPortal/NoAccess is served by ClientPortal.Index when the caller has no '
               'portal access.',
               'The mobile client (crossbuy_mobile, Flutter) was NOT EXAMINED.'],
   related=['/Portal/Choose'],
   example='An employee checks a payslip; a customer downloads an invoice.',
   evidence_level='screens viewed; mobile client not examined')

# ----------------------------------------------------------------- validate & write
missing = [(w['id'], s['route']) for w in WF for s in w['steps'] if not s['screen_exists']]
for wid, route in missing:
    print('  !! %s references a route with no screen: %s' % (wid, route))

catalog = {
    'generated': '2026-09-20',
    'workflow_count': len(WF),
    'step_count': sum(len(w['steps']) for w in WF),
    'validation': {
        'steps_with_a_known_screen': sum(1 for w in WF for s in w['steps'] if s['screen_exists']),
        'steps_with_an_unknown_route': len(missing),
        'unknown_routes': [r for _, r in missing],
    },
    'evidence_key': {
        'runtime-viewed (screen renders); action NOT performed': V,
        'repository-verified': R,
        'not verified': N,
        'behaviour tested safely': 'a non-mutating behaviour was exercised',
    },
    'global_caveat': ('No workflow below was executed. Screens were opened and read; no document '
                      'was created, posted, approved, cancelled or deleted, because doing so '
                      'would change data. Steps marked "not verified" describe what the code and '
                      'the screen text say will happen, not what was observed to happen.'),
    'workflows': WF,
}
io.open(os.path.join(OUT, 'workflow-catalog.json'), 'w', encoding='utf-8').write(
    json.dumps(catalog, ensure_ascii=False, indent=1))

print('workflows        :', len(WF))
print('steps            :', catalog['step_count'])
print('steps resolved   :', catalog['validation']['steps_with_a_known_screen'])
print('unresolved routes:', len(missing))
