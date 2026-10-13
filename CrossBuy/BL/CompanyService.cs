using AutoMapper;
using Azure.Core;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using CrossBuy.Models.Platform;
using CrossBuy.BL.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Server;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Transactions;


namespace CrossBuy.BL
{
	public class CompanyService : ICompanyService
	{
		private readonly CrossDbContext _context;
		private readonly IMapper _mapper;
		private readonly IServiceProvider _serviceProvider;
		private readonly IHttpContextAccessor _httpContextAccessor;

		// =====================================================================================
		// What the first administrator of a new company is given, per module scope.
		//
		// THIS LIST IS BOUNDED BY WHAT THE PLATFORM CAN ACTUALLY GRANT, NOT BY WHAT WOULD BE
		// USEFUL. PlatformGrantWriter administers only the scopes that declare a role vocabulary
		// ("AdministrableScopes"), and exactly four exist: Hr, Projects, Tasks, Communication.
		// Inventory, Accounting and Crm keep their roles in their own tables
		// (InventoryUserRoles / AccountingUserRoles / CrmUserRoles) and Calendar declares no
		// vocabulary at all, so naming any of them here would produce a refusal, not a grant.
		// Wiring those three subsystems into company provisioning is separate work.
		//
		// Two further exclusions are deliberate rather than forced:
		//
		//   * PayrollOfficer / payroll-manage. HrAccessService says it outright - "NOT HrManager:
		//     money is a separate right". A provisioning step should not be the thing that hands
		//     out payroll.
		//   * Pos. PlatformGrantWriter.RefusedScopes rejects it outright (IMP-001): POS is
		//     administered through BranchUserRoles, and a new company has no branches yet.
		// =====================================================================================
		private static readonly (string Scope, string Role)[] FirstAdministratorGrants =
		{
			(EntityRegistry.ScopeHr, HrRoles.HrManager),
			(EntityRegistry.ScopeProjects, ProjectsRoles.ProjectsAdministrator),
			(EntityRegistry.ScopeTasks, TasksRoles.TasksAdministrator),
			(EntityRegistry.ScopeCommunication, CommunicationRoles.CommunicationAdministrator),
		};

		// =====================================================================================
		// The three module manager rows a first administrator needs, written where each module's
		// gate actually looks. See the call site in SaveCompanyAsync for why these are seeded into
		// the module tables rather than granted through PlatformGrantWriter.
		//
		// Shared by company creation and by the backfill below, so a company created before this
		// existed is repaired with exactly the same rows rather than a hand-written approximation.
		// =====================================================================================

		// =====================================================================================
		// The company's first fiscal year, and its twelve monthly periods, all open.
		//
		// January start. Companies carries no fiscal-year-start field, so there is nothing to read
		// a different opening month from; when one is added, this is the single place to honour it.
		//
		// IDEMPOTENT BY COMPANY: a company that already has a fiscal year gets nothing, so this can
		// never produce a second calendar for the same tenant.
		// =====================================================================================
		private static async Task AddFirstFiscalYearAsync(CrossDbContext context, int companyId, DateTime today)
		{
			if (await context.FiscalYears.AnyAsync(f => f.CompanyID == companyId)) return;

			var year = today.Year;

			var fiscalYear = new Models.Context.Accounting.FiscalYear
			{
				CompanyID = companyId,
				Name = year.ToString(),
				StartDate = new DateTime(year, 1, 1),
				EndDate = new DateTime(year, 12, 31),
				Status = "Open",
			};

			context.FiscalYears.Add(fiscalYear);

			// The periods carry the year's id, so it has to exist before they are written.
			await context.SaveChangesAsync();

			for (byte month = 1; month <= 12; month++)
			{
				context.FiscalPeriods.Add(new Models.Context.Accounting.FiscalPeriod
				{
					FiscalYearId = fiscalYear.ID,
					PeriodNo = month,
					StartDate = new DateTime(year, month, 1),
					EndDate = new DateTime(year, month, DateTime.DaysInMonth(year, month)),
					Status = Models.Context.Accounting.AccountingPeriodStatuses.Open,
				});
			}

			await context.SaveChangesAsync();
		}

		private static void AddFirstAdministratorModuleRoles(CrossDbContext context, int companyId, int employeeId)
		{
			var now = DateTime.UtcNow;

			context.AccountingUserRoles.Add(new Models.Context.Accounting.AccountingUserRole
			{
				CompanyID = companyId, EmployeeId = employeeId, Role = "ChiefAccountant", CreatedAt = now,
			});

			context.InventoryUserRoles.Add(new Models.Context.Inventory.InventoryUserRole
			{
				// Company-wide: InventoryManager is not branch-scoped, and a new company has no
				// branches to scope to anyway.
				CompanyID = companyId, EmployeeId = employeeId, Role = "InventoryManager",
				ScopeBranchId = null, CreatedAt = now,
			});

			context.CrmUserRoles.Add(new Models.Context.Crm.CrmUserRole
			{
				CompanyID = companyId, EmployeeId = employeeId, Role = "SalesManager", CreatedAt = now,
			});
		}

