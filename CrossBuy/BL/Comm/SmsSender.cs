namespace CrossBuy.BL.Comm
{
	// ==========================================================================================
	// The ONE place an SMS provider plugs in (H70, owner 2026-11-09: "build it without a provider"). Any module that sends a
	// text message — the hospital's patient messages first — writes to its own outbox and hands each message to an
	// ISmsSender; connecting a provider (Cequens, Victory Link, Unifonic, Taqnyat, Twilio...) is an implementation of
	// this interface registered in DI, and nothing else changes. Until one is registered, NoSmsSender answers
	// "not configured" and the outboxes keep their messages queued — nothing is pretended sent.
	// ==========================================================================================
	public sealed record SmsSendResult(bool Ok, string? ProviderRef, string? Error);

	public interface ISmsSender
	{
		/// False until a provider is connected; a caller then leaves its messages queued.
		bool Configured { get; }
		/// The provider's name, shown to staff ("Unifonic").
		string Name { get; }
		Task<SmsSendResult> SendAsync(string phone, string body, CancellationToken ct = default);
	}

	/// The stand-in while no provider is chosen: it never sends and says so.
	public sealed class NoSmsSender : ISmsSender
	{
		public bool Configured => false;
		public string Name => "";
		public Task<SmsSendResult> SendAsync(string phone, string body, CancellationToken ct = default) =>
			Task.FromResult(new SmsSendResult(false, null, "No SMS provider is connected"));
	}
}
