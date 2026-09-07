-- =============================================================================================
-- CrossBusiness Construction & Contracting — C1 CONTRACT MAPPING MEASUREMENT
--
-- READ-ONLY. This script contains SELECT statements ONLY. It creates nothing, alters nothing and
-- writes nothing. It exists so the ContractId ownership decision (D-01) is taken against MEASURED
-- rows rather than an assumption, as the C1 brief requires ("measure existing rows … stop if
-- mapping cannot be deterministic").
--
-- IT WAS NOT EXECUTED BY THE FOURTH TAB. No SQL was run against CrossBuyDB2 or any other database
-- in this increment. The owner runs it and records the output in
-- docs/construction/Stage-Construction-Contract-Mapping-Measurement.md §4.
--
-- Safety: every statement is a SELECT. There is no INSERT/UPDATE/DELETE/ALTER/CREATE/DROP/MERGE in
-- this file — grep it before running if you want to confirm that yourself.
-- =============================================================================================
SET NOCOUNT ON;

-- ---------------------------------------------------------------------------------------------
-- M1. Population sizes. Establishes the scale of any future backfill.
-- ---------------------------------------------------------------------------------------------
SELECT 'M1_population' AS Measurement,
       (SELECT COUNT(*) FROM dbo.Projects)                                   AS Projects,
       (SELECT COUNT(*) FROM dbo.Projects WHERE CustomerId IS NOT NULL)      AS ProjectsWithCustomer,
       (SELECT COUNT(*) FROM dbo.Projects WHERE ContractValue IS NOT NULL)   AS ProjectsWithContractValue,
       (SELECT COUNT(*) FROM dbo.BoqItems)                                   AS BoqItems,
       (SELECT COUNT(*) FROM dbo.ProjectProgresses)                          AS Measurements,
       (SELECT COUNT(*) FROM dbo.ProgressBillings)                           AS ClientCertificates,
       (SELECT COUNT(*) FROM dbo.ProgressBillings WHERE Status = 'Posted')    AS ClientCertificatesPosted,
       (SELECT COUNT(*) FROM dbo.Subcontracts)                               AS Subcontracts,
       (SELECT COUNT(*) FROM dbo.SubcontractBillings)                        AS SubcontractCertificates,
       (SELECT COUNT(*) FROM dbo.SubcontractBillings WHERE Status = 'Posted') AS SubcontractCertificatesPosted,
       (SELECT COUNT(*) FROM dbo.VariationOrders)                            AS Variations,
       (SELECT COUNT(*) FROM dbo.VariationOrders WHERE Status = 'Approved')   AS VariationsApproved;

-- ---------------------------------------------------------------------------------------------
-- M2. Contract-bearing projects per company. One row per company.
--     A project is "contract-bearing" when it carries any of the five implicit contract terms.
-- ---------------------------------------------------------------------------------------------
SELECT 'M2_contract_bearing_by_company' AS Measurement,
       CompanyID,
       COUNT(*) AS Projects,
       SUM(CASE WHEN CustomerId IS NOT NULL OR ContractValue IS NOT NULL
                  OR AdvancePercent IS NOT NULL OR RetentionPercent IS NOT NULL
                THEN 1 ELSE 0 END) AS ContractBearingProjects
FROM dbo.Projects
GROUP BY CompanyID
ORDER BY CompanyID;

-- ---------------------------------------------------------------------------------------------
-- M3. THE DETERMINISM TEST — projects whose implied contract mapping is AMBIGUOUS.
--
-- Mapping rule proposed by C1: each contract-bearing project maps to EXACTLY ONE ClientContract
-- (its current implicit terms), marked Primary. That rule is deterministic unless a project shows
-- evidence of more than one commercial counterparty already.
--
-- AMBIGUOUS = a project whose posted client certificates reference more than one CustomerId, or
-- whose certificates reference a customer other than the project's own CustomerId.
--
-- EXPECTED RESULT: zero rows. If ANY row returns, STOP — the backfill is not deterministic and the
-- brief requires halting before schema change.
-- ---------------------------------------------------------------------------------------------
SELECT 'M3_ambiguous_mapping' AS Measurement,
       p.CompanyID,
       p.ID                                   AS ProjectId,
       p.Code                                 AS ProjectCode,
       p.CustomerId                            AS ProjectCustomerId,
       COUNT(DISTINCT b.CustomerId)            AS DistinctCertificateCustomers,
       MIN(b.CustomerId)                       AS SampleCertificateCustomerA,
       MAX(b.CustomerId)                       AS SampleCertificateCustomerB,
       COUNT(*)                                AS CertificateCount
FROM dbo.Projects p
JOIN dbo.ProgressBillings b
       ON b.ProjectId = p.ID
      AND b.CompanyID = p.CompanyID
WHERE b.CustomerId IS NOT NULL
GROUP BY p.CompanyID, p.ID, p.Code, p.CustomerId
HAVING COUNT(DISTINCT b.CustomerId) > 1
    OR (p.CustomerId IS NOT NULL AND MIN(b.CustomerId) <> p.CustomerId)
    OR (p.CustomerId IS NULL);

-- ---------------------------------------------------------------------------------------------
-- M4. Orphan and cross-company integrity checks on the rows a backfill would touch.
--     BoqItems has NO foreign key to Projects (deploy/sql/boq.sql), so orphans are possible.
--     EXPECTED RESULT: zero rows in every branch.
-- ---------------------------------------------------------------------------------------------
SELECT 'M4_orphan_boq_items' AS Measurement, b.ID AS BoqItemId, b.CompanyID, b.ProjectId
FROM dbo.BoqItems b
WHERE NOT EXISTS (SELECT 1 FROM dbo.Projects p WHERE p.ID = b.ProjectId AND p.CompanyID = b.CompanyID);

SELECT 'M4_orphan_boq_parents' AS Measurement, b.ID AS BoqItemId, b.ParentId
FROM dbo.BoqItems b
WHERE b.ParentId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.BoqItems p
                  WHERE p.ID = b.ParentId AND p.ProjectId = b.ProjectId AND p.CompanyID = b.CompanyID);