		// =====================================================================================
		// Repairs ONE company that was created before the seeding above existed.
		//
		// It is deliberately narrow, because the thing it writes is module-manager authority:
		//   * the employee must already belong to the company named, so this cannot hand authority
		//     over one tenant to someone from another;
		//   * it refuses a company that already holds ANY row in these tables, so it can only ever
		//     fill a vacuum and never quietly widen an existing configuration;
		//   * it names exactly one company and never iterates.
		// =====================================================================================
		public async Task<string> BackfillFirstAdministratorModuleRolesAsync(int companyId, int employeeId)
		{
			if (companyId <= 0 || employeeId <= 0) return "Refused: a company and an employee must both be named.";

			var belongs = await _context.Employee.AsNoTracking()
				.AnyAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId && e.IsActive);

			if (!belongs)
				return $"Refused: employee {employeeId} is not an active employee of company {companyId}.";

			bool already =
				await _context.AccountingUserRoles.AnyAsync(r => r.CompanyID == companyId) ||
				await _context.InventoryUserRoles.AnyAsync(r => r.CompanyID == companyId) ||
				await _context.CrmUserRoles.AnyAsync(r => r.CompanyID == companyId);

			if (already)
				return $"Refused: company {companyId} already holds module roles — this only fills a vacuum.";

			AddFirstAdministratorModuleRoles(_context, companyId, employeeId);
			await _context.SaveChangesAsync();

			return $"Seeded ChiefAccountant, InventoryManager and SalesManager for employee {employeeId} in company {companyId}.";
		}

		public CompanyService(CrossDbContext context, IMapper mapper , IServiceProvider serviceProvider , IHttpContextAccessor httpContextAccessor)
		{
			this._context = context;
			_serviceProvider = serviceProvider;
			//_context = context;
			_mapper = mapper;
			_httpContextAccessor = httpContextAccessor;
		}

		public async Task<List<BranchiesDto>> GetAllBranchesAsync()
		{
			return await _context.Branches
			   .Select(c => new BranchiesDto
			   {
				   ID = c.ID,
				   CompanyID = c.CompanyID,
				   Country = c.Country,
				   CompanyNameAr = c.Company.ComoanyNameAr,
				   CompanyNameEn = c.Company.CompanyName,
				   Name = c.Name,
				   NameAr = c.NameAr,
				   Location = c.Location,
				   CompanyTypeID = c.Company.CompanyTypeId,
				   CompanyTypeNameAr = c.Company.CompanyType.NameAr,
				   CompanyTypeNameEn = c.Company.CompanyType.NameEn,
				   ImageUrl = c.ImageUrl,
			   })
			   .ToListAsync();
		}

		public async Task<List<BranchiesDto>> GetBranchesByCompanyAsync(int companyId)
		{
			return await _context.Branches
				.Where(b => b.CompanyID == companyId)
				.Select(b => new BranchiesDto
				{
					ID = b.ID,
					CompanyID = b.CompanyID,
					Name = b.Name,
					NameAr = b.NameAr,
					Location = b.Location,
					ImageUrl = b.ImageUrl,
				})
				.ToListAsync();
		}

		public async Task<List<CompanyDto>> GetAllCompaniesAsync()
		{
			try
			{
				var companies = await _context.Companies
											   .Include(c => c.CompanyType)
											   .Include(c => c.Country)
											   .Include(c => c.Employees)
											   .Include(c => c.Parent)
											   .ToListAsync();

				var companyDtos = companies.Select(c => new CompanyDto
				{
					CompanyID = c.CompanyID,
					CompanyName = c.CompanyName,
					CompanyNameAr = c.ComoanyNameAr,
					Address = c.Address,
					PhoneNumber = c.PhoneNumber,
					Email = c.Email,
					PostalCode = c.PostalCode,
					Website = c.Website,
					CountryName = c.Country.CountryName,
					CountryNameAr = c.Country.CountryNameAr,
					CompanyTypeName = c.CompanyType.NameAr,
					CompanyTypeNameEn = c.CompanyType.NameEn,
					RegistrationNumber = c.RegistrationNumber,
					TaxNumber = c.TaxNumber,
					Description = c.Description,
					ParentCompany = c.ParentCompany,
					ParentCompanyName = c.Parent?.ComoanyNameAr,
					ParentCompanyNameEn = c.Parent?.CompanyName,
					CompanyImageUrl = c.CompanyImage
				}).OrderByDescending(c=>c.CompanyID).ToList();

				return companyDtos;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in GetAllCompaniesAsync: {ex.Message}");
				return new List<CompanyDto>();
			}
		}

		public Task<BranchiesDto> GetBranchByIdAsync(int id)
		{
			throw new NotImplementedException();
		}

		public async Task<BranchiesDto> GetBranchForm()
		{
			try
			{
				return new BranchiesDto
				{
					companies = await _context.Companies.Select(c=> new CompanyDto { CompanyID  = c.CompanyID , CompanyNameAr = c.ComoanyNameAr , CompanyName = c.CompanyName} ).ToListAsync(),
					countriesLookups = await _context.CountriesLookup.ToListAsync(),
					
				};
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in GetCompanyForm: {ex.Message}");
				throw;
			}
		}

		public async Task<CompanyDto> GetCompanyData(int? ID)
		{
			try
			{
				var companies = await _context.Companies
											   .Include(c => c.CompanyType)
											   .Include(c => c.Country)
											   .Include(c => c.Employees)
											   .Include(c => c.Parent)
											   .ToListAsync();

				var companyDtos = companies.Select(c => new CompanyDto
				{
					CompanyID = c.CompanyID,
					CompanyName = c.CompanyName,
					CompanyNameAr = c.ComoanyNameAr,
					Address = c.Address,
					PhoneNumber = c.PhoneNumber,
					Email = c.Email,
					PostalCode = c.PostalCode,
					Website = c.Website,
					CountryName = c.Country.CountryName,
					CountryNameAr = c.Country.CountryNameAr,
					CompanyTypeName = c.CompanyType.NameAr,
					CompanyTypeNameEn = c.CompanyType.NameEn,
					RegistrationNumber = c.RegistrationNumber,
					TaxNumber = c.TaxNumber,
					Description = c.Description,
					ParentCompany = c.ParentCompany,
					ParentCompanyName = c.Parent?.ComoanyNameAr,
					ParentCompanyNameEn = c.Parent?.CompanyName,
					CompanyImageUrl = c.CompanyImage , 
					countriesLookups = _context.CountriesLookup.ToList(),
					ChildCompanies = _context.Companies.ToList(),
					companyTypes = _context.CompanyTypes.ToList(),
					CountryID = c.CountryID, 
					CompanyTypeId = c.CompanyTypeId,

					AttachmentsFiles = _context.Attachments.Where(a=>a.FormID == 1 && a.RequestID == c.CompanyID).ToList(),
				}).Where(c=>c.CompanyID==ID).FirstOrDefault();

				return companyDtos;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in GetAllCompaniesAsync: {ex.Message}");
				return new CompanyDto();
			}
		}

		public async Task<CompanyDto> GetCompanyForm()
		{
			try
			{
				return new CompanyDto
				{
					companyTypes = await _context.CompanyTypes.ToListAsync(),
					ChildCompanies = await _context.Companies.ToListAsync(),
					countriesLookups = await _context.CountriesLookup.ToListAsync(),
				};
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in GetCompanyForm: {ex.Message}");
				throw;
			}
		}




		public async Task<CompanyDto> SaveCompanyAsync(CompanyDto model, List<Models.Context.Admin.Attachment> attachments)
		{
			try
			{
				var employeeJson = _httpContextAccessor.HttpContext?.Session.GetString("Employee");
				var employee = employeeJson != null ? JsonConvert.DeserializeObject<EmployeeViewModel>(employeeJson) : null;


				using (var scope = _serviceProvider.CreateScope())
				{
					var context = scope.ServiceProvider.GetRequiredService<CrossDbContext>();

					// Start a database transaction
					await using (var transaction = await ScopedTx.BeginOrJoinAsync(context))
					{
						try
						{
							if (model.CompanyID == 0)
							{
								// Create and initialize a new company entity
								var company = new Companies
								{
									ComoanyNameAr = model.CompanyNameAr,
									CompanyName = model.CompanyName,
									CompanyTypeId = model.CompanyTypeId,
									CountryID = model.CountryID,
									Address = model.Address,
									Description = model.Description,
									Email = model.Email,
									ParentCompany = model.ParentCompany,
									PhoneNumber = model.PhoneNumber,
									PostalCode = model.PostalCode,
									TaxNumber = model.TaxNumber,
									RegistrationNumber = model.RegistrationNumber,
									Website = model.Website,
									CreatedBy = employee.ID , 
									CreatedAt = DateTime.UtcNow
							};

								// Add the company to the database
								context.Companies.Add(company);
								await context.SaveChangesAsync();

                                int? ParentID = null;

                                if (company.ParentCompany != null)
                                {
                                    ParentID = context.Hierarchicals
                                        .Where(c => c.H_ObjectID == company.ParentCompany)
                                        .Select(c => (int?)c.H_ID)
                                        .FirstOrDefault();
                                }

                                // Create and initialize a new hierarchical entity
                                var hierarchical = new Hierarchical
								{
									H_Name = company.ComoanyNameAr,
									H_NameEn = company.CompanyName,
									H_ObjectID = company.CompanyID,
									H_Type = 1,
									H_Parent = ParentID
								};

								// Add the hierarchical entity to the database
								context.Hierarchicals.Add(hierarchical);
								await context.SaveChangesAsync();

								// ---- the company is born with a fiscal calendar ----
								// WITHOUT ONE THE COMPANY CAN POST NOTHING. StockService.PeriodGuardAsync and the
								// journal path both resolve a fiscal period for the posting date and refuse when
								// none covers it. Nothing in the product ever created a FiscalYear -- no screen,
								// no endpoint, no service -- so a company created through this method could never
								// record opening stock, an invoice or a journal entry. Worse, the refusal reaches
								// the UI as a toast, so the screen simply does nothing and says nothing.
								//
								// This belongs to the COMPANY rather than to its administrator, so it is created
								// here rather than inside the administrator block: a company provisioned without
								// an administrator still needs a calendar.
								await AddFirstFiscalYearAsync(context, company.CompanyID, DateTime.UtcNow);


								// ---- the company is born with an administrator ----
								// Creating a company used to write Companies + Hierarchical and stop there, which left the
								// company unreachable. BusinessContextFactory treats the Employee row as THE authority for
								// company, its lookup is FirstOrDefault(e => e.UserId == userId) with no ordering, and there
								// is no company switcher — so nobody could ever act inside the new company. The administrator
								// gets its OWN login rather than a second Employee row on an existing one, because a second
								// row would make the company that user lands in depend on row order.
								string? adminTempPassword = null;
								string? adminMessage = null;
								bool adminProvisioned = false;

								if (!string.IsNullOrWhiteSpace(model.AdministratorEmail))
								{
									var adminEmail = model.AdministratorEmail.Trim();
									var userManager = scope.ServiceProvider
										.GetRequiredService<UserManager<Users>>();

									if (await userManager.FindByEmailAsync(adminEmail) != null)
									{
										// Reusing a login that already resolves to an employee elsewhere would make that
										// user's company ambiguous. Refuse rather than create the ambiguity.
										adminMessage = "A login already exists for this address. The company administrator needs an address of their own.";
									}
									else
									{
										var adminUser = new Users
										{
											UserName = adminEmail,
											Email = adminEmail,
											EmailConfirmed = true,
											IsActive = true,
											// REQUIRED FOR SIGN-IN, not a classification. AccountController.Login
											// refuses on `!user.IsActive || !user.IsEndUser` before it ever checks the
											// password, so an administrator created with this false is handed working
											// credentials that answer "Username or password is incorrect!" forever.
											IsEndUser = true,
										};

										adminTempPassword = "Cb@" + Guid.NewGuid().ToString("N").Substring(0, 8) + "9";
										var created = await userManager.CreateAsync(adminUser, adminTempPassword);

										if (!created.Succeeded)
										{
											adminTempPassword = null;
											adminMessage = string.Join("; ", created.Errors.Select(e => e.Description));
										}
										else
										{
											// "Admin" is the own-company administration tier
											// (GrantAdminIdentityRoles.CompanyAdministration) — exactly the authority a
											// company needs over itself, and NOT the cross-company tier.
											try { await userManager.AddToRoleAsync(adminUser, "Admin"); } catch { /* role optional */ }

											// JobTitleID is required on Employee, so the administrator needs a title — and it
											// has to be an EXISTING one.
											//
											// WHY NOT CREATE A COMPANY-SCOPED TITLE, which is what this plainly wants:
											// JobTitle.CompanyID exists on the model but NOT in the deployed schema
											// (deploy/sql/fresh/01_schema.sql defines JobTitles as ID, Title, TitleAr,
											// Description only). RSK-35 Phase B added the property, and its own comment
											// says "nothing reads this property today, so adding it changes no
											// behaviour" — which held until something tried to WRITE one. EF emits the
											// mapped column and SQL Server answers "Invalid column name 'CompanyID'",
											// so ANY JobTitles insert fails, with or without the company set.
											//
											// Reusing a title is therefore not a preference, it is the only thing that
											// works against the schema as deployed. Closing this properly means adding
											// the column to the schema slice, which is a coupled slice+manifest change
											// and is deliberately not bundled in here.
											var adminTitleId = await context.JobTitles.AsNoTracking()
												.OrderBy(t => t.ID)
												.Select(t => (int?)t.ID)
												.FirstOrDefaultAsync();

											if (adminTitleId is null)
											{
												// Nothing to point JobTitleID at, and it is NOT NULL. Say so instead of
												// failing the whole company save with a constraint error.
												adminTempPassword = null;
												adminMessage = "The company was created, but no administrator could be added: the system has no job title to assign.";
											}
											else
											{

											var adminName = string.IsNullOrWhiteSpace(model.AdministratorFullName)
												? adminEmail
												: model.AdministratorFullName.Trim();

											var adminEmployee = new Employee
											{
												// Stamped from the company just created — never from the form. A posted
												// EmpCompanyID is what lets SaveEmployeeAsync write into any company at
												// all, so this path does not accept one.
												EmpCompanyID = company.CompanyID,
												UserId = adminUser.Id,
												JobTitleID = adminTitleId.Value,
												FirstName = adminName,
												LastName = "",
												FullName = adminName,
												FullNameEn = string.IsNullOrWhiteSpace(model.AdministratorFullNameEn)
													? adminName
													: model.AdministratorFullNameEn.Trim(),
												Email = adminEmail,
												Address = model.Address ?? "",
												PhoneNumber = model.PhoneNumber ?? "",
												ProfileImage = "",
												Gender = "",
												MaritalStatus = "",
												CountryID = model.CountryID > 0 ? (int?)model.CountryID : null,
												DateOfBirth = DateTime.UtcNow,
												DateOfJoining = DateTime.UtcNow,
												IsActive = true,
												CreatedBy = employee?.ID,
												CreatedAt = DateTime.UtcNow,
											};

											context.Employee.Add(adminEmployee);
											await context.SaveChangesAsync();
											adminProvisioned = true;

											// ---- module grants, in the NEW company only ----
											// The identity role "Admin" above only buys GRANT ADMINISTRATION
											// (GrantAdminIdentityRoles.CompanyAdministration). It buys nothing
											// in the modules: HrAccessService and friends read
											// PlatformRoleAssignments, so without these rows the administrator
											// cannot even create the company's second employee.
											//
											// These go through IPlatformGrantWriter rather than inserting rows
											// directly, because the writer is what enforces "a real, active
											// employee of the TARGET company", records the audit trail, and
											// refuses a company the actor has no authority over. Writing the
											// rows by hand would bypass exactly the checks that make these
											// grants trustworthy.
											var grantWriter = scope.ServiceProvider.GetService<IPlatformGrantWriter>();
											var contextFactory = scope.ServiceProvider.GetService<IBusinessContextFactory>();
											var actor = contextFactory == null
												? null
												: await contextFactory.TryForHttpAsync();

											if (grantWriter == null || actor == null)
											{
												adminMessage = "The administrator was created but holds no module roles: the grant writer or the caller's business context was unavailable.";
											}
											else
											{
												var refused = new List<string>();

												foreach (var (grantScope, grantRole) in FirstAdministratorGrants)
												{
													var granted = await grantWriter.CreateGrantAsync(actor, new CreatePlatformGrantCommand
													{
														CompanyId = company.CompanyID,
														Scope = grantScope,
														PrincipalType = PlatformPrincipalTypesRef.Employee,
														PrincipalId = adminEmployee.ID,
														Role = grantRole,
														Reason = "First administrator of a newly created company.",
														// A retried creation returns the original grant rather
														// than stacking a second one.
														IdempotencyKey = $"first-admin:{company.CompanyID}:{grantScope}:{grantRole}",
													});

													if (granted.Outcome != PlatformGrantOutcome.Success
														&& granted.Outcome != PlatformGrantOutcome.IdempotentReplay)
													{
														refused.Add($"{grantScope}/{grantRole}: {granted.Message}");
													}
												}

												// A silently role-less administrator looks identical to a working
												// one until someone tries to use the company, so say it here.
												if (refused.Count > 0)
												{
													adminMessage = "The administrator was created, but some module roles were refused — "
														+ string.Join("; ", refused);
												}
											}

											// ---- Accounting, Inventory and CRM: seeded, not granted ----
											// These three do NOT read PlatformRoleAssignments. Their gates ask their
											// own access service, which reads its own table:
											//   AccPerm -> AccountingAccessService.RolesAsync -> AccountingUserRoles
											//   InvPerm -> InventoryAccessService.RolesAsync  -> InventoryUserRoles
											//   CrmPerm -> CrmAccessService.RolesAsync        -> CrmUserRoles
											// and AccountingAccessService says so outright: "this service does not
											// query PlatformRoleAssignments itself".
											//
											// So a platform grant for these scopes would sit in a table nothing
											// consults — PlatformGrantWriter refuses them for exactly that reason.
											// The row has to go where the gate looks.
											//
											// WHY THIS IS NEEDED AT ALL: the three role screens (AssignAccRole,
											// Inventory AssignRole, AssignCrmRole) are each gated on their own
											// module's "manage" tier — ChiefAccountant, InventoryManager,
											// SalesManager. Without this, a new company's administrator can never
											// hold any of them and can never grant them either, so the company is
											// permanently locked out of Accounting, Inventory and CRM. Seeding the
											// manager row is what breaks that circle; everything after it happens
											// through the ordinary screens.
											//
											// NOT AUDITED, AND THAT IS A PROPERTY OF THESE TABLES: they carry only
											// ID, CompanyID, EmployeeId, Role, CreatedAt — no actor, no reason, no
											// revocation, unlike PlatformRoleAssignments. Recorded here because it
											// is one of the reasons to migrate them.
											AddFirstAdministratorModuleRoles(context, company.CompanyID, adminEmployee.ID);
											await context.SaveChangesAsync();
											}   // end: a job title was available
										}
									}
								}
								else
								{
									adminMessage = "No administrator address was supplied, so this company has nobody who can administer it.";
								}
								// Commit the transaction if all operations succeed
								await transaction.CommitAsync();

								// Now that the transaction is committed, save the company image asynchronously
								if (model.CompanyImage != null)
								{
									// Call the method to save the company image outside the transaction
									await SaveCompanyImageAsync(model, company, attachments , employee);
								}

								// Prepare the return DTO
								var modelReturn = new CompanyDto
								{
									CompanyID = company.CompanyID,
									AdministratorProvisioned = adminProvisioned,
									AdministratorTempPassword = adminTempPassword,
									AdministratorMessage = adminMessage,
								};

								return modelReturn;
							}
							else
							{
								// Create and initialize a new company entity
								var company = context.Companies.Where(c => c.CompanyID == model.CompanyID).FirstOrDefault();

								company.ComoanyNameAr = model.CompanyNameAr;
								company.CompanyName = model.CompanyName;
								company.CompanyTypeId = model.CompanyTypeId;
								company.CountryID = model.CountryID;
								company.Address = model.Address;
								company.Description = model.Description;
								company.Email = model.Email;
								company.ParentCompany = model.ParentCompany;
								company.PhoneNumber = model.PhoneNumber;
								company.PostalCode = model.PostalCode;
								company.TaxNumber = model.TaxNumber;
								company.RegistrationNumber = model.RegistrationNumber;
								company.Website = model.Website;
								company.updatedBy = employee.ID;
								company.UpdatedAt = DateTime.UtcNow;


								await context.SaveChangesAsync();

								// Retrieve the parent company ID (if any)
								int? ParentID = context.Hierarchicals
									.Where(c => c.H_ObjectID == company.ParentCompany)
									.Select(c => (int?)c.H_ID)
									.FirstOrDefault();

								var CompanyisInHierarchicals = context.Hierarchicals.Where(c => c.H_ObjectID == model.CompanyID).FirstOrDefault();

								if (CompanyisInHierarchicals == null)
								{
									// Create and initialize a new hierarchical entity
									var hierarchical = new Hierarchical
									{
										H_Name = company.ComoanyNameAr,
										H_NameEn = company.CompanyName,
										H_ObjectID = company.CompanyID,
										H_Type = 1,
										H_Parent = ParentID
									};

									// Add the hierarchical entity to the database
									context.Hierarchicals.Add(hierarchical);
									await context.SaveChangesAsync();
								}
								else
								{
									// Create and initialize a new hierarchical entity
									var hierarchical = new Hierarchical
									{
										H_Name = company.ComoanyNameAr,
										H_NameEn = company.CompanyName,
										H_ObjectID = company.CompanyID,
										H_Type = 1,
										H_Parent = ParentID
									};

									// Add the hierarchical entity to the database
									await context.SaveChangesAsync();
								}

								

								// Commit the transaction if all operations succeed
								await transaction.CommitAsync();

								// Now that the transaction is committed, save the company image asynchronously
								if (model.CompanyImage != null || model.Attachments != null)
								{
									// Call the method to save the company image outside the transaction
									await SaveCompanyImageAsync(model, company, attachments, employee);
								}

								// Prepare the return DTO
								var modelReturn = new CompanyDto
								{
									CompanyID = company.CompanyID,
								};

								return modelReturn;
							}
							
						}
						catch (Exception ex)
						{
							// Rollback the transaction in case of an error
							await transaction.RollbackAsync();
							Console.WriteLine($"Error in SaveCompanyAsync: {ex.Message}");
							throw new Exception("An error occurred while adding the company", ex);
						}
					}
				}
			}
			catch (Exception ex)
			{
				// Handle critical errors at the outer level
				Console.WriteLine($"Critical Error in SaveCompanyAsync: {ex.Message}");
				throw new Exception("An error occurred while adding the company", ex);
			}
		}

