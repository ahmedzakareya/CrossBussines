namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H198 — reading an HL7 v2 ORU^R01 (unsolicited observation result), the message laboratory analysers and
	// their middleware send. Only what the laboratory needs is read; the rest is kept in the raw message:
	//   MSH-3 sending application · MSH-9 message type · MSH-10 control id
	//   OBR-2 placer order number / OBR-3 filler order number — our accession number (LAB-000016)
	//   OBX-3 the observation (identifier ^ text ^ coding system), OBX-5 value, OBX-6 units, OBX-11 result status
	//     (F final, C corrected; P preliminary and anything else is not a result to file)
	// The encoding characters are the message's own (MSH-1 field separator, MSH-2 component separator); segments end
	// with a carriage return, a line feed being tolerated.
	// ==========================================================================================
	public sealed record HospitalHl7Obx(string Code, string Text, string Value, string? Units, string Status);
	public sealed record HospitalHl7Oru(string Device, string MessageId, string Type, string? Placer, string? Filler, IReadOnlyList<HospitalHl7Obx> Results);

	public static class HospitalHl7
	{
		public static (HospitalHl7Oru? Message, string? Refusal) ParseOru(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return (null, "The message is empty");
			var segments = raw.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart()).Where(s => s.Length > 0).ToList();
			var msh = segments.FirstOrDefault();
			if (msh == null || !msh.StartsWith("MSH") || msh.Length < 8) return (null, "This is not an HL7 message (no MSH segment)");
			char fs = msh[3]; char cs = msh[4];
			string[] F(string seg) => seg.Split(fs);
			// MSH-1 is the separator itself, so MSH field n is at index n-1
			var m = F(msh);
			string Msh(int n) => m.Length > n - 1 ? m[n - 1] : "";
			string Comp(string field, int n) { var c = field.Split(cs); return c.Length > n - 1 ? c[n - 1].Trim() : ""; }
			string type = Comp(Msh(9), 1) + "^" + Comp(Msh(9), 2);
			if (!type.StartsWith("ORU^R01", StringComparison.OrdinalIgnoreCase)) return (null, "Only result messages (ORU^R01) are read");
			string device = Comp(Msh(3), 1), control = Msh(10).Trim();
			if (device.Length == 0) return (null, "The message names no sending device (MSH-3)");
			if (control.Length == 0) return (null, "The message has no control id (MSH-10)");
			string? placer = null, filler = null; var obx = new List<HospitalHl7Obx>();
			foreach (var s in segments.Skip(1))
			{
				var f = F(s);
				string Fld(int n) => f.Length > n ? f[n] : "";
				switch (f[0])
				{
					case "OBR": placer ??= Comp(Fld(2), 1); filler ??= Comp(Fld(3), 1); break;
					case "OBX": obx.Add(new HospitalHl7Obx(Comp(Fld(3), 1), Comp(Fld(3), 2), Fld(5).Trim(), Comp(Fld(6), 1) is { Length: > 0 } u ? u : null, Fld(11).Trim().ToUpperInvariant())); break;
				}
			}
			if (string.IsNullOrEmpty(placer) && string.IsNullOrEmpty(filler)) return (null, "The message names no order (OBR-2 or OBR-3)");
			if (obx.Count == 0) return (null, "The message holds no results (OBX)");
			return (new HospitalHl7Oru(device, control, type, string.IsNullOrEmpty(placer) ? null : placer, string.IsNullOrEmpty(filler) ? null : filler, obx), null);
		}

		/// A result is filed only when final or a correction.
		public static bool Files(HospitalHl7Obx o) => o.Status is "F" or "C";

		/// The acknowledgement the sender expects: AA accepted, AE an error (with the reason), AR a message refused.
		public static string Ack(string? device, string? controlId, string code, string? text, DateTime nowUtc) =>
			"MSH|^~\\&|CROSSBUSINESS|HOSPITAL|" + (device ?? "") + "||" + nowUtc.ToString("yyyyMMddHHmmss") + "||ACK^R01|ACK" + nowUtc.ToString("yyyyMMddHHmmssfff") + "|P|2.5\r" +
			"MSA|" + code + "|" + (controlId ?? "") + (string.IsNullOrEmpty(text) ? "" : "|" + text.Replace("|", "/").Replace("\r", " ")) + "\r";
	}
}