SELECT 'M4_certificate_lines_pointing_at_missing_boq' AS Measurement,
       l.ID AS ProgressBillingLineId, l.BillingId, l.BoqItemId
FROM dbo.ProgressBillingLines l
JOIN dbo.ProgressBillings h ON h.ID = l.BillingId
WHERE l.BoqItemId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.BoqItems b
                  WHERE b.ID = l.BoqItemId AND b.CompanyID = h.CompanyID AND b.ProjectId = h.ProjectId);

SELECT 'M4_progress_lines_pointing_at_missing_boq' AS Measurement,
       l.ID AS ProjectProgressLineId, l.ProgressId, l.BoqItemId
FROM dbo.ProjectProgressLines l
JOIN dbo.ProjectProgresses h ON h.ID = l.ProgressId
WHERE l.BoqItemId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.BoqItems b
                  WHERE b.ID = l.BoqItemId AND b.CompanyID = h.CompanyID AND b.ProjectId = h.ProjectId);

SELECT 'M4_material_issue_lines_pointing_at_missing_boq' AS Measurement,
       l.ID AS ProjectMaterialIssueLineId, l.IssueId, l.BoqItemId
FROM dbo.ProjectMaterialIssueLines l
JOIN dbo.ProjectMaterialIssues h ON h.ID = l.IssueId
WHERE l.BoqItemId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.BoqItems b
                  WHERE b.ID = l.BoqItemId AND b.CompanyID = h.CompanyID AND b.ProjectId = h.ProjectId);

-- ---------------------------------------------------------------------------------------------
-- M5. CR-01 EXPOSURE — BOQ items that are already referenced by a commercial or stock document.
--     These are the rows that must never be physically deleted, and the rows whose identity a
--     delete-and-reinsert would have already broken.
-- ---------------------------------------------------------------------------------------------
SELECT 'M5_referenced_boq_items' AS Measurement,
       b.CompanyID,
       b.ProjectId,
       COUNT(DISTINCT b.ID) AS BoqItems,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.ProgressBillingLines l
                             JOIN dbo.ProgressBillings h ON h.ID = l.BillingId
                             WHERE l.BoqItemId = b.ID AND h.Status = 'Posted') THEN 1 ELSE 0 END)
            AS ReferencedByPostedCertificate,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.ProjectProgressLines l
                             WHERE l.BoqItemId = b.ID) THEN 1 ELSE 0 END)
            AS ReferencedByMeasurement,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.ProjectMaterialIssueLines l
                             JOIN dbo.ProjectMaterialIssues h ON h.ID = l.IssueId
                             WHERE l.BoqItemId = b.ID AND h.Status = 'Posted') THEN 1 ELSE 0 END)
            AS ReferencedByPostedIssue,
       SUM(CASE WHEN b.VariationOrderId IS NOT NULL THEN 1 ELSE 0 END)
            AS CreatedByVariation
FROM dbo.BoqItems b
GROUP BY b.CompanyID, b.ProjectId
ORDER BY b.CompanyID, b.ProjectId;

-- ---------------------------------------------------------------------------------------------
-- M6. CR-01 SMOKING GUN — certificate lines whose BoqItemId no longer resolves, or whose BOQ item
--     was created AFTER the certificate that references it. Either is a footprint of the
--     delete-and-reinsert behaviour having already run on live data.
-- ---------------------------------------------------------------------------------------------
SELECT 'M6_certificate_line_identity_drift' AS Measurement,
       h.CompanyID, h.ProjectId, h.ID AS BillingId, h.BillingNo, h.Status,
       l.ID AS LineId, l.BoqItemId, l.PeriodValue,
       CASE WHEN b.ID IS NULL THEN 'boq_item_missing'
            WHEN b.CreatedAt > h.CreatedAt THEN 'boq_item_newer_than_certificate'
            ELSE 'ok' END AS Finding
