using System.ComponentModel.DataAnnotations;

namespace CrossBuy.ViewModel
{
    public class LoginViewModel
    {
        // The message comes from the resource file, not from a literal here: these two strings are
        // rendered by the server straight into the sign-in page's error notice, so an English literal
        // reaches an Arabic screen at a layer no view translation can correct.
        [Required(ErrorMessageResourceType = typeof(CrossBuy.Resources.SharedResources),
                  ErrorMessageResourceName = "UserNameRequired")]
        public string UserName { get; set; }

        [Required(ErrorMessageResourceType = typeof(CrossBuy.Resources.SharedResources),
                  ErrorMessageResourceName = "PasswordRequired")]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }
}
