////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Lib;
using OutSmart.DAXon.Text;
using OutSmart.DAXon.Internal.Collections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Internal;
namespace OutSmart.DAXon.Expressions.Sorting
{
    /// <summary>
    /// A simple collation that just wraps a supplied Comparator
    /// </summary>
    internal class SimpleCollation : IStringCollator
    {
        private static readonly IPlatform platform = Core.Version.platform;
        private IComparer<string> comparator;
        private readonly string uri;

        public virtual string CollationURI => uri;

        public virtual IComparer<string> Comparator
        {
            get => comparator; set
            {
                this.comparator = value;
            }
        }

        public virtual ISubstringMatcher SubstringMatcher
        {
            get
            {
                if (comparator is ISubstringMatcher)
                {
                    return (ISubstringMatcher)comparator;
                }

                // .NET simple-UCA-fallback: the comparator built by DotNetPlatform.MakeCollation (used for both
                // lang= collations and the UCA fallback the StandardCollationURIResolver routes here) is a
                // CompareInfo-backed comparer. Java returns a RuleBasedSubstringMatcher for a RuleBasedCollator;
                // the .NET twin uses CompareInfo.IndexOf/IsPrefix/IsSuffix for collation-aware substring matching.
                if (comparator is DotNetPlatform.CompareInfoComparer)
                {
                    DotNetPlatform.CompareInfoComparer cic = (DotNetPlatform.CompareInfoComparer)comparator;
                    return new CompareInfoSubstringMatcher(this, cic.CompareInfo, cic.Options, cic.Ordinal);
                }

                return null;
            }
        }
        public SimpleCollation(string uri, IComparer<string> comparator)
        {
            this.uri = uri;
            this.comparator = comparator;
        }

        public virtual int CompareStrings(UnicodeString o1, UnicodeString o2)
        {
            return comparator.Compare(o1.ToString(), o2.ToString());
        }

        // Java IStringCollator.isEqualToEmpty default method (no DIM on net472 -> emitted per-impl).
        public virtual bool IsEqualToEmpty(UnicodeString s1)
        {
            return ComparesEqual(s1, EmptyUnicodeString.GetInstance());
        }
        public virtual bool ComparesEqual(UnicodeString s1, UnicodeString s2)
        {
            return comparator.Compare(s1.ToString(), s2.ToString()) == 0;
        }

        public virtual IAtomicMatchKey GetCollationKey(UnicodeString s)
        {
            return platform.GetCollationKey(this, s.ToString());
        }
    }

    // .NET simple-UCA-fallback substring matcher. Java's SimpleCollation.getSubstringMatcher() returns a
    // RuleBasedSubstringMatcher (driven by a CollationElementIterator) for a RuleBasedCollator; the .NET port
    // has no CollationElementIterator, so collation-aware substring matching is done with CompareInfo, whose
    // IndexOf/IsPrefix/IsSuffix honour the same CompareOptions used for ordering. substring-before/after need
    // the matched-region length, and net472's CompareInfo.IndexOf has no matchLength overload, so it is
    // recovered by a shortest-region scan. Locale ordering is already a documented divergence from Java
    // (CLDR vs JRE tables); these substring ops inherit exactly that divergence, nothing more.
    internal sealed class CompareInfoSubstringMatcher : ISubstringMatcher
    {
        private readonly SimpleCollation baseCollation;
        private readonly CompareInfo compareInfo;
        private readonly CompareOptions options;
        private readonly bool ordinal;

        // ---- IStringCollator: delegate to the underlying SimpleCollation (identical comparator) ----
        public string CollationURI => baseCollation.CollationURI;

        public CompareInfoSubstringMatcher(SimpleCollation baseCollation, CompareInfo compareInfo, CompareOptions options, bool ordinal)
        {
            this.baseCollation = baseCollation;
            this.compareInfo = compareInfo;
            this.options = options;
            this.ordinal = ordinal;
        }
        public int CompareStrings(UnicodeString o1, UnicodeString o2) => baseCollation.CompareStrings(o1, o2);
        public bool ComparesEqual(UnicodeString s1, UnicodeString s2) => baseCollation.ComparesEqual(s1, s2);
        public bool IsEqualToEmpty(UnicodeString s1) => baseCollation.IsEqualToEmpty(s1);
        public IAtomicMatchKey GetCollationKey(UnicodeString s) => baseCollation.GetCollationKey(s);

        // ---- ISubstringMatcher ----
        public bool Contains(UnicodeString s1, UnicodeString s2) => IndexOf(s1.ToString(), s2.ToString()) >= 0;
        public bool StartsWith(UnicodeString s1, UnicodeString s2) => IsPrefix(s1.ToString(), s2.ToString());
        public bool EndsWith(UnicodeString s1, UnicodeString s2) => IsSuffix(s1.ToString(), s2.ToString());

