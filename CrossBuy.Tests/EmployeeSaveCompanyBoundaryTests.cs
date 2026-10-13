using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Xunit;

namespace CrossBuy.Tests;

public class EmployeeSaveCompanyBoundaryTests
{
    [Fact]
    public async Task Foreign_company_on_form_is_refused_before_creating_employee()
    {
        using var host = new PlatformTestHost(companyId: 41);
        var service = new EmployeeService(host.Db, new StubContextAccessor(new BusinessContext { CompanyId = 41 }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveEmployeeAsync(
            new EmployeeViewModel { EmpCompanyID = 77 }, null!, ""));
        Assert.Empty(host.Db.Employee);
        Assert.False(host.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Unresolved_caller_cannot_create_employee_using_posted_company()
    {
        using var host = new PlatformTestHost(companyId: null);
        var service = new EmployeeService(host.Db, StubContextAccessor.Unresolved());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveEmployeeAsync(
            new EmployeeViewModel { EmpCompanyID = 77 }, null!, ""));
        Assert.Empty(host.Db.Employee);
        Assert.False(host.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Foreign_employee_id_cannot_be_updated_or_recreated_locally()
    {
        using var host = new PlatformTestHost(companyId: 41);
        host.Db.Employee.Add(new Employee
        {
            ID = 901, EmpCompanyID = 77, IsActive = true,
            FullName = "Neighbour", FullNameEn = "Neighbour", FirstName = "N", LastName = "N",
            Email = "neighbour@x.local", PhoneNumber = "0901", Address = "", Gender = "F",
            MaritalStatus = "Single", ProfileImage = "", UserId = "u901", JobTitleID = 1,
            DateOfJoining = new DateTime(2026, 6, 1),
        });
        await host.Db.SaveChangesAsync();
        host.Db.ChangeTracker.Clear();
        var service = new EmployeeService(host.Db, new StubContextAccessor(new BusinessContext { CompanyId = 41 }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveEmployeeAsync(
            new EmployeeViewModel { ID = 901, EmpCompanyID = 41, FullName = "Changed" }, null!, ""));
        var existing = Assert.Single(host.Db.Employee);
        Assert.Equal(77, existing.EmpCompanyID);
        Assert.Equal("Neighbour", existing.FullName);
        Assert.False(host.Db.ChangeTracker.HasChanges());
    }
}
