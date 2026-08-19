using System.Globalization;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE PARAMETER ENGINE.
    //
    // The ONE place raw text becomes a typed, validated parameter value. Parameters reach the platform as text
    // from three unrelated places — a query string, a saved template layout, a schedule's frozen JSON — and all
    // three go through this binder. That is what makes "the report I scheduled" and "the report I ran by hand"
    // provably the same question.
    //
    // It is also the isolation boundary for parameters: a caller CANNOT supply CompanyId, BranchId or EmployeeId.
    // Those are declared SystemSupplied and are filled from the resolved BusinessContext, and a supplied value
    // for one is discarded with a warning rather than honoured. CLAUDE.md records why: "a request-supplied
    // companyId is compatibility-only: validate it against the resolved BusinessContext and reject a mismatch —
    // never coerce it." Here it is not coerced and not accepted; it is dropped and reported.
    // ============================================================================================

    // Well-known parameter keys the engine fills. A definition opts in by declaring a parameter with one of these
    // keys and SystemSupplied = true; the engine then guarantees a value.
    public static class ReportSystemParameters
    {
        public const string CompanyId = "CompanyId";
        public const string BranchId = "BranchId";
        public const string EmployeeId = "EmployeeId";
        public const string UserId = "UserId";
        public const string Culture = "Culture";

        // The moment the report ran, from IReportClock. A report that needs "now" must take it from here rather
        // than calling DateTime.Now inside a data source — otherwise an archived artifact cannot be reproduced.
        public const string Now = "Now";

        public static readonly IReadOnlyList<string> All = new[]
        {
            CompanyId, BranchId, EmployeeId, UserId, Culture, Now,
        };

        public static bool IsSystemKey(string? key) =>
            key != null && All.Contains(key, StringComparer.Ordinal);
    }

    // One bound value.
    public sealed class ReportParameterValue
    {
        public required string Key { get; init; }
        public ReportFieldType Type { get; init; }

        // Scalar value (null when unset, or when this is a multi-value parameter).
        public object? Value { get; init; }

        // Values for AllowMultiple parameters. Empty for a scalar.
        public IReadOnlyList<object?> Values { get; init; } = Array.Empty<object?>();

        // Exactly what was supplied, before conversion. Kept for the history row and the parameter hash, so a
        // run records the question as it was asked, not as it was interpreted.
        public string? RawText { get; init; }

        public bool IsMultiple { get; init; }
        public bool HasValue => IsMultiple ? Values.Count > 0 : Value != null;

        // true = the engine filled it, not the caller.
        public bool SystemSupplied { get; init; }
    }

    // The bound parameter set handed to a data source. Read-only by construction: once binding is done, nothing
    // downstream may add a parameter — a data source that could inject a parameter could bypass validation.
    public sealed class ReportParameterSet
    {
        private readonly Dictionary<string, ReportParameterValue> _values;

        public ReportParameterSet(IEnumerable<ReportParameterValue> values,
            IReadOnlyDictionary<string, string?> rawText)
        {
            _values = values.ToDictionary(v => v.Key, StringComparer.Ordinal);
            RawText = rawText;
        }

        // The supplied text, canonicalised (system-supplied keys excluded — a hash that included CompanyId would
        // differ per tenant for what is the same question, defeating the "same question" comparison).
        public IReadOnlyDictionary<string, string?> RawText { get; }

        public IReadOnlyCollection<ReportParameterValue> Values => _values.Values;

        public bool Has(string key) => _values.TryGetValue(key, out var v) && v.HasValue;

        public object? Get(string key) => _values.TryGetValue(key, out var v) ? v.Value : null;

        public IReadOnlyList<object?> GetList(string key) =>
            _values.TryGetValue(key, out var v) ? v.Values : Array.Empty<object?>();

        public string? GetString(string key) => Get(key) as string;

        public int? GetInt(string key) => Get(key) switch
        {
            null => null,
            int i => i,
            var o => (int)ReportValues.AsDecimal(o),
        };

        public decimal? GetDecimal(string key) => Get(key) is { } o ? ReportValues.AsDecimal(o) : null;

        public DateTime? GetDate(string key) => Get(key) is { } o ? ReportValues.AsDateTime(o) : null;

        public bool GetBool(string key) => ReportValues.AsBool(Get(key));

        public IReadOnlyList<int> GetIntList(string key) =>
            GetList(key).Where(v => v != null).Select(v => (int)ReportValues.AsDecimal(v)).ToList();

        // The company this run is scoped to. Present on every set — the binder guarantees it whether or not the
        // definition declares the parameter, precisely so a data source can never be handed a set without one.
        public int CompanyId => GetInt(ReportSystemParameters.CompanyId) ?? 0;

        public int? EmployeeId => GetInt(ReportSystemParameters.EmployeeId);
        public int? BranchId => GetInt(ReportSystemParameters.BranchId);
        public DateTime Now => GetDate(ReportSystemParameters.Now) ?? DateTime.MinValue;

        public static ReportParameterSet Empty { get; } =
            new(Array.Empty<ReportParameterValue>(), new Dictionary<string, string?>());
    }

    public sealed class ReportParameterBindResult
    {
        public ReportParameterSet? Parameters { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public bool IsValid => Parameters != null &&
            !Diagnostics.Any(d => d.Severity == ReportDiagnosticSeverity.Error);
    }

    public interface IReportParameterBinder
    {
        // Binds and validates in one pass. Never throws for bad input — invalid values come back as Error
        // diagnostics so the engine can return a Failed result and the controller can show a validation message.
        ReportParameterBindResult Bind(ReportDefinition definition,
            IReadOnlyDictionary<string, string?> supplied,
            BusinessContext context,
            CultureInfo culture);
    }

    public class ReportParameterBinder : IReportParameterBinder
    {
        // Diagnostic codes. Stable machine strings — a caller may branch on them, so they are part of the
        // contract and are never reworded.
        public const string CodeRequiredMissing = "parameter_required";
        public const string CodeInvalidValue = "parameter_invalid";
        public const string CodeNotInOptions = "parameter_not_allowed";
        public const string CodeOutOfRange = "parameter_out_of_range";
        public const string CodeUnknownParameter = "parameter_unknown";
        public const string CodeSystemSuppliedIgnored = "parameter_system_supplied";
        public const string CodeMultipleNotAllowed = "parameter_single_valued";

        private readonly IReportClock _clock;

        public ReportParameterBinder(IReportClock clock) { _clock = clock; }

        public ReportParameterBindResult Bind(ReportDefinition definition,
            IReadOnlyDictionary<string, string?> supplied,
            BusinessContext context,
            CultureInfo culture)
        {
            var diagnostics = new List<ReportDiagnostic>();
            var bound = new List<ReportParameterValue>();
            var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
            var now = _clock.LocalNow;

            supplied ??= new Dictionary<string, string?>();

            // ---- 1. Anything supplied that the definition does not declare -------------------------------
            //
            // Dropped with a warning, not silently. An unknown parameter is usually a renamed key or a stale
            // bookmark, and a report that quietly ignores it produces a plausible answer to a different
            // question. Warning rather than Error because an extra query-string value (a cache-buster, a
            // navigation token) must not break a report.
            foreach (var key in supplied.Keys)
            {
                if (definition.FindParameter(key) == null)
                    diagnostics.Add(ReportDiagnostic.Warning(CodeUnknownParameter,
                        $"Parameter '{key}' is not declared by report '{definition.Code}' and was ignored.", key));
            }

            // ---- 2. Each declared parameter ---------------------------------------------------------------
            foreach (var descriptor in definition.Parameters)
            {
                if (descriptor.SystemSupplied)
                {
                    // A supplied value for a system parameter is REFUSED, not validated and not coerced. This is
                    // the parameter-level expression of the isolation rule.
                    if (supplied.ContainsKey(descriptor.Key))
                        diagnostics.Add(ReportDiagnostic.Warning(CodeSystemSuppliedIgnored,
                            $"Parameter '{descriptor.Key}' is supplied by the platform and any caller-provided " +
                            "value is ignored.", descriptor.Key));

                    bound.Add(BindSystemParameter(descriptor, context, culture, now));
                    continue;
                }

                var hasSupplied = supplied.TryGetValue(descriptor.Key, out var text);
                var effective = hasSupplied && !string.IsNullOrWhiteSpace(text) ? text : descriptor.DefaultValue;

                if (string.IsNullOrWhiteSpace(effective))
                {
                    if (descriptor.Required)
                    {
                        diagnostics.Add(ReportDiagnostic.Error(CodeRequiredMissing,
                            $"Parameter '{descriptor.Key}' is required and no value was supplied.", descriptor.Key));
                        continue;
                    }

                    bound.Add(new ReportParameterValue
                    {
                        Key = descriptor.Key,
                        Type = descriptor.Type,
                        IsMultiple = descriptor.AllowMultiple,
                        RawText = null,
                    });
                    continue;
                }

                raw[descriptor.Key] = effective;

                if (descriptor.AllowMultiple)
                {
                    var parts = effective.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                     StringSplitOptions.TrimEntries);
                    var values = new List<object?>();
                    var ok = true;

                    foreach (var part in parts)
                    {
                        if (!ReportValues.TryConvert(part, descriptor.Type, culture, now, out var v))
                        {
                            diagnostics.Add(ReportDiagnostic.Error(CodeInvalidValue,
                                $"Value '{part}' is not a valid {descriptor.Type} for parameter " +
                                $"'{descriptor.Key}'.", descriptor.Key));
                            ok = false;
                            continue;
                        }
                        if (!CheckOptions(descriptor, part, diagnostics)) { ok = false; continue; }
                        if (!CheckRange(descriptor, v, culture, now, diagnostics)) { ok = false; continue; }
                        values.Add(v);
                    }

                    if (!ok) continue;

                    if (descriptor.Required && values.Count == 0)
                    {
                        diagnostics.Add(ReportDiagnostic.Error(CodeRequiredMissing,
                            $"Parameter '{descriptor.Key}' is required and resolved to an empty list.",
                            descriptor.Key));
                        continue;
                    }

                    bound.Add(new ReportParameterValue
                    {
                        Key = descriptor.Key,
                        Type = descriptor.Type,
                        Values = values,
                        IsMultiple = true,
                        RawText = effective,
                    });
                    continue;
                }

                // Scalar. A comma in a single-valued parameter is an error rather than "take the first" — taking
                // the first would answer a narrower question than the user asked, without saying so.
                if (effective.Contains(','))
                {
                    diagnostics.Add(ReportDiagnostic.Error(CodeMultipleNotAllowed,
                        $"Parameter '{descriptor.Key}' accepts a single value but a list was supplied.",
                        descriptor.Key));
                    continue;
                }

                if (!ReportValues.TryConvert(effective, descriptor.Type, culture, now, out var value))
                {
                    diagnostics.Add(ReportDiagnostic.Error(CodeInvalidValue,
                        $"Value '{effective}' is not a valid {descriptor.Type} for parameter " +
                        $"'{descriptor.Key}'.", descriptor.Key));
                    continue;
                }

                if (!CheckOptions(descriptor, effective, diagnostics)) continue;
                if (!CheckRange(descriptor, value, culture, now, diagnostics)) continue;

                bound.Add(new ReportParameterValue
                {
                    Key = descriptor.Key,
                    Type = descriptor.Type,
                    Value = value,
                    RawText = effective,
                });
            }

            // ---- 3. The isolation guarantee -------------------------------------------------------------
            //
            // CompanyId, EmployeeId and Now are added whether or not the definition declares them, so no data
            // source can ever receive a parameter set without a company. A source that forgets to filter is a bug
            // in that source; a source that COULD NOT KNOW the company would be a platform bug.
            EnsureSystemParameter(bound, ReportSystemParameters.CompanyId, ReportFieldType.Integer, context.CompanyId);
            EnsureSystemParameter(bound, ReportSystemParameters.EmployeeId, ReportFieldType.Integer, context.EmployeeId);
            EnsureSystemParameter(bound, ReportSystemParameters.BranchId, ReportFieldType.Integer, context.BranchId);
            EnsureSystemParameter(bound, ReportSystemParameters.Now, ReportFieldType.DateTime, now);
            EnsureSystemParameter(bound, ReportSystemParameters.Culture, ReportFieldType.String, culture.Name);

            var set = new ReportParameterSet(bound, raw);
            return new ReportParameterBindResult
            {
                // A parameter set is still returned alongside errors: the engine records the attempted parameters
                // on the failed run, which is what makes a failed run diagnosable.
                Parameters = set,
                Diagnostics = diagnostics,
            };
        }

        private static ReportParameterValue BindSystemParameter(ReportParameterDescriptor descriptor,
            BusinessContext context, CultureInfo culture, DateTime now)
        {
            object? value = descriptor.Key switch
            {
                ReportSystemParameters.CompanyId => context.CompanyId,
                ReportSystemParameters.BranchId => context.BranchId,
                ReportSystemParameters.EmployeeId => context.EmployeeId,
                ReportSystemParameters.UserId => context.UserId,
                ReportSystemParameters.Culture => culture.Name,
                ReportSystemParameters.Now => now,

                // A definition may mark its OWN parameter SystemSupplied without using a well-known key. It then
                // gets its declared default and nothing else — which is the only safe reading of "the platform
                // supplies this" for a key the platform does not know.
                _ => descriptor.DefaultValue,
            };

            return new ReportParameterValue
            {
                Key = descriptor.Key,
                Type = descriptor.Type,
                Value = value,
                SystemSupplied = true,
                RawText = null,
            };
        }

        private static void EnsureSystemParameter(List<ReportParameterValue> bound, string key,
            ReportFieldType type, object? value)
        {
            if (bound.Any(b => string.Equals(b.Key, key, StringComparison.Ordinal))) return;
            bound.Add(new ReportParameterValue
            {
                Key = key,
                Type = type,
                Value = value,
                SystemSupplied = true,
            });
        }

        private static bool CheckOptions(ReportParameterDescriptor descriptor, string text,
            List<ReportDiagnostic> diagnostics)
        {
            if (descriptor.Options.Count == 0) return true;
            if (descriptor.Options.Any(o => string.Equals(o.Value, text, StringComparison.Ordinal))) return true;

            diagnostics.Add(ReportDiagnostic.Error(CodeNotInOptions,
                $"Value '{text}' is not one of the allowed values for parameter '{descriptor.Key}'.",
                descriptor.Key));
            return false;
        }

        private static bool CheckRange(ReportParameterDescriptor descriptor, object? value, CultureInfo culture,
            DateTime now, List<ReportDiagnostic> diagnostics)
        {
            if (value == null) return true;

            if (!string.IsNullOrWhiteSpace(descriptor.MinValue)
                && ReportValues.TryConvert(descriptor.MinValue, descriptor.Type, culture, now, out var min)
                && min != null
                && ReportValues.Compare(value, min, descriptor.Type, culture) < 0)
            {
                diagnostics.Add(ReportDiagnostic.Error(CodeOutOfRange,
                    $"Parameter '{descriptor.Key}' is below its minimum of '{descriptor.MinValue}'.",
                    descriptor.Key));
                return false;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.MaxValue)
                && ReportValues.TryConvert(descriptor.MaxValue, descriptor.Type, culture, now, out var max)
                && max != null
                && ReportValues.Compare(value, max, descriptor.Type, culture) > 0)
            {
                diagnostics.Add(ReportDiagnostic.Error(CodeOutOfRange,
                    $"Parameter '{descriptor.Key}' is above its maximum of '{descriptor.MaxValue}'.",
                    descriptor.Key));
                return false;
            }

            return true;
        }
    }
}