using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // SHARED SUPPORT FOR THE CONTACT-PHOTOGRAPH BOUNDARY.
    //
    // IPortalPhotoService is the ONE authority over a piece of personal data that two different
    // audiences ask for, so its doubles live in one file rather than being re-invented per fixture.
    // Two fixtures that each grew their own "can the staff member read this" stub is how one of them
    // ends up testing a rule the product does not have.
    //
    // THE CRM DOUBLE ANSWERS ONE QUESTION AND REFUSES THE REST. Every other member throws, because a
    // test that silently got a default from an unimplemented member would be asserting against this
    // file instead of against the product.
    // =============================================================================================

    /// A CRM access service that answers CanAsync with a fixed verdict. The verdict is the thing
    /// under test in the staff-read cases, so it is a constructor argument rather than a guess.
    internal sealed class StubCrmAccess : ICrmAccessService
    {
        private readonly bool _can;
        public StubCrmAccess(bool can) { _can = can; }

        public Task<bool> CanAsync(string action) => Task.FromResult(_can);

        // Deliberately unimplemented: IPortalPhotoService consults CanAsync and nothing else, and a
        // future member that started consulting one of these should fail loudly here rather than
        // quietly receive an empty set.
        public int? CurrentEmployeeId() => throw new NotSupportedException("not consulted by the photo authority");
        public Task<List<string>> MyRolesAsync() => throw new NotSupportedException("not consulted by the photo authority");
        public Task<HashSet<int>?> VisibleOwnerIdsAsync() => throw new NotSupportedException("not consulted by the photo authority");
        public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => throw new NotSupportedException("not consulted by the photo authority");
        public Task<string> RoleLabelAsync(bool isAr) => throw new NotSupportedException("not consulted by the photo authority");
    }

    /// A content root in the system temp area, so a test writes real files to a real disk without
    /// touching the repository. Each instance gets its own directory: two tests sharing a store is
    /// how a "foreign photo is refused" case accidentally passes because the file was never there.
    internal sealed class TempContentRoot : IHostEnvironment, IDisposable
    {
        public TempContentRoot()
        {
            ContentRootPath = Path.Combine(Path.GetTempPath(), "cb-portal-photo-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(ContentRootPath);
        }

        public string ApplicationName { get; set; } = "CrossBuy.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        /// Where PortalPhotoService puts a company's files. Tests use this to plant bytes directly,
        /// which is the only way to prove the READ rules without going through the write path.
        public string PhotoFolder(int companyId)
            => Path.Combine(ContentRootPath, "App_Data", "portal-photos", companyId.ToString());

        /// Writes a file with a name of the shape the service generates, and returns the LOCATOR the
        /// PortalUsers row would carry.
        public string PlantPhoto(int companyId, string extension = ".jpg", string body = "photo-bytes")
        {
            var folder = PhotoFolder(companyId);
            Directory.CreateDirectory(folder);
            var name = Guid.NewGuid().ToString("N") + extension;
            File.WriteAllText(Path.Combine(folder, name), body);
            return $"{companyId}/{name}";
        }

        public void Dispose()
        {
            try { if (Directory.Exists(ContentRootPath)) Directory.Delete(ContentRootPath, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not a test failure */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class PortalPhotoTest
    {
        public static PortalPhotoService Service(
            CrossDbContext db, IPortalContextAccessor contexts, IHostEnvironment env, bool crmCan = true)
            => new(db, contexts, new StubCrmAccess(crmCan), env);
    }
}
