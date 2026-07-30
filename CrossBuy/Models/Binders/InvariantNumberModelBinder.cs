using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CrossBuy.Models.Binders
{
    /// <summary>
    /// HM-1 — central fix for the Arabic-culture decimal binding bug.
    /// Parses ALL fractional numeric fields (decimal/double/float + nullable) with the INVARIANT
    /// culture after normalizing digits/separators, so decimals round-trip regardless of the request
    /// culture. Dates, UI language and collation are untouched.
    /// Convention (Kuwait): "." = decimal, "," = thousands. Arabic ٫ (U+066B) = decimal, ٬ (U+066C) = thousands.
    /// </summary>
    public enum NumberBindStatus { Bound, NullValue, Error }

    public static class NumberInputNormalizer
    {
        private static bool AllDigits(string x)
        {
            if (x.Length == 0) return false;
            foreach (var c in x) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>
        /// Normalize a user-entered numeric string to an invariant-parseable form.
        /// Comma rule (applied strictly in this order, after Arabic ٫→"." and ٬→"," normalization):
        ///   (أ) "," and "." both present  → "," = thousands (removed), "." = decimal.
        ///   (ب) "," only, and the part before the first comma is 1–3 digits AND every part after a
        ///        comma is EXACTLY 3 digits → thousands separators, removed.
        ///   (ج) "," only, a SINGLE comma followed by 1 or 2 digits → decimal separator, → ".".
        ///   (د) any other "," form → left in place so the invariant parse FAILS (explicit bind error).
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw.Trim())
            {
                if (ch >= '٠' && ch <= '٩') { sb.Append((char)('0' + (ch - '٠'))); continue; } // ٠-٩
                if (ch >= '۰' && ch <= '۹') { sb.Append((char)('0' + (ch - '۰'))); continue; } // ۰-۹

                switch (ch)
                {
                    case '٫': sb.Append('.'); break; // Arabic decimal ٫ → "."
                    case '٬': sb.Append(','); break; // Arabic thousands ٬ → ","
                    // spaces + bidi marks are never separators → strip
                    case ' ':
                    case ' ':  // NBSP
                    case ' ':  // narrow NBSP
                    case ' ':  // thin space
                    case '‎':  // LRM
                    case '‏':  // RLM
                    case '؜':  // ALM
                    case '​':  // zero-width space
                        break;
                    default: sb.Append(ch); break;
                }
            }

            var s = sb.ToString();
            if (s.IndexOf(',') < 0) return s; // no comma → nothing to resolve

            // preserve a leading sign while analysing the digit body
            string sign = "";
            if (s.Length > 0 && (s[0] == '-' || s[0] == '+')) { sign = s.Substring(0, 1); s = s.Substring(1); }

            // (أ) both "," and "." present → comma is thousands, dot is decimal — but ONLY if the
            //     positions are valid: exactly one dot, every comma before it, and the integer part
            //     is a well-formed thousands grouping (1–3 digits, then exact groups of 3).
            int dot = s.IndexOf('.');
            if (dot >= 0)
            {
                if (s.IndexOf('.', dot + 1) >= 0) return sign + s;   // more than one dot → fail
                if (s.LastIndexOf(',') > dot)     return sign + s;   // a comma after the dot → fail

                var intPart = s.Substring(0, dot);
                var frac = s.Substring(dot);                          // includes the "."
                var gp = intPart.Split(',');
                bool okGroup = gp.Length >= 2 && gp[0].Length >= 1 && gp[0].Length <= 3 && AllDigits(gp[0]);
                if (okGroup)
                    for (int i = 1; i < gp.Length; i++)
                        if (gp[i].Length != 3 || !AllDigits(gp[i])) { okGroup = false; break; }
                if (!okGroup) return sign + s;                        // invalid grouping → fail
                return sign + string.Concat(gp) + frac;
            }

            var parts = s.Split(',');

            // (ب) all commas are thousands separators (1–3 digits, then exact groups of 3)
            bool thousands = parts.Length >= 2 && parts[0].Length >= 1 && parts[0].Length <= 3 && AllDigits(parts[0]);
            if (thousands)
                for (int i = 1; i < parts.Length; i++)
                    if (parts[i].Length != 3 || !AllDigits(parts[i])) { thousands = false; break; }
            if (thousands)
                return sign + string.Concat(parts);

            // (ج) a single comma followed by 1 or 2 digits → decimal separator
            if (parts.Length == 2 && AllDigits(parts[0]) &&
                (parts[1].Length == 1 || parts[1].Length == 2) && AllDigits(parts[1]))
                return sign + parts[0] + "." + parts[1];

            // (د) any other comma form → leave comma in → parse fails → explicit ModelError
            return sign + s;
        }

        // Explicit mask = Number minus AllowThousands, and WITHOUT AllowExponent (so "1e5"/"2E3" fail).
        // No currency symbol, no parentheses. Any comma left by rule (د) also makes parsing fail.
        internal const NumberStyles Styles =
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
            NumberStyles.AllowLeadingSign  | NumberStyles.AllowTrailingSign  |
            NumberStyles.AllowDecimalPoint;

        /// <summary>Full bind decision for decimal, reused by the model binder and the culture-check endpoint.</summary>
        public static NumberBindStatus TryBindDecimal(string raw, bool isNullable, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(raw))
                return isNullable ? NumberBindStatus.NullValue : NumberBindStatus.Error;
            if (decimal.TryParse(Normalize(raw), Styles, CultureInfo.InvariantCulture, out var d))
            { value = d; return NumberBindStatus.Bound; }
            return NumberBindStatus.Error;
        }
    }

    public class InvariantNumberModelBinder : IModelBinder
    {
        private const NumberStyles Styles = NumberInputNormalizer.Styles; // shared explicit mask

        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            if (bindingContext == null) throw new ArgumentNullException(nameof(bindingContext));

            var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (valueResult == ValueProviderResult.None)
                return Task.CompletedTask; // not posted → leave to normal handling

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);

            var raw = valueResult.FirstValue;
            var type = bindingContext.ModelMetadata.UnderlyingOrModelType;     // unwrap Nullable<T>
            bool nullable = Nullable.GetUnderlyingType(bindingContext.ModelType) != null;
            var inv = CultureInfo.InvariantCulture;

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (nullable) bindingContext.Result = ModelBindingResult.Success(null);
                else bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "A value is required.");
                return Task.CompletedTask;
            }

            var norm = NumberInputNormalizer.Normalize(raw);
            object parsed = null;
            bool ok = false;
            if (type == typeof(decimal)) { ok = decimal.TryParse(norm, Styles, inv, out var d); parsed = d; }
            else if (type == typeof(double)) { ok = double.TryParse(norm, Styles, inv, out var d); parsed = d; }
            else if (type == typeof(float)) { ok = float.TryParse(norm, Styles, inv, out var d); parsed = d; }

            if (ok)
                bindingContext.Result = ModelBindingResult.Success(parsed);
            else
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.ModelName, $"The value '{raw}' is not a valid number.");

            return Task.CompletedTask;
        }
    }

    public class InvariantNumberModelBinderProvider : IModelBinderProvider
    {
        public IModelBinder GetBinder(ModelBinderProviderContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var t = context.Metadata.UnderlyingOrModelType; // unwrap Nullable<T>
            if (t == typeof(decimal) || t == typeof(double) || t == typeof(float))
                return new InvariantNumberModelBinder();

            return null; // all other types → default binders
        }
    }
}
