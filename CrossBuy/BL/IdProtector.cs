using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace CrossBuy.BL
{
    // Encrypts integer entity IDs into opaque, URL-safe tokens for PUBLIC (end-user) storefront links, so we never expose
    // raw sequential DB IDs in the address bar. Uses the app's Data Protection key ring (machine-wide, already configured).
    // Tokens are tamper-evident: a modified/invalid token unprotects to null -> the controller returns 404.
    public interface IIdProtector
    {
        string Protect(int id);
        int? Unprotect(string? token);
    }

    public class IdProtector : IIdProtector
    {
        private readonly IDataProtector _p;
        // versioned purpose string: rotating it invalidates all old links at once if ever needed
        public IdProtector(IDataProtectionProvider provider) => _p = provider.CreateProtector("CrossBuy.Store.PublicIds.v1");

        public string Protect(int id) => _p.Protect(id.ToString(CultureInfo.InvariantCulture));

        public int? Unprotect(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try { return int.Parse(_p.Unprotect(token), CultureInfo.InvariantCulture); }
            catch { return null; }   // tampered / malformed / wrong-purpose token
        }
    }
}
