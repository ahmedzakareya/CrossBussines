namespace CrossBuy.BL
{
	// ============================================================================================
	// WHAT DOCUMENT A STOCK MOVEMENT CAME FROM, in words.
	//
	// The map used to live inside _MovementRows.cshtml, which was fine while one screen rendered it.
	// A filter on the same page now needs the identical label on its options, and a map copied into
	// a second view is a map that drifts: the day a type is added to one, the dropdown and the rows
	// disagree about the same movement. One map, two readers.
	//
	// IT RETURNS A KEY AND A FALLBACK, NOT A STRING, on purpose. Resolving the text here would need
	// an IViewLocalizer and would bake this file's culture into a shared helper; instead each view
	// keeps its own `Lz(key, arabicFallback)` and this supplies both halves. That is also what lets
	// a new resource key start working the moment the satellite assembly is rebuilt, without the
	// literal being deleted first.
	//
	// THE LIST WAS SHORT BY EIGHT. The old comment claimed "all twelve types the data actually
	// holds"; the database has twenty, and PurchaseInvoiceEdit - the second most common type on the
	// screen - fell through to its raw English column value and sat in Latin script beside Arabic
	// labels in the same column. Anything genuinely unknown still falls through, which is correct:
	// an invented label is worse than a visible gap.
	// ============================================================================================
	public static class MovementSourceLabels
	{
		public static (string Key, string Ar) For(string? sourceType) => sourceType switch
		{
			"Opening" => ("Opening", "رصيد افتتاحي"),
			"OpeningStock" => ("Opening stock", "رصيد افتتاحي"),
			"Receipt" => ("Receipt", "إذن استلام"),
			"Issue" => ("Issue", "إذن صرف"),
			"Adjustment" => ("Adjustment", "تسوية"),
			"PurchaseInvoice" => ("Purchase", "فاتورة شراء"),
			"SalesInvoice" => ("Sale", "فاتورة بيع"),
			"SalesInvoiceReversal" => ("Sales reversal", "عكس فاتورة بيع"),
			"SalesReturn" => ("Sales return", "مرتجع بيع"),
			"PurchaseReturn" => ("Purchase return", "مرتجع شراء"),
			"WorkOrder" => ("Work order", "أمر تشغيل"),
			"Assembly" => ("Assembly", "تجميع"),

			// ---- the eight that were falling through ----
			// An EDIT is its own movement, not a variant of the document it corrects: re-posting an
			// invoice reverses the original lines and posts the new ones, so both appear in the
			// ledger and a reader has to be able to tell them apart at a glance.
			"PurchaseInvoiceEdit" => ("Purchase edit", "تعديل فاتورة شراء"),
			"SalesInvoiceEdit" => ("Sale edit", "تعديل فاتورة بيع"),
			"PurchaseReturnEdit" => ("Purchase return edit", "تعديل مرتجع شراء"),

			// A transfer is one logical move written as two movements, and naming them apart is the
			// only way the pair reads as a transfer rather than as an unexplained loss and gain.
			"TransferIn" => ("Transfer in", "تحويل وارد"),
			"TransferOut" => ("Transfer out", "تحويل منصرف"),

			"ProjectIssue" => ("Project issue", "صرف لمشروع"),
			"StockWriteOff" => ("Write-off", "إهلاك مخزون"),

			// Value only: a landed-cost row carries a quantity of zero and exists to move cost onto
			// stock already received. Labelling it as a receipt would make it look like goods arrived.
			"LandedCost" => ("Landed cost", "تكلفة إضافية"),

			// Stock leaving inventory to become a fixed asset. Found by listing what the data actually
			// holds rather than by reading the code - which is the whole argument for building that
			// dropdown from a DISTINCT over the column instead of from a literal list.
			"AssetCapitalization" => ("Asset capitalization", "رسملة أصل"),

			_ => (sourceType ?? "—", sourceType ?? "—"),
		};
	}
}
