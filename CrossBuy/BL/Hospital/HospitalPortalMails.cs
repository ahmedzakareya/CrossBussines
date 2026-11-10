using System.Net;
using Microsoft.Extensions.Localization;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H46/H47 — the patient portal's three emails, written once: the account invitation (set your password),
	// the reset link, and the notice of a change. The portal's "forgot password" and the desk's portal-accounts screen
	// send the same words through the same mailer (sent at once, stored nowhere).
	// ==========================================================================================
	public static class HospitalPortalMails
	{
		private static string E(string s) => WebUtility.HtmlEncode(s);
		private const string Box = "<div style=\"font-family:Arial,sans-serif;font-size:15px;color:#1e2129\">";
		private static string Button(string link, string text) =>
			"<p><a href=\"" + E(link) + "\" style=\"display:inline-block;background:#3A5795;color:#ffffff;padding:12px 22px;border-radius:6px;text-decoration:none\">" + E(text) + "</a></p>";

		public static (string Subject, string Html) Reset(IStringLocalizer t, string link, string userName) =>
			(t["Reset your patient portal password"].Value,
			 Box + "<p>" + E(t["We were asked to reset the password of your patient portal account"].Value) + " <b>" + E(userName) + "</b>.</p>" + Button(link, t["Set a new password"].Value) +
			 "<p style=\"color:#6b7280\">" + E(t["The link works for {0} minutes, once. If you did not ask for it, ignore this email: your password stays as it is.", HospitalPortalPasswordRules.LinkMinutes].Value) + "</p></div>");

		public static (string Subject, string Html) Invite(IStringLocalizer t, string link, string userName) =>
			(t["Your patient portal account is ready"].Value,
			 Box + "<p>" + E(t["The hospital opened a patient portal account for you. Your user name is"].Value) + " <b>" + E(userName) + "</b>.</p>" + Button(link, t["Set your password"].Value) +
			 "<p style=\"color:#6b7280\">" + E(t["The link works for {0} hours, once.", HospitalPortalPasswordRules.InviteHours].Value) + "</p></div>");

		/// H48: an earlier time with the same doctor, held for this patient for a few minutes; taken or declined from the link.
		public static (string Subject, string Html) Offer(IStringLocalizer t, string link, HospitalWaitlistOfferMail m)
		{
			static string When(DateTime d) => d.ToString("yyyy-MM-dd  HH:mm", System.Globalization.CultureInfo.InvariantCulture);
			return (t["An earlier appointment is free"].Value,
			 Box + "<p>" + E(t["An earlier time with {0} has freed up for {1}.", m.DoctorName, m.PatientName].Value) + "</p>" +
			 "<table style=\"border-collapse:collapse;margin:8px 0 4px\"><tr><td style=\"color:#6b7280;padding:4px 12px 4px 0\">" + E(t["The earlier time"].Value) + "</td><td style=\"padding:4px 0\"><b dir=\"ltr\">" + E(When(m.NewStart)) + "</b></td></tr>" +
			 "<tr><td style=\"color:#6b7280;padding:4px 12px 4px 0\">" + E(t["Your appointment now"].Value) + "</td><td style=\"padding:4px 0\" dir=\"ltr\">" + E(When(m.OldStart)) + "</td></tr></table>" +
			 Button(link, t["See the offer"].Value) +
			 "<p style=\"color:#6b7280\">" + E(t["It is held for you for {0} minutes. If you take it, your later appointment is released by itself; if you do not answer, it goes to the next patient waiting.", m.Minutes].Value) + "</p></div>");
		}

		public static (string Subject, string Html) Changed(IStringLocalizer t, string userName) =>
			(t["Your patient portal password was changed"].Value,
			 Box + "<p>" + E(t["The password of your patient portal account was just changed, and every other device was signed out."].Value) + " <b>" + E(userName) + "</b></p>" +
			 "<p style=\"color:#6b7280\">" + E(t["Wasn't you? Call the hospital at once."].Value) + "</p></div>");
	}
}