        public UnicodeString SubstringBefore(UnicodeString s1, UnicodeString s2)
        {
            string source = s1.ToString();
            int start = IndexOf(source, s2.ToString());
            if (start < 0)
            {
                return EmptyUnicodeString.GetInstance();
            }

            return StringTool.FromCharSequence(source.Substring(0, start));
        }

        public UnicodeString SubstringAfter(UnicodeString s1, UnicodeString s2)
        {
            string source = s1.ToString();
            string target = s2.ToString();
            int start = IndexOf(source, target);
            if (start < 0)
            {
                return EmptyUnicodeString.GetInstance();
            }

            int len = MatchLength(source, start, target);
            return StringTool.FromCharSequence(source.Substring(start + len));
        }

        private int IndexOf(string source, string target)
        {
            if (target.Length == 0 || CollatesEmpty(target))
            {
                return 0;
            }

            if (ordinal)
            {
                return source.IndexOf(target, StringComparison.Ordinal);
            }

            return compareInfo.IndexOf(source, target, options);
        }

        private bool IsPrefix(string source, string target)
        {
            if (target.Length == 0 || CollatesEmpty(target))
            {
                return true;
            }

            if (ordinal)
            {
                return source.StartsWith(target, StringComparison.Ordinal);
            }

            if (compareInfo.IsPrefix(source, target, options))
            {
                return true;
            }

            // ICU reports the SHORTEST matching region, so a match separated from position 0 by
            // ignorable characters only (UCA alternate=blanked drops punctuation at primary
            // strength) is not a prefix to it, while NLS calls it one. Those characters contribute
            // nothing: skip them and ask again. A source opening with a significant character
            // stops here after one comparison, whatever its length.
            int head = LeadingIgnorables(source);
            return head > 0 && compareInfo.IsPrefix(source.Substring(head), target, options);
        }

        private bool IsSuffix(string source, string target)
        {
            if (target.Length == 0 || CollatesEmpty(target))
            {
                return true;
            }

            if (ordinal)
            {
                return source.EndsWith(target, StringComparison.Ordinal);
            }

            if (compareInfo.IsSuffix(source, target, options))
            {
                return true;
            }

            // Mirror of IsPrefix: a trailing run of ignorable characters must not hide the match.
            int tail = TrailingIgnorables(source);
            return tail > 0 && compareInfo.IsSuffix(source.Substring(0, source.Length - tail), target, options);
        }

        // True when the pattern collation-equals the empty string — all its collation elements are
        // ignorable (e.g. UCA alternate=blanked at primary strength reduces punctuation to nothing).
        // Such a pattern matches a zero-length region at the start of any string: contains/starts-with/
        // ends-with are true and substring-before/after treat the match as zero-length. net472's
        // CompareInfo.IndexOf is inconsistent here (returns 0 for a non-empty source but -1 for an empty
        // one), so this is screened explicitly.
        private bool CollatesEmpty(string target)
        {
            return !ordinal && target.Length != 0 && compareInfo.Compare(target, string.Empty, options) == 0;
        }

        // Length of the leading run of code points that collation-equal the empty string. Whole
        // code points only: half a surrogate pair would be judged as garbage, not as ignorable.
        private int LeadingIgnorables(string s)
        {
            int pos = 0;
            while (pos < s.Length)
            {
                int width = char.IsHighSurrogate(s[pos]) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]) ? 2 : 1;
                if (compareInfo.Compare(s, pos, width, string.Empty, 0, 0, options) != 0)
                {
                    break;
                }

                pos += width;
            }

            return pos;
        }

        // Mirror of LeadingIgnorables, walking back from the end.
        private int TrailingIgnorables(string s)
        {
            int pos = s.Length;
            while (pos > 0)
            {
                int width = char.IsLowSurrogate(s[pos - 1]) && pos >= 2 && char.IsHighSurrogate(s[pos - 2]) ? 2 : 1;
                if (compareInfo.Compare(s, pos - width, width, string.Empty, 0, 0, options) != 0)
                {
                    break;
                }

                pos -= width;
            }

            return s.Length - pos;
        }

        // A collation-aware match can span a region whose code-unit length differs from the pattern's (e.g.
        // primary strength: "e" matches "é"). Recover the shortest region at the located start that
        // collation-equals the pattern, so substring-after() cuts after the whole matched region.
        private int MatchLength(string source, int start, string target)
        {
            if (target.Length == 0 || CollatesEmpty(target))
            {
                return 0;
            }

            if (ordinal)
            {
                return target.Length;
            }

            for (int len = 1; start + len <= source.Length; len++)
            {
                if (compareInfo.Compare(source.Substring(start, len), target, options) == 0)
                {
                    return len;
                }
            }

            return target.Length;
        }
    }
}