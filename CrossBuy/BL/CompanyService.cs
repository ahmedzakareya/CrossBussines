using AutoMapper;
using Azure.Core;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
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
							throw new Exception("حدث خطأ أثناء إضافة الشركة", ex);
						}
					}
				}
			}
			catch (Exception ex)
			{
				// Handle critical errors at the outer level
				Console.WriteLine($"Critical Error in SaveCompanyAsync: {ex.Message}");
				throw new Exception("حدث خطأ  أثناء إضافة الشركة", ex);
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
				throw new Exception("حدث خطأ أثناء معالجة الصورة", ex);
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
				throw new Exception("حدث خطأ أثناء معالجة الصورة", ex);
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
							throw new Exception("حدث خطأ أثناء إضافة الشركة", ex);
						}
					}
				}
			}
			catch (Exception ex)
			{
				// Handle critical errors at the outer level
				Console.WriteLine($"Critical Error in SaveCompanyAsync: {ex.Message}");
				throw new Exception("حدث خطأ  أثناء إضافة الشركة", ex);
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
