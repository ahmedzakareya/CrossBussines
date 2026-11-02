-- =============================================================================================
-- Task ON HOLD / SLA PAUSE - current operational state. Additive + idempotent.
--
-- AUTHORED, NOT EXECUTED. No DDL ran against CrossBuyDev or production for this slice.
--
-- THE SHAPE OF THE DECISION, stated first because it is the part that was NOT obvious.
--
--   TaskItem carries CURRENT STATE ONLY. The request/decision HISTORY lives in Business Events,
--   which is where this repository already keeps the who-and-when of a lifecycle transition
--   (Task.Created, Task.Assigned, Task.StatusChanged, Task.BecameOverdue, Task.Escalated). A
--   TaskHold table would be a SECOND audit system beside one that already works, and every query
--   that wanted to know "is this task on hold" would have to reach for the latest row of it.
--
--   NO NEW TABLE. NO NEW TASK STATUS. Three operational conditions have to be distinguishable -
--   NORMAL, HOLD REQUESTED, ON HOLD - and they are, from the nullability of two timestamps:
--
--       HoldRequestedAtUtc  HoldApprovedAtUtc   state
--       ------------------  -----------------   ---------------
--       NULL                NULL                NORMAL
--       set                 NULL                HOLD REQUESTED
--       (either)            set                 ON HOLD
--
--   A manager holding a task DIRECTLY sets the approval pair with no request pair, which the table
--   above already covers - that is why ON HOLD does not care what the request column says.
--
--   REJECT clears the request columns and the task is NORMAL again. RESUME extends DueDate and
--   clears all five, so the row returns to NORMAL and the NEXT hold cycle starts from a clean
--   state. That is what makes hold/resume/hold/resume work with no cumulative column: each cycle
--   pays its own extension into DueDate at the moment it ends, so there is nothing left to
--   accumulate. A total-held-duration column would be a reporting convenience, and nothing in the
--   approved policy asks a question that needs it.
--
--   Status is untouched on purpose. TaskItem.Status has said since the entity was written that
--   "Overdue" is DERIVED, never stored, and TaskLifecycle repeats it: a derived operational
--   condition does not become a stored lifecycle status just because it is now interesting.
--   Adding OnHold to the status vocabulary would also put it into the transition matrix, where
--   every existing edge would have to answer what it means to complete an on-hold task.
--
-- FIVE COLUMNS.
--
--   HoldRequestedAtUtc  DATETIME2 NULL     when the employee asked
--   HoldRequestedByEmployeeId  INT NULL    who asked - kept because the approver may not be the
--                                          only person who later needs to know who did
--   HoldReason  NVARCHAR(500) NULL         the mandatory reason, held at the width ReopenReason
--                                          already uses for the same kind of override note
--   HoldApprovedAtUtc  DATETIME2 NULL      THE SLA PAUSE START. Approval time, not request time:
--                                          waiting for a manager does not stop the clock, so the
--                                          column that anchors the pause is the decision, not the ask
--   HoldApprovedByEmployeeId  INT NULL     who decided
--
--   ALL NULLABLE, ALL DEFAULT-FREE, AND NOTHING IS BACK-FILLED. A DEFAULT on any of them would
--   turn every task that existed before this slice into a hold request that nobody made. NULL here
--   means "this task has no hold in flight", which is the truth for every historical row.
--
--   HoldReason is nullable at the DATABASE and mandatory at the REQUEST PATH. That is deliberate:
--   a direct manager hold and a requested hold write the same columns, and a NOT NULL constraint
--   would have to be satisfied by both. The approved policy makes the reason mandatory for the
--   REQUEST; the constraint that enforces it belongs where the policy is stated, not on a column
--   that a different path also writes.
--
-- WHAT THIS SLICE DELIBERATELY DOES NOT ADD.
--
--   No index. The two sweeps this pause exists to stop already filter on CompanyId/DueDate/Status
--   through IX_TaskItems_Due, and the hold predicate is an additional NULL test on rows that query
--   has already narrowed - not a new access path. An index for it would be optimisation ahead of a
--   measurement.
--   No total-held-duration column (see above), no hold table, no status value, no constraint, no
--   trigger, no seed, no back-fill, and no change to any existing column.
-- =============================================================================================

IF COL_LENGTH('TaskItems','HoldRequestedAtUtc') IS NULL ALTER TABLE TaskItems ADD HoldRequestedAtUtc DATETIME2 NULL;
GO

IF COL_LENGTH('TaskItems','HoldRequestedByEmployeeId') IS NULL ALTER TABLE TaskItems ADD HoldRequestedByEmployeeId INT NULL;
GO

IF COL_LENGTH('TaskItems','HoldReason') IS NULL ALTER TABLE TaskItems ADD HoldReason NVARCHAR(500) NULL;
GO

IF COL_LENGTH('TaskItems','HoldApprovedAtUtc') IS NULL ALTER TABLE TaskItems ADD HoldApprovedAtUtc DATETIME2 NULL;
GO

IF COL_LENGTH('TaskItems','HoldApprovedByEmployeeId') IS NULL ALTER TABLE TaskItems ADD HoldApprovedByEmployeeId INT NULL;
GO
