using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H163: documents and communication at the desk. What must never happen:
	//   · an interpreter asked for with no language, or "other help" with no word;
	//   · a guardian or attorney recorded without an ID number, or a representative past their end date shown as current;
	//   · a message sent to a patient who asked for none;
	//   · a scan file accepted by its name (only its own bytes say what it is);
	//   · another company's patient touched.
	// ==========================================================================================
	public class HospitalPatientContactTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime NowUtc = new(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static async Task<(PlatformTestHost Host, HospitalPatientContactService Svc, int Patient)> World()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M", Phone = "+96550001122" }, 7, NowUtc)).Id!.Value;
			return (host, new HospitalPatientContactService(host.Db, access), p);
		}

		private static HospitalContactInput Pref(params string[] needs) => new() { Channel = HospitalContactChannel.Sms, Language = "ar", BestTime = HospitalBestTime.Morning, Needs = needs.ToList() };

		[Fact]
		public void Help_and_representatives_say_what_they_need()
		{
			Assert.Equal("Write the language the interpreter must speak", HospitalPatientContactRules.Refuse(Pref(HospitalAssistanceNeed.Interpreter)));
			Assert.Equal("Write what other help the patient needs", HospitalPatientContactRules.Refuse(Pref(HospitalAssistanceNeed.Other)));
			Assert.Equal("Choose the help the patient needs from the list", HospitalPatientContactRules.Refuse(Pref("Jetpack")));
			Assert.Null(HospitalPatientContactRules.Refuse(Pref(HospitalAssistanceNeed.Wheelchair)));
			Assert.Equal("A guardian or an attorney needs an ID number", HospitalPatientContactRules.Refuse(new HospitalRepresentativeInput { Authority = HospitalRepresentativeAuthority.Guardian, Name = "Hana", Phone = "1" }, Today));
			Assert.Null(HospitalPatientContactRules.Refuse(new HospitalRepresentativeInput { Authority = HospitalRepresentativeAuthority.NextOfKin, Name = "Hana", Phone = "1" }, Today));
			Assert.Equal("The end date cannot be in the past", HospitalPatientContactRules.Refuse(new HospitalRepresentativeInput { Authority = HospitalRepresentativeAuthority.Parent, Name = "Hana", Phone = "1", ValidTo = Today.AddDays(-1) }, Today));
		}

		[Theory]
		[InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/pdf")]
		[InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
		[InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, null)]   // an executable named card.pdf is still refused
		public void A_scan_is_known_by_its_own_bytes(byte[] head, string? type)
			=> Assert.Equal(type, HospitalPatientContactRules.Sniff(head)?.ContentType);

		[Fact]
		public async Task Preferences_are_one_per_patient_and_the_card_shows_the_help_and_current_representatives()
		{
			var (host, svc, p) = await World(); using var _h = host;
			Assert.True((await svc.SavePreferenceAsync(Ctx(), p, Pref(HospitalAssistanceNeed.Wheelchair), 7, NowUtc)).Ok);
			var two = Pref(HospitalAssistanceNeed.Interpreter, HospitalAssistanceNeed.Wheelchair); two.InterpreterLanguage = "Urdu";
			Assert.True((await svc.SavePreferenceAsync(Ctx(), p, two, 7, NowUtc)).Ok);
			Assert.Equal(1, await host.Db.Set<HospitalContactPreference>().CountAsync());
			Assert.True((await svc.AddRepresentativeAsync(Ctx(), p, new HospitalRepresentativeInput { Authority = HospitalRepresentativeAuthority.Parent, Name = "Hana", Phone = "+96550000001" }, 7, Today, NowUtc)).Ok);
			var old = (await svc.AddRepresentativeAsync(Ctx(), p, new HospitalRepresentativeInput { Authority = HospitalRepresentativeAuthority.NextOfKin, Name = "Ali", Phone = "+96550000002", ValidTo = Today }, 7, Today, NowUtc)).Id!.Value;
			var (needs, interp, reps) = await svc.CardAsync(Ctx(), p, Today);
			Assert.Equal(new[] { HospitalAssistanceNeed.Wheelchair, HospitalAssistanceNeed.Interpreter }, needs.ToArray());   // in the list's order
			Assert.Equal("Urdu", interp);
			Assert.Equal(2, reps.Count);
			Assert.Single((await svc.CardAsync(Ctx(), p, Today.AddDays(1))).Representatives);   // Ali's end date passed
			Assert.Equal("Write why the representative is stopped", (await svc.StopRepresentativeAsync(Ctx(), old, " ", 7, NowUtc)).Error);
			Assert.True((await svc.StopRepresentativeAsync(Ctx(), old, "no longer the contact", 7, NowUtc)).Ok);
			Assert.Equal("This representative is stopped already", (await svc.StopRepresentativeAsync(Ctx(), old, "again", 7, NowUtc)).Error);
			Assert.Equal("not_found", (await svc.SavePreferenceAsync(Ctx(2), p, Pref(), 7, NowUtc)).Error);
			Assert.Equal(2, (await svc.ViewAsync(Ctx(), p))!.Representatives.Count);   // a stopped one stays on the file
		}

		[Fact]
		public async Task A_message_goes_through_the_outbox_unless_the_patient_asked_for_none()
		{
			var (host, svc, p) = await World(); using var _h = host;
			Assert.Equal("Write the message", (await svc.SendMessageAsync(Ctx(), p, null, " ", 7, NowUtc)).Error);
			Assert.True((await svc.SendMessageAsync(Ctx(), p, null, "يرجى إحضار بطاقة الهوية غدًا", 7, NowUtc)).Ok);
			var m = await host.Db.Set<HospitalSmsMessage>().SingleAsync();
			Assert.Equal((HospitalSmsStatus.Queued, "Desk"), (m.Status, m.SourceType));
			host.Db.Add(new HospitalSmsOptOut { CompanyId = 1, PatientId = p, At = NowUtc }); await host.Db.SaveChangesAsync();
			Assert.Equal("The patient asked for no messages", (await svc.SendMessageAsync(Ctx(), p, null, "again", 7, NowUtc.AddMinutes(1))).Error);
			var v = (await svc.ViewAsync(Ctx(), p))!;
			Assert.True(v.OptedOut);
			Assert.Single(v.Messages);
		}

		[Theory]
		[InlineData(typeof(HospitalContactPreference), "HospitalContactPreferences")]
		[InlineData(typeof(HospitalPatientRepresentative), "HospitalPatientRepresentatives")]
		public void Each_table_is_read_in_the_shape_its_slice_creates(Type entity, string table)
		{
			var model = HospitalSliceParity.Model(HospitalPharmacyModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h163_patient_contact.sql"), entity, table);
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h163_patient_contact.sql"), entity, table);
		}
	}
}
