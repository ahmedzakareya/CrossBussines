using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H228 — the DICOM Modality Worklist, served directly to the machines (owner-approved 2026-11-22, fo-dicom).
	//
	// H215 served the scheduled imaging orders over HTTPS for a worklist broker to pass on. A CT, an X-ray room or an
	// ultrasound asks for its worklist itself, over DICOM (C-FIND, Modality Worklist Information Model). This service
	// answers that from EXACTLY the same list (HospitalInterfaceService.WorklistAsync), so the two channels cannot differ.
	//
	// WHO IS ASKING. DICOM carries no credential, so the port is configured with an ImagingWorklist interface AND its key
	// (Hospital:Dicom:Worklist), checked against the stored hash on every query: rotating or switching the interface off
	// on the screen cuts the machines off at once, and the list is read AS the employee the interface acts for, with that
	// person's radiology rights checked each time. A machine must call this server by its AE title; when AllowedCallingAEs
	// is given, only those machines are heard. Loopback unless Bind is set. Nothing configured ⇒ no port opens.
	//
	// WHAT IS ANSWERED. One response per scheduled order, filtered by the modality, the scheduled date (or range) and the
	// patient id when the machine asks for them. The Study Instance UID is DERIVED from the company and the accession
	// (2.25.<128-bit hash>), so every machine that pulls the order — and every re-pull — stamps the same study.
	// ==========================================================================================
	public sealed class HospitalDicomWorklistOptions
	{
		public int Port { get; set; }
		public string Bind { get; set; } = "127.0.0.1";
		public string AeTitle { get; set; } = "CROSSBUSINESS";
		public string Interface { get; set; } = "";
		public string Key { get; set; } = "";
		public List<string> AllowedCallingAEs { get; set; } = new();
	}

	public sealed record HospitalWorklistQuery(string? Modality, DateTime? From, DateTime? To, string? PatientId);

	public static class HospitalDicomWorklistRules
	{
		public static (HospitalDicomWorklistOptions? Ok, string? Skipped) Read(IConfiguration? config)
		{
			var o = new HospitalDicomWorklistOptions();
			var s = config?.GetSection("Hospital:Dicom:Worklist");
			if (s == null || !s.Exists()) return (null, null);
			s.Bind(o);
			return Validate(o);
		}

		public static (HospitalDicomWorklistOptions? Ok, string? Skipped) Validate(HospitalDicomWorklistOptions o)
		{
			if (o.Port is < 1 or > 65535) return (null, $"port {o.Port}: not a port");
			if (string.IsNullOrWhiteSpace(o.Interface) || string.IsNullOrWhiteSpace(o.Key)) return (null, $"port {o.Port}: no interface or key");
			if (!System.Net.IPAddress.TryParse(o.Bind, out _)) return (null, $"port {o.Port}: bind '{o.Bind}' is not an address");
			var ae = (o.AeTitle ?? "").Trim();
			if (ae.Length is 0 or > 16) return (null, $"port {o.Port}: AE title '{o.AeTitle}' is not 1-16 characters");
			o.AeTitle = ae;
			return (o, null);
		}

		public static bool CallingAllowed(HospitalDicomWorklistOptions o, string? callingAe) =>
			o.AllowedCallingAEs.Count == 0 || o.AllowedCallingAEs.Any(a => string.Equals(a.Trim(), (callingAe ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

		/// A DICOM date or range: "20261122", "20261120-20261122", "20261120-", "-20261122" (empty ⇒ any day).
		public static (DateTime? From, DateTime? To) DateRange(string? value)
		{
			var v = (value ?? "").Trim();
			if (v.Length == 0) return (null, null);
			static DateTime? D(string x) => DateTime.TryParseExact(x.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
			int dash = v.IndexOf('-');
			if (dash < 0) { var d = D(v); return (d, d); }
			return (D(v[..dash]), D(v[(dash + 1)..]));
		}

		public static bool Matches(HospitalWorklistItem item, HospitalWorklistQuery q)
		{
			if (!string.IsNullOrWhiteSpace(q.Modality) && !string.Equals(item.Modality, q.Modality.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
			var day = item.OrderedAt.ToLocalTime().Date;
			if (q.From is DateTime f && day < f.Date) return false;
			if (q.To is DateTime t && day > t.Date) return false;
			var pid = (q.PatientId ?? "").Trim().TrimEnd('*');
			if (pid.Length > 0 && !item.Mrn.StartsWith(pid, StringComparison.OrdinalIgnoreCase)) return false;
			return true;
		}

		/// The same study UID for an order every time it is pulled: 2.25 + the decimal of a 128-bit hash of company + accession.
		public static string StudyUid(int companyId, string accession)
		{
			var h = MD5.HashData(Encoding.UTF8.GetBytes("crossbusiness-hospital-study|" + companyId + "|" + accession));
			h[6] = (byte)((h[6] & 0x0F) | 0x30); h[8] = (byte)((h[8] & 0x3F) | 0x80);   // a name-based UUID, as 2.25 expects
			var n = new System.Numerics.BigInteger(h.Reverse().Concat(new byte[] { 0 }).ToArray());
			return "2.25." + n.ToString(CultureInfo.InvariantCulture);
		}

		public static DicomDataset Answer(HospitalWorklistItem item, int companyId, string stationAe)
		{
			var at = item.OrderedAt.ToLocalTime();
			var sps = new DicomDataset
			{
				{ DicomTag.Modality, item.Modality },
				{ DicomTag.ScheduledStationAETitle, stationAe },
				{ DicomTag.ScheduledProcedureStepStartDate, at.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
				{ DicomTag.ScheduledProcedureStepStartTime, at.ToString("HHmmss", CultureInfo.InvariantCulture) },
				{ DicomTag.ScheduledProcedureStepDescription, Clip(item.Exam, 64) },
				{ DicomTag.ScheduledProcedureStepID, Clip(item.Accession, 16) },
			};
			return new DicomDataset
			{
				{ DicomTag.SpecificCharacterSet, "ISO_IR 192" },   // UTF-8: the patient's name may be Arabic
				{ DicomTag.AccessionNumber, Clip(item.Accession, 16) },
				{ DicomTag.PatientID, Clip(item.Mrn, 64) },
				{ DicomTag.PatientName, Clip(item.Patient, 64) },
				{ DicomTag.RequestedProcedureID, Clip(item.Accession, 16) },
				{ DicomTag.RequestedProcedureDescription, Clip(item.Exam, 64) },
				{ DicomTag.RequestedProcedurePriority, item.Priority.Equals("Stat", StringComparison.OrdinalIgnoreCase) || item.Priority.Equals("Urgent", StringComparison.OrdinalIgnoreCase) ? "HIGH" : "ROUTINE" },
				{ DicomTag.StudyInstanceUID, StudyUid(companyId, item.Accession) },
				{ DicomTag.ScheduledProcedureStepSequence, sps },
			};
		}

		public static HospitalWorklistQuery QueryOf(DicomDataset? request)
		{
			if (request == null) return new(null, null, null, null);
			var sps = request.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var seq) && seq.Items.Count > 0 ? seq.Items[0] : null;
			string? modality = sps?.GetSingleValueOrDefault<string?>(DicomTag.Modality, null);
			var (from, to) = DateRange(sps?.GetSingleValueOrDefault<string?>(DicomTag.ScheduledProcedureStepStartDate, null));
			string? pid = request.GetSingleValueOrDefault<string?>(DicomTag.PatientID, null);
			return new(string.IsNullOrWhiteSpace(modality) || modality == "*" ? null : modality, from, to, pid);
		}

		private static string Clip(string? s, int n) { s ??= ""; return s.Length > n ? s[..n] : s; }
	}

	/// What every association of the server shares: the configured port and a way into the application's services.
	public sealed record HospitalDicomWorklistState(HospitalDicomWorklistOptions Options, IServiceScopeFactory Scopes, ILogger Log);

	/// One association with a machine.
	public sealed class HospitalDicomWorklistScp : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
	{
		private static readonly DicomTransferSyntax[] Syntaxes = { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian };
		private string? _callingAe;

		public HospitalDicomWorklistScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
			: base(stream, fallbackEncoding, log, dependencies) { }

		private HospitalDicomWorklistState State => (HospitalDicomWorklistState)UserState;

		public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
		{
			var o = State.Options;
			if (!string.Equals(association.CalledAE?.Trim(), o.AeTitle, StringComparison.OrdinalIgnoreCase))
				return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.CalledAENotRecognized);
			if (!HospitalDicomWorklistRules.CallingAllowed(o, association.CallingAE))
			{
				State.Log.LogWarning("Hospital DICOM worklist: calling AE {Ae} refused (not an allowed machine).", association.CallingAE);
				return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.CallingAENotRecognized);
			}
			_callingAe = association.CallingAE;
			foreach (var pc in association.PresentationContexts)
			{
				if (pc.AbstractSyntax == DicomUID.Verification || pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind) pc.AcceptTransferSyntaxes(Syntaxes);
				else pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
			}
			return SendAssociationAcceptAsync(association);
		}

		public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
		public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
		public void OnConnectionClosed(Exception exception) { }
		public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) => Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));

		public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
		{
			/* a worklist query names its SOP class; it carries no query level */ if (request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind) { yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass); yield break; }
			List<DicomDataset>? answers = null;
			try
			{
				using var scope = State.Scopes.CreateScope();
				var sp = scope.ServiceProvider;
				var open = await HospitalInterfaceInbound.OpenAsync(sp, State.Options.Interface, State.Options.Key, new[] { HospitalInterfaceKind.ImagingWorklist }, CancellationToken.None);
				if (open != null)
				{
					var q = HospitalDicomWorklistRules.QueryOf(request.Dataset);
					var items = await ActivatorUtilities.CreateInstance<HospitalInterfaceService>(sp).WorklistAsync(open.Value.Ctx, q.Modality, DateTime.UtcNow, CancellationToken.None);
					answers = items.Where(i => HospitalDicomWorklistRules.Matches(i, q)).Select(i => HospitalDicomWorklistRules.Answer(i, open.Value.I.CompanyId, _callingAe ?? "")).ToList();
				}
				else State.Log.LogWarning("Hospital DICOM worklist: the configured interface {Interface} is not open (wrong key, switched off, or not ImagingWorklist).", State.Options.Interface);
			}
			catch (Exception ex) { State.Log.LogError(ex, "Hospital DICOM worklist: a query from {Ae} failed.", _callingAe); }
			if (answers == null) { yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure); yield break; }
			foreach (var a in answers) yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = a };
			yield return new DicomCFindResponse(request, DicomStatus.Success);
		}
	}

	public sealed class HospitalDicomWorklistService : BackgroundService
	{
		private static int _setup;
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly IConfiguration? _config;
		private readonly ILogger<HospitalDicomWorklistService> _log;

		public HospitalDicomWorklistService(IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification, ILogger<HospitalDicomWorklistService> log, IConfiguration? config = null)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; _config = config; }

		protected override async Task ExecuteAsync(CancellationToken stop)
		{
			var (o, skipped) = HospitalDicomWorklistRules.Read(_config);
			if (skipped != null) _log.LogWarning("Hospital DICOM worklist skipped — {Reason}.", skipped);
			if (o == null) { _log.LogInformation("Hospital DICOM worklist: not configured (Hospital:Dicom:Worklist); no port is open."); return; }
			if (_certification.BackgroundWritersSuppressed) { _log.LogInformation("Hospital DICOM worklist is suppressed: this host is a certification runtime."); return; }
			if (!await _gate.WaitUntilAllowedAsync(nameof(HospitalDicomWorklistService), stop)) return;
			if (Interlocked.Exchange(ref _setup, 1) == 0) new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom()).Build();
			IDicomServer? server = null;
			try
			{
				server = DicomServerFactory.Create<HospitalDicomWorklistScp>(o.Bind, o.Port, userState: new HospitalDicomWorklistState(o, _scopes, _log));
				_log.LogInformation("Hospital DICOM worklist: AE {Ae} listening on {Bind}:{Port} for interface {Interface}.", o.AeTitle, o.Bind, o.Port, o.Interface);
				await Task.Delay(Timeout.Infinite, stop);
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
			catch (Exception ex) { _log.LogError(ex, "Hospital DICOM worklist: port {Port} on {Bind} could not be opened.", o.Port, o.Bind); }
			finally { server?.Dispose(); }
		}
	}
}
