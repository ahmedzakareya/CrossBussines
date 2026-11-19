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
	// None of them carries the PDF engine's running footer (the report code and "page 1 of 1"): it printed over the foot
	// of a one-piece paper, so ShowPageNumbers is off on every one (H147).
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

		private static ReportPageSetup Roll() => new() { PageSize = ReportPageSize.Thermal80, MarginTopMm = 4, MarginBottomMm = 4, MarginLeftMm = 4, MarginRightMm = 4, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };

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
			var page = new ReportPageSetup { PageSize = ReportPageSize.Wristband, MarginTopMm = 2, MarginBottomMm = 2, MarginLeftMm = 2, MarginRightMm = 2, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
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

		/// H149 — the CREDIT NOTE: an 80 mm roll like the deposit receipt (it is handed over at the same counter): the
		/// note and the bill it credits, the patient, what came back, the amount the patient no longer owes framed, its net
		/// and VAT, the reason, who issued it, and what went to the patient's deposit.
		public static ReportVisualLayout CreditNote()
		{
			const double W = Roll80.W;
			var r = new Roll80()
				.Centered(Field("CompanyName", 0, 0, W, 0, S(11, true, align: ReportTextAlign.Center)))
				.Centered(Field("CompanyTaxNo", 0, 0, W, 0, S(8, color: Muted, align: ReportTextAlign.Center)))
				.Gap(1)
				.Centered(Text("إشعار دائن", "Credit note", 0, 0, W, 0, S(13, true, Brand, ReportTextAlign.Center)))
				.Rule()
				.Row("رقم الإشعار", "Note no.", "DocumentNo", true)
				.Row("التاريخ", "Date", "DocumentDate", date: "yyyy-MM-dd HH:mm")
				.Row("على الفاتورة", "On bill", "BillNo")
				.Row("الروشتة", "Prescription", "RxNo")
				.Gap(1)
				.Centered(Field("PatientName", 0, 0, W, 0, S(10.5, true, align: ReportTextAlign.Center)))
				.Row("رقم الملف", "File no.", "PatientMrn")
				.Rule()
				.Row("الدواء المرتجع", "Returned", "Drug", true, lines: 2)
				.Row("الكمية", "Quantity", "Quantity")
				.Row("السعر", "Price", "UnitPrice")
				.Rule().Gap(1);
			double top = r.Y, label = Need(8), amount = Need(20);
			r.E.Add(Box(0, top, W, label + amount + 2, Brand));
			r.E.Add(Text("لم يعد على المريض", "No longer owed", 2, top + 0.5, W - 4, label, S(8, color: Muted, align: ReportTextAlign.Center)));
			r.E.Add(Field("Total", 2, top + 0.5 + label, W - 24, amount, S(20, true, Ink, ReportTextAlign.End)));
			r.E.Add(Field("Currency", W - 21, top + 0.5 + label, 19, amount, S(11, true, Brand)));
			r.Y = top + label + amount + 2 + 2;
			r.Row("الصافي", "Net", "Net").Row("الضريبة", "VAT", "Vat")
				.Rule()
				.Row("السبب", "Reason", "Reason", pt: 8.5, lines: 2).Row("أصدره", "Issued by", "IssuedBy").Row("إلى العربون", "To the deposit", "ToDeposit", true)
				.Gap(2)
				.Centered(Text("خُصم هذا المبلغ من فاتورتك، وما دُفع زيادة صار رصيدًا في عربونك", "This amount is off your bill; anything you had overpaid is now credit on your deposit", 0, 0, W, Need(8) * 2, S(8, color: Muted, align: ReportTextAlign.Center)));
			return OneBand(Roll(), r.Y + 2, r.E);
		}

		/// H153 — the COMPOUNDED PREPARATION's bag label, on the dispensing label stock: whose it is, what it is (the dose
		/// and route), in what (diluent and volume), until when it may be given — framed in red with how it is kept — the
		/// cytotoxic warning, and the two pharmacists who prepared and checked it.
		public static ReportVisualLayout CompoundLabel()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.RxLabel, MarginTopMm = 1, MarginBottomMm = 1, MarginLeftMm = 2, MarginRightMm = 2, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
			const double W = 96, H = 58;
			double a = Need(7.5), b = Need(9.5), c = Need(11), lab = Need(6.5), bud = Need(11), haz = Need(8), sign = Need(6.5);
			double yB = a + 0.8, yC = yB + b, yD = yC + c, yBox = yD + b + 0.5, box = bud + 1, yH = yBox + box + 0.4, yS = yH + haz;
			var e = new List<ReportElement>
			{
				Field("CompanyName", 0, 0, 56, a, S(7.5, true, Brand)),
				Field("DocumentNo", 56, 0, 40, a, S(7.5, color: Muted, align: ReportTextAlign.End)),
				Line(0, a + 0.4, W),
				Field("PatientName", 0, yB, 70, b, S(9.5, true)),
				Field("PatientMrn", 70, yB, 26, b, S(8.5, true, Brand, ReportTextAlign.End)),
				Field("Product", 0, yC, W, c, S(11, true)),
				Field("Dose", 0, yD, 40, b, S(9.5, true, Brand)),
				Field("Diluent", 40, yD, 56, b, S(8, color: Ink, align: ReportTextAlign.End)),
				// until when: framed in red, with how it is kept
				Box(0, yBox, W, box, Alert),
				Text("يُستعمل قبل", "Use before", 1, yBox + (box - lab) / 2, 20, lab, S(6.5, color: Muted)),
				Field("BeyondUse", 21, yBox + 0.5, 40, bud, S(11, true, Alert, date: "yyyy-MM-dd HH:mm")),
				Field("Storage", 62, yBox + (box - lab) / 2, 33, lab, S(6.5, true, align: ReportTextAlign.End)),
				Field("Hazard", 0, yH, W, haz, S(8, true, Alert)),
				Text("حضّره", "Prepared", 0, yS, 12, sign, S(6.5, color: Muted)),
				Field("PreparedBy", 12, yS, 36, sign, S(6.5)),
				Text("راجعه", "Checked", 48, yS, 12, sign, S(6.5, color: Muted)),
				Field("CheckedBy", 60, yS, 36, sign, S(6.5)),
			};
			if (yS + sign > H) throw new InvalidOperationException("The preparation label no longer fits its label: " + (yS + sign) + " mm");
			var band = new ReportBand { Kind = ReportBandKind.Detail, HeightMm = H };
			band.Elements.AddRange(e);
			var layout = new ReportVisualLayout { Page = page, GridMm = 1, SnapToGrid = false };
			layout.Bands.Add(band);
			return layout;
		}

		/// H147 — the PHARMACY LABEL: a 100 × 60 mm thermal label, one per medicine. The label is a DETAIL band exactly
		/// one label page high, so each dispensed medicine (one row) prints on a label of its own. Read top to bottom the
		/// way a patient does: whose it is, what it is, how to take it (framed, the largest instruction text), then how
		/// much, the batch and the expiry beside it, and the warnings in red along the foot.
		public static ReportVisualLayout RxLabel()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.RxLabel, MarginTopMm = 1, MarginBottomMm = 1, MarginLeftMm = 2, MarginRightMm = 2, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
			const double W = 96, H = 58;   // the label less its margins
			double a = Need(7.5), b = Need(9.5), c = Need(11), side = Need(7.5), ins = Need(9.5) * 2;
			double yB = a + 0.8, yC = yB + b, yD = yC + c, yBox = yD + a + 0.5, box = side * 3, yE = yBox + box + 0.4;
			var e = new List<ReportElement>
			{
				Field("CompanyName", 0, 0, 56, a, S(7.5, true, Brand)),
				Field("DocumentNo", 56, 0, 20, a, S(7.5, color: Muted, align: ReportTextAlign.End)),
				Field("DispensedOn", 76, 0, 20, a, S(7.5, color: Muted, align: ReportTextAlign.End, date: "yyyy-MM-dd")),
				Line(0, a + 0.4, W),
				Field("PatientName", 0, yB, 70, b, S(9.5, true)),
				Field("PatientMrn", 70, yB, 26, b, S(8.5, true, Brand, ReportTextAlign.End)),
				Field("Drug", 0, yC, W, c, S(11, true)),
				Field("DrugDetail", 0, yD, 58, a, S(7.5, color: Muted)),
				Field("DoctorName", 58, yD, 38, a, S(7.5, color: Muted, align: ReportTextAlign.End)),
				// how to take it: framed, the instruction the patient must not miss
				Box(0, yBox, 62, box, Brand),
				Field("Instructions", 1, yBox + (box - ins) / 2, 60, ins, S(9.5, true)),
				// how much, which batch, until when
				Text("الكمية", "Qty", 64, yBox, 12, side, S(6.5, color: Muted)),
				Field("Quantity", 76, yBox, 20, side, S(7.5, true)),
				Text("التشغيلة", "Batch", 64, yBox + side, 12, side, S(6.5, color: Muted)),
				Field("Batch", 76, yBox + side, 20, side, S(7.5)),
				Text("ينتهي", "Exp.", 64, yBox + side * 2, 12, side, S(6.5, color: Muted)),
				Field("Expiry", 76, yBox + side * 2, 20, side, S(7.5, true, date: "yyyy-MM")),
				Field("Warnings", 0, yE, W, a, S(7.5, true, Alert)),
			};
			if (yE + a > H) throw new InvalidOperationException("The pharmacy label no longer fits its label: " + (yE + a) + " mm");
			var band = new ReportBand { Kind = ReportBandKind.Detail, HeightMm = H };
			band.Elements.AddRange(e);
			var layout = new ReportVisualLayout { Page = page, GridMm = 1, SnapToGrid = false };
			layout.Bands.Add(band);
			return layout;
		}

		/// H204 — the REFERRAL LETTER, ONE A4 page: the facility's letterhead; the letter's number, date, urgency and the
		/// time the answer is due (two columns); to whom it goes; the patient with the allergies in red; the question, the
		/// clinical summary and the visit's diagnoses (long texts start at the top of their box); the referring doctor
		/// beside a line to sign and stamp. It never runs to a second page (HospitalReferralTests).
		public static readonly ReportPageSetup LetterPage = new() { PageSize = ReportPageSize.A4, MarginTopMm = 12, MarginBottomMm = 12, MarginLeftMm = 15, MarginRightMm = 15, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };

		public static ReportVisualLayout ReferralLetter()
		{
			const double W = 180, Half = 90, L = 30;
			var e = new List<ReportElement>(); double y = 0;
			ReportElementStyle Top(double pt, bool bold = false, string color = Ink) => new()
			{
				FontSizePt = pt, Bold = bold, Color = color, Align = ReportTextAlign.Start, VerticalAlign = ReportVerticalAlign.Top, PaddingMm = 0.5, BorderStyle = ReportBorderStyle.None,
			};
			// one labelled value in a column (x = 0 or Half), `lines` tall
			void Cell(double x, double width, string ar, string en, string key, bool bold = false, string? date = null, int lines = 1, string color = Ink)
			{
				double h = Need(10) * lines;
				e.Add(Text(ar, en, x, y, L, Need(8.5), S(8.5, color: Muted)));
				e.Add(Field(key, x + L, y, width - L, h, lines > 1 ? Top(10, bold, color) : S(10, bold, color, date: date)));
			}
			void Pair((string ar, string en, string key, bool bold, string? date) a, (string ar, string en, string key, bool bold, string? date)? b = null)
			{
				Cell(0, Half - 2, a.ar, a.en, a.key, a.bold, a.date);
				if (b is { } c) Cell(Half, Half, c.ar, c.en, c.key, c.bold, c.date);
				y += Need(10);
			}
			void Heading(string ar, string en) { y += 1.5; e.Add(Text(ar, en, 0, y, W, Need(10), S(10, true, Brand))); y += Need(10); e.Add(Line(0, y, W)); y += 1; }
			void Block(string key, int lines, double pt = 10) { double h = Need(pt) * lines; e.Add(Field(key, 0, y, W, h, Top(pt))); y += h; }

			e.Add(Field("CompanyName", 0, y, W, Need(14), S(14, true, align: ReportTextAlign.Center))); y += Need(14);
			e.Add(Field("CompanyAddress", 0, y, Half - 1, Need(8.5), S(8.5, color: Muted, align: ReportTextAlign.End)));
			e.Add(Field("CompanyPhone", Half + 1, y, Half - 1, Need(8.5), S(8.5, color: Muted))); y += Need(8.5) + 2;
			e.Add(Text("خطاب تحويل", "Referral letter", 0, y, W, Need(16), S(16, true, Brand, ReportTextAlign.Center))); y += Need(16);
			e.Add(Line(0, y, W)); y += 2;
			Pair(("رقم التحويل", "Referral no.", "DocumentNo", true, null), ("التاريخ", "Date", "DocumentDate", false, "yyyy-MM-dd HH:mm"));
			Cell(0, Half - 2, "درجة الاستعجال", "Urgency", "Urgency", true, color: Alert);
			Cell(Half, Half, "مطلوب الرد قبل", "Answer due by", "DueBy", false, "yyyy-MM-dd HH:mm"); y += Need(10);
			Heading("إلى", "To");
			Pair(("الجهة / القسم", "Facility / department", "ToWhom", true, null));
			Pair(("التخصص", "Specialty", "ToSpecialty", false, null));
			Heading("المريض", "Patient");
			Pair(("الاسم", "Name", "PatientName", true, null), ("رقم الملف", "File no.", "PatientMrn", false, null));
			Pair(("السن", "Age", "PatientAge", false, null), ("النوع", "Sex", "PatientSex", false, null));
			Cell(0, W, "الحساسية", "Allergies", "Allergies", true, lines: 2, color: Alert); y += Need(10) * 2;
			Heading("سبب التحويل", "Reason for referral");
			Block("Reason", 3);
			Heading("الملخص السريري", "Clinical summary");
			Block("Summary", 4, 9.5);
			Heading("التشخيص", "Diagnosis");
			Block("Diagnoses", 2, 9.5);
			Heading("الطبيب المحوِّل", "Referring doctor");
			double doc = y;
			Cell(0, Half - 2, "الاسم", "Name", "DoctorName", true); y += Need(10);
			Cell(0, Half - 2, "التخصص", "Specialty", "DoctorTitle"); y += Need(10);
			Cell(0, Half - 2, "رقم الترخيص", "Licence no.", "DoctorLicense"); y += Need(10);
			e.Add(Text("التوقيع والختم", "Signature and stamp", Half, doc, Half, Need(8.5), S(8.5, color: Muted)));
			e.Add(Box(Half, doc + Need(8.5), Half, y - doc - Need(8.5), Rule));
			return OneBand(LetterPage, y + 1, e);
		}

		/// H206 — the SPECIMEN LABEL on the 100 × 60 mm label: the accession number large with its QR beside it (the code the
		/// bedside scan reads), the patient and file number, the test, the specimen and section, the priority in red, and
		/// when it was ordered and collected.
		public static ReportVisualLayout SpecimenLabel()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.RxLabel, MarginTopMm = 1, MarginBottomMm = 1, MarginLeftMm = 2, MarginRightMm = 2, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
			const double W = 96, H = 58, Qs = 30, Lw = W - Qs - 2;
			double a = Need(7.5), b = Need(10), c = Need(14), d = Need(11), y = 0;
			var e = new List<ReportElement>();
			e.Add(Field("CompanyName", 0, y, W, a, S(7.5, true, Brand))); y += a + 0.4;
			e.Add(Line(0, y, W)); y += 0.8;
			double top = y;
			e.Add(Field("PatientName", 0, y, Lw, b, S(10, true))); y += b;
			e.Add(Field("PatientMrn", 0, y, Lw, b, S(10, true, Brand))); y += b;
			e.Add(Field("DocumentNo", 0, y, Lw, c, S(14, true))); y += c;
			e.Add(Field("Test", 0, y, Lw, d, S(11, true))); y += d;
			e.Add(Field("Specimen", 0, y, Lw / 2, a, S(7.5, color: Muted)));
			e.Add(Field("Priority", Lw / 2, y, Lw / 2, a, S(7.5, true, Alert, ReportTextAlign.End))); y += a;
			e.Add(Text("طُلب", "Ordered", 0, y, 12, a, S(6.5, color: Muted)));
			e.Add(Field("DocumentDate", 12, y, Lw - 12, a, S(7.5, date: "yyyy-MM-dd HH:mm"))); y += a;
			e.Add(Qr("DocumentNo", W - Qs, top, Qs));
			if (y > H) throw new InvalidOperationException("The specimen label no longer fits its label: " + y + " mm");
			return OneBand(page, Math.Max(y, top + Qs) + 0.5, e);
		}

		/// H208 — the SICK LEAVE CERTIFICATE, one A5 page: the letterhead; the certificate's number and date with its QR (the
		/// public verification address) beside them; the patient; the period and its days large; the recommendation; the
		/// diagnosis only when the doctor chose to print it; the notes; the doctor beside a box to sign and stamp; the
		/// Ministry's authentication line; a void certificate says so in red across its head.
		public static ReportVisualLayout SickLeave()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.A5, MarginTopMm = 10, MarginBottomMm = 10, MarginLeftMm = 12, MarginRightMm = 12, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
			const double W = 124, Half = 62, L = 24, Q = 30;
			var e = new List<ReportElement>(); double y = 0;
			void Cell(double x, double width, string ar, string en, string key, bool bold = false, string? date = null, double pt = 9.5, string color = Ink, string? number = null)
			{
				e.Add(Text(ar, en, x, y, L, Need(8), S(8, color: Muted)));
				e.Add(Field(key, x + L, y, width - L, Need(pt), S(pt, bold, color, date: date, number: number)));
			}
			void Heading(string ar, string en) { y += 1; e.Add(Text(ar, en, 0, y, W, Need(9.5), S(9.5, true, Brand))); y += Need(9.5); e.Add(Line(0, y, W)); y += 1; }

			e.Add(Field("CompanyName", 0, y, W - Q - 2, Need(13), S(13, true))); y += Need(13);
			e.Add(Field("CompanyAddress", 0, y, W - Q - 2, Need(8), S(8, color: Muted))); y += Need(8);
			e.Add(Field("CompanyPhone", 0, y, W - Q - 2, Need(8), S(8, color: Muted))); y += Need(8) + 1;
			e.Add(Text("شهادة إجازة مرضية", "Sick leave certificate", 0, y, W - Q - 2, Need(14), S(14, true, Brand))); y += Need(14);
			e.Add(Qr("VerifyUrl", W - Q, 0, Q));
			e.Add(Field("VerifyCode", W - Q, Q + 0.5, Q, Need(7.5), S(7.5, true, Muted, ReportTextAlign.Center)));
			y = Math.Max(y, Q + 0.5 + Need(7.5)) + 1;
			e.Add(Field("Void", 0, y, W, Need(10), S(10, true, Alert, ReportTextAlign.Center))); y += Need(10);
			e.Add(Line(0, y, W)); y += 1.5;
			Cell(0, Half - 2, "رقم الشهادة", "Certificate no.", "DocumentNo", true);
			Cell(Half, Half, "تاريخ الإصدار", "Issued", "DocumentDate", date: "yyyy-MM-dd HH:mm"); y += Need(9.5);
			Heading("المريض", "Patient");
			Cell(0, W, "الاسم", "Name", "PatientName", true); y += Need(9.5);
			Cell(0, Half - 2, "رقم الملف", "File no.", "PatientMrn");
			Cell(Half, Half, "السن", "Age", "PatientAge"); y += Need(9.5);
			Heading("الإجازة", "Leave");
			Cell(0, Half - 2, "من", "From", "FromDate", true, "yyyy-MM-dd");
			Cell(Half, Half, "إلى", "To", "ToDate", true, "yyyy-MM-dd"); y += Need(9.5);
			Cell(0, Half - 2, "عدد الأيام", "Days", "Days", true, pt: 12, color: Brand, number: "0");
			y += Need(12);
			Cell(0, W, "التوصية", "Recommendation", "Kind", true); y += Need(9.5);
			e.Add(Text("التشخيص", "Diagnosis", 0, y, L, Need(8), S(8, color: Muted)));
			e.Add(Field("Diagnosis", L, y, W - L, Need(9) * 2, S(9))); y += Need(9) * 2;
			e.Add(Text("ملاحظات", "Notes", 0, y, L, Need(8), S(8, color: Muted)));
			e.Add(Field("Note", L, y, W - L, Need(9), S(9))); y += Need(9);
			Heading("الطبيب", "Doctor");
			// the name across the page (a doctor's name runs long), the box to sign and stamp beneath it
			Cell(0, W, "الاسم", "Name", "DoctorName", true); y += Need(9.5);
			Cell(0, Half - 2, "التخصص", "Specialty", "DoctorTitle");
			Cell(Half, Half, "الترخيص", "Licence", "DoctorLicense"); y += Need(9.5);
			e.Add(Text("التوقيع والختم", "Signature and stamp", Half, y, Half, Need(8), S(8, color: Muted))); y += Need(8);
			e.Add(Box(Half, y, Half, 14, Rule)); y += 14;
			y += 1;
			Cell(0, W, "وزارة الصحة", "Ministry of Health", "MohAuth", pt: 8.5); y += Need(9.5);
			e.Add(Text("امسح الرمز للتحقق من صحة هذه الشهادة", "Scan the code to verify this certificate", 0, y, W, Need(7.5), S(7.5, color: Muted, align: ReportTextAlign.Center))); y += Need(7.5);
			return OneBand(page, y + 0.5, e);
		}

		/// H209 — the NOTIFIABLE DISEASE case form, one A5 page: the letterhead and the title; the case number and date; the
		/// disease large with its group in red; the patient with civil id, age, sex and phone; the diagnosis and when, and the
		/// time the report is due by; the diagnosing doctor beside a box to sign; the Ministry's reference and the state.
		public static ReportVisualLayout DiseaseReport()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.A5, MarginTopMm = 10, MarginBottomMm = 10, MarginLeftMm = 12, MarginRightMm = 12, ShowHeader = false, ShowFooter = false, ShowPageNumbers = false };
			const double W = 124, Half = 62, L = 26;
			var e = new List<ReportElement>(); double y = 0;
			void Cell(double x, double width, string ar, string en, string key, bool bold = false, string? date = null, double pt = 9.5, string color = Ink)
			{
				e.Add(Text(ar, en, x, y, L, Need(8), S(8, color: Muted)));
				e.Add(Field(key, x + L, y, width - L, Need(pt), S(pt, bold, color, date: date)));
			}
			void Heading(string ar, string en) { y += 1; e.Add(Text(ar, en, 0, y, W, Need(9.5), S(9.5, true, Brand))); y += Need(9.5); e.Add(Line(0, y, W)); y += 1; }

			e.Add(Field("CompanyName", 0, y, W, Need(13), S(13, true))); y += Need(13);
			e.Add(Field("CompanyAddress", 0, y, W, Need(8), S(8, color: Muted))); y += Need(8);
			e.Add(Field("CompanyPhone", 0, y, W, Need(8), S(8, color: Muted))); y += Need(8) + 1;
			e.Add(Text("إبلاغ عن مرض واجب الإبلاغ", "Notifiable disease report", 0, y, W, Need(14), S(14, true, Brand))); y += Need(14);
			e.Add(Text("إلى: وزارة الصحة — إدارة الصحة العامة", "To: Ministry of Health — Public Health Department", 0, y, W, Need(9), S(9, color: Muted))); y += Need(9) + 1;
			e.Add(Line(0, y, W)); y += 1.5;
			Cell(0, Half - 2, "رقم الحالة", "Case no.", "DocumentNo", true);
			Cell(Half, Half, "التاريخ", "Date", "DocumentDate", date: "yyyy-MM-dd HH:mm"); y += Need(9.5);
			Heading("المرض", "Disease");
			e.Add(Field("Disease", 0, y, W, Need(13), S(13, true))); y += Need(13);
			e.Add(Field("Group", 0, y, W, Need(9.5), S(9.5, true, Alert))); y += Need(9.5);
			Cell(0, W, "التشخيص", "Diagnosis", "Diagnosis"); y += Need(9.5);
			Cell(0, Half - 2, "وقت التشخيص", "Diagnosed", "DiagnosedAt", date: "yyyy-MM-dd HH:mm");
			Cell(Half, Half, "موعد الإبلاغ", "Report due by", "DueBy", true, "yyyy-MM-dd HH:mm", color: Alert); y += Need(9.5);
			Heading("المريض", "Patient");
			Cell(0, W, "الاسم", "Name", "PatientName", true); y += Need(9.5);
			Cell(0, Half - 2, "رقم الملف", "File no.", "PatientMrn");
			Cell(Half, Half, "الرقم المدني", "Civil id", "CivilId"); y += Need(9.5);
			Cell(0, Half - 2, "السن", "Age", "PatientAge");
			Cell(Half, Half, "الجنس", "Sex", "PatientSex"); y += Need(9.5);
			Cell(0, W, "الهاتف", "Phone", "PatientPhone"); y += Need(9.5);
			Heading("الطبيب المبلِّغ", "Reporting doctor");
			// the name across the page (a doctor's name runs long), the box to sign and stamp beneath it
			Cell(0, W, "الاسم", "Name", "DoctorName", true); y += Need(9.5);
			Cell(0, Half - 2, "التخصص", "Specialty", "DoctorTitle");
			Cell(Half, Half, "الترخيص", "Licence", "DoctorLicense"); y += Need(9.5);
			e.Add(Text("التوقيع والختم", "Signature and stamp", Half, y, Half, Need(8), S(8, color: Muted))); y += Need(8);
			e.Add(Box(Half, y, Half, 14, Rule)); y += 14;
			y += 1;
			Heading("الإبلاغ", "Report");
			Cell(0, W, "الحالة", "State", "Status"); y += Need(9.5);
			Cell(0, W, "رقم الوزارة", "Ministry reference", "MohRef", true); y += Need(9.5);
			return OneBand(page, y + 0.5, e);
		}

		/// H211 — the DOWNTIME WARD SUMMARY, A4 landscape: on every page the facility, the title in red, the ward and the
		/// time it was printed (a paper copy is only as good as its time) and how to use it; for each patient (grouped by
		/// bed) a framed head with the bed large, the name, file, age, sex and blood group, the allergies and isolation in
		/// red, the diagnosis and the attending doctor, then the medicine columns; one line per medicine with four empty
		/// boxes to chart the doses given while the system is down.
		public static ReportVisualLayout WardDowntime()
		{
			var page = new ReportPageSetup { PageSize = ReportPageSize.A4, Orientation = ReportOrientation.Landscape, MarginTopMm = 10, MarginBottomMm = 10, MarginLeftMm = 12, MarginRightMm = 12, ShowHeader = false, ShowFooter = false, ShowPageNumbers = true };
			const double W = 273;
			// the page head
			var head = new List<ReportElement>(); double y = 0;
			head.Add(Field("CompanyName", 0, y, 140, Need(12), S(12, true)));
			head.Add(Text("نسخة وقت التوقف — ملخص القسم", "DOWNTIME — ward summary", 140, y, W - 140, Need(12), S(12, true, Alert, ReportTextAlign.End))); y += Need(12);
			head.Add(Text("القسم", "Ward", 0, y, 18, Need(9.5), S(8, color: Muted)));
			head.Add(Field("Ward", 18, y, 110, Need(9.5), S(9.5, true, Brand)));
			head.Add(Text("طُبع في", "Printed", 140, y, 25, Need(9.5), S(8, color: Muted)));
			head.Add(Field("DocumentDate", 165, y, W - 165, Need(9.5), S(9.5, true, date: "yyyy-MM-dd HH:mm"))); y += Need(9.5);
			head.Add(Text("نسخة للقراءة فقط: سجّل الجرعات المعطاة في الخانات ورقيًا، وأدخلها في النظام عند عودته.", "Read-only copy: chart the doses given in the boxes on paper, and enter them in the system when it is back.", 0, y, W, Need(8), S(8, color: Muted))); y += Need(8);
			head.Add(Line(0, y + 0.5, W));
			var pageHead = new ReportBand { Kind = ReportBandKind.PageHeader, HeightMm = y + 1.5 };
			pageHead.Elements.AddRange(head);

			// one patient's head
			var g = new List<ReportElement>(); y = 1;
			double h1 = Need(11), h2 = Need(9), h3 = Need(8.5), hc = Need(7.5);
			g.Add(Box(0, 0.5, W, h1 + h2 + h3 + 1.5, Rule, "#F1F4F9"));
			g.Add(Field("Bed", 1, y, 22, h1, S(11, true, Brand)));
			g.Add(Field("PatientName", 24, y, 100, h1, S(11, true)));
			g.Add(Field("PatientMrn", 126, y, 36, h1, S(9, true, Muted)));
			g.Add(Field("PatientAge", 164, y, 24, h1, S(9)));
			g.Add(Field("PatientSex", 190, y, 24, h1, S(9)));
			g.Add(Text("الفصيلة", "Blood", 216, y, 18, h1, S(8, color: Muted)));
			g.Add(Field("BloodGroup", 234, y, 38, h1, S(10, true, Alert))); y += h1;
			g.Add(Field("Allergies", 1, y, 135, h2, S(9, true, Alert)));
			g.Add(Field("Isolation", 138, y, 134, h2, S(9, true, Alert))); y += h2;
			g.Add(Field("Diagnosis", 1, y, 180, h3, S(8.5)));
			g.Add(Field("DoctorName", 183, y, 89, h3, S(8.5, color: Muted, align: ReportTextAlign.End))); y += h3 + 1.5;
			(string Ar, string En, double X, double Wd)[] cols = { ("الدواء", "Medicine", 0, 70), ("الجرعة", "Dose", 70, 30), ("الطريقة", "Route", 100, 14), ("المواعيد", "Schedule", 114, 36), ("آخر إعطاء", "Last given", 150, 30), ("الجرعة التالية", "Next due", 180, 30) };
			foreach (var c in cols) g.Add(Text(c.Ar, c.En, c.X + 1, y, c.Wd - 1, hc, S(7.5, true, Muted)));
			g.Add(Text("أُعطيت (الوقت / التوقيع)", "Given (time / initials)", 211, y, W - 211, hc, S(7.5, true, Muted, ReportTextAlign.Center))); y += hc;
			var group = new ReportBand { Kind = ReportBandKind.GroupHeader, GroupFieldKey = "PatientKey", HeightMm = y + 0.5 };
			group.Elements.AddRange(g);

			// one medicine
			double hd = Need(8.5);
			var d = new List<ReportElement>
			{
				Field("Drug", 1, 0, 69, hd, S(8.5, true)),
				Field("Dose", 71, 0, 29, hd, S(8.5)),
				Field("Route", 101, 0, 13, hd, S(8.5)),
				Field("Schedule", 115, 0, 35, hd, S(8.5)),
				Field("LastGiven", 151, 0, 29, hd, S(8, color: Muted, date: "MM-dd HH:mm")),
				Field("NextDue", 181, 0, 29, hd, S(8.5, true, Brand, date: "MM-dd HH:mm")),
			};
			for (int k = 0; k < 4; k++) d.Add(Box(211 + k * 15.5, 0.5, 15, hd - 1, Rule));
			d.Add(Line(0, hd + 0.3, W));
			var detail = new ReportBand { Kind = ReportBandKind.Detail, HeightMm = hd + 0.8 };
			detail.Elements.AddRange(d);

			var layout = new ReportVisualLayout { Page = page, GridMm = 1, SnapToGrid = false };
			layout.Bands.Add(pageHead); layout.Bands.Add(group); layout.Bands.Add(detail);
			return layout;
		}

		/// H216 — the PATHOLOGY REPORT, one A4 page: the letterhead and title; the case and the dates; the patient and the
		/// requesting doctor; the specimen and its source; the clinical history; the gross and microscopic descriptions
		/// (long texts start at the top of their box); the DIAGNOSIS framed, with MALIGNANT in red when it is; the
		/// pathologist's signature; the amendments, if any, beneath.
		public static ReportVisualLayout PathReport()
		{
			const double W = 180, Half = 90, L = 30;
			var e = new List<ReportElement>(); double y = 0;
			ReportElementStyle Top(double pt, bool bold = false, string color = Ink) => new()
			{
				FontSizePt = pt, Bold = bold, Color = color, Align = ReportTextAlign.Start, VerticalAlign = ReportVerticalAlign.Top, PaddingMm = 0.5, BorderStyle = ReportBorderStyle.None,
			};
			void Cell(double x, double width, string ar, string en, string key, bool bold = false, string? date = null, string color = Ink)
			{
				e.Add(Text(ar, en, x, y, L, Need(8.5), S(8.5, color: Muted)));
				e.Add(Field(key, x + L, y, width - L, Need(10), S(10, bold, color, date: date)));
			}
			void Heading(string ar, string en) { y += 1.5; e.Add(Text(ar, en, 0, y, W, Need(10), S(10, true, Brand))); y += Need(10); e.Add(Line(0, y, W)); y += 1; }
			void Block(string key, int lines, double pt = 9.5) { double h = Need(pt) * lines; e.Add(Field(key, 0, y, W, h, Top(pt))); y += h; }

			e.Add(Field("CompanyName", 0, y, W, Need(14), S(14, true, align: ReportTextAlign.Center))); y += Need(14);
			e.Add(Field("CompanyAddress", 0, y, Half - 1, Need(8.5), S(8.5, color: Muted, align: ReportTextAlign.End)));
			e.Add(Field("CompanyPhone", Half + 1, y, Half - 1, Need(8.5), S(8.5, color: Muted))); y += Need(8.5) + 2;
			e.Add(Text("تقرير علم الأمراض", "Pathology report", 0, y, W, Need(16), S(16, true, Brand, ReportTextAlign.Center))); y += Need(16);
			e.Add(Line(0, y, W)); y += 2;
			Cell(0, Half - 2, "رقم الحالة", "Case no.", "DocumentNo", true);
			Cell(Half, Half, "تاريخ الاستلام", "Received", "Received", date: "yyyy-MM-dd HH:mm"); y += Need(10);
			Cell(0, Half - 2, "الاسم", "Patient", "PatientName", true);
			Cell(Half, Half, "رقم الملف", "File no.", "PatientMrn"); y += Need(10);
			Cell(0, Half - 2, "السن", "Age", "PatientAge");
			Cell(Half, Half, "النوع", "Sex", "PatientSex"); y += Need(10);
			Cell(0, Half - 2, "الطبيب الطالب", "Requested by", "DoctorName");
			Cell(Half, Half, "المصدر", "Source", "Source"); y += Need(10);
			Heading("العينة", "Specimen");
			Block("Specimen", 1, 10);
			Heading("التاريخ السريري", "Clinical history");
			Block("ClinicalHistory", 2);
			Heading("الوصف العياني", "Gross description");
			Block("Gross", 2);
			Heading("الوصف المجهري", "Microscopic description");
			Block("Microscopic", 4);
			Heading("التشخيص", "Diagnosis");
			double box = Need(11) * 2 + 2;
			e.Add(Box(0, y, W, box, Brand));
			e.Add(Field("Diagnosis", 2, y + 1, W - 40, Need(11) * 2, Top(11, true)));
			e.Add(Field("Malignant", W - 36, y + 1, 34, Need(11), S(11, true, Alert, ReportTextAlign.End)));
			y += box + 2;
			double sig = y;
			Cell(0, Half - 2, "أخصائي علم الأمراض", "Pathologist", "Pathologist", true); y += Need(10);
			Cell(0, Half - 2, "تاريخ التوقيع", "Signed", "SignedAt", date: "yyyy-MM-dd HH:mm"); y += Need(10);
			e.Add(Text("التوقيع", "Signature", Half, sig, Half, Need(8.5), S(8.5, color: Muted)));
			e.Add(Box(Half, sig + Need(8.5), Half, y - sig - Need(8.5), Rule));
			Heading("التعديلات", "Amendments");
			Block("Amendments", 2, 9);
			if (y + 1 > ReportPaper.ContentHeightMm(LetterPage)) throw new InvalidOperationException("The pathology report no longer fits one page: " + (y + 1) + " mm");
			return OneBand(LetterPage, y + 1, e);
		}

		/// The designed layout a desk report ships with, by report code (null = the platform's starter).
		public static ReportVisualLayout? For(string code) => code switch
		{
			HospitalDocumentDatasetCodes.VisitSlip => VisitSlip(),
			HospitalDocumentDatasetCodes.Wristband => Wristband(),
			HospitalDocumentDatasetCodes.DepositReceipt => DepositReceipt(),
			HospitalDocumentDatasetCodes.RxLabel => RxLabel(),   // H147
			HospitalDocumentDatasetCodes.CreditNote => CreditNote(),   // H149
			HospitalDocumentDatasetCodes.CompoundLabel => CompoundLabel(),   // H153
			HospitalDocumentDatasetCodes.ReferralLetter => ReferralLetter(),   // H204
			HospitalDocumentDatasetCodes.SpecimenLabel => SpecimenLabel(),   // H206
			HospitalDocumentDatasetCodes.SickLeave => SickLeave(),   // H208
			HospitalDocumentDatasetCodes.DiseaseReport => DiseaseReport(),   // H209
			HospitalDocumentDatasetCodes.WardDowntime => WardDowntime(),   // H211
			HospitalDocumentDatasetCodes.PathReport => PathReport(),   // H216
			_ => null,
		};
	}
}
