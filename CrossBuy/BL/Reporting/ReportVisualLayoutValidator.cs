using System.Text.RegularExpressions;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE VISUAL LAYOUT GATE.
    //
    // §18's central rule: A PERSISTED VISUAL LAYOUT MUST NEVER BYPASS CURRENT DATASET/FIELD PERMISSIONS.
    //
    // That makes a saved layout untrusted input in exactly the way a submitted draft is — more so, because it
    // arrives with the authority of "the user saved this last month" and may name a field they have since lost.
    // So every reopen and every execution runs through here, against the permitted field set resolved NOW.
    //
    // WHAT IT REFUSES, and why each one is a real attack rather than tidiness:
    //
    //   a field the caller may not see      → the whole point; a stored binding is not a grant
    //   a field the dataset does not have   → a probe for schema by watching which bindings survive
    //   an unbindable aggregate             → summing a text column is nonsense the renderer would guess at
    //   a colour that is not a hex token    → free CSS in a style attribute is script injection
    //   an asset id from another company     → cross-tenant image read
    //   any http/file/data URL as an image  → server-side fetch of an attacker-chosen URL (SSRF), and a
    //                                         filesystem path read. Images are ASSET IDS only.
    //   geometry outside the printable box  → a元素 that cannot be seen or printed, and a way to hide content
    //                                         from a reviewer while keeping it in the export
    //
    // TEXT IS NOT SANITISED HERE, DELIBERATELY. It is ENCODED at render time (the renderer emits every text
    // node through HtmlEncode). Stripping tags on the way in would leave the encoder as the only defence
    // while implying there were two, and would corrupt legitimate text containing < or &.
    // ============================================================================================
    public sealed class VisualLayoutValidation
    {
        public bool Ok => Errors.Count == 0;
        public List<string> Errors { get; } = new();

        // The layout with every rejected binding REMOVED. Used on reopen, where refusing the whole document
        // would leave its owner unable to open their own report after a permission change; the removal is
        // safe in that direction because it only ever takes access away.
        public ReportVisualLayout? Sanitised { get; set; }

        public List<string> Dropped { get; } = new();
    }

    public interface IReportVisualLayoutValidator
    {
        // STRICT: used when the caller submits a layout. Anything illegal is an error, so a mistake is
        // reported rather than silently altered.
        VisualLayoutValidation Validate(ReportVisualLayout layout, IReportDatasetDefinition dataset,
            IReadOnlySet<string> permittedFieldKeys, IReadOnlySet<int> permittedAssetIds);

        // LENIENT: used when a stored layout is reopened or executed. Illegal bindings are dropped and named
        // rather than failing the open — see VisualLayoutValidation.Sanitised.
        VisualLayoutValidation Sanitise(ReportVisualLayout layout, IReportDatasetDefinition dataset,
            IReadOnlySet<string> permittedFieldKeys, IReadOnlySet<int> permittedAssetIds);
    }

    public sealed class ReportVisualLayoutValidator : IReportVisualLayoutValidator
    {
        private static readonly Regex HexColour = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        // An approved list, because a font name reaches the renderer's CSS. "Arial'; background:url(…)" is why
        // this is a list and not a free string.
        //
        // NOW ONE LIST. It was a literal here and a second literal in Studio's own FONTS array, and they had
        // already drifted: this one named Amiri, which is not installed on the render host, and neither named
        // Segoe UI, which is. A designer that offers a face the validator rejects — or a face the machine
        // cannot draw — produces a layout that fails or looks wrong with nothing to explain it.
        public static readonly string[] ApprovedFonts = ReportTypography.DesignerFaces;

        private static bool IsNumeric(ReportFieldType type) =>
            type is ReportFieldType.Integer or ReportFieldType.Decimal or ReportFieldType.Money;

        private const double MaxDimensionMm = 2000;   // far beyond any paper; catches nonsense, not creativity

        public VisualLayoutValidation Validate(ReportVisualLayout layout, IReportDatasetDefinition dataset,
            IReadOnlySet<string> permittedFieldKeys, IReadOnlySet<int> permittedAssetIds) =>
            Run(layout, dataset, permittedFieldKeys, permittedAssetIds, strict: true);

        public VisualLayoutValidation Sanitise(ReportVisualLayout layout, IReportDatasetDefinition dataset,
            IReadOnlySet<string> permittedFieldKeys, IReadOnlySet<int> permittedAssetIds) =>
            Run(layout, dataset, permittedFieldKeys, permittedAssetIds, strict: false);

        private VisualLayoutValidation Run(ReportVisualLayout layout, IReportDatasetDefinition dataset,
            IReadOnlySet<string> permitted, IReadOnlySet<int> assets, bool strict)
        {
            var result = new VisualLayoutValidation();

            if (layout is null)
            {
                result.Errors.Add("No layout was supplied.");
                return result;
            }

            if (layout.SchemaVersion is < 1 or > ReportVisualLayout.CurrentSchemaVersion)
            {
                result.Errors.Add($"Unsupported layout schema version {layout.SchemaVersion}.");
                return result;
            }

            var fields = dataset.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
            var contentWidth = ReportPaper.ContentWidthMm(layout.Page);

            // THE PAGE FONT IS VALIDATED like an element font, and for the same reason: it reaches the
            // sheet's CSS. An unapproved name is dropped back to the platform stack rather than rejected
            // outright — a stored layout naming a face that has since left the list should still open.
            var page = layout.Page;
            if (page.FontFamily != null
                && !ApprovedFonts.Contains(page.FontFamily, StringComparer.OrdinalIgnoreCase))
            {
                Reject(result, strict, $"Font '{page.FontFamily}' is not on the approved list.");
                page = page.WithTypography(null, page.FontSizePt);
            }
            if (page.FontSizePt is { } pt && (pt < 5 || pt > 30))
            {
                Reject(result, strict, "A document font size must be between 5 and 30 points.");
                page = page.WithTypography(page.FontFamily, null);
            }

            var clean = new ReportVisualLayout
            {
                SchemaVersion = ReportVisualLayout.CurrentSchemaVersion,
                Page = page,
                GridMm = layout.GridMm is >= 1 and <= 25 ? layout.GridMm : 5,
                SnapToGrid = layout.SnapToGrid,
                Parameters = new Dictionary<string, string?>(),
            };

            // ---- parameters (§9) ---------------------------------------------------------------------
            //
            // Only keys the DATASET declares, and never a system-supplied one: CompanyId is filled from the
            // resolved BusinessContext and a stored value for it would be a tenant arriving through a saved
            // document.
            foreach (var (key, value) in layout.Parameters ?? new Dictionary<string, string?>())
            {
                var descriptor = dataset.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Key, key, StringComparison.Ordinal));

                if (descriptor is null)
                {
                    Reject(result, strict, $"Parameter '{key}' is not declared by this data set.");
                    continue;
                }
                if (descriptor.SystemSupplied)
                {
                    Reject(result, strict, $"Parameter '{key}' is supplied by the server and cannot be stored.");
                    continue;
                }
                clean.Parameters[key] = value;
            }

            // ---- bands -------------------------------------------------------------------------------
            var seenKinds = new HashSet<ReportBandKind>();
            foreach (var band in layout.Bands ?? new List<ReportBand>())
            {
                if (!seenKinds.Add(band.Kind))
                {
                    Reject(result, strict, $"Band '{band.Kind}' appears more than once.");
                    continue;
                }

                var cleanBand = new ReportBand
                {
                    Kind = band.Kind,
                    HeightMm = Math.Clamp(band.HeightMm, 0, MaxDimensionMm),
                };

                // A group band's field must be GROUPABLE and permitted — the engine's own grouping rule, not a
                // second one.
                if (band.Kind is ReportBandKind.GroupHeader or ReportBandKind.GroupFooter
                    && !string.IsNullOrWhiteSpace(band.GroupFieldKey))
                {
                    if (!permitted.Contains(band.GroupFieldKey) || !fields.TryGetValue(band.GroupFieldKey, out var gf))
                        Reject(result, strict, $"Group field '{band.GroupFieldKey}' is not available.",
                               result.Dropped, band.GroupFieldKey);
                    else if (!gf.Groupable)
                        Reject(result, strict, $"Field '{gf.TitleEn}' is not groupable.",
                               result.Dropped, band.GroupFieldKey);
                    else
                        cleanBand.GroupFieldKey = band.GroupFieldKey;
                }

                foreach (var element in band.Elements ?? new List<ReportElement>())
                {
                    var cleanElement = ValidateElement(element, band, fields, permitted, assets,
                                                      contentWidth, result, strict);
                    if (cleanElement != null) cleanBand.Elements.Add(cleanElement);
                }

                clean.Bands.Add(cleanBand);
            }

            result.Sanitised = clean;
            return result;
        }

        private ReportElement? ValidateElement(ReportElement e, ReportBand band,
            IReadOnlyDictionary<string, ReportDatasetField> fields, IReadOnlySet<string> permitted,
            IReadOnlySet<int> assets, double contentWidth, VisualLayoutValidation result, bool strict)
        {
            // Geometry first: an element the printable box cannot hold is refused before its binding matters.
            if (e.WidthMm is <= 0 or > MaxDimensionMm || e.HeightMm is < 0 or > MaxDimensionMm
                || e.XMm < -1 || e.YMm < -1 || double.IsNaN(e.XMm) || double.IsNaN(e.YMm))
            {
                Reject(result, strict, $"Element {e.Kind} has invalid geometry.");
                return null;
            }

            if (e.XMm > contentWidth + 1)
            {
                Reject(result, strict,
                    $"Element {e.Kind} starts outside the printable width ({e.XMm:0.#}mm of {contentWidth:0.#}mm).");
                return null;
            }

            var clean = new ReportElement
            {
                Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N")[..12] : e.Id,
                Kind = e.Kind,
                XMm = e.XMm, YMm = e.YMm,
                WidthMm = Math.Min(e.WidthMm, contentWidth),
                HeightMm = e.HeightMm,
                Z = e.Z,
                Style = ValidateStyle(e.Style, result, strict),
            };

            switch (e.Kind)
            {
                case ReportElementKind.Text:
                    // Kept verbatim; ENCODED at render. See the file header. BOTH languages: a twin
                    // dropped here would be silently lost on the first save after it was typed.
                    clean.Text = e.Text ?? "";
                    clean.TextEn = e.TextEn;
                    break;

                case ReportElementKind.Line:
                case ReportElementKind.Rectangle:
                    break;   // geometry and style only

                case ReportElementKind.SystemField:
                    clean.SystemField = e.SystemField;
                    break;

                case ReportElementKind.Field:
                {
                    if (!Bind(e.FieldKey, fields, permitted, result, strict, out var field)) return null;
                    clean.FieldKey = field!.Key;
                    break;
                }

                case ReportElementKind.QrCode:
                {
                    // EITHER a bound field OR typed text. A field goes through the same Bind as every
                    // other binding — a QR must not become the one element that can read a column the
                    // caller may not see — and text is kept verbatim and never reaches markup: it is
                    // encoded into an image, so there is nothing here for a script to ride on.
                    if (!string.IsNullOrWhiteSpace(e.FieldKey))
                    {
                        if (!Bind(e.FieldKey, fields, permitted, result, strict, out var qrField)) return null;
                        clean.FieldKey = qrField!.Key;
                    }
                    clean.Text = e.Text;
                    clean.TextEn = e.TextEn;
                    break;
                }

                case ReportElementKind.Summary:
                {
                    if (!Bind(e.FieldKey, fields, permitted, result, strict, out var field)) return null;

                    // The aggregate must be one the DATASET says is meaningful for that field. Summing an
                    // exchange rate is not an option the user should have to know to avoid.
                    //
                    // A field that declares NO list is unconstrained by the platform's own convention — which
                    // is right for Count and Min/Max and wrong for arithmetic: SUM(InvoiceNo) is not a number
                    // anyone can defend, and the dataset should not have to say so field by field. So an
                    // undeclared field falls back to TYPE compatibility, which is a fact about the column
                    // rather than a second opinion about the dataset.
                    if (field!.SupportedAggregates.Count > 0)
                    {
                        if (!field.SupportedAggregates.Contains(e.Aggregate))
                        {
                            Reject(result, strict,
                                $"{e.Aggregate} is not available on '{field.TitleEn}'.", result.Dropped, field.Key);
                            return null;
                        }
                    }
                    else if ((e.Aggregate == ReportAggregate.Sum || e.Aggregate == ReportAggregate.Average)
                             && !IsNumeric(field.Type))
                    {
                        Reject(result, strict,
                            $"{e.Aggregate} is not available on '{field.TitleEn}'.", result.Dropped, field.Key);
                        return null;
                    }
                    clean.FieldKey = field.Key;
                    clean.Aggregate = e.Aggregate;
                    break;
                }

                case ReportElementKind.Image:
                {
                    // AN ASSET ID, AND NOTHING ELSE. There is no URL and no path on the contract, so there is
                    // nothing for a server-side fetch or a filesystem read to act on — the class of bug §18
                    // names twice cannot be expressed here.
                    // AN UNBOUND IMAGE IS A PLACEHOLDER, not a mistake — and refusing one made the designer
                    // unusable in the ordinary order people work: you drop a logo box where you want it and
                    // pick the picture afterwards. Found by the runtime probe, which could not save a report
                    // at all after placing a logo. It renders as an empty box, which is exactly what it is.
                    //
                    // Nothing is loosened by this: the refusal that matters is the one below, for an id that
                    // is not this company's. "No id" and "somebody else's id" are different questions.
                    if (e.AssetId is null or <= 0)
                    {
                        clean.AssetId = null;
                        clean.ImageRole = e.ImageRole;
                        clean.Fit = e.Fit;
                        clean.PreserveAspect = e.PreserveAspect;
                        break;
                    }
                    if (!assets.Contains(e.AssetId.Value))
                    {
                        // Same message for "no such asset" and "another company's asset": telling them apart
                        // would confirm the existence of a foreign asset id.
                        Reject(result, strict, $"Image asset {e.AssetId} is not available.",
                               result.Dropped, $"asset:{e.AssetId}");
                        return null;
                    }
                    clean.AssetId = e.AssetId;
                    clean.ImageRole = e.ImageRole;
                    clean.Fit = e.Fit;
                    clean.PreserveAspect = e.PreserveAspect;
                    break;
                }

                case ReportElementKind.Table:
                {
                    if (band.Kind != ReportBandKind.Detail)
                    {
                        Reject(result, strict, "A table repeats dataset rows and belongs in the Detail band.");
                        return null;
                    }
                    foreach (var column in e.Columns ?? new List<ReportTableColumn>())
                    {
                        if (!Bind(column.FieldKey, fields, permitted, result, strict, out var field)) continue;

                        clean.Columns.Add(new ReportTableColumn
                        {
                            FieldKey = field!.Key,
                            HeaderText = column.HeaderText,
                            HeaderTextEn = column.HeaderTextEn,
                            WidthMm = Math.Clamp(column.WidthMm, 5, contentWidth),
                            Align = column.Align,
                            Format = column.Format,
                            Total = field.SupportedAggregates.Count == 0
                                    || field.SupportedAggregates.Contains(column.Total)
                                ? column.Total
                                : ReportAggregate.None,
                        });
                    }
                    if (clean.Columns.Count == 0)
                    {
                        Reject(result, strict, "A table needs at least one bound column.");
                        return null;
                    }
                    break;
                }

                default:
                    Reject(result, strict, $"Unknown element kind {(int)e.Kind}.");
                    return null;
            }

            return clean;
        }

        private static bool Bind(string? key, IReadOnlyDictionary<string, ReportDatasetField> fields,
            IReadOnlySet<string> permitted, VisualLayoutValidation result, bool strict,
            out ReportDatasetField? field)
        {
            field = null;
            if (string.IsNullOrWhiteSpace(key))
            {
                Reject(result, strict, "A bound element has no field.");
                return false;
            }

            // ONE MESSAGE for "no such field" and "not permitted". Distinguishing them turns a reopen into a
            // schema oracle: bind every guess, see which survive.
            if (!fields.TryGetValue(key, out var found) || !permitted.Contains(key))
            {
                Reject(result, strict, $"Field '{key}' is not available on this data set.", result.Dropped, key);
                return false;
            }

            field = found;
            return true;
        }

        private static ReportElementStyle ValidateStyle(ReportElementStyle? style,
            VisualLayoutValidation result, bool strict)
        {
            var s = style?.Clone() ?? new ReportElementStyle();

            if (s.FontFamily != null && !ApprovedFonts.Contains(s.FontFamily, StringComparer.OrdinalIgnoreCase))
            {
                Reject(result, strict, $"Font '{s.FontFamily}' is not on the approved list.");
                s.FontFamily = null;
            }

            s.FontSizePt = Math.Clamp(s.FontSizePt <= 0 ? 9 : s.FontSizePt, 4, 96);
            s.BorderWidthMm = Math.Clamp(s.BorderWidthMm, 0, 5);
            s.PaddingMm = Math.Clamp(s.PaddingMm, 0, 20);

            s.Color = Colour(s.Color, result, strict, "text colour");
            s.Background = Colour(s.Background, result, strict, "background");
            s.BorderColor = Colour(s.BorderColor, result, strict, "border colour");

            // Formats reach a ToString call, not CSS, but an unbounded string there is still a bad idea.
            if (s.NumberFormat?.Length > 16) s.NumberFormat = null;
            if (s.DateFormat?.Length > 32) s.DateFormat = null;

            return s;
        }

        private static string? Colour(string? value, VisualLayoutValidation result, bool strict, string what)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (HexColour.IsMatch(value)) return value;

            // A colour is the one style value a user picks freely, so it is the one that must be a token.
            Reject(result, strict, $"The {what} must be a #rrggbb value.");
            return null;
        }

        private static void Reject(VisualLayoutValidation result, bool strict, string message,
            List<string>? dropped = null, string? droppedItem = null)
        {
            if (strict) result.Errors.Add(message);
            else if (dropped != null && droppedItem != null) dropped.Add(droppedItem);
            else if (droppedItem != null) result.Dropped.Add(droppedItem);
        }
    }
}
