namespace CrossBuy.BL.Hr
{
	// What a person picker needs for one option: the photo's web path and the initials shown when there is no photo.
	// One place, so every <option data-photo data-initials> in the product agrees (Views/Shared/_PersonSelect.cshtml).
	public static class PersonTile
	{
		public static string? Photo(string? storedProfileImage) => CrossBuy.BL.Platform.EmployeePhotos.Web(storedProfileImage);

		public static string Initials(string? fullName, string? fullNameEn = null)
		{
			var name = string.IsNullOrWhiteSpace(fullNameEn) ? fullName : fullNameEn;
			var parts = (name ?? "?").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return parts.Length == 0 ? "?" : string.Concat(parts.Take(2).Select(w => w[0])).ToUpperInvariant();
		}
	}
}
