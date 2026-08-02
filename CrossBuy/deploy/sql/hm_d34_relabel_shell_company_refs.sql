-- HM-D34: relabel entities MISLABELED onto empty shell companies (65 Global Corporation / 71 PrimeTech Solutions /
-- 79 "Test Branches Co.") back to the real operating company #1.
--
-- WHY THIS IS A LABEL FIX, NOT DATA SURGERY (proven by read-only forensic 2026-08-02):
--   * Companies 65/71/79 own 0 accounts, 0 fiscal years, 0 warehouses, 0 JEs, 0 invoices, 0 receipts, 0 stock moves.
--     With no accounts/fiscal years it is structurally impossible to post any document to them.
--   * 100% of the real footprint is company 1 (JEs=1569, SalesInvoices=284, Receipts=274, StockMovements=691, all
--     DISTINCT CompanyID = 1). Every terminal cash account and every branch default warehouse resolves to company 1.
--   * The only branch-linked company reference is PosOrders.CompanyId, pinned to 1 by the HM-D31 hardcode.
--   => Branches.CompanyID (and the identity rows below) point at empty test shells while reality is company 1.
--
-- SCOPE (approved 2026-08-02, "full identity" — keyed on shell membership so it is naturally idempotent and catches
-- any future stray seeded onto a shell; equals exactly branches {4,12,15,16,17}, employees {19..24}, customer 1025,
-- job application 18 at authoring time). The shell COMPANY records 65/67/71/72/79 are KEPT (no deletion) — 65 and 71
-- are parents of empty companies 67/72; deleting them is a separate cleanup phase after proving zero references.
-- Company 67's single empty branch is intentionally OUT of scope (it is on company 67, not on a shell).
--
-- Idempotent: the "<> 1" / "IN (65,71,79)" guards mean a second run affects 0 rows.

SET NOCOUNT ON;

UPDATE Branches        SET CompanyID    = 1 WHERE CompanyID    IN (65, 71, 79);
UPDATE Employee        SET EmpCompanyID = 1 WHERE EmpCompanyID IN (65, 71, 79);
UPDATE Customers       SET CompanyID    = 1 WHERE CompanyID    IN (65, 71, 79);
UPDATE JobApplications SET EmpCompanyID = 1 WHERE EmpCompanyID IN (65, 71, 79);

-- Verify: no movable entity references a shell company any more (each SELECT must return 0).
SELECT 'Branches_on_shell'  AS Check_, COUNT(*) AS Remaining FROM Branches        WHERE CompanyID    IN (65,71,79)
UNION ALL SELECT 'Employee_on_shell',  COUNT(*) FROM Employee        WHERE EmpCompanyID IN (65,71,79)
UNION ALL SELECT 'Customer_on_shell',  COUNT(*) FROM Customers       WHERE CompanyID    IN (65,71,79)
UNION ALL SELECT 'JobApp_on_shell',    COUNT(*) FROM JobApplications WHERE EmpCompanyID IN (65,71,79);
