# 10 — HR UX

HR covers employee self-service and administration in `Views/People`, `Views/Admin`, `Views/Portal`, and `_LayoutPeople.cshtml`. Personal data requires careful disclosure, role-aware actions, and accessible forms.

## Contract

Employee screens organize profile, attendance, leave, payslips, requests, training, and appraisals around “my work.” Administrative screens provide scoped search, organizational hierarchy, policy/setup, approval queues, and auditable record detail. Evidence includes `Views/People/Dashboard.cshtml`, `Leaves.cshtml`, `Requests.cshtml`, and `Views/Admin/AdministrativeStructure.cshtml` using ApexCharts, flatpickr, Select2, and jsTree.

Permissions distinguish self, manager, HR, payroll, and administrator scopes. Redact sensitive fields rather than merely disabling edit. Empty/no entitlement, unavailable payroll period, denied personnel record, validation error, and successful submission must be distinct. Confirm consequential approvals/rejections and show status/history.

Mobile prioritizes balances, requests and approvals; large administration grids remain desktop/tablet optimized with an honest mobile fallback. RTL mirrors structure but not identifiers/dates where locale conventions require otherwise. Charts have text equivalents; trees and dialogs are keyboard operable. Defer heavy dashboard charts and page large directories. Future evolution should separate self-service and administration navigation while preserving shared identity and authorization.
