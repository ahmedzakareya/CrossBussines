using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace CrossBuy.Tests
{
	/// The model against its deploy slice, DATABASE-FREE: the EF model built with the SQL Server conventions and no
	/// connection, read against the slice's text. Shared by the hospital units that ship a slice (H15 on).
	internal static class HospitalSliceParity
	{
		public static string Slice(string file)
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
			{
				var f = Path.Combine(d.FullName, "CrossBuy", "deploy", "sql", file);   // the registered slice, not a copy
				if (File.Exists(f)) return File.ReadAllText(f);
			}
			throw new FileNotFoundException($"CrossBuy/deploy/sql/{file} not found above " + AppContext.BaseDirectory);
		}

		public static IModel Model(Action<ModelBuilder> configure)
		{
			var mb = new ModelBuilder(SqlServerConventionSetBuilder.Build());
			configure(mb);
			return mb.FinalizeModel();
		}

		/// The model names the table and the slice creates it.
		public static void NamesItsTable(IModel model, string sql, Type entity, string table)
		{
			Assert.Equal(table, model.FindEntityType(entity)!.GetTableName());
			Assert.Contains($"CREATE TABLE dbo.{table} (", sql.Split('\n').Select(l => l.Trim()));
		}

		/// Every column the model reads is in the slice, in the type the slice creates it in (a tinyint read as int
		/// throws at runtime: H13's InvalidCastException), and the slice has no column the model does not know.
		public static void ColumnsMatch(IModel model, string sql, Type entity, string table)
		{
			int a = sql.IndexOf($"CREATE TABLE dbo.{table} (", StringComparison.Ordinal), b = sql.IndexOf(");", a, StringComparison.Ordinal);
			var cols = sql[a..b].Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
				.Where(p => p.Length > 1 && !p[0].StartsWith("CONSTRAINT") && !p[0].StartsWith("--") && char.IsLower(p[1][0]))   // a column line: name, then its (lower-case) type
				.ToDictionary(p => p[0], p => p[1].TrimEnd(','));
			// a later slice may add a column (H33: "ALTER TABLE dbo.T ADD Col type NULL") — pass both slices' text
			foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, $@"ALTER TABLE dbo\.{table} ADD (\w+) (\w+(?:\(\d+(?:,\d+)?\))?)"))
				cols[m.Groups[1].Value] = m.Groups[2].Value;
			var et = model.FindEntityType(entity)!;
			foreach (var p in et.GetProperties())
			{
				Assert.True(cols.ContainsKey(p.GetColumnName()), $"{table}.{p.GetColumnName()} is not in the slice");
				// the type the model reads it as: the configured column type, else the SQL Server default for the CLR type
				var clr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
				if (clr == typeof(string)) continue;   // text widths are the slice's; the reader does not care
				var read = p.FindAnnotation("Relational:ColumnType")?.Value as string
					?? (clr == typeof(int) ? "int" : clr == typeof(long) ? "bigint" : clr == typeof(byte) ? "tinyint" : clr == typeof(bool) ? "bit" : clr == typeof(DateTime) ? "datetime2"
						: clr == typeof(decimal) ? $"decimal({p.GetPrecision()},{p.GetScale()})" : clr.Name);
				Assert.True(cols[p.GetColumnName()] == read, $"{table}.{p.GetColumnName()}: the slice creates {cols[p.GetColumnName()]}, the model reads {read}");
			}
			Assert.Equal(cols.Count, et.GetProperties().Count());
		}

		/// A slice that references a table it does not create refuses to run without it (its THROW guard).
		public static IReadOnlyList<string> CheckedPrerequisites(string sql)
		{
			var created = System.Text.RegularExpressions.Regex.Matches(sql, @"CREATE TABLE dbo\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
			var referenced = System.Text.RegularExpressions.Regex.Matches(sql, @"REFERENCES dbo\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Except(created).ToList();
			var guard = sql[..sql.IndexOf("THROW", StringComparison.Ordinal)];
			foreach (var t in referenced) Assert.Contains($"OBJECT_ID('dbo.{t}','U') IS NULL", guard);
			return referenced;
		}
	}
}
