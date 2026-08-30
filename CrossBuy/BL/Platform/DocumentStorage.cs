namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // The storage seam. ONE job: stop the business layer naming physical paths.
    //
    // Today most writers call File.Create under WebRootPath and then store that web path in a column,
    // so the path is simultaneously the storage location, the business identifier and the download
    // URL. Those three things have different lifetimes and different security properties, and fusing
    // them is what made "knowing the path" equivalent to "being allowed to read the file".
    //
    // A StorageKey is opaque BY CONSTRUCTION - it is not a path, cannot be turned into one by the
    // caller, and carries no company, employee or document identity to leak. Where the bytes actually
    // live is this file's business and nobody else's.
    //
    // DELIBERATELY LOCAL. No cloud provider in this batch. The point is not where the bytes go; it is
    // that the business layer stops knowing. Swapping the implementation later is then a registration
    // change rather than an archaeology exercise across every writer.
    //
    // NOTHING IS MIGRATED ONTO THIS HERE. HR documents, recruitment files, the FileManager library,
    // chat attachments and item/brand images all keep their current storage untouched; adopting this
    // contract per family is the document platform's work, in controlled batches.
    // =============================================================================================

    /// <summary>
    /// An opaque handle to stored bytes. Capability-sensitive: treat it like a bearer token, because
    /// combined with an authorized session that is what it is. Never publish it in a business event, an
    /// audit payload or a public DTO - the CommAttachmentService precedent this follows keeps it server-side.
    /// </summary>
    public readonly record struct StorageKey
    {
        public string Value { get; }

        private StorageKey(string value) { Value = value; }

        /// <summary>
        /// Keys are generated, never accepted from a caller in raw form. The shape is fixed and
        /// path-hostile: 32 lowercase hex characters, so there is no separator, no drive qualifier and
        /// no dot-segment a key could smuggle into a filesystem call.
        /// </summary>
        public static StorageKey New() => new(Guid.NewGuid().ToString("n"));

        /// <summary>
        /// Rehydrates a key previously issued by <see cref="New"/> - from a database column, never from a
        /// request. Anything that is not the exact issued shape is refused rather than sanitised: a key
        /// that needs cleaning up is not a key, it is an attempt.
        /// </summary>
        public static bool TryParse(string? value, out StorageKey key)
        {
            key = default;
            if (value is not { Length: 32 }) return false;

            foreach (var c in value)
                if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;

            key = new StorageKey(value);
            return true;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// The minimum a document platform needs. No listing, no enumeration, no path accessor: a caller
    /// that could enumerate the store could rediscover exactly the property this seam removes.
    /// </summary>
    public interface IDocumentStorage
    {
        /// <summary>Writes the content and returns the key that names it. The suggested name is metadata
        /// for the caller to persist; it never becomes part of the key or of the physical location.</summary>
        Task<StorageKey> StoreAsync(Stream content, string? suggestedName = null,
            CancellationToken cancellationToken = default);

        /// <summary>Opens stored content, or null when the key names nothing. Null rather than an
        /// exception: a missing object and a refused one must be equally uninformative upstream.</summary>
        Task<Stream?> OpenReadAsync(StorageKey key, CancellationToken cancellationToken = default);

        Task<bool> ExistsAsync(StorageKey key, CancellationToken cancellationToken = default);

        /// <summary>Removes stored content. Returns false when the key names nothing.</summary>
        Task<bool> DeleteAsync(StorageKey key, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Local-filesystem implementation, under a root that is NOT inside wwwroot.
    ///
    /// That placement is the point rather than a detail: anything under the web root is reachable by
    /// UseStaticFiles, and the whole reason this platform exists is that a static pipeline cannot ask
    /// who is calling. A file stored here has no URL at all - the only way to read it is through code
    /// that has already taken a DocumentAccessResolver decision.
    /// </summary>
    public sealed class LocalDocumentStorage : IDocumentStorage
    {
        private readonly string _root;

        public LocalDocumentStorage(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A storage root is required.", nameof(root));
            _root = Path.GetFullPath(root);
        }

        public async Task<StorageKey> StoreAsync(Stream content, string? suggestedName = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);

            var key = StorageKey.New();
            var path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var file = File.Create(path);
            await content.CopyToAsync(file, cancellationToken);
            return key;
        }

        public Task<Stream?> OpenReadAsync(StorageKey key, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
        }

        public Task<bool> ExistsAsync(StorageKey key, CancellationToken cancellationToken = default)
            => Task.FromResult(File.Exists(PathFor(key)));

        public Task<bool> DeleteAsync(StorageKey key, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return Task.FromResult(false);
            File.Delete(path);
            return Task.FromResult(true);
        }

        /// <summary>
        /// Key to location. Fanned two levels by the key's own first characters purely so no directory
        /// grows to a size the filesystem handles badly - it encodes nothing about the document.
        ///
        /// The containment assertion is belt-and-braces: StorageKey cannot hold a separator or a dot
        /// segment, so traversal is already impossible by construction. Asserting it here as well means
        /// a future change to the key shape fails loudly instead of quietly opening a path escape.
        /// </summary>
        private string PathFor(StorageKey key)
        {
            var value = key.Value;
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("An empty storage key names nothing.", nameof(key));

            var path = Path.GetFullPath(Path.Combine(_root, value[..2], value[2..4], value));

            var boundary = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A storage key resolved outside the storage root.");

            return path;
        }
    }
}
