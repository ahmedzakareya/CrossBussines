using Microsoft.AspNetCore.Identity;

namespace CrossBuy.Models.Context.Admin
{
    public class Users : IdentityUser
    {
        public bool IsEndUser { get; set; }
        public bool IsActive { get; set; }
    }
}
