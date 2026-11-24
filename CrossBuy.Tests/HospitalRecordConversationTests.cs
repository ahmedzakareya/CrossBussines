using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// A CONVERSATION ON EVERY HOSPITAL RECORD (owner 2026-11-24). What must never happen:
	//   · the reception desk or the cashier reading what the care team wrote on a clinical record (a visit, a result,
	//     a labour...) - they talk on the file, the room, the bill and the estimate only;
	//   · a record type joining the panel without the registry knowing it (comments on, a ResolveAsync case), which
	//     denies every comment with a message that blames the company;
	//   · a record conversation endpoint without its gate.
	// ==========================================================================================
	public class HospitalRecordConversationTests
	{
		private static BusinessContext Ctx(int employeeId) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task Grant(PlatformTestHost host, int employeeId, string role)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
		}

		private static readonly string[] Administrative = { EntityRegistry.HospitalPatient, EntityRegistry.HospitalRoom, EntityRegistry.HospitalBill, EntityRegistry.HospitalEstimate };

		[Fact]
		public async Task The_desk_talks_on_administrative_records_and_never_reads_a_clinical_one()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 20, HospitalRoles.HospitalReception);
			await Grant(host, 22, HospitalRoles.HospitalCashier);
			await Grant(host, 21, HospitalRoles.HospitalNurse);
			var a = Access(host);
			foreach (var code in Administrative)
				foreach (var who in new[] { 20, 21, 22 })
					Assert.True(await a.CanAsync(Ctx(who), HospitalActions.Read, PermissionTarget.ForEntity(code, 7)), who + " " + code);
			foreach (var code in HospitalActions.ClinicalRecords)
			{
				Assert.False(await a.CanAsync(Ctx(20), HospitalActions.Read, PermissionTarget.ForEntity(code, 7)), "reception " + code);
				Assert.False(await a.CanAsync(Ctx(22), HospitalActions.Read, PermissionTarget.ForEntity(code, 7)), "cashier " + code);
				Assert.True(await a.CanAsync(Ctx(21), HospitalActions.Read, PermissionTarget.ForEntity(code, 7)), "nurse " + code);
			}
			// the module-level read (no record) is unchanged for the desk
			Assert.True(await a.CanAsync(Ctx(20), HospitalActions.Read, null));
		}

		[Fact]
		public void Every_family_is_registered_with_comments_resolved_and_checked_in_the_callers_company()
		{
			var families = (IReadOnlySet<string>)typeof(HospitalController).GetField("HospitalConversationFamilies", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
			Assert.Equal(11, families.Count);
			Assert.Subset(families.ToHashSet(), HospitalActions.ClinicalRecords.ToHashSet());
			Assert.All(Administrative, c => Assert.Contains(c, families));

			var registry = new EntityRegistry(null!);
			var source = File.ReadAllText(Path.Combine(Root(), "CrossBuy", "BL", "Platform", "EntityRegistry.cs"));
			var gate = File.ReadAllText(Path.Combine(Root(), "CrossBuy", "Controllers", "HospitalController.Conversations.cs"));
			foreach (var code in families)
			{
				var d = registry.GetDefinition(code);
				Assert.True(d.SupportsComments, code);
				Assert.Equal(EntityRegistry.ScopeHospital, d.PermissionScope);
				Assert.Contains($"case {code}:", source);                       // the registry can resolve it (two-step registration)
				Assert.Contains($"EntityRegistry.{code} => await", gate);        // the gate looks the record up
			}
		}

		[Fact]
		public void Every_record_endpoint_carries_the_hospital_permission_and_the_desk_marker()
		{
			// the @-picker's endpoint ships with the Communication work it stands on (HospitalController.ConversationMentions)
			foreach (var name in new[] { "RecordConversation", "RecordConversationAdd", "RecordConversationReact", "RecordConversationMentions" })
			{
				var m = typeof(HospitalController).GetMethod(name);
				if (m == null && name == "RecordConversationMentions") continue;
				Assert.NotNull(m);
				Assert.NotNull(m!.GetCustomAttribute<HospitalPermAttribute>());
				Assert.NotNull(m.GetCustomAttribute<HospitalReceptionAllowedAttribute>());
			}
		}

		private static string Root()
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy", "CrossBuy.csproj"))) dir = dir.Parent;
			return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
		}
	}
}
