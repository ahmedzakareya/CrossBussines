using ClosedXML.Excel;

namespace CrossBuy.BL
{
	// Reusable .xlsx builder (ClosedXML). RTL sheet, branded green header, autofilter, frozen header, auto-width.
	public static class ExcelExporter
	{
		public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

		public static byte[] Build(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, string? title = null)
		{
			using var wb = new XLWorkbook();
			var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : Clean(sheetName));
			ws.RightToLeft = true;
			int r = 1;
			if (!string.IsNullOrWhiteSpace(title))
			{
				var t = ws.Cell(r, 1); t.Value = title;
				t.Style.Font.Bold = true; t.Style.Font.FontSize = 14; t.Style.Font.FontColor = XLColor.FromHtml("#0E4A9E");
				ws.Range(r, 1, r, Math.Max(1, headers.Count)).Merge();
				r += 2;
			}
			int headerRow = r;
			for (int c = 0; c < headers.Count; c++)
			{
				var cell = ws.Cell(headerRow, c + 1);
				cell.Value = headers[c];
				cell.Style.Font.Bold = true;
				cell.Style.Font.FontColor = XLColor.White;
				cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0E4A9E");
				cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
			}
			r = headerRow + 1;
			foreach (var row in rows)
			{
				for (int c = 0; c < row.Count; c++) ws.Cell(r, c + 1).Value = ToCell(row[c]);
				r++;
			}
			if (headers.Count > 0)
			{
				ws.Range(headerRow, 1, headerRow, headers.Count).SetAutoFilter();
				ws.SheetView.FreezeRows(headerRow);
			}
			ws.Columns().AdjustToContents();
			using var ms = new MemoryStream();
			wb.SaveAs(ms);
			return ms.ToArray();
		}

		private static XLCellValue ToCell(object? v) => v switch
		{
			null => Blank.Value,
			string s => s,
			bool b => b,
			int i => i,
			long l => l,
			decimal d => d,
			double db => db,
			DateTime dt => dt,
			_ => v.ToString() ?? ""
		};

		// worksheet names can't contain : \ / ? * [ ] and max 31 chars
		private static string Clean(string s)
		{
			foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']' }) s = s.Replace(ch, ' ');
			return s.Length > 31 ? s.Substring(0, 31) : s;
		}
	}
}