FROM dbo.ProgressBillings h
JOIN dbo.ProgressBillingLines l ON l.BillingId = h.ID
LEFT JOIN dbo.BoqItems b ON b.ID = l.BoqItemId
WHERE l.BoqItemId IS NOT NULL
  AND (b.ID IS NULL OR (b.CreatedAt IS NOT NULL AND h.CreatedAt IS NOT NULL AND b.CreatedAt > h.CreatedAt));

-- ---------------------------------------------------------------------------------------------
-- M7. CR-02 EXPOSURE — subcontracts whose posted certification already exceeds the contract value.
--     There are no certificate lines and no cap today, so this is the direct measure of the defect.
-- ---------------------------------------------------------------------------------------------
SELECT 'M7_subcontract_over_certification' AS Measurement,
       s.CompanyID, s.ProjectId, s.ID AS SubcontractId, s.VendorId,
       s.ContractValue,
       SUM(CASE WHEN b.Status = 'Posted' THEN b.GrossWork ELSE 0 END) AS PostedGrossWork,
       SUM(CASE WHEN b.Status = 'Posted' THEN b.GrossWork ELSE 0 END) - ISNULL(s.ContractValue, 0) AS ExcessOverContractValue,
       COUNT(b.ID) AS Certificates
FROM dbo.Subcontracts s
LEFT JOIN dbo.SubcontractBillings b ON b.SubcontractId = s.ID AND b.CompanyID = s.CompanyID
GROUP BY s.CompanyID, s.ProjectId, s.ID, s.VendorId, s.ContractValue
HAVING s.ContractValue IS NULL
    OR SUM(CASE WHEN b.Status = 'Posted' THEN b.GrossWork ELSE 0 END) > s.ContractValue
ORDER BY ExcessOverContractValue DESC;

-- ---------------------------------------------------------------------------------------------
-- M8. CR-03 EXPOSURE — approved variations that adjusted an existing BOQ item in place, and
--     whether a certificate had already been POSTED against that item before the adjustment.
--     Each such row is a posted certificate whose commercial basis was changed afterwards.
-- ---------------------------------------------------------------------------------------------
SELECT 'M8_in_place_rate_overwrites' AS Measurement,
       v.CompanyID, v.ProjectId, v.ID AS VariationOrderId, v.VoNo, v.ApprovedAt,
       vl.BoqItemId, vl.OldQuantity, vl.Quantity AS NewQuantity,
       vl.OldUnitPrice, vl.UnitPrice AS NewUnitPrice,
       (SELECT COUNT(*) FROM dbo.ProgressBillingLines l
         JOIN dbo.ProgressBillings h ON h.ID = l.BillingId
        WHERE l.BoqItemId = vl.BoqItemId
          AND h.Status = 'Posted'
          AND (v.ApprovedAt IS NULL OR h.PostedAt IS NULL OR h.PostedAt < v.ApprovedAt))
            AS PostedCertificatesBeforeThisChange
FROM dbo.VariationOrders v
JOIN dbo.VariationOrderLines vl ON vl.VariationOrderId = v.ID
WHERE v.Status = 'Approved'
  AND vl.Kind = 'Adjust'
  AND vl.BoqItemId IS NOT NULL
  AND (vl.OldUnitPrice <> vl.UnitPrice OR vl.OldQuantity <> vl.Quantity)
ORDER BY PostedCertificatesBeforeThisChange DESC, v.ApprovedAt DESC;

-- ---------------------------------------------------------------------------------------------
-- M9. Duplicate BOQ line codes within a project — the uniqueness rule C1 introduces per
--     (company, project, contract) cannot be enforced until these are resolved.
--     EXPECTED RESULT: zero rows, or a list the owner agrees to clean.
-- ---------------------------------------------------------------------------------------------
SELECT 'M9_duplicate_boq_codes' AS Measurement,
       CompanyID, ProjectId, Code, COUNT(*) AS Occurrences
FROM dbo.BoqItems
WHERE Code IS NOT NULL AND LTRIM(RTRIM(Code)) <> ''
GROUP BY CompanyID, ProjectId, Code
HAVING COUNT(*) > 1
ORDER BY Occurrences DESC;

-- ---------------------------------------------------------------------------------------------
-- M10. Existence of the C1 objects. Confirms whether the C1 schema script has been applied yet.
-- ---------------------------------------------------------------------------------------------
SELECT 'M10_c1_objects_present' AS Measurement,
       OBJECT_ID('dbo.ClientContracts','U')             AS ClientContracts,
       OBJECT_ID('dbo.BoqLineStates','U')               AS BoqLineStates,
       OBJECT_ID('dbo.CommercialRevisions','U')         AS CommercialRevisions,
       OBJECT_ID('dbo.CommercialRevisionLines','U')     AS CommercialRevisionLines,
       OBJECT_ID('dbo.SubcontractScopes','U')           AS SubcontractScopes,
       OBJECT_ID('dbo.SubcontractCertificateLines','U') AS SubcontractCertificateLines,
       OBJECT_ID('dbo.CertificateLineSnapshots','U')    AS CertificateLineSnapshots,
       OBJECT_ID('dbo.ConstructionAuditEntries','U')    AS ConstructionAuditEntries;
