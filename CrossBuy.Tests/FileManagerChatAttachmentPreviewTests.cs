using System.IO;
using System.Linq;
using Xunit;

namespace CrossBuy.Tests
{
    /// <summary>
    /// Keeps the File Manager's chat attachment action distinct from download: Open must show
    /// the existing preview modal, and download remains an explicit user choice.
    /// </summary>
    public sealed class FileManagerChatAttachmentPreviewTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        [Fact]
        public void Chat_attachment_open_uses_the_preview_modal_and_keeps_download_separate()
        {
            var view = File.ReadAllText(RepoFile("CrossBuy", "Views", "FileManager", "Index.cshtml"));

            Assert.Contains("id=\"fmPreviewModal\"", view);
            Assert.Contains("id=\"fmPrevOpen\"", view);
            Assert.Contains("id=\"fmPrevDownload\"", view);
            Assert.Contains("data-act=\"view\"", view);
            Assert.Contains("openPreview({id:id,name:a.getAttribute('data-name')", view);
            Assert.Contains("preview=true", view);
            Assert.Contains("previewModal.show()", view);
            Assert.Contains("download=true", view);

            // Chat attachments must not use the library-file shortcut that opens a new tab directly.
            //
            // THE SHAPE CHANGED, THE PROPERTY DID NOT. This used to assert
            //     a.getAttribute('data-chat')==='true') openPreview
            // from when chat-ness GATED the preview - one branch previewed, another could open a tab. The
            // view now routes EVERY 'view' action through openPreview and passes chat-ness in as a property,
            // which is strictly stronger: there is no branch left that opens a tab directly. Pinning the old
            // literal made a better arrangement read as a regression.
            Assert.Contains("if (act==='view')", view);
            Assert.Contains("isChatAttachment:a.getAttribute('data-chat')==='true'", view);
        }
    }
}
