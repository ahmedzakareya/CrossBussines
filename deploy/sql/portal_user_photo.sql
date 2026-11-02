/* ================================================================================================
   PortalUsers.PhotoPath — the contact's photograph, set from the CRM screen.

   ADDITIVE AND IDEMPOTENT. One nullable column; no data is written, nothing is dropped, and running
   this twice changes nothing the second time. A portal contact with no picture keeps NULL, and the
   portal renders the theme's neutral avatar for that — the absence is a normal state, not a gap to
   backfill.

   WHAT THE VALUE IS, AND THIS CHANGED IN THE PORTAL CONVERGENCE. It is an OPAQUE STORED LOCATOR of
   the form "{companyId}/{32-hex}.jpg|png|webp", resolved by IPortalPhotoService against a private
   root under the CONTENT root (App_Data/portal-photos). It is NOT a web path and must never be
   rendered into an <img src>.

   THE PREVIOUS DESIGN, and why it was replaced rather than kept:
     * it stored a web path under /uploads/portal and put it straight into an <img src>, so a
       guessable URL was the only thing between a stranger and a named person's photograph;
     * PrivateFileGate classified that prefix RequireAuthenticated, which reads the DEFAULT
       authentication scheme. The Client Portal has its own scheme by design, so the signed-in
       CUSTOMER got 404 on their own picture, while every authenticated employee of EVERY company
       could fetch anyone's — the gate enforces no company and no customer.
   The bytes now live outside wwwroot, where they have no URL at all, and are served only by
   ClientPortalController.ContactPhoto (portal identity, self only) and CrmController
   .PortalUserPhotoContent (CRM read right, caller's own company).

   THE COLUMN IS UNCHANGED BY THAT MOVE: same name, same type, same NVARCHAR(400), which is why
   this script is still the only schema delta the feature needs. Length 400 matches Capabilities and
   is far beyond what a generated file name needs; PortalUserModel states the same width, so EF and
   the database cannot disagree about it.

   NO DEPLOYMENT AUTHORITY IS CLAIMED HERE. Authoring this file and running it are different
   permissions; the deploy tree is TAB-1's and this wave executed no SQL.
   ================================================================================================ */

IF COL_LENGTH('dbo.PortalUsers', 'PhotoPath') IS NULL
BEGIN
    ALTER TABLE dbo.PortalUsers ADD PhotoPath NVARCHAR(400) NULL;
    PRINT 'PortalUsers.PhotoPath added.';
END
ELSE
BEGIN
    PRINT 'PortalUsers.PhotoPath already present - nothing to do.';
END
GO
