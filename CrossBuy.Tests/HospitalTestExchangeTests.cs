using System.Net;
using System.Net.Sockets;
using System.Text;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H257 — the test exchange. What must never happen: its answers failing the hospital's own FHIR readers;
	// a pre-authorisation answered for another request; the key not being checked; the MLLP receiver opening outside
	// Development or answering anything but AA with the sender's control id.
	// ==========================================================================================
	public class HospitalTestExchangeTests
	{
		private static readonly HospitalPatient P = new() { ID = 25, Mrn = "MRN-25", Name = "مريض", NameEn = "Sara Ali", Sex = HospitalPatientSex.Female };
		private static readonly HospitalPayer Payer = new() { ID = 3, Code = "GIG", Name = "جي آي جي", NameEn = "GIG" };
		private static HospitalPatientPolicy Policy(string no) => new() { ID = 9, PolicyNo = no };

		[Fact]
		public void Eligibility_is_read_by_the_hospitals_own_reader()
		{
			var (ok, _) = HospitalFhirEligibility.ParseEligibility(HospitalTestExchange.Eligibility(HospitalFhirEligibility.EligibilityRequest(41, P, Policy("POL-1"), Payer, 1, DateTime.UtcNow)));
			Assert.True(ok!.InForce); Assert.False(ok.Error);
			var (lapsed, _) = HospitalFhirEligibility.ParseEligibility(HospitalTestExchange.Eligibility(HospitalFhirEligibility.EligibilityRequest(42, P, Policy("POL-2X"), Payer, 1, DateTime.UtcNow)));
			Assert.False(lapsed!.InForce);
			Assert.Null(HospitalTestExchange.Eligibility("{\"resourceType\":\"Claim\"}"));
			Assert.Null(HospitalTestExchange.Eligibility("not json"));
		}

		[Fact]
		public void Preauth_up_to_the_limit_is_approved_for_the_same_request_and_above_it_refused()
		{
			string Claim(int id, decimal amount) => HospitalFhirEligibility.PreauthClaim(new HospitalPreauth { ID = id, RequestNo = "PA-" + id, Amount = amount, RequestedAt = DateTime.UtcNow },
				P, Policy("POL-1"), Payer, "S1", "MRI", "KWD", 1);
			var (yes, _) = HospitalFhirEligibility.ParsePreauth(HospitalTestExchange.Preauth(Claim(17, 250m)));
			Assert.True(yes!.Approved); Assert.Equal(17, HospitalFhirEligibility.PreauthIdOf(yes.Request));
			Assert.StartsWith("TEST-", yes.ApprovalRef);
			var (no, _) = HospitalFhirEligibility.ParsePreauth(HospitalTestExchange.Preauth(Claim(18, HospitalTestExchange.AutoApproveLimit + 1)));
			Assert.False(no!.Approved); Assert.Equal(18, HospitalFhirEligibility.PreauthIdOf(no.Request));
			Assert.Contains("limit", no.Reason);
		}

		[Fact]
		public void The_key_is_checked()
		{
			Assert.Null(HospitalTestExchange.KeyRefusal(HospitalTestExchange.Key));
			Assert.NotNull(HospitalTestExchange.KeyRefusal(null));
			Assert.NotNull(HospitalTestExchange.KeyRefusal("guess"));
		}

		private sealed class Env : IHostEnvironment
		{
			public string EnvironmentName { get; set; } = Environments.Development;
			public string ApplicationName { get; set; } = "CrossBuy";
			public string ContentRootPath { get; set; } = ".";
			public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
		}

		private static IConfiguration Port(int p) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hospital:TestExchange:MllpPort"] = p.ToString() }).Build();

		[Fact]
		public void The_receiver_opens_only_in_Development()
		{
			Assert.Equal(HospitalTestExchange.DefaultMllpPort, HospitalTestExchange.MllpPort(null, new Env()));
			Assert.Null(HospitalTestExchange.MllpPort(null, new Env { EnvironmentName = Environments.Production }));
			Assert.Null(HospitalTestExchange.MllpPort(null, null));
			Assert.Null(HospitalTestExchange.MllpPort(Port(0), new Env()));
		}

		[Fact]
		public async Task The_receiver_answers_AA_with_the_senders_control_id()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
			var svc = new HospitalMllpTestReceiverService(NullLogger<HospitalMllpTestReceiverService>.Instance, Port(port), new Env());
			await svc.StartAsync(CancellationToken.None);
			try
			{
				var msg = HospitalHl7Out.Adt("A01", "F1-A01-adm-7", P, new HospitalHl7Visit("ADM-7", "W1", "201", "201-A", null, DateTime.UtcNow, null), "HIS", "MOH", "CB1", DateTime.UtcNow, DateTime.UtcNow);
				string reply = "";
				for (int attempt = 0; attempt < 20 && reply.Length == 0; attempt++)
				{
					try
					{
						using var tcp = new TcpClient();
						await tcp.ConnectAsync(IPAddress.Loopback, port);
						var s = tcp.GetStream();
						await s.WriteAsync(HospitalHl7Out.Frame(msg));
						var buf = new byte[4096]; var sb = new StringBuilder();
						while (!sb.ToString().Contains('\u001c')) { int n = await s.ReadAsync(buf); if (n <= 0) break; sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
						reply = sb.ToString();
					}
					catch (SocketException) { await Task.Delay(100); }
				}
				Assert.Equal("AA", HospitalHl7Out.AckCode(reply));
				Assert.Contains("|F1-A01-adm-7", reply);
				Assert.Contains(HospitalTestReceiverLog.Recent(), r => r.ControlId == "F1-A01-adm-7" && r.Type.StartsWith("ADT^A01"));
			}
			finally { await svc.StopAsync(CancellationToken.None); }
		}
	}
}
