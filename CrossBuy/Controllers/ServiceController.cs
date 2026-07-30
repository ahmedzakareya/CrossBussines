using CrossBuy.BL;
using CrossBuy.Models;
//using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Newtonsoft.Json;

namespace CrossBuy.Controllers
{
	[SessionValidation]

	public class ServiceController : Controller
	{
        private readonly ICompanyService _companyService;
        private readonly IStringLocalizer<CrossBuy.SharedResources> L;
        public ServiceController(ICompanyService companyService, IStringLocalizer<CrossBuy.SharedResources> localizer)
        {
            _companyService = companyService;
            L = localizer;
        }
        [SessionValidation]
		[HttpGet]
		public IActionResult Index()
		{
			// Back-office landing = the HR/Administration dashboard (Admin/Index)
			return RedirectToAction("Index", "Admin");
		}

		[SessionValidation]
		[HttpGet]
		public async Task<IActionResult> CompaniesList()
		{
            var companies = await _companyService.GetAllCompaniesAsync();
            return View(companies);
        }



		[SessionValidation]
		[HttpGet]
		public async  Task<IActionResult> CompanyDetails(int? ID)
		{
			CompanyDto companies ;
			if (ID==null)
			{
				companies = await _companyService.GetCompanyForm();

			}
			else
			{
				companies = await _companyService.GetCompanyData(ID);

			}
			return View(companies);
        }


		[SessionValidation]
		[HttpPost]
		public async Task<IActionResult> CompanyDetails(CompanyDto model, string attachmentsFiles)
		{
			// Check if the model state is valid
			if (ModelState.IsValid)
			{
				try
				{
					// Check if the attachmentsFiles string is empty or null
					if (string.IsNullOrEmpty(attachmentsFiles))
					{
						// Return failure if no attachments are provided
						return Json(new { success = false, message = "No attachment files provided" });
					}

					// Try to deserialize the attachments data from the JSON string

					List<Models.Context.Admin.Attachment> attachments ;
					if (model.Attachments == null || model.Attachments.Count == 0)
					{
						attachments = null;
					}
					try
					{
						attachments = JsonConvert.DeserializeObject<List<Models.Context.Admin.Attachment>>(attachmentsFiles);
					}
					catch (JsonException ex)
					{
						// Handle error if deserialization fails
						return Json(new { success = false, message = "Failed to process attachment files: " + ex.Message });
					}

					// Call the service method to save the company data
					var company = await _companyService.SaveCompanyAsync(model, attachments);

					// Check if the company was saved successfully (CompanyID should be valid)
					if (company != null && company.CompanyID > 0) // assuming CompanyID is a positive value if saved successfully
					{
						// Return success message if company was saved successfully
						return Json(new { success = true, message = CrossBuy.Resources.SharedResources.Success });
					}
					else
					{
						// Return failure message if company could not be saved
						return Json(new { success = false, message = CrossBuy.Resources.SharedResources.Error });
					}
				}
				catch (Exception ex)
				{
					// Handle any other errors (e.g., database connection issues) and provide error message
					return Json(new { success = false, message = CrossBuy.Resources.SharedResources.Error + " " + ex.Message });
				}
			}

			// If the model validation fails, return failure message
			return Json(new { success = false, message = "Invalid data" });
		}

		public async Task<IActionResult> BranchesList()
		{
			var branches = await _companyService.GetAllBranchesAsync();
			return View(branches);
		}

		[HttpGet]
		public async Task<IActionResult> GetCompanyDataById(int id)
		{
			try
			{
				var data = await _companyService.GetCompanyData(id);

				return Json(new
				{
					data.CompanyNameAr,
					data.CompanyName,
					data.Email,
					data.PhoneNumber,
					data.CompanyID , 
					data.CompanyImageUrl , 
					data.CompanyTypeName,
					data.CompanyTypeNameEn,
					data.ParentCompanyName , 
					data.ParentCompanyNameEn,
					data.CountryName , 
					data.CountryNameAr , 
					data.Website,
					data.Address


				});
			}
			catch (Exception ex)
			{
				return StatusCode(500, L["Internal error:"].Value + " " + ex.Message);
			}
		}


		public async Task< IActionResult> BranchDetails(int? ID)
		{
			BranchiesDto  Model= null; 
			if (ID == null )
			{
				Model = await _companyService.GetBranchForm();
			}
			else
			{
				Model = await _companyService.GetBranchData(ID);
			}
			return View(Model);
		}
		[SessionValidation]
		[HttpPost]
		public async Task<IActionResult> BranchDetails(BranchiesDto model, string attachmentsFiles)
		{
			// Check if the model state is valid
			if (ModelState.IsValid)
			{
				try
				{
					// Check if the attachmentsFiles string is empty or null
					if (string.IsNullOrEmpty(attachmentsFiles))
					{
						// Return failure if no attachments are provided
						return Json(new { success = false, message = "No attachment files provided" });
					}

					// Try to deserialize the attachments data from the JSON string

					List<Models.Context.Admin.Attachment> attachments;
					if (model.Attachments == null || model.Attachments.Count == 0)
					{
						attachments = null;
					}
					try
					{
						attachments = JsonConvert.DeserializeObject<List<Models.Context.Admin.Attachment>>(attachmentsFiles);
					}
					catch (JsonException ex)
					{
						// Handle error if deserialization fails
						return Json(new { success = false, message = "Failed to process attachment files: " + ex.Message });
					}

					// Call the service method to save the company data
					var company = await _companyService.SaveBranchAsync(model, attachments);

					// Check if the company was saved successfully (CompanyID should be valid)
					if (company != null && company.ID > 0) // assuming CompanyID is a positive value if saved successfully
					{
						// Return success message if company was saved successfully
						return Json(new { success = true, message = CrossBuy.Resources.SharedResources.Success });
					}
					else
					{
						// Return failure message if company could not be saved
						return Json(new { success = false, message = CrossBuy.Resources.SharedResources.Error });
					}
				}
				catch (Exception ex)
				{
					// Handle any other errors (e.g., database connection issues) and provide error message
					return Json(new { success = false, message = CrossBuy.Resources.SharedResources.Error + " " + ex.Message });
				}
			}

			// If the model validation fails, return failure message
			return Json(new { success = false, message = "Invalid data" });
		}
	}
}
