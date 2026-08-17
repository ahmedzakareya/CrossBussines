using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers
{
    /// <summary>
    /// Attribute reading, done through symbols rather than text.
    ///
    /// This is the single largest reason the analyzer exists. Every one of the scanner's seven historical defects
    /// (CORRECTION-001 through 003) was a TEXT problem: attributes on one line, attributes inline before
    /// <c>public</c>, a trailing comment after the closing bracket, a namespace qualifier, a multi-line attribute.
    /// A symbol has no such shapes — <c>[CrossBuy.Models.AccPerm("post")]</c> and <c>[AccPerm("post")]</c> are the
    /// same <see cref="AttributeData"/>, and a commented-out attribute is not an AttributeData at all.
    /// </summary>
    internal static class AttributeFacts
    {
        /// <summary>
        /// The simple name of an attribute, with the "Attribute" suffix removed. Falls back to the syntax name
        /// when the attribute type failed to bind, so an unresolved attribute is still classified rather than
        /// dropped — an unresolved permission attribute must not read as "no permission".
        /// </summary>
        internal static string Name(AttributeData attribute)
        {
            var name = attribute.AttributeClass?.Name;
            if (string.IsNullOrEmpty(name))
            {
                var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
                name = syntax?.ToString() ?? string.Empty;
                var paren = name.IndexOf('(');
                if (paren >= 0) name = name.Substring(0, paren);
                var dot = name.LastIndexOf('.');
                if (dot >= 0) name = name.Substring(dot + 1);
                name = name.Trim();
            }

            if (name!.EndsWith("Attribute", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Attribute".Length);

            return name;
        }

        /// <summary>
        /// The HTTP verbs an action accepts, from its attributes.
        ///
        /// Handles the five verb attributes and <c>[AcceptVerbs]</c>. AcceptVerbs is applied nowhere in the
        /// codebase today — it is supported because the brief requires it and because the failure mode of not
        /// supporting it is silent: an AcceptVerbs("POST") action would be classified GET, i.e. not mutating, and
        /// would vanish from the measured surface entirely.
        ///
        /// An action with no verb attribute is GET, which is the MVC default.
        /// </summary>
        internal static IReadOnlyList<string> HttpVerbs(ISymbol member)
        {
            var verbs = new List<string>();

            foreach (var attribute in member.GetAttributes())
            {
                var name = Name(attribute);

                switch (name)
                {
                    case "HttpGet": Add(verbs, "GET"); continue;
                    case "HttpPost": Add(verbs, "POST"); continue;
                    case "HttpPut": Add(verbs, "PUT"); continue;
                    case "HttpDelete": Add(verbs, "DELETE"); continue;
                    case "HttpPatch": Add(verbs, "PATCH"); continue;
                    case "HttpHead": Add(verbs, "HEAD"); continue;
                    case "HttpOptions": Add(verbs, "OPTIONS"); continue;
                }

                if (name != "AcceptVerbs") continue;

                // [AcceptVerbs("POST", "PUT")] and [AcceptVerbs(HttpVerbs.Post)] are both real shapes. The
                // string form is read from the constant arguments; the enum form is read from its member name.
                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument.Kind == TypedConstantKind.Array)
                    {
                        foreach (var element in argument.Values) AddVerbConstant(verbs, element);
                    }
                    else
                    {
                        AddVerbConstant(verbs, argument);
                    }
                }
            }

            if (verbs.Count == 0) verbs.Add("GET");
            return verbs;
        }

        private static void AddVerbConstant(List<string> verbs, TypedConstant constant)
        {
            if (constant.Value is string text && text.Length > 0)
            {
                Add(verbs, text.ToUpperInvariant());
                return;
            }

            // An enum constant: recover the member name (HttpVerbs.Post -> POST).
            if (constant.Type?.TypeKind == TypeKind.Enum && constant.Value != null)
            {
                foreach (var field in constant.Type.GetMembers())
                {
                    if (field is IFieldSymbol f && f.HasConstantValue && Equals(f.ConstantValue, constant.Value))
                    {
                        Add(verbs, f.Name.ToUpperInvariant());
                        return;
                    }
                }
            }
        }

        private static void Add(List<string> verbs, string verb)
        {
            if (!verbs.Contains(verb)) verbs.Add(verb);
        }

        internal static bool IsMutating(IReadOnlyList<string> verbs)
        {
            for (var i = 0; i < verbs.Count; i++)
            {
                var v = verbs[i];
                if (v == "POST" || v == "PUT" || v == "DELETE" || v == "PATCH") return true;
            }
            return false;
        }
    }
}
