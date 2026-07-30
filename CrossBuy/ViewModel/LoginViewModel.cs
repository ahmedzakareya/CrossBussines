using System.ComponentModel.DataAnnotations;

namespace CrossBuy.ViewModel
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "اسم المستخدم مطلوب")]
        public string UserName { get; set; }

        [Required(ErrorMessage = "كلمة المرور مطلوبة")]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Display(Name = "تذكرني")]
        public bool RememberMe { get; set; }
    }
}
