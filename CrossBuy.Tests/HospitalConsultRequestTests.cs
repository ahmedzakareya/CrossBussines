using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H77 — inpatient consultation requests, with no database: what a request needs, the answer window each
	// urgency gives, overdue, who may answer (the specialty, or only the doctor named), what an answer needs, and the
	// table against its slice.
	// ==========================================================================================
	public class HospitalConsultRequestTests
	{
		private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void A_request_names_the_specialty_its_urgency_and_the_question()
		{
			Assert.Null(HospitalConsultRequestRules.RequestRefusal(new HospitalConsultRequestInput { DepartmentId = 3, Urgency = HospitalConsultUrgency.Urgent, Question = "Chest pain, troponin rising — cardiology opinion?" }));
			Assert.Equal("Choose the specialty", HospitalConsultRequestRules.RequestRefusal(new HospitalConsultRequestInput { Urgency = HospitalConsultUrgency.Routine, Question = "x" }));
			Assert.Equal("Choose how urgent it is", HospitalConsultRequestRules.RequestRefusal(new HospitalConsultRequestInput { DepartmentId = 3, Urgency = "Soon", Question = "x" }));
			Assert.Equal("Write the question for the consultant", HospitalConsultRequestRules.RequestRefusal(new HospitalConsultRequestInput { DepartmentId = 3, Urgency = HospitalConsultUrgency.Routine, Question = "  " }));
			Assert.Equal("The question is too long", HospitalConsultRequestRules.RequestRefusal(new HospitalConsultRequestInput { DepartmentId = 3, Urgency = HospitalConsultUrgency.Routine, Question = new string('x', 1001) }));
		}

		[Fact]
		public void Urgency_sets_the_answer_window_and_an_open_request_past_it_is_overdue()
		{
			Assert.Equal(TimeSpan.FromHours(1), HospitalConsultUrgency.Window(HospitalConsultUrgency.Emergency));
			Assert.Equal(TimeSpan.FromHours(4), HospitalConsultUrgency.Window(HospitalConsultUrgency.Urgent));
			Assert.Equal(TimeSpan.FromHours(24), HospitalConsultUrgency.Window(HospitalConsultUrgency.Routine));
			var r = new HospitalConsultRequest { Status = HospitalConsultRequestStatus.Accepted, DueAt = Now.AddMinutes(-1) };
			Assert.True(HospitalConsultRequestRules.Overdue(r, Now));
			r.Status = HospitalConsultRequestStatus.Answered;
			Assert.False(HospitalConsultRequestRules.Overdue(r, Now));
			Assert.False(HospitalConsultRequestRules.Overdue(new HospitalConsultRequest { Status = HospitalConsultRequestStatus.Requested, DueAt = Now.AddMinutes(5) }, Now));
		}

		[Fact]
		public void The_specialty_answers_unless_a_doctor_is_named_and_an_administrator_always_can()
		{
			var toDep = new HospitalConsultRequest { DepartmentId = 3 };
			var toDoctor = new HospitalConsultRequest { DepartmentId = 3, PractitionerId = 12 };
			var cardiologist = new[] { (11, (int?)3) };
			var named = new[] { (12, (int?)3) };
			var surgeon = new[] { (20, (int?)5) };
			Assert.True(HospitalConsultRequestRules.MayAnswer(toDep, false, cardiologist));
			Assert.False(HospitalConsultRequestRules.MayAnswer(toDep, false, surgeon));
			Assert.False(HospitalConsultRequestRules.MayAnswer(toDoctor, false, cardiologist));   // named: another cardiologist cannot
			Assert.True(HospitalConsultRequestRules.MayAnswer(toDoctor, false, named));
			Assert.True(HospitalConsultRequestRules.MayAnswer(toDoctor, true, Array.Empty<(int, int?)>()));
			Assert.False(HospitalConsultRequestRules.MayAnswer(toDep, false, Array.Empty<(int, int?)>()));
		}

		[Fact]
		public void An_answer_needs_recommendations_and_an_open_request()
		{
			var open = new HospitalConsultRequest { Status = HospitalConsultRequestStatus.Accepted };
			Assert.Null(HospitalConsultRequestRules.AnswerRefusal(open, new HospitalConsultAnswerInput { Findings = "NSTEMI", Recommendations = "Aspirin, heparin, echo" }));
			Assert.Equal("Write the recommendations", HospitalConsultRequestRules.AnswerRefusal(open, new HospitalConsultAnswerInput { Findings = "x", Recommendations = " " }));
			Assert.Equal("The answer is too long", HospitalConsultRequestRules.AnswerRefusal(open, new HospitalConsultAnswerInput { Recommendations = new string('x', 2001) }));
			Assert.Equal("This consultation is closed", HospitalConsultRequestRules.AnswerRefusal(new HospitalConsultRequest { Status = HospitalConsultRequestStatus.Cancelled }, new HospitalConsultAnswerInput { Recommendations = "x" }));
			Assert.True(HospitalConsultRequestStatus.Open(HospitalConsultRequestStatus.Requested));
			Assert.False(HospitalConsultRequestStatus.Open(HospitalConsultRequestStatus.Answered));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h77_consult_requests.sql");
			var m = HospitalSliceParity.Model(HospitalConsultRequestModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalConsultRequest), "HospitalConsultRequests");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalConsultRequest), "HospitalConsultRequests");
			foreach (var x in HospitalConsultUrgency.All.Concat(HospitalConsultRequestStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalDepartments", "HospitalPatients", "HospitalPractitioners" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalConsultRequest));
		}
	}
}
