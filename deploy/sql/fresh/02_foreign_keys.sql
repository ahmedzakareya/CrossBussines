SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Accounts_Parent]') AND parent_object_id = OBJECT_ID(N'[dbo].[Accounts]'))
ALTER TABLE [dbo].[Accounts]  WITH CHECK ADD  CONSTRAINT [FK_Accounts_Parent] FOREIGN KEY([ParentId])
REFERENCES [dbo].[Accounts] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Accounts_Parent]') AND parent_object_id = OBJECT_ID(N'[dbo].[Accounts]'))
ALTER TABLE [dbo].[Accounts] CHECK CONSTRAINT [FK_Accounts_Parent]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetRoleClaims_AspNetRoles_RoleId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetRoleClaims]'))
ALTER TABLE [dbo].[AspNetRoleClaims]  WITH CHECK ADD  CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY([RoleId])
REFERENCES [dbo].[AspNetRoles] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetRoleClaims_AspNetRoles_RoleId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetRoleClaims]'))
ALTER TABLE [dbo].[AspNetRoleClaims] CHECK CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserClaims_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserClaims]'))
ALTER TABLE [dbo].[AspNetUserClaims]  WITH CHECK ADD  CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY([UserId])
REFERENCES [dbo].[AspNetUsers] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserClaims_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserClaims]'))
ALTER TABLE [dbo].[AspNetUserClaims] CHECK CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserLogins_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserLogins]'))
ALTER TABLE [dbo].[AspNetUserLogins]  WITH CHECK ADD  CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY([UserId])
REFERENCES [dbo].[AspNetUsers] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserLogins_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserLogins]'))
ALTER TABLE [dbo].[AspNetUserLogins] CHECK CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserRoles_AspNetRoles_RoleId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]'))
ALTER TABLE [dbo].[AspNetUserRoles]  WITH CHECK ADD  CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY([RoleId])
REFERENCES [dbo].[AspNetRoles] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserRoles_AspNetRoles_RoleId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]'))
ALTER TABLE [dbo].[AspNetUserRoles] CHECK CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserRoles_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]'))
ALTER TABLE [dbo].[AspNetUserRoles]  WITH CHECK ADD  CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY([UserId])
REFERENCES [dbo].[AspNetUsers] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserRoles_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserRoles]'))
ALTER TABLE [dbo].[AspNetUserRoles] CHECK CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserTokens_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserTokens]'))
ALTER TABLE [dbo].[AspNetUserTokens]  WITH CHECK ADD  CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY([UserId])
REFERENCES [dbo].[AspNetUsers] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AspNetUserTokens_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[AspNetUserTokens]'))
ALTER TABLE [dbo].[AspNetUserTokens] CHECK CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AttendancePolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[AttendancePolicies]'))
ALTER TABLE [dbo].[AttendancePolicies]  WITH CHECK ADD  CONSTRAINT [FK_AttendancePolicies_Policies_LeavePolicyTypeID] FOREIGN KEY([LeavePolicyTypeID])
REFERENCES [dbo].[Policies] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_AttendancePolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[AttendancePolicies]'))
ALTER TABLE [dbo].[AttendancePolicies] CHECK CONSTRAINT [FK_AttendancePolicies_Policies_LeavePolicyTypeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Branches_Companies_CompanyID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Branches]'))
ALTER TABLE [dbo].[Branches]  WITH CHECK ADD  CONSTRAINT [FK_Branches_Companies_CompanyID] FOREIGN KEY([CompanyID])
REFERENCES [dbo].[Companies] ([CompanyID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Branches_Companies_CompanyID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Branches]'))
ALTER TABLE [dbo].[Branches] CHECK CONSTRAINT [FK_Branches_Companies_CompanyID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_Companies_ParentCompany]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies]  WITH CHECK ADD  CONSTRAINT [FK_Companies_Companies_ParentCompany] FOREIGN KEY([ParentCompany])
REFERENCES [dbo].[Companies] ([CompanyID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_Companies_ParentCompany]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies] CHECK CONSTRAINT [FK_Companies_Companies_ParentCompany]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_CompanyTypes_CompanyTypeId]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies]  WITH CHECK ADD  CONSTRAINT [FK_Companies_CompanyTypes_CompanyTypeId] FOREIGN KEY([CompanyTypeId])
REFERENCES [dbo].[CompanyTypes] ([Id])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_CompanyTypes_CompanyTypeId]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies] CHECK CONSTRAINT [FK_Companies_CompanyTypes_CompanyTypeId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_CountriesLookup_CountryID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies]  WITH CHECK ADD  CONSTRAINT [FK_Companies_CountriesLookup_CountryID] FOREIGN KEY([CountryID])
REFERENCES [dbo].[CountriesLookup] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Companies_CountriesLookup_CountryID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Companies]'))
ALTER TABLE [dbo].[Companies] CHECK CONSTRAINT [FK_Companies_CountriesLookup_CountryID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_AspNetUsers_UserId] FOREIGN KEY([UserId])
REFERENCES [dbo].[AspNetUsers] ([Id])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_AspNetUsers_UserId]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_AspNetUsers_UserId]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Branches_BranchID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_Branches_BranchID] FOREIGN KEY([BranchID])
REFERENCES [dbo].[Branches] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Branches_BranchID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_Branches_BranchID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Companies_EmpCompanyID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_Companies_EmpCompanyID] FOREIGN KEY([EmpCompanyID])
REFERENCES [dbo].[Companies] ([CompanyID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Companies_EmpCompanyID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_Companies_EmpCompanyID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_CountriesLookup_CountryID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_CountriesLookup_CountryID] FOREIGN KEY([CountryID])
REFERENCES [dbo].[CountriesLookup] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_CountriesLookup_CountryID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_CountriesLookup_CountryID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Hierarchicals_AdministrativeStructureID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_Hierarchicals_AdministrativeStructureID] FOREIGN KEY([AdministrativeStructureID])
REFERENCES [dbo].[Hierarchicals] ([H_ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Hierarchicals_AdministrativeStructureID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_Hierarchicals_AdministrativeStructureID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Hierarchicals_DepartmentID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_Hierarchicals_DepartmentID] FOREIGN KEY([DepartmentID])
REFERENCES [dbo].[Hierarchicals] ([H_ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_Hierarchicals_DepartmentID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_Hierarchicals_DepartmentID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_JobTitles_JobTitleID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_JobTitles_JobTitleID] FOREIGN KEY([JobTitleID])
REFERENCES [dbo].[JobTitles] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_JobTitles_JobTitleID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_JobTitles_JobTitleID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_SalaryPolicies_SalaryPoliciesID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee]  WITH CHECK ADD  CONSTRAINT [FK_Employee_SalaryPolicies_SalaryPoliciesID] FOREIGN KEY([SalaryPoliciesID])
REFERENCES [dbo].[SalaryPolicies] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Employee_SalaryPolicies_SalaryPoliciesID]') AND parent_object_id = OBJECT_ID(N'[dbo].[Employee]'))
ALTER TABLE [dbo].[Employee] CHECK CONSTRAINT [FK_Employee_SalaryPolicies_SalaryPoliciesID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Hierarchicals_Hierarchicals_H_Parent]') AND parent_object_id = OBJECT_ID(N'[dbo].[Hierarchicals]'))
ALTER TABLE [dbo].[Hierarchicals]  WITH CHECK ADD  CONSTRAINT [FK_Hierarchicals_Hierarchicals_H_Parent] FOREIGN KEY([H_Parent])
REFERENCES [dbo].[Hierarchicals] ([H_ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Hierarchicals_Hierarchicals_H_Parent]') AND parent_object_id = OBJECT_ID(N'[dbo].[Hierarchicals]'))
ALTER TABLE [dbo].[Hierarchicals] CHECK CONSTRAINT [FK_Hierarchicals_Hierarchicals_H_Parent]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Hierarchicals_HierarchicalTypes_H_Type]') AND parent_object_id = OBJECT_ID(N'[dbo].[Hierarchicals]'))
ALTER TABLE [dbo].[Hierarchicals]  WITH CHECK ADD  CONSTRAINT [FK_Hierarchicals_HierarchicalTypes_H_Type] FOREIGN KEY([H_Type])
REFERENCES [dbo].[HierarchicalTypes] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Hierarchicals_HierarchicalTypes_H_Type]') AND parent_object_id = OBJECT_ID(N'[dbo].[Hierarchicals]'))
ALTER TABLE [dbo].[Hierarchicals] CHECK CONSTRAINT [FK_Hierarchicals_HierarchicalTypes_H_Type]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_JEL_JE]') AND parent_object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]'))
ALTER TABLE [dbo].[JournalEntryLines]  WITH CHECK ADD  CONSTRAINT [FK_JEL_JE] FOREIGN KEY([JournalEntryId])
REFERENCES [dbo].[JournalEntries] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_JEL_JE]') AND parent_object_id = OBJECT_ID(N'[dbo].[JournalEntryLines]'))
ALTER TABLE [dbo].[JournalEntryLines] CHECK CONSTRAINT [FK_JEL_JE]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeavePolicies_LeaveTypes_LeaveTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeavePolicies]'))
ALTER TABLE [dbo].[LeavePolicies]  WITH CHECK ADD  CONSTRAINT [FK_LeavePolicies_LeaveTypes_LeaveTypeID] FOREIGN KEY([LeaveTypeID])
REFERENCES [dbo].[LeaveTypes] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeavePolicies_LeaveTypes_LeaveTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeavePolicies]'))
ALTER TABLE [dbo].[LeavePolicies] CHECK CONSTRAINT [FK_LeavePolicies_LeaveTypes_LeaveTypeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeavePolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeavePolicies]'))
ALTER TABLE [dbo].[LeavePolicies]  WITH CHECK ADD  CONSTRAINT [FK_LeavePolicies_Policies_LeavePolicyTypeID] FOREIGN KEY([LeavePolicyTypeID])
REFERENCES [dbo].[Policies] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeavePolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeavePolicies]'))
ALTER TABLE [dbo].[LeavePolicies] CHECK CONSTRAINT [FK_LeavePolicies_Policies_LeavePolicyTypeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeaveRequests_Employee_EmployeeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeaveRequests]'))
ALTER TABLE [dbo].[LeaveRequests]  WITH CHECK ADD  CONSTRAINT [FK_LeaveRequests_Employee_EmployeeID] FOREIGN KEY([EmployeeID])
REFERENCES [dbo].[Employee] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeaveRequests_Employee_EmployeeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeaveRequests]'))
ALTER TABLE [dbo].[LeaveRequests] CHECK CONSTRAINT [FK_LeaveRequests_Employee_EmployeeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeaveRequests_LeaveTypes_LeaveTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeaveRequests]'))
ALTER TABLE [dbo].[LeaveRequests]  WITH CHECK ADD  CONSTRAINT [FK_LeaveRequests_LeaveTypes_LeaveTypeID] FOREIGN KEY([LeaveTypeID])
REFERENCES [dbo].[LeaveTypes] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_LeaveRequests_LeaveTypes_LeaveTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[LeaveRequests]'))
ALTER TABLE [dbo].[LeaveRequests] CHECK CONSTRAINT [FK_LeaveRequests_LeaveTypes_LeaveTypeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Notifications_Employee]') AND parent_object_id = OBJECT_ID(N'[dbo].[Notifications]'))
ALTER TABLE [dbo].[Notifications]  WITH CHECK ADD  CONSTRAINT [FK_Notifications_Employee] FOREIGN KEY([RecipientEmployeeID])
REFERENCES [dbo].[Employee] ([ID])
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_Notifications_Employee]') AND parent_object_id = OBJECT_ID(N'[dbo].[Notifications]'))
ALTER TABLE [dbo].[Notifications] CHECK CONSTRAINT [FK_Notifications_Employee]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_PolicyAssignments_Employee_EmployeeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]'))
ALTER TABLE [dbo].[PolicyAssignments]  WITH CHECK ADD  CONSTRAINT [FK_PolicyAssignments_Employee_EmployeeID] FOREIGN KEY([EmployeeID])
REFERENCES [dbo].[Employee] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_PolicyAssignments_Employee_EmployeeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]'))
ALTER TABLE [dbo].[PolicyAssignments] CHECK CONSTRAINT [FK_PolicyAssignments_Employee_EmployeeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_PolicyAssignments_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]'))
ALTER TABLE [dbo].[PolicyAssignments]  WITH CHECK ADD  CONSTRAINT [FK_PolicyAssignments_Policies_LeavePolicyTypeID] FOREIGN KEY([LeavePolicyTypeID])
REFERENCES [dbo].[Policies] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_PolicyAssignments_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[PolicyAssignments]'))
ALTER TABLE [dbo].[PolicyAssignments] CHECK CONSTRAINT [FK_PolicyAssignments_Policies_LeavePolicyTypeID]
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_SalaryPolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[SalaryPolicies]'))
ALTER TABLE [dbo].[SalaryPolicies]  WITH CHECK ADD  CONSTRAINT [FK_SalaryPolicies_Policies_LeavePolicyTypeID] FOREIGN KEY([LeavePolicyTypeID])
REFERENCES [dbo].[Policies] ([ID])
ON DELETE CASCADE
GO
IF  EXISTS (SELECT * FROM sys.foreign_keys WHERE object_id = OBJECT_ID(N'[dbo].[FK_SalaryPolicies_Policies_LeavePolicyTypeID]') AND parent_object_id = OBJECT_ID(N'[dbo].[SalaryPolicies]'))
ALTER TABLE [dbo].[SalaryPolicies] CHECK CONSTRAINT [FK_SalaryPolicies_Policies_LeavePolicyTypeID]
GO
