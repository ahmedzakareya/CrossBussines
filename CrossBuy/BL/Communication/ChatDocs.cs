using System.Text;
using CrossBuy.Hubs;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// SHARED DOCUMENTS INSIDE A CONVERSATION (owner, 2026-11-22). A member opens a document from the conversation's
	// "+" menu; a card for it is posted in the conversation, and every member reads and edits it:
	//
	//   * Each save carries the version the editor started from. When nobody saved since, the text is taken as it is.
	//     When someone did, the two edits are MERGED line by line against that common version (a three-way merge):
	//     lines only one of them changed take that change; lines both changed the same way are kept once; lines both
	//     changed differently keep BOTH versions, marked with each writer's name, so nothing anyone wrote is lost.
	//   * Every save is a version (who, when, the whole text, whether it was merged): the history, the base a later
	//     concurrent save merges against, and what "restore" puts back — as a NEW version, never by rewriting history.
	//   * A save tells the conversation ("doc" on the conversation's hub group), so an open editor follows along.
	//   * The document is kept as a file in the company's File Manager too (/uploads/library/{co}/chat/docs/{id}.md),
	//     rewritten on every save — one file, not a copy per version.
	//   * The authority is the conversation's: whoever may read it reads the document; whoever may send in it edits.
	//     The controller asks that; this service checks the document belongs to the company and the conversation.
	// ==========================================================================================
	public sealed class ChatDocService
	{
		private readonly CrossDbContext _db;
		private readonly IHubContext<ChatHub> _hub;
		private readonly IWebHostEnvironment _env;
		private readonly IServiceProvider _services;
		public ChatDocService(CrossDbContext db, IHubContext<ChatHub> hub, IWebHostEnvironment env, IServiceProvider services) { _db = db; _hub = hub; _env = env; _services = services; }

		public const int MaxChars = 200_000;
		public const int MaxTitle = 150;

		public sealed record SaveResult(bool Ok, string? Code, int Version, string? Content, bool Merged, bool Conflict);

		private static DateTime U(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
		private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		private static string CleanTitle(string? t) { t = (t ?? "").Trim(); return t.Length > MaxTitle ? t[..MaxTitle] : t; }

		public Task<int> ConversationOfAsync(int companyId, int docId)
			=> _db.Set<ChatDoc>().AsNoTracking().Where(d => d.ID == docId && d.CompanyId == companyId && d.DeletedAt == null).Select(d => d.ConversationId).FirstOrDefaultAsync();

		private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids)
		{
			var list = ids.Distinct().ToList(); if (list.Count == 0) return new();
			var ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var rows = await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();
			return rows.ToDictionary(e => e.ID, e => (ar ? (e.FullName ?? e.FullNameEn) : (string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn)) ?? "-");
		}

		// ---------------- create: the document, its first version, its file and its card in the conversation ----------------
		public async Task<(ChatDoc doc, ChatMessage msg)?> CreateAsync(int companyId, int convId, int meId, string? title, string? content, ChatPlusService plus)
		{
			title = CleanTitle(title); var text = Norm(content);
			if (title.Length == 0 || text.Length > MaxChars) return null;
			var now = DateTime.UtcNow;
			var doc = new ChatDoc { CompanyId = companyId, ConversationId = convId, Title = title, Content = text, Version = 1, CreatedByEmployeeId = meId, CreatedAt = now, UpdatedByEmployeeId = meId, UpdatedAt = now };
			_db.Set<ChatDoc>().Add(doc);
			await _db.SaveChangesAsync();
			_db.Set<ChatDocVersion>().Add(new ChatDocVersion { DocId = doc.ID, Version = 1, Content = text, Title = title, EmployeeId = meId, CreatedAt = now });
			await _db.SaveChangesAsync();
			// the card: a message the conversation reads like any other, pointing at the document
			var msg = await plus.PostAsync(companyId, convId, meId, new ChatPlusService.PostInput { AttachmentPath = "doc:" + doc.ID, AttachmentName = title, AttachmentType = "doc" });
			if (msg == null)
			{
				_db.Set<ChatDocVersion>().RemoveRange(_db.Set<ChatDocVersion>().Where(v => v.DocId == doc.ID));
				_db.Set<ChatDoc>().Remove(doc); await _db.SaveChangesAsync();
				return null;
			}
			doc.MessageId = msg.ID;
			await WriteFileAsync(doc, meId, register: true);
			await _db.SaveChangesAsync();
			return (doc, msg);
		}

		// ---------------- read ----------------
		public async Task<object?> GetAsync(int companyId, int convId, int docId)
		{
			var d = await _db.Set<ChatDoc>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == docId && x.CompanyId == companyId && x.ConversationId == convId && x.DeletedAt == null);
			if (d == null) return null;
			var names = await NamesAsync(new[] { d.CreatedByEmployeeId, d.UpdatedByEmployeeId });
			var editors = await _db.Set<ChatDocVersion>().AsNoTracking().Where(v => v.DocId == d.ID).Select(v => v.EmployeeId).Distinct().CountAsync();
			return new
			{
				id = d.ID, conversationId = d.ConversationId, title = d.Title, content = d.Content, version = d.Version,
				createdBy = names.GetValueOrDefault(d.CreatedByEmployeeId), createdAt = U(d.CreatedAt),
				updatedBy = names.GetValueOrDefault(d.UpdatedByEmployeeId), updatedById = d.UpdatedByEmployeeId, updatedAt = U(d.UpdatedAt), editors,
			};
		}

		public async Task<object?> HistoryAsync(int companyId, int convId, int docId)
		{
			if (!await _db.Set<ChatDoc>().AsNoTracking().AnyAsync(x => x.ID == docId && x.CompanyId == companyId && x.ConversationId == convId && x.DeletedAt == null)) return null;
			var rows = await _db.Set<ChatDocVersion>().AsNoTracking().Where(v => v.DocId == docId).OrderByDescending(v => v.Version).Take(200)
				.Select(v => new { v.Version, v.Title, v.EmployeeId, v.CreatedAt, v.Merged, Length = v.Content.Length }).ToListAsync();
			var names = await NamesAsync(rows.Select(r => r.EmployeeId));
			return rows.Select(r => new { version = r.Version, title = r.Title, by = names.GetValueOrDefault(r.EmployeeId), at = U(r.CreatedAt), merged = r.Merged, length = r.Length }).ToList();
		}

		public async Task<object?> VersionAsync(int companyId, int convId, int docId, int version)
		{
			if (!await _db.Set<ChatDoc>().AsNoTracking().AnyAsync(x => x.ID == docId && x.CompanyId == companyId && x.ConversationId == convId && x.DeletedAt == null)) return null;
			var v = await _db.Set<ChatDocVersion>().AsNoTracking().FirstOrDefaultAsync(x => x.DocId == docId && x.Version == version);
			if (v == null) return null;
			var names = await NamesAsync(new[] { v.EmployeeId });
			return new { version = v.Version, title = v.Title, content = v.Content, by = names.GetValueOrDefault(v.EmployeeId), at = U(v.CreatedAt), merged = v.Merged };
		}

		// ---------------- save: as it is, or merged with what others saved since the editor's version ----------------
		public async Task<SaveResult> SaveAsync(int companyId, int convId, int meId, int docId, int baseVersion, string? title, string? content)
		{
			var mine = Norm(content);
			if (mine.Length > MaxChars) return new SaveResult(false, "too_long", 0, null, false, false);
			for (var attempt = 0; attempt < 3; attempt++)
			{
				var d = await _db.Set<ChatDoc>().FirstOrDefaultAsync(x => x.ID == docId && x.CompanyId == companyId && x.ConversationId == convId && x.DeletedAt == null);
				if (d == null) return new SaveResult(false, "not_found", 0, null, false, false);
				var newTitle = CleanTitle(title); if (newTitle.Length == 0) newTitle = d.Title;
				string result; bool merged = false, conflict = false;
				if (baseVersion == d.Version) result = mine;
				else
				{
					var b = await _db.Set<ChatDocVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.DocId == docId && v.Version == baseVersion);
					if (b == null || baseVersion > d.Version) return new SaveResult(false, "bad_version", d.Version, d.Content, false, false);
					var names = await NamesAsync(new[] { meId, d.UpdatedByEmployeeId });
					(result, conflict) = Merge3(b.Content, mine, d.Content, names.GetValueOrDefault(meId) ?? "-", names.GetValueOrDefault(d.UpdatedByEmployeeId) ?? "-");
					merged = true;
					// a title both renamed: the later one stands unless this editor did not touch it
					if (newTitle == b.Title) newTitle = d.Title;
				}
				if (result.Length > MaxChars) return new SaveResult(false, "too_long", d.Version, d.Content, false, false);
				if (result == d.Content && newTitle == d.Title) return new SaveResult(true, null, d.Version, merged ? d.Content : null, merged, false);
				var now = DateTime.UtcNow;
				d.Content = result; d.Title = newTitle; d.Version += 1; d.UpdatedByEmployeeId = meId; d.UpdatedAt = now;
				_db.Set<ChatDocVersion>().Add(new ChatDocVersion { DocId = d.ID, Version = d.Version, Content = result, Title = newTitle, EmployeeId = meId, CreatedAt = now, Merged = merged });
				try { await _db.SaveChangesAsync(); }
				catch (DbUpdateException)
				{
					// another save took this version number in the meantime (the version table's key): read again and merge again
					_db.ChangeTracker.Clear();
					continue;
				}
				await WriteFileAsync(d, meId, register: false);
				try { await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("doc", new { conversationId = convId, id = d.ID, version = d.Version, title = d.Title, by = meId }); } catch { }
				return new SaveResult(true, null, d.Version, merged ? result : null, merged, conflict);
			}
			return new SaveResult(false, "busy", 0, null, false, false);
		}

		/// Puts an earlier version back, as a new version on top of the current one.
		public async Task<SaveResult> RestoreAsync(int companyId, int convId, int meId, int docId, int version)
		{
			var d = await _db.Set<ChatDoc>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == docId && x.CompanyId == companyId && x.ConversationId == convId && x.DeletedAt == null);
			if (d == null) return new SaveResult(false, "not_found", 0, null, false, false);
			var v = await _db.Set<ChatDocVersion>().AsNoTracking().FirstOrDefaultAsync(x => x.DocId == docId && x.Version == version);
			if (v == null) return new SaveResult(false, "bad_version", d.Version, null, false, false);
			return await SaveAsync(companyId, convId, meId, docId, d.Version, v.Title, v.Content);
		}

		// ---------------- the File Manager's copy: one .md file, rewritten on every save ----------------
		private async Task WriteFileAsync(ChatDoc d, int meId, bool register)
		{
			try
			{
				var dir = Path.Combine(_env.WebRootPath, "uploads", "library", d.CompanyId.ToString(), "chat", "docs");
				Directory.CreateDirectory(dir);
				var bytes = new UTF8Encoding(false).GetBytes(d.Content.Replace("\n", "\r\n"));
				await File.WriteAllBytesAsync(Path.Combine(dir, d.ID + ".md"), bytes);
				if (register)
				{
					var path = "/uploads/library/" + d.CompanyId + "/chat/docs/" + d.ID + ".md";
					d.FilePath = path;
					if (_services.GetService(typeof(IFileManagerService)) is IFileManagerService files)
						await files.AddFileAsync(d.CompanyId, meId, null, SafeName(d.Title) + ".md", null, path, "text/markdown", bytes.Length);
				}
			}
			catch { /* the document lives in the database; the file is its copy for the File Manager */ }
		}
		private static string SafeName(string t)
		{
			var s = new string(t.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray()).Trim();
			return s.Length == 0 ? "document" : s.Length > 100 ? s[..100] : s;
		}

		// ------------------------------------------------------------------------------------------
		// THE THREE-WAY MERGE (lines). The common version is matched to each side by a longest common subsequence;
		// lines matched on BOTH sides are fixed points, and between two fixed points each side either left the common
		// lines as they were or changed them:
		//   only one side changed      → that side's lines
		//   both changed the same way  → those lines once
		//   both changed differently   → both, each under its writer's name (a conflict: nothing is lost)
		// ------------------------------------------------------------------------------------------
		public static (string text, bool conflict) Merge3(string baseText, string mine, string theirs, string myName, string theirName)
		{
			var o = Lines(baseText); var a = Lines(mine); var b = Lines(theirs);
			var ma = Match(o, a); var mb = Match(o, b);
			var outp = new List<string>(); var conflict = false;
			int i = 0, ia = 0, ib = 0;
			while (true)
			{
				int s = i;
				while (s < o.Length && (ma[s] < 0 || mb[s] < 0)) s++;
				int ea = s < o.Length ? ma[s] : a.Length, eb = s < o.Length ? mb[s] : b.Length;
				var co = o[i..s]; var ca = a[ia..ea]; var cb = b[ib..eb];
				if (co.Length + ca.Length + cb.Length > 0)
				{
					if (Same(ca, co)) outp.AddRange(cb);
					else if (Same(cb, co)) outp.AddRange(ca);
					else if (Same(ca, cb)) outp.AddRange(ca);
					else if (ca.Length == co.Length && cb.Length == co.Length)
					{
						// both rewrote the same span line for line: settle it line by line, so a line only one of them
						// changed is not dragged into the other's conflict; neighbouring conflicting lines stay one block
						for (int q = 0; q < co.Length;)
						{
							if (ca[q] == co[q]) { outp.Add(cb[q]); q++; continue; }
							if (cb[q] == co[q] || ca[q] == cb[q]) { outp.Add(ca[q]); q++; continue; }
							int e = q; while (e < co.Length && ca[e] != co[e] && cb[e] != co[e] && ca[e] != cb[e]) e++;
							conflict = true;
							outp.Add("<<<<<<< " + myName); outp.AddRange(ca[q..e]);
							outp.Add("======="); outp.AddRange(cb[q..e]);
							outp.Add(">>>>>>> " + theirName);
							q = e;
						}
					}
					else
					{
						conflict = true;
						outp.Add("<<<<<<< " + myName); outp.AddRange(ca);
						outp.Add("======="); outp.AddRange(cb);
						outp.Add(">>>>>>> " + theirName);
					}
				}
				if (s >= o.Length) break;
				outp.Add(o[s]);
				i = s + 1; ia = ea + 1; ib = eb + 1;
			}
			return (string.Join("\n", outp), conflict);
		}
		private static string[] Lines(string s) => s.Length == 0 ? Array.Empty<string>() : s.Split('\n');
		private static bool Same(string[] x, string[] y) => x.Length == y.Length && x.AsSpan().SequenceEqual(y);

		/// For each line of the common version, the index of the line it is matched to on one side (or −1). The common
		/// head and tail are matched first, so the quadratic table covers only the changed middle; a middle too large
		/// for it is left unmatched (that whole span then merges as one block).
		private static int[] Match(string[] o, string[] x)
		{
			var m = new int[o.Length]; Array.Fill(m, -1);
			int h = 0; while (h < o.Length && h < x.Length && o[h] == x[h]) { m[h] = h; h++; }
			int t = 0; while (t < o.Length - h && t < x.Length - h && o[o.Length - 1 - t] == x[x.Length - 1 - t]) { m[o.Length - 1 - t] = x.Length - 1 - t; t++; }
			int n = o.Length - h - t, k = x.Length - h - t;
			if (n == 0 || k == 0 || (long)n * k > 4_000_000) return m;
			var L = new int[n + 1, k + 1];
			for (int p = n - 1; p >= 0; p--)
				for (int q = k - 1; q >= 0; q--)
					L[p, q] = o[h + p] == x[h + q] ? L[p + 1, q + 1] + 1 : Math.Max(L[p + 1, q], L[p, q + 1]);
			for (int p = 0, q = 0; p < n && q < k;)
			{
				if (o[h + p] == x[h + q]) { m[h + p] = h + q; p++; q++; }
				else if (L[p + 1, q] >= L[p, q + 1]) p++;
				else q++;
			}
			return m;
		}
	}
}
