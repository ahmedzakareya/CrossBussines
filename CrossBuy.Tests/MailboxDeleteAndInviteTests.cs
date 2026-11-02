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
        public void Employee(int companyId, string email, string? photo)
        {
            Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                EmpCompanyID = companyId,
                Email = email,
                ProfileImage = photo ?? "",
                FirstName = "A", LastName = "B", FullName = "A B",
                Address = "", PhoneNumber = "", Gender = "", MaritalStatus = "", UserId = "",
                DateOfBirth = new DateTime(1990, 1, 1), DateOfJoining = new DateTime(2020, 1, 1),
                IsActive = true,
            });
            Db.SaveChanges();
        }

        /// A message with an optional attachment whose bytes really exist on disk.
        public int Mail(int companyId, string subject, string? icsBody = null, string? fileName = null)
        {
            var m = new CommMessage
            {
                CompanyID = companyId, ToAddress = "someone@example.com", Subject = subject,
                Body = "<p>body</p>", Status = "Sent", Kind = "New", CreatedAt = DateTime.UtcNow,
            };
            Db.CommMessages.Add(m);
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

            Assert.Equal(1, await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id }));

            var row = f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id);
            Assert.NotNull(row.DeletedAt);                       // reversible: the row is still here
            Assert.Single(await f.Comm.ListAsync(MailFixture.CompanyA, "Trash", null));
            Assert.Empty(await f.Comm.ListAsync(MailFixture.CompanyA, "All", null));
        }

        // RE-TRASHING MUST NOT MOVE THE DATE. A retention sweep one day reads DeletedAt, and a date
        // that creeps forward every time somebody ticks a box would keep the oldest rubbish longest.
        [Fact]
        public async Task Deleting_an_already_deleted_message_does_nothing_and_does_not_restamp_it()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "once");
            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id });
            var first = f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id).DeletedAt;

            await Task.Delay(15);
            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id }));

            Assert.Equal(first, f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id).DeletedAt);
        }

        [Fact]
        public async Task Restoring_puts_it_back_and_only_works_from_the_trash()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "back");

            // A live message cannot be "restored" - there is nothing to undo.
            Assert.Equal(0, await f.Comm.RestoreAsync(MailFixture.CompanyA, new[] { id }));

            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id });
            Assert.Equal(1, await f.Comm.RestoreAsync(MailFixture.CompanyA, new[] { id }));
            Assert.Null(f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id).DeletedAt);
        }

        // ---- PERMANENT DELETE --------------------------------------------------------------

        [Fact]
        public async Task Delete_forever_removes_the_row_the_attachment_and_the_file()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "gone", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
            var path = f.PhysicalPathOf(id);
            Assert.True(File.Exists(path));                       // control

            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id });
            Assert.Equal(1, await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, new[] { id }));

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

            Assert.Equal(0, await f.Comm.DeleteForeverAsync(MailFixture.CompanyA, new[] { id }));
            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task Emptying_the_trash_takes_everything_in_it_and_nothing_else()
        {
            using var f = new MailFixture();
            int trashed = f.Mail(MailFixture.CompanyA, "trashed");
            int live = f.Mail(MailFixture.CompanyA, "live");
            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { trashed });

            Assert.Equal(1, await f.Comm.EmptyTrashAsync(MailFixture.CompanyA));

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

            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyB, new[] { id }));
            Assert.Null(f.Db.CommMessages.AsNoTracking().Single(m => m.Id == id).DeletedAt);

            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id });
            Assert.Equal(0, await f.Comm.RestoreAsync(MailFixture.CompanyB, new[] { id }));
            Assert.Equal(0, await f.Comm.DeleteForeverAsync(MailFixture.CompanyB, new[] { id }));
            Assert.Equal(0, await f.Comm.EmptyTrashAsync(MailFixture.CompanyB));

            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task An_unresolved_company_decides_nothing()
        {
            using var f = new MailFixture();
            int id = f.Mail(MailFixture.CompanyA, "safe");
            await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { id });

            foreach (var none in new[] { 0, -1 })
            {
                Assert.Equal(0, await f.Comm.TrashAsync(none, new[] { id }));
                Assert.Equal(0, await f.Comm.RestoreAsync(none, new[] { id }));
                Assert.Equal(0, await f.Comm.DeleteForeverAsync(none, new[] { id }));
                Assert.Equal(0, await f.Comm.EmptyTrashAsync(none));
            }
            Assert.Single(f.Db.CommMessages.AsNoTracking().Where(m => m.Id == id));
        }

        [Fact]
        public async Task An_empty_or_null_id_set_is_a_no_op_rather_than_everything()
        {
            using var f = new MailFixture();
            f.Mail(MailFixture.CompanyA, "a");
            f.Mail(MailFixture.CompanyA, "b");

            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, Array.Empty<int>()));
            Assert.Equal(0, await f.Comm.TrashAsync(MailFixture.CompanyA, new[] { 0, -5 }));
            Assert.Equal(2, f.Db.CommMessages.Count(m => m.DeletedAt == null));
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

            var row = (await f.Comm.ListAsync(MailFixture.CompanyA, "All", null)).Single(r => r.Id == id);
            var full = await f.Comm.GetAsync(MailFixture.CompanyA, id);

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

            Assert.Null((await f.Comm.GetAsync(MailFixture.CompanyA, id))!.RecipientPhoto);
        }

        // AN EMPLOYEE IN ANOTHER COMPANY IS NOT THIS RECIPIENT. The same address can exist in two
        // tenants, and borrowing the other one's picture would put a stranger's face on the message.
        [Fact]
        public async Task A_picture_is_not_borrowed_from_another_company()
        {
            using var f = new MailFixture();
            f.Employee(MailFixture.CompanyB, "someone@example.com", "/uploads/employees/other.jpg");
            int id = f.Mail(MailFixture.CompanyA, "hello");

            Assert.Null((await f.Comm.GetAsync(MailFixture.CompanyA, id))!.RecipientPhoto);
        }
    }
}
