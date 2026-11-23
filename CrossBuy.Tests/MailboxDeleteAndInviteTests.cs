using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Calendar;
using CrossBuy.Models.Context.Comm;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE MAILBOX: DELETING, RESTORING, AND THE MEETING A MESSAGE CARRIES.
    //
    // WHY THESE EXIST, AND IT IS NOT A FORMALITY. This work shipped a permanent delete that removes
    // rows AND files from disk, and a tenant predicate that is the only thing standing between two
    // companies' mail. Both were verified by clicking through a browser, which proves the happy path
    // and nothing about the boundary. Twice during the same work a change looked right on screen and
    // was broken underneath - once a script that no longer parsed, once a column that collapsed to
    // 1px - and both were caught by eye rather than by anything that would catch them again.
    //
    // THE SCHEMA HERE COMES FROM THE EF MODEL, which is also why these can prove the invitation
    // RESPONSE: the deployed database has not had deploy/sql/calendar.sql applied yet, so the
    // response columns are absent there and present here. That gap is exactly what the graceful
    // degradation below is about, and it is asserted too.
    // =============================================================================================
    internal sealed class MailFixture : IDisposable
    {
        public const int CompanyA = 1;
        public const int CompanyB = 77;

        // A MAILBOX PER EMPLOYEE (2026-11-23). A message is seen only through a copy: the writer's in Sent, each
        // recipient's in Inbox. By default the fixture's mail is written by Owner and delivered to employees 5 and 99
        // (the invitation tests' "Me" and "Someone"), so a test chooses whose mailbox it acts in.
        public const int Owner = 1;
        public static readonly int[] DefaultInbox = { 5, 99 };

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly string _root;

        public CrossDbContext Db { get; }
        public CommService Comm { get; }
        public MailInviteResponder Invites { get; }

        public MailFixture(int companyId = CompanyA)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(companyId, null);

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;

            Db = new CrossDbContext(options, _holder);
            Db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            // A real web root, because permanent delete removes files and "the file went too" is
            // half the claim. A stub environment would make that assertion vacuous.
            _root = Path.Combine(Path.GetTempPath(), "cbmail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "uploads", "comm"));

            var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Comm = new CommService(Db, cfg, new StubWebRoot(_root),
                NullLogger<CommService>.Instance);
            Invites = new MailInviteResponder(Db, Comm, NullLogger<MailInviteResponder>.Instance);
        }

        public string WebRoot => _root;

        /// An employee the mailbox can recognise by their address. Only the three fields the photo
        /// lookup reads carry meaning; the rest are filled because the columns are not nullable.
        public int Employee(int companyId, string email, string? photo)
        {
            var e = new CrossBuy.Models.Context.Admin.Employee
            {
                EmpCompanyID = companyId,
                Email = email,
                ProfileImage = photo ?? "",
                FirstName = "A", LastName = "B", FullName = "A B",
                Address = "", PhoneNumber = "", Gender = "", MaritalStatus = "", UserId = Guid.NewGuid().ToString(),
                DateOfBirth = new DateTime(1990, 1, 1), DateOfJoining = new DateTime(2020, 1, 1),
                IsActive = true,
            };
            Db.Employee.Add(e);
            Db.SaveChanges();
            return e.ID;
        }

        public CommMailboxItem Copy(int messageId, int employeeId, string box = "Sent") =>
            Db.Set<CommMailboxItem>().AsNoTracking().Single(i => i.MessageId == messageId && i.EmployeeId == employeeId && i.Box == box);

        /// A message with an optional attachment whose bytes really exist on disk, held by Owner (Sent) and by
        /// `inbox` (default: 5 and 99) in their Inboxes.
        public int Mail(int companyId, string subject, string? icsBody = null, string? fileName = null, params int[] inbox)
        {
            var m = new CommMessage
            {
                CompanyID = companyId, ToAddress = "someone@example.com", Subject = subject,
                Body = "<p>body</p>", Status = "Sent", Kind = "New", CreatedAt = DateTime.UtcNow,
            };
            Db.CommMessages.Add(m);
            Db.SaveChanges();
            Db.Set<CommMailboxItem>().Add(new CommMailboxItem { CompanyID = companyId, MessageId = m.Id, EmployeeId = Owner, Box = "Sent", ReadAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            foreach (var e in inbox.Length > 0 ? inbox : DefaultInbox)
                Db.Set<CommMailboxItem>().Add(new CommMailboxItem { CompanyID = companyId, MessageId = m.Id, EmployeeId = e, Box = "Inbox", CreatedAt = DateTime.UtcNow });
            Db.SaveChanges();

            if (icsBody != null)
            {
                var stored = Guid.NewGuid().ToString("N") + ".ics";
                File.WriteAllText(Path.Combine(_root, "uploads", "comm", stored), icsBody);
                Db.CommAttachments.Add(new CommAttachment
                {
                    CommMessageId = m.Id,
                    FilePath = "/uploads/comm/" + stored,
                    FileName = fileName ?? "invite.ics",
                    Size = icsBody.Length,
                    CreatedAt = DateTime.UtcNow,
                });
                Db.SaveChanges();
            }
            return m.Id;
        }

        public string PhysicalPathOf(int messageId)
        {
            var a = Db.CommAttachments.AsNoTracking().First(x => x.CommMessageId == messageId);
            return Path.Combine(_root, a.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        }

        public void Dispose()
        {
            Db.Dispose();
            _conn.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private sealed class StubWebRoot : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
        {
            public StubWebRoot(string root)
            {
                WebRootPath = root; ContentRootPath = root;
                EnvironmentName = "Test"; ApplicationName = "CrossBuy.Tests";
            }
            public string WebRootPath { get; set; }
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
            public string ApplicationName { get; set; }
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
            public string ContentRootPath { get; set; }
            public string EnvironmentName { get; set; }
        }
    }

    public class MailboxDeleteTests
    {
        // ---- MOVE TO DELETED ITEMS ---------------------------------------------------------

        [Fact]
        public async Task Deleting_moves_to_the_trash_and_keeps_the_row()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "keep me");

            Assert.Equal(1, await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));

            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));   // reversible: the row is still here
            Assert.NotNull(f.Copy(id, MailFixture.Owner).DeletedAt);                       // only MY copy is in the trash
            Assert.Null(f.Copy(id, 5, "Inbox").DeletedAt);                                  // the recipient's is not
            Assert.Single(await f.Comm.ListAsync(MailFixture.CompanyA, MailFixture.Owner, "Trash", null));
            Assert.Empty(await f.Comm.ListAsync(MailFixture.CompanyA, MailFixture.Owner, "All", null));
        }

        // RE-TRASHING MUST NOT MOVE THE DATE. A retention sweep one day reads DeletedAt, and a date
        // that creeps forward every time somebody ticks a box would keep the oldest rubbish longest.
        [Fact]
        public async Task Deleting_an_already_deleted_message_does_nothing_and_does_not_restamp_it()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "once");
            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });
            var first = f.Copy(id, MailFixture.Owner).DeletedAt;

            await Task.Delay(15);
            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));

            Assert.Equal(first, f.Copy(id, MailFixture.Owner).DeletedAt);
        }

        [Fact]
        public async Task Restoring_puts_it_back_and_only_works_from_the_trash()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "back");

            // A live message cannot be "restored" - there is nothing to undo.
            Assert.Equal(0, await f.Comm.RestoreAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));

            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });
            Assert.Equal(1, await f.Comm.RestoreAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));
            Assert.Null(f.Copy(id, MailFixture.Owner).DeletedAt);
        }

        // ---- PERMANENT DELETE --------------------------------------------------------------

        [Fact]
        public async Task Delete_forever_removes_the_row_the_attachment_and_the_file()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "gone", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", null, MailFixture.Owner);
            var path = f.PhysicalPathOf(id);
            Assert.True(File.Exists(path));                       // control

            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });
            Assert.Equal(1, await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));

            Assert.Empty(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
            Assert.Empty(f.Db.CommAttachments.AsNoTracking().Where(a => a.CommMessageId == id));
            Assert.False(File.Exists(path));                      // the bytes went too
        }

        // ONLY FROM THE TRASH, and the predicate enforces it rather than the caller promising it: a
        // mis-posted folder parameter must not be able to destroy live mail.
        [Fact]
        public async Task Delete_forever_refuses_a_message_that_is_not_in_the_trash()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "alive");

            Assert.Equal(0, await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id }));
            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task Emptying_the_trash_takes_everything_in_it_and_nothing_else()
        {
            using var f = new MailFixture();
            int trashed = f.Mail(MailFixture.CompanyA, "trashed", null, null, MailFixture.Owner);
            int live = f.Mail(MailFixture.CompanyA, "live", null, null, MailFixture.Owner);
            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { trashed });

            Assert.Equal(1, await f.Comm.EmptyTrashAsync(MailFixture.CompanyA, MailFixture.Owner));

            Assert.Empty(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == trashed));
            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == live));
        }

        // ---- THE TENANT BOUNDARY -----------------------------------------------------------
        //
        // An id is a lookup key and nothing more. These are the assertions that matter most: every
        // one of them is the difference between a mailbox and a leak.

        [Fact]
        public async Task Another_company_cannot_trash_restore_or_destroy_this_companys_mail()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "mine");

            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyB, MailFixture.Owner, new[] { id }));
            Assert.Null(f.Copy(id, MailFixture.Owner).DeletedAt);

            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });
            Assert.Equal(0, await f.Comm.RestoreAsync(MailFixture.CompanyB, MailFixture.Owner, new[] { id }));
            Assert.Equal(0, await f.Comm.DeleteForeverAsync(MailFixture.CompanyB, MailFixture.Owner, new[] { id }));
            Assert.Equal(0, await f.Comm.EmptyTrashAsync(MailFixture.CompanyB, MailFixture.Owner));

            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task An_unresolved_company_decides_nothing()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "safe");
            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });

            foreach (var none in new[] { 0, -1 })
            {
                Assert.Equal(0, await f.Comm.TrashAsync(none, MailFixture.Owner, new[] { id }));
                Assert.Equal(0, await f.Comm.RestoreAsync(none, MailFixture.Owner, new[] { id }));
                Assert.Equal(0, await f.Comm.DeleteForeverAsync(none, MailFixture.Owner, new[] { id }));
                Assert.Equal(0, await f.Comm.EmptyTrashAsync(none, MailFixture.Owner));
            }
            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task An_empty_or_null_id_set_is_a_no_op_rather_than_everything()
        {
            using var f = new MailFixture();
            f.Mail(MailFixture.CompanyA, "a");
            f.Mail(MailFixture.CompanyA, "b");

            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, Array.Empty<int>()));
            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { 0, -5 }));
            Assert.Equal(0, f.Db.Set<CommMailboxItem>().Count(i => i.DeletedAt != null));
        }

        // ---- A MAILBOX PER EMPLOYEE (owner, 2026-11-23) -----------------------------------
        //
        // Until this change the mailbox was the company's: everybody saw, starred and deleted everybody's mail.

        [Fact]
        public async Task A_colleague_neither_sees_nor_touches_mail_that_is_not_theirs()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "between Owner and 5", null, null, 5);
            const int colleague = 42;

            Assert.Null(await f.Comm.GetAsync(MailFixture.CompanyA, colleague, id));
            Assert.Empty(await f.Comm.ListAsync(MailFixture.CompanyA, colleague, "All", null));
            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, colleague, new[] { id }));
            Assert.False(await f.Comm.ToggleStarAsync(MailFixture.CompanyA, colleague, id));
            // ...while the company outbox's manager still finds it, and only there.
            Assert.NotNull(await f.Comm.GetAsync(MailFixture.CompanyA, colleague, id, manager: true));
            Assert.Single(await f.Comm.ListAsync(MailFixture.CompanyA, colleague, "Company", null, manager: true));
            Assert.Empty(await f.Comm.ListAsync(MailFixture.CompanyA, colleague, "Company", null, manager: false));
        }

        [Fact]
        public async Task A_recipient_deleting_forever_leaves_the_senders_copy_and_the_message()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "shared", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", null, 5);
            var path = f.PhysicalPathOf(id);

            await f.Comm.TrashAsync(MailFixture.CompanyA, 5, new[] { id });
            Assert.Equal(1, await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, 5, new[] { id }));

            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
            Assert.Null(f.Copy(id, MailFixture.Owner).DeletedAt);
            Assert.True(File.Exists(path));                       // the file stays while anyone holds the message
            Assert.Empty(f.Db.Set<CommMailboxItem>().AsNoTracking().Where(i => i.MessageId == id && i.EmployeeId == 5));
        }

        // A FILE PICKED FROM THE LIBRARY IS THE LIBRARY'S. Purging the last copy of a message must not delete it.
        [Fact]
        public async Task Purging_a_message_never_deletes_a_library_file()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "with a library file", null, null, MailFixture.Owner);
            var lib = Path.Combine(f.WebRoot, "uploads", "library", "1");
            Directory.CreateDirectory(lib);
            File.WriteAllText(Path.Combine(lib, "plan.pdf"), "pdf");
            f.Db.CommAttachments.Add(new CommAttachment { CommMessageId = id, FilePath = "/uploads/library/1/plan.pdf", FileName = "plan.pdf", Size = 3, CreatedAt = DateTime.UtcNow });
            f.Db.SaveChanges();

            await f.Comm.TrashAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });
            await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, MailFixture.Owner, new[] { id });

            Assert.Empty(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
            Assert.True(File.Exists(Path.Combine(lib, "plan.pdf")));
        }

        // A COLLEAGUE'S ADDRESS IS A MAILBOX HERE: delivered at once, never handed to SMTP, unread until opened.
        [Fact]
        public async Task Mail_to_a_colleague_is_delivered_inside_and_read_when_opened()
        {
            using var f = new MailFixture();
            int sender = f.Employee(MailFixture.CompanyA, "sara@company.test", null);
            int colleague = f.Employee(MailFixture.CompanyA, "Khaled@Company.test", null);

            var (id, status, error) = await f.Comm.QueueAsync(MailFixture.CompanyA, sender, "khaled@company.test", null, "hello", "<p>hi</p>", null, null, "New");

            Assert.Equal("Sent", status); Assert.Null(error);
            Assert.Equal(0, f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id).Attempts);   // no transmission
            var inbox = await f.Comm.ListAsync(MailFixture.CompanyA, colleague, "Inbox", null);
            Assert.True(Assert.Single(inbox).Unread);
            Assert.Single(await f.Comm.ListAsync(MailFixture.CompanyA, sender, "Sent", null));
            Assert.Equal(1, (await f.Comm.CountsAsync(MailFixture.CompanyA, colleague)).Unread);

            await f.Comm.GetAsync(MailFixture.CompanyA, colleague, id, markRead: true);
            Assert.Equal(0, (await f.Comm.CountsAsync(MailFixture.CompanyA, colleague)).Unread);
        }

        // A REPLY JOINS ITS PARENT'S CONVERSATION.
        [Fact]
        public async Task A_reply_carries_its_parents_thread()
        {
            using var f = new MailFixture();
            int sender = f.Employee(MailFixture.CompanyA, "sara@company.test", null);
            var first = await f.Comm.QueueAsync(MailFixture.CompanyA, sender, "out@example.com", null, "q", "b", null, null, "New");
            var reply = await f.Comm.QueueAsync(MailFixture.CompanyA, sender, "out@example.com", null, "Re: q", "b", null, first.id, "Reply");

            Assert.Equal(first.id, f.Db.CommMessages.AsNoTracking().Single(m => m.Id == first.id).ThreadId);
            Assert.Equal(first.id, f.Db.CommMessages.AsNoTracking().Single(m => m.Id == reply.id).ThreadId);
        }

        // ---- THE FACE ON THE OPENED MESSAGE ------------------------------------------------
        //
        // The list resolved a recipient's photo and the detail screen did not, so the cramped row
        // showed the person while the screen with room for them showed a coloured letter. These pin
        // the two together: whatever the row resolves, the message resolves identically.

        [Fact]
        public async Task An_opened_message_shows_the_same_picture_the_list_does()
        {
            using var f = new MailFixture();
            f.Employee(MailFixture.CompanyA, "someone@example.com", "/uploads/employees/pic.jpg");
            int id = f.Mail(MailFixture.CompanyA, "hello");

            var row = (await f.Comm.ListAsync(MailFixture.CompanyA, MailFixture.Owner, "All", null)).Single(r => r.Id == id);
            var full = await f.Comm.GetAsync(MailFixture.CompanyA, MailFixture.Owner, id);

            Assert.Equal("/uploads/employees/pic.jpg", full!.RecipientPhoto);
            Assert.Equal(row.RecipientPhoto, full.RecipientPhoto);
        }

        // NULL IS THE ORDINARY ANSWER, not a failure: most mail goes to customers and vendors who
        // have no employee record, and the screen draws the initial for them.
        [Fact]
        public async Task An_address_with_nobody_on_file_has_no_picture()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "hello");

            Assert.Null((await f.Comm.GetAsync(MailFixture.CompanyA, MailFixture.Owner, id))!.RecipientPhoto);
        }

        // AN EMPLOYEE IN ANOTHER COMPANY IS NOT THIS RECIPIENT. The same address can exist in two
        // tenants, and borrowing the other one's picture would put a stranger's face on the message.
        [Fact]
        public async Task A_picture_is_not_borrowed_from_another_company()
        {
            using var f = new MailFixture();
            f.Employee(MailFixture.CompanyB, "someone@example.com", "/uploads/employees/other.jpg");
            int id = f.Mail(MailFixture.CompanyA, "hello");

            Assert.Null((await f.Comm.GetAsync(MailFixture.CompanyA, MailFixture.Owner, id))!.RecipientPhoto);
        }
    }
}
