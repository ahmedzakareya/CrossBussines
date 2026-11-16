namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H144 — the front desk's papers as DESIGNED Report Studio layouts, shipped with their reports (the
	// platform seeds them as the reports' platform templates instead of the plain starter table). They are ordinary
	// Studio layouts: bands of placed elements, opened, edited, versioned and forked in the Studio like any other.
	// Their shape follows the approved prototype (design-output/hospital-prototype #/frontdesk):
	//   · the VISIT SLIP — an 80 mm roll: the facility, the patient, the doctor, the clinic and room, the time, the
	//     payer, then the queue number large with how many are ahead, and the file's QR;
	//   · the WRISTBAND — a 280 × 25.4 mm band: the name and file large, date of birth, age, sex and blood group,
	//     the allergies in red, the ward and bed when admitted, and the file's QR for the bedside scan;
	//   · the DEPOSIT RECEIPT — an 80 mm roll: the deposit number and date, the patient, the amount large, how and
	//     what for, who took it, and what is used, refunded and left.
	// One document = ONE row, laid out in the report header band (which reads the first row). X is measured from the
	// content's start edge, so one layout serves Arabic and English. Colours are the brand palette, no green.
	// ============================================================================================
	public static class HospitalDeskLayouts
	{
		private const string Ink = "#071437", Muted = "#78829D", Brand = "#3A5795", Alert = "#D8203C", Rule = "#DBDFE9";
		private static int _n;

		private static ReportElementStyle S(double pt, bool bold = false, string color = Ink, ReportTextAlign align = ReportTextAlign.Start,
			string? date = null, string? background = null, string? border = null, string? number = null) => new()
		{
			FontSizePt = pt, Bold = bold, Color = color, Align = align, VerticalAlign = ReportVerticalAlign.Middle, DateFormat = date, NumberFormat = number,
			Background = background, BorderColor = border, BorderStyle = border == null ? ReportBorderStyle.None : ReportBorderStyle.Solid, BorderWidthMm = 0.3, PaddingMm = 0.5,
		};

		private static ReportElement Text(string ar, string en, double x, double y, double w, double h, ReportElementStyle s)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.Text, Text = ar, TextEn = en, XMm = x, YMm = y, WidthMm = w, HeightMm = h, Style = s };

		private static ReportElement Field(string key, double x, double y, double w, double h, ReportElementStyle s)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.Field, FieldKey = key, XMm = x, YMm = y, WidthMm = w, HeightMm = h, Style = s };

		private static ReportElement Line(double x, double y, double w)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.Line, XMm = x, YMm = y, WidthMm = w, HeightMm = 0, Style = new ReportElementStyle { BorderColor = Rule, BorderWidthMm = 0.3 } };   // no border style: a style would also frame the line (the renderer draws the rule itself)

		private static ReportElement Box(double x, double y, double w, double h, string border, string? fill = null)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.Rectangle, XMm = x, YMm = y, WidthMm = w, HeightMm = h, Style = new ReportElementStyle { BorderColor = border, BorderStyle = ReportBorderStyle.Solid, BorderWidthMm = 0.4, Background = fill } };

		private static ReportElement Qr(string key, double x, double y, double size)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.QrCode, FieldKey = key, XMm = x, YMm = y, WidthMm = size, HeightMm = size, Style = new ReportElementStyle() };

		private static ReportElement Now(double x, double y, double w, double h, ReportElementStyle s)
			=> new() { Id = "d" + (++_n), Kind = ReportElementKind.SystemField, SystemField = ReportSystemField.CurrentDateTime, XMm = x, YMm = y, WidthMm = w, HeightMm = h, Style = s };

		private static ReportVisualLayout OneBand(ReportPageSetup page, double height, IEnumerable<ReportElement> elements)
		{
			var band = new ReportBand { Kind = ReportBandKind.ReportHeader, HeightMm = height };
			band.Elements.AddRange(elements);
			var layout = new ReportVisualLayout { Page = page, GridMm = 1, SnapToGrid = false };
			layout.Bands.Add(band);
			return layout;
		}

		private static ReportPageSetup Roll() => new() { PageSize = ReportPageSize.Thermal80, MarginTopMm = 4, MarginBottomMm = 4, MarginLeftMm = 4, MarginRightMm = 4, ShowHeader = false, ShowFooter = false };

		/// The height a line of text needs at a size: Arabic glyphs (marks above and below) need about 1.9× the font's
		/// em, plus the padding. A box any shorter clips the text - the owner's "النصوص مقصوصة".
		public static double Need(double pt) => Math.Ceiling((pt * 0.3528 * 1.9 + 1.4) * 2) / 2;

		/// Stacks elements down an 80 mm roll: each line takes the height its font needs, never less.
		private sealed class Roll80
		{
			public const double W = 72;
			public readonly List<ReportElement> E = new();
			public double Y;
			public Roll80 Centered(ReportElement el) { el.YMm = Y; el.HeightMm = Math.Max(el.HeightMm, Need(el.Style.FontSizePt)); E.Add(el); Y += el.HeightMm; return this; }
			public Roll80 Gap(double mm) { Y += mm; return this; }
			public Roll80 Rule() { Y += 1; E.Add(Line(0, Y, W)); Y += 2; return this; }
			public Roll80 Row(string ar, string en, string key, bool bold = false, string? date = null, double pt = 9.5, int lines = 1)
			{
				double h = Need(pt) * lines;
				E.Add(Text(ar, en, 0, Y, 22, h, S(8, color: Muted)));
				E.Add(Field(key, 22, Y, W - 22, h, S(pt, bold, date: date)));
				Y += h; return this;
			}
		}

		public static ReportVisualLayout VisitSlip()
		{
			const double W = Roll80.W;
			var r = new Roll80()
				.Centered(Field("CompanyName", 0, 0, W, 0, S(11, true, align: ReportTextAlign.Center)))
				.Centered(Field("BranchName", 0, 0, W, 0, S(8, color: Muted, align: ReportTextAlign.Center)))
				.Gap(1)
				.Centered(Text("ورقة زيارة", "Visit slip", 0, 0, W, 0, S(13, true, Brand, ReportTextAlign.Center)))
				.Centered(Now(0, 0, W, 0, S(8, color: Muted, align: ReportTextAlign.Center)))
				.Rule()
				.Centered(Field("PatientName", 0, 0, W, 0, S(11, true, align: ReportTextAlign.Center)))
				.Gap(1)
				.Row("رقم الملف", "File no.", "PatientMrn", true)
				.Row("الطبيب", "Doctor", "DoctorName", true)
				.Row("العيادة", "Clinic", "Department")
				.Row("الغرفة", "Room", "Room")
				.Row("اليوم", "Day", "VisitDate", date: "yyyy-MM-dd")
				.Row("الموعد", "Time", "VisitTime")
				.Row("نوع الزيارة", "Visit", "VisitType")
				.Row("الجهة", "Payer", "Payer")
				.Rule().Gap(1);
			// the queue number, framed
			double top = r.Y, label = Need(9), number = Need(40), ahead = Need(10);
			r.E.Add(Box(6, top, W - 12, label + number + ahead + 2, Brand));
			r.E.Add(Text("رقم الدور", "Your number", 6, top + 1, W - 12, label, S(9, color: Muted, align: ReportTextAlign.Center)));
			r.E.Add(Field("QueueNumber", 6, top + 1 + label, W - 12, number, S(40, true, Ink, ReportTextAlign.Center, number: "0")));
			r.E.Add(Text("أمامك", "Ahead of you", 6, top + 1 + label + number, (W - 12) / 2, ahead, S(9, color: Muted, align: ReportTextAlign.End)));
			r.E.Add(Field("AheadOfYou", 6 + (W - 12) / 2 + 1, top + 1 + label + number, (W - 12) / 2 - 1, ahead, S(10, true, number: "0")));
			r.Y = top + label + number + ahead + 2 + 3;
			r.E.Add(Qr("PatientMrn", (W - 22) / 2, r.Y, 22)); r.Y += 23;
			r.Centered(Text("يرجى الانتظار حتى يُنادى رقمك على الشاشة", "Please wait until your number is called on the screen", 0, 0, W, Need(8) * 2, S(8, color: Muted, align: ReportTextAlign.Center)));
			return OneBand(Roll(), r.Y + 2, r.E);
		}

		public static ReportVisualLayout Wristband()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.Wristband, MarginTopMm = 2, MarginBottomMm = 2, MarginLeftMm = 2, MarginRightMm = 2, ShowHeader = false, ShowFooter = false };
			const double H = 21.4;   // 25.4 mm band less its margins: two lines of text per column, no more
			double l1 = Need(12), l2 = Need(9.5), c1 = Need(9.5), c2 = Need(8.5);
			var e = new List<ReportElement>
			{
				// column A (18 mm in, clear of the clasp): who
				Field("PatientName", 18, 0, 118, l1, S(12, true)),
				Field("PatientMrn", 18, l1 + 0.5, 38, l2, S(9.5, true, Brand)),
				Text("مواليد", "Born", 57, l1 + 0.5, 12, l2, S(7.5, color: Muted)),
				Field("BirthDate", 69, l1 + 0.5, 24, l2, S(9, date: "yyyy-MM-dd")),
				Field("PatientAge", 94, l1 + 0.5, 20, l2, S(9)),
				Field("PatientSex", 115, l1 + 0.5, 20, l2, S(9)),
				// column B: what the bedside must see
				Field("AllergyAlert", 140, 0, 34, c1, S(9.5, true, Alert)),
				Field("Allergies", 174, 0, 62, c1, S(9, true, Alert)),
				Text("فصيلة الدم", "Blood", 140, c1 + 1, 18, c2, S(7.5, color: Muted)),
				Field("BloodGroup", 158, c1 + 1, 14, c2, S(9, true)),
				Field("Ward", 173, c1 + 1, 40, c2, S(8.5, color: Muted)),
				Field("Bed", 214, c1 + 1, 22, c2, S(8.5, true)),
				// column C: the file's QR for the bedside scan
				Qr("PatientMrn", 238, 0.4, 20.5),
			};
			return OneBand(page, H, e);
		}

		public static ReportVisualLayout DepositReceipt()
		{
			const double W = Roll80.W;
			var r = new Roll80()
				.Centered(Field("CompanyName", 0, 0, W, 0, S(11, true, align: ReportTextAlign.Center)))
				.Centered(Field("CompanyTaxNo", 0, 0, W, 0, S(8, color: Muted, align: ReportTextAlign.Center)))
				.Gap(1)
				.Centered(Text("إيصال عربون", "Deposit receipt", 0, 0, W, 0, S(13, true, Brand, ReportTextAlign.Center)))
				.Rule()
				.Row("رقم الإيصال", "Receipt no.", "DocumentNo", true)
				.Row("التاريخ", "Date", "DocumentDate", date: "yyyy-MM-dd HH:mm")
				.Gap(1)
				.Centered(Field("PatientName", 0, 0, W, 0, S(10.5, true, align: ReportTextAlign.Center)))
				.Row("رقم الملف", "File no.", "PatientMrn")
				.Rule().Gap(1);
			double top = r.Y, label = Need(8), amount = Need(20);
			r.E.Add(Box(0, top, W, label + amount + 2, Brand));
			r.E.Add(Text("المبلغ", "Amount", 2, top + 0.5, W - 4, label, S(8, color: Muted, align: ReportTextAlign.Center)));
			r.E.Add(Field("Amount", 2, top + 0.5 + label, W - 24, amount, S(20, true, Ink, ReportTextAlign.End)));
			r.E.Add(Field("Currency", W - 21, top + 0.5 + label, 19, amount, S(11, true, Brand)));
			r.Y = top + label + amount + 2 + 2;
			r.Row("طريقة الدفع", "Paid by", "Method").Row("الغرض", "For", "Purpose").Row("المحصّل", "Received by", "Cashier").Row("ملاحظة", "Note", "Note", pt: 8.5, lines: 2)
				.Rule()
				.Row("المستخدم", "Used", "Used").Row("المردود", "Refunded", "Refunded").Row("المتبقي", "Left", "Left", true)
				.Gap(2)
				.Centered(Text("يُخصم العربون من فاتورتك أو يُرد بهذا الإيصال", "The deposit is set against your bill or refunded against this receipt", 0, 0, W, Need(8) * 2, S(8, color: Muted, align: ReportTextAlign.Center)));
			return OneBand(Roll(), r.Y + 2, r.E);
		}

		/// The designed layout a desk report ships with, by report code (null = the platform's starter).
		public static ReportVisualLayout? For(string code) => code switch
		{
			HospitalDocumentDatasetCodes.VisitSlip => VisitSlip(),
			HospitalDocumentDatasetCodes.Wristband => Wristband(),
			HospitalDocumentDatasetCodes.DepositReceipt => DepositReceipt(),
			_ => null,
		};
	}
}
