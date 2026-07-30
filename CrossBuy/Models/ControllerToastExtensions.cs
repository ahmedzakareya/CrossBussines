using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Models
{
    /// <summary>
    /// Standardized server -> toastr bridge for redirect-based (non-AJAX) actions.
    /// Set a message here before a RedirectToAction; the shared _CbToastr partial
    /// (included in the backend layouts) renders the toast on the next page load.
    ///
    /// Usage:  this.ToastSuccess(T("تم اعتماد المستند", "Document approved"));
    ///         return RedirectToAction(nameof(Approvals));
    /// </summary>
    public static class ControllerToastExtensions
    {
        public static void ToastSuccess(this Controller c, string message) => c.TempData["CbToastSuccess"] = message;
        public static void ToastError(this Controller c, string message)   => c.TempData["CbToastError"]   = message;
        public static void ToastWarning(this Controller c, string message) => c.TempData["CbToastWarning"] = message;
        public static void ToastInfo(this Controller c, string message)    => c.TempData["CbToastInfo"]    = message;
    }
}