		private async Task SaveCompanyImageAsync(CompanyDto model, Companies company, List<Models.Context.Admin.Attachment> attachments , EmployeeViewModel employee)
		{
			try
			{
				// Set root path for saving files
				var rootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Files");

				// Ensure root directory exists
				if (!Directory.Exists(rootPath))
				{
					Directory.CreateDirectory(rootPath);
				}

				// Set company folder path
				var companyFolderPath = Path.Combine(rootPath, company.CompanyID.ToString());
				if (!Directory.Exists(companyFolderPath))
				{
					Directory.CreateDirectory(companyFolderPath);
				}
				if (model.CompanyImage != null)
				{
					// Save company image
					var fileExtension = Path.GetExtension(model.CompanyImage.FileName);
					var fileName = $"{Guid.NewGuid()}{fileExtension}";
					var filePath = Path.Combine(companyFolderPath, fileName);

					using (var stream = new FileStream(filePath, FileMode.Create))
					{
						await model.CompanyImage.CopyToAsync(stream);
					}

					// Save the company image URL to the database
					company.CompanyImage = Path.Combine("Files", company.CompanyID.ToString(), fileName);
					_context.Companies.Update(company);
					await _context.SaveChangesAsync();
				}
			

				// Process attachments
				if (model.Attachments != null && model.Attachments.Any())
				{
					int index = 0; // Counter for attachments list

					foreach (var file in model.Attachments)
					{
						// Check if the current index exists in the attachments list
						if (index < attachments.Count)
						{
							var attachName = attachments[index].AttachName; // Get attachment name from the list
							index++; // Increment counter

							var fileExtensionF = Path.GetExtension(file.FileName);
							var fileNameF = $"{Guid.NewGuid()}{fileExtensionF}";
							var filePathF = Path.Combine(companyFolderPath, fileNameF);

							using (var stream = new FileStream(filePathF, FileMode.Create))
							{
								await file.CopyToAsync(stream);
							}

							var attachment = new Models.Context.Admin.Attachment
							{
								AttachName = attachName, // Use name from the attachments list
								attachPath = Path.Combine("Files", company.CompanyID.ToString(), fileNameF),
								FormID = 1,
								RequestID = company.CompanyID ,
								CreatedAt = DateTime.UtcNow,
								CreatedBy= employee.ID

							};
							_context.Attachments.Add(attachment);
						}
					}

					// Save changes for attachments
					await _context.SaveChangesAsync();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in SaveCompanyImageAsync: {ex.Message}");
				throw new Exception("An error occurred while processing the image", ex);
			}
		}


		private async Task SaveBranchImageAsync(BranchiesDto model, Branch branch, List<Models.Context.Admin.Attachment> attachments, EmployeeViewModel employee)
		{
			try
			{
				// Set root path for saving files
				var rootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Files");

				// Ensure root directory exists
				if (!Directory.Exists(rootPath))
				{
					Directory.CreateDirectory(rootPath);
				}

				// Set company folder path
				var companyFolderPath = Path.Combine(rootPath, branch.ID.ToString());
				if (!Directory.Exists(companyFolderPath))
				{
					Directory.CreateDirectory(companyFolderPath);
				}
				if (model.Image != null)
				{
					// Save company image
					var fileExtension = Path.GetExtension(model.Image.FileName);
					var fileName = $"{Guid.NewGuid()}{fileExtension}";
					var filePath = Path.Combine(companyFolderPath, fileName);

					using (var stream = new FileStream(filePath, FileMode.Create))
					{
						await model.Image.CopyToAsync(stream);
					}

					// Save the company image URL to the database
					branch.ImageUrl = Path.Combine("Files", branch.ID.ToString(), fileName);
					_context.Branches.Update(branch);
					await _context.SaveChangesAsync();
				}


				// Process attachments
				if (model.Attachments != null && model.Attachments.Any())
				{
					int index = 0; // Counter for attachments list

					foreach (var file in model.Attachments)
					{
						// Check if the current index exists in the attachments list
						if (index < attachments.Count)
						{
							var attachName = attachments[index].AttachName; // Get attachment name from the list
							index++; // Increment counter

							var fileExtensionF = Path.GetExtension(file.FileName);
							var fileNameF = $"{Guid.NewGuid()}{fileExtensionF}";
							var filePathF = Path.Combine(companyFolderPath, fileNameF);

							using (var stream = new FileStream(filePathF, FileMode.Create))
							{
								await file.CopyToAsync(stream);
							}

							var attachment = new Models.Context.Admin.Attachment
							{
								AttachName = attachName, // Use name from the attachments list
								attachPath = Path.Combine("Files", branch.ID.ToString(), fileNameF),
								FormID = 2,
								RequestID = branch.ID,
								CreatedAt = DateTime.UtcNow,
								CreatedBy = employee.ID

							};
							_context.Attachments.Add(attachment);
						}
					}

					// Save changes for attachments
					await _context.SaveChangesAsync();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in SaveCompanyImageAsync: {ex.Message}");
				throw new Exception("An error occurred while processing the image", ex);
			}
		}
		public async Task<BranchiesDto> SaveBranchAsync(BranchiesDto model, List<Models.Context.Admin.Attachment> attachments)
		{
			try
			{
				var employeeJson = _httpContextAccessor.HttpContext?.Session.GetString("Employee");
				var employee = employeeJson != null ? JsonConvert.DeserializeObject<EmployeeViewModel>(employeeJson) : null;


				using (var scope = _serviceProvider.CreateScope())
				{
					var context = scope.ServiceProvider.GetRequiredService<CrossDbContext>();

					// Start a database transaction
					await using (var transaction = await ScopedTx.BeginOrJoinAsync(context))
					{
						try
						{
							if (model.ID == 0)
							{
								// Create and initialize a new company entity
								var branch = new Branch
								{
									NameAr = model.NameAr,
									Name = model.Name,
									CompanyID = model.CompanyID,
									CountryID = model.CountryID,
									Location = model.Location,
									Description = model.Description,
									Email = model.Email,
									PhoneNumber = model.PhoneNumber,
									CreatedBy = employee.ID,
									CreatedAt = DateTime.UtcNow
								};

								// Add the company to the database
								context.Branches.Add(branch);
								await context.SaveChangesAsync();

								// Retrieve the parent company ID (if any)
								int? ParentID = context.Hierarchicals.Where(c => c.H_ObjectID == branch.CompanyID).Select(c => c.H_ID).FirstOrDefault();

								// Create and initialize a new hierarchical entity
								var hierarchical = new Hierarchical
								{
									H_Name = branch.NameAr,
									H_NameEn = branch.Name,
									H_ObjectID = branch.CompanyID,
									H_Type = 2,
									H_Parent = ParentID
								};

								// Add the hierarchical entity to the database
								context.Hierarchicals.Add(hierarchical);
								await context.SaveChangesAsync();

								// Commit the transaction if all operations succeed
								await transaction.CommitAsync();

								// Now that the transaction is committed, save the company image asynchronously
								if (model.Image != null)
								{
									// Call the method to save the company image outside the transaction
									await SaveBranchImageAsync(model, branch, attachments, employee);
								}

								// Prepare the return DTO
								var modelReturn = new BranchiesDto
								{
									ID = branch.ID,
								};

								return modelReturn;
							}
							else
							{
								// Create and initialize a new company entity
								var branch = context.Branches.Where(c => c.ID == model.ID).FirstOrDefault();

								branch.Name = model.Name;
								branch.NameAr = model.NameAr;
								branch.CompanyID = model.CompanyID;	
								branch.CountryID = model.CountryID;
								branch.Location = model.Location;
								branch.Description = model.Description;
								branch.Email = model.Email;
								branch.PhoneNumber = model.PhoneNumber;
								
								branch.updatedBy = employee.ID;
								branch.UpdatedAt = DateTime.UtcNow;


								await context.SaveChangesAsync();

								// Retrieve the parent company ID (if any)
								int? ParentID =context.Hierarchicals.Where(c=>c.H_ObjectID == branch.CompanyID).Select(c=>c.H_ID).FirstOrDefault();

								var CompanyisInHierarchicals = context.Hierarchicals.Where(c => c.H_ObjectID == model.ID).FirstOrDefault();

								if (CompanyisInHierarchicals == null)
								{
									// Create and initialize a new hierarchical entity
									var hierarchical = new Hierarchical
									{
										H_Name = branch.NameAr,
										H_NameEn = branch.Name,
										H_ObjectID = branch.CompanyID,
										H_Type = 2,
										H_Parent = ParentID
									};

									// Add the hierarchical entity to the database
									context.Hierarchicals.Add(hierarchical);
									await context.SaveChangesAsync();
								}
								else
								{
									// Create and initialize a new hierarchical entity
									var hierarchical = new Hierarchical
									{
										H_Name = branch.NameAr,
										H_NameEn = branch.Name,
										H_ObjectID = branch.CompanyID,
										H_Type = 1,
										H_Parent = ParentID
									};

									// Add the hierarchical entity to the database
									await context.SaveChangesAsync();
								}



								// Commit the transaction if all operations succeed
								await transaction.CommitAsync();

								// Now that the transaction is committed, save the company image asynchronously
								if (model.Image != null || model.Attachments != null)
								{
									// Call the method to save the company image outside the transaction
									await SaveBranchImageAsync(model, branch, attachments, employee);
								}

								// Prepare the return DTO
								var modelReturn = new BranchiesDto
								{
									ID = branch.ID,
								};

								return modelReturn;
							}

						}
						catch (Exception ex)
						{
							// Rollback the transaction in case of an error
							await transaction.RollbackAsync();
							Console.WriteLine($"Error in SaveCompanyAsync: {ex.Message}");
							throw new Exception("An error occurred while adding the company", ex);
						}
					}
				}
			}
			catch (Exception ex)
			{
				// Handle critical errors at the outer level
				Console.WriteLine($"Critical Error in SaveCompanyAsync: {ex.Message}");
				throw new Exception("An error occurred while adding the company", ex);
			}
		}


		public async Task<BranchiesDto> GetBranchData(int? ID)
		{
			try
			{
				var branches = await _context.Branches
											   .Include(c => c.Company)
											   .Include(c => c.Country)
											   .ToListAsync();

				var companyDtos = branches.Select(c => new BranchiesDto
				{
					ID = c.ID , 
					CompanyID = c.CompanyID,
					Name = c.Name,
					NameAr = c.NameAr,
					Location	 = c.Location,
					PhoneNumber = c.PhoneNumber,
					Email = c.Email,
					ImageUrl = c.ImageUrl,
					countriesLookups = _context.CountriesLookup.ToList(),
					CountryID = c.CountryID,
					companies = _context.Companies.Select(co=> new CompanyDto { CompanyID = co.CompanyID , CompanyName = co.CompanyName , CompanyNameAr = co.ComoanyNameAr}).ToList(),	
					AttachmentsFiles = _context.Attachments.Where(a => a.FormID == 2 && a.RequestID == c.ID).ToList(),
					Description = c.Description
				}).Where(c => c.ID == ID).FirstOrDefault();

				return companyDtos;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in GetAllCompaniesAsync: {ex.Message}");
				return new BranchiesDto();
			}
		}

	}
}
