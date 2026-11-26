using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// ==============================================================================================
	// CRM ACTIVITY REMINDERS — every five minutes, per company.
	//
	// WHAT WAS WRONG. `private const int CompanyId = 1` was passed to both GetDueRemindersAsync and
	// MarkRemindedAsync, so only company 1's activities were ever scanned. Every other company's sales
	// team simply never received a reminder, and nothing reported it: the worker logged a cheerful
	// "CRM reminders dispatched: 0" forever.
	//
	// It also had no gate, so a second instance dispatched the SAME reminders again — and since the
	// notification carries a dedup key only within its own recipient, that is a duplicate ping to a
	// real person.
	//
	// NOT A LEAK, AND WORTH BEING PRECISE ABOUT IT: the hardcoded 1 was too NARROW, never too wide. It
	// could not read another company's records because every query was pinned to company 1. The defect
	// is omission, and the fix is to process each company explicitly rather than to widen anything.
	//
	// Notification company is unchanged and deliberately not touched here: NotificationService derives
	// CompanyID from the RECIPIENT's EmpCompanyID, not from ambient context, so a reminder is stamped
	// with the company of the employee it reaches.
	// ==============================================================================================
	public class CrmReminderHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<CrmReminderHostedService> _log;

		public CrmReminderHostedService(
			IServiceScopeFactory scopes, IWorkerGate gate,
			CertificationRuntimeState certification, ILogger<CrmReminderHostedService> log)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// Reminders mutate state twice — a notification row and the activity's reminded flag — so a
			// certification host must not run them.
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation(
					"CrmReminderHostedService is suppressed: this host is a certification runtime, so no " +
					"reminders are dispatched.");
				return;
			}

			try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }

			// One dispatcher. A second instance would send the same person the same reminder twice.
			if (!await _gate.WaitUntilAllowedAsync(nameof(CrmReminderHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
			do
			{
				// D-16. THE LEASE CAN BE LOST MID-RUN - a dropped connection, a failover, another process
				// taking it. The gate used to be asked ONCE, before this loop, so a worker that lost primacy
				// kept working while the new primary worked too: duplicate output, unobserved.
				//
				// PARK, NEVER RETURN. False means "not right now" and is recoverable. Returning would end this
				// BackgroundService for good, so if the process that took the lease later died, nothing would
				// re-acquire and no worker would run anywhere until a restart. Only cancellation ends a worker,
				// which is what a false from WaitUntilAllowedAsync means here.
				if (!await _gate.IsStillAllowedAsync(nameof(CrmReminderHostedService), stoppingToken))
				{
					if (!await _gate.WaitUntilAllowedAsync(nameof(CrmReminderHostedService), stoppingToken)) return;
				}

				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

					await WorkerCompanyRunner.ForEachCompanyAsync(
						companyScope, _log, nameof(CrmReminderHostedService),
						async companyId => await DispatchForCompanyAsync(companyId, stoppingToken),
						stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex) { _log.LogError(ex, "CRM reminder dispatch failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}

		// One company, one scope, one BusinessContext. The scope is created per company rather than per
		// tick so that a company whose dispatch throws cannot leave a half-resolved context behind for
		// the next company in the loop — the runner catches it and the scope is disposed with it.
		private async Task DispatchForCompanyAsync(int companyId, CancellationToken cancellationToken)
		{
			using var scope = WorkerScope.ForCompany(_scopes, companyId);
			var crm = scope.ServiceProvider.GetRequiredService<ICrmService>();
			var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();
			// CRM C2: website-form submissions become leads here, inside this company's own scope - never in the
			// anonymous request that stored them. A failure is logged and never stops the reminders below.
			try
			{
				var capture = ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmCaptureService>(scope.ServiceProvider);
				var r = await capture.ProcessPendingAsync(companyId);
				if (r.Converted + r.Linked + r.Failed > 0)
					_log.LogInformation("CRM web-form intake for company {CompanyId}: {Converted} new, {Linked} linked, {Failed} failed", companyId, r.Converted, r.Linked, r.Failed);
			}
			catch (Exception ex) { _log.LogError(ex, "CRM web-form intake failed for company {CompanyId}", companyId); }
			// CRM C4: chat and WhatsApp conversations that arrived since the last pass are matched to the CRM and given an
			// owner here, in this company's scope. Same isolation: a failure never stops the reminders.
			try
			{
				var inbox = ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmInboxService>(scope.ServiceProvider);
				var linked = await inbox.LinkPendingAsync(companyId);
				if (linked > 0) _log.LogInformation("CRM inbox linked {Count} new conversations for company {CompanyId}", linked, companyId);
			}
			catch (Exception ex) { _log.LogError(ex, "CRM inbox linking failed for company {CompanyId}", companyId); }
			// CRM C6: bookings become meetings, sequences check who answered and run the steps that are due. After the inbox
			// pass on purpose: a WhatsApp reply linked a moment ago must stop its sequence before the next message goes out.
			try
			{
				var seq = ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmSequenceService>(scope.ServiceProvider);
				var r = await seq.RunAsync(companyId, cancellationToken);
				if (r.Ran + r.Stopped + r.Bookings > 0) _log.LogInformation("CRM sequences for company {CompanyId}: {Ran} steps, {Stopped} stopped, {Bookings} bookings", companyId, r.Ran, r.Stopped, r.Bookings);
			}
			catch (Exception ex) { _log.LogError(ex, "CRM sequences failed for company {CompanyId}", companyId); }
			// CRM C7: segments are recomputed hourly; whoever entered one starts its active journeys; journeys advance.
			try
			{
				var journeys = ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmJourneyService>(scope.ServiceProvider);
				var (refreshed, entered, ran) = await journeys.RunAsync(companyId, cancellationToken);
				if (refreshed + entered + ran > 0) _log.LogInformation("CRM journeys for company {CompanyId}: {Refreshed} segments refreshed, {Entered} entered, {Ran} steps", companyId, refreshed, entered, ran);
			}
			catch (Exception ex) { _log.LogError(ex, "CRM journeys failed for company {CompanyId}", companyId); }
			// CRM C8: the SLA breach notice (once per ticket per day) and surveys for tickets resolved without one.
			try
			{
				var desk = ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmServiceDeskService>(scope.ServiceProvider);
				var n = await desk.RunAsync(companyId);
				if (n > 0) _log.LogInformation("CRM service desk for company {CompanyId}: {Surveys} surveys created", companyId, n);
			}
			catch (Exception ex) { _log.LogError(ex, "CRM service desk pass failed for company {CompanyId}", companyId); }
			// CRM C9: every open lead's behavioural score, after the passes that create the behaviour it reads
			try { await ActivatorUtilities.CreateInstance<CrossBuy.BL.CrmIdentity.CrmAssistService>(scope.ServiceProvider).RefreshLeadsAsync(companyId); }
			catch (Exception ex) { _log.LogError(ex, "CRM lead scoring failed for company {CompanyId}", companyId); }

			var due = await crm.GetDueRemindersAsync(companyId, DateTime.UtcNow);
			if (due.Count == 0) return;

			var fired = new List<int>();
			foreach (var a in due)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (a.OwnerEmployeeId is int emp && emp > 0)
				{
					await notify.NotifyAsync(emp,
						"Activity reminder: " + a.Subject, "Activity reminder: " + a.Subject,
						a.Notes, a.Notes, "crm-reminder", a.ID);
				}

				fired.Add(a.ID);   // mark fired even if unowned, so it doesn't re-scan forever
			}

			await crm.MarkRemindedAsync(companyId, fired);
			_log.LogInformation("CRM reminders dispatched for company {CompanyId}: {Count}", companyId, fired.Count);
		}
	}
}
