////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
/*
 * Originally part of Apache's Jakarta project (downloaded January 2012),
 * this file has been extensively modified for integration into Saxon by
 * Michael Kay, Saxonica.
 */
using OutSmart.DAXon.Regex;
using OutSmart.DAXon.Text;
using OutSmart.DAXon.Values;
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Internal.Collections;
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Events;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Collections.Trie;
using OutSmart.DAXon.Regex.CharClass;
using OutSmart.DAXon.Collections;
using OutSmart.DAXon.Internal;
namespace OutSmart.DAXon.Regex
{
    /*
 * Changes made for Saxon:
 *
 * - handle full Unicode repertoire (esp non-BMP characters) using UnicodeString class for
 *   both the source string and the regular expression
 * - added support for subtraction in a character class
 * - in a character range, changed the condition start < end to start <= end
 * - removed support for [:POSIX:] construct
 * - added support for \p{} and \P{} classes
 * - removed support for unsupported escapes: f, x, u, b, octal characters; added i and c
 * - changed the handling of hyphens within square brackets, and ^ appearing other than at the start
 * - changed the data structure used for the executable so that terms that match a character class
 *   now reference an Func<int, bool> that tests for membership of the character in a set
 * - added support for reluctant {n,m}? quantifiers
 * - allow a quantifier on a nullable expression
 * - allow a quantifier on '$' or '^'
 * - some constructs (back-references, non-capturing groups, etc) are conditional on which XPath/XSD version
 *   is in use
 * - regular expression flags are now fixed at the time the RE is compiled, this can no longer be deferred
 *   until the RE is evaluated
 * - split() function includes a zero-length string at the end of the returned sequence if the last
 *   separator is at the end of the string
 * - added support for the 'q' and 'x' flags; improved support for the 'i' flag
 * - added a method to determine whether there is an anchored match (for XSD use)
 * - tests for newline (e.g in multiline mode) now match \n only, as required by the XPath specification
 * - reorganised the executable program to use Operation objects rather than integer opcodes
 * - introduced optimization for non-backtracking + and * operators (with simple operands)
 *
 * Further changes made February 2014:
 * - complete rewrite of the run-time engine to use an interpreter approach directly on the parsed expression
 *   tree, bypassing the generation of a finite state machine. This achieves a substantial reduction in
 *   recursive depth; the old code had one level of recursion per input character in some cases. In addition
 *   the compiled code for expressions involving large finite counters is much more compact.
 */
    internal class RECompiler
    {
        // Node flags
        static readonly int NODE_NORMAL = 0; // No flags (nothing special)
        static readonly int NODE_TOPLEVEL = 2; // True if top level expr
        private static readonly bool TRACING = false;
        // Input state for compiling regular expression
        UnicodeString pattern; // Input string
        int len; // Length of the pattern string
        int idx; // Current input index into ac
        int capturingOpenParenCount; // Total number of paren pairs
        // {m,n} stacks
        int bracketMin; // Minimum number of matches
        int bracketMax; // Maximum number of matches
        bool isXPath = true;
        bool isXPath30 = true;
        bool isXSD11 = false;
        IntHashSet captures = new IntHashSet();
        bool hasBackReferences = false;
        REFlags reFlags;
        IList<string> warnings;

        public virtual IList<string> Warnings
        {
            get
            {
                if (warnings == null)
                {
                    return new List<string>();
                }
                else
                {
                    return warnings;
                }
            }
        }
        /// <summary>
        /// Constructor.  Creates (initially empty) storage for a regular expression program.
        /// </summary>
        public RECompiler()
        {
        }

        public virtual void SetFlags(REFlags flags)
        {
            this.reFlags = flags;
            isXPath = flags.IsAllowsXPath20Extensions();
            isXPath30 = flags.IsAllowsXPath30Extensions();
            isXSD11 = flags.IsAllowsXSD11Syntax();
        }

        private void Warning(string s)
        {
            if (warnings == null)
            {
                warnings = new List<string>(4);
            }

            warnings.Add(s);
        }

        protected virtual void InternalError()
        {
            throw new InvalidOperationException("Internal error!");
        }

        protected virtual void SyntaxError(string s)
        {
            throw new RESyntaxException(s, idx);
        }

        static Operation Trace(Operation @base)
        {
            if (TRACING && !(@base is OpTrace))
            {
                return new OpTrace(@base);
            }
            else
            {
                return @base;
            }
        }

        protected virtual void Bracket()
        {

            // Current character must be a '{'
            if (idx >= len || pattern.CodePointAt(idx++) != '{')
            {
                InternalError();
            }


            // Next char must be a digit
            if (idx >= len || !IsAsciiDigit(pattern.CodePointAt(idx)))
            {
                SyntaxError("Expected digit");
            }


            // Get min ('m' of {m,n}) number
            StringBuilder number = new StringBuilder(16);
            while (idx < len && IsAsciiDigit(pattern.CodePointAt(idx)))
            {
                number.AppendCodePoint(pattern.CodePointAt(idx++));
            }

            try
            {
                bracketMin = int.Parse(number.ToString());
            }
            catch (Exception e) when (e is FormatException || e is OverflowException)
            {
                // .NET throws OverflowException where Java's parseInt throws NumberFormatException; both mean
                // the quantifier bound isn't a usable int -> a regex syntax error (FORX0002), not a raw crash.
                SyntaxError("Expected valid number");
            }


            // If out of input, fail
            if (idx >= len)
            {
                SyntaxError("Expected comma or right bracket");
            }


            // If end of expr, optional limit is 0
            if (pattern.CodePointAt(idx) == '}')
            {
                idx++;
                bracketMax = bracketMin;
                return;
            }


            // Must have at least {m,} and maybe {m,n}.
            if (idx >= len || pattern.CodePointAt(idx++) != ',')
            {
                SyntaxError("Expected comma");
            }


            // If out of input, fail
            if (idx >= len)
            {
                SyntaxError("Expected comma or right bracket");
            }


            // If {m,} max is unlimited
            if (pattern.CodePointAt(idx) == '}')
            {
                idx++;
                bracketMax = int.MaxValue;
                return;
            }


            // Next char must be a digit
            if (idx >= len || !IsAsciiDigit(pattern.CodePointAt(idx)))
            {
                SyntaxError("Expected digit");
            }


            // Get max number
            number.Length = 0;
            while (idx < len && IsAsciiDigit(pattern.CodePointAt(idx)))
            {
                number.AppendCodePoint(pattern.CodePointAt(idx++));
            }

            try
            {
                bracketMax = int.Parse(number.ToString());
            }
            catch (Exception e) when (e is FormatException || e is OverflowException)
            {
                SyntaxError("Expected valid number");
            }


            // Optional repetitions must be >= 0
            if (bracketMax < bracketMin)
            {
                SyntaxError("Bad range");
            }


            // Must have close brace
            if (idx >= len || pattern.CodePointAt(idx++) != '}')
            {
                SyntaxError("Missing close brace");
            }
        }

        private static bool IsAsciiDigit(int ch)
        {
            return ch >= '0' && ch <= '9';
        }

        protected virtual ICharacterClass Escape(bool inSquareBrackets)
        {

            // "Shouldn't" happen
            if (pattern.CodePointAt(idx) != '\\')
            {
                InternalError();
            }


            // Escape shouldn't occur as last character in string!
            if (idx + 1 == len)
            {
                SyntaxError("Escape terminates string");
            }


            // Switch on character after backslash
            idx += 2;
            int escapeChar = pattern.CodePointAt(idx - 1);
            switch (escapeChar)
            {
                case 'n':
                    return new SingletonCharacterClass('\n');
                case 'r':
                    return new SingletonCharacterClass('\r');
                case 't':
                    return new SingletonCharacterClass('\t');
                case '\\':
                case '|':
                case '.':
                case '-':
                case '^':
                case '?':
                case '*':
                case '+':
                case '{':
                case '}':
                case '(':
                case ')':
                case '[':
                case ']':
                    return new SingletonCharacterClass(escapeChar);
                case '$':
                    if (isXPath)
                    {
                        return new SingletonCharacterClass(escapeChar);
                    }
                    else
                    {
                        SyntaxError("In XSD, '$' must not be escaped");
                    }

                    break;
                case 's':
                    return Categories.ESCAPE_s;
                case 'S':
                    return Categories.ESCAPE_S;
                case 'i':
                    return Categories.ESCAPE_i;
                case 'I':
                    return Categories.ESCAPE_I;
                case 'c':
                    return Categories.ESCAPE_c;
                case 'C':
                    return Categories.ESCAPE_C;
                case 'd':
                    return Categories.ESCAPE_d;
                case 'D':
                    return Categories.ESCAPE_D;
                case 'w':
                    return Categories.ESCAPE_w;
                case 'W':
                    return Categories.ESCAPE_W;
                case 'p':
                case 'P':
                    if (idx == len)
                    {
                        SyntaxError("Expected '{' after \\" + escapeChar);
                    }

                    if (pattern.CodePointAt(idx) != '{')
                    {
                        SyntaxError("Expected '{' after \\" + escapeChar);
                    }

                    int from = idx++;
                    int close = (int)pattern.IndexOf('}', from);
                    if (close == -1)
                    {
                        SyntaxError("No closing '}' after \\" + escapeChar);
                    }

                    string block = pattern.Substring(idx, close).ToString();
                    if (block.Length == 1 || block.Length == 2)
                    {
                        ICharacterClass primary = Categories.GetCategory(block);
                        if (primary == null)
                        {
                            SyntaxError("Unknown character category " + block);
                        }

                        idx = close + 1;
                        if (escapeChar == 'p')
                        {
                            return primary;
                        }
                        else
                        {
                            return MakeComplement(primary);
                        }
                    }
                    else if (block.StartsWith("Is", StringComparison.Ordinal))
                    {
                        string blockName = block.Substring(2);
                        IntSet uniBlock = UnicodeBlocks.GetBlock(blockName);
                        if (uniBlock == null)
                        {

                            // XSD 1.1 says this is not an error, but by default we reject it
                            if (reFlags.IsAllowUnknownBlockNames())
                            {
                                Warning("Unknown Unicode block: " + blockName);
                                idx = close + 1;
                                return EmptyCharacterClass.Complement;
                            }
                            else
                            {
                                SyntaxError("Unknown Unicode block: " + blockName);
                            }
                        }

                        idx = close + 1;
                        IntSetCharacterClass primary = new IntSetCharacterClass(uniBlock);
                        if (escapeChar == 'p')
                        {
                            return primary;
                        }
                        else
                        {
                            return MakeComplement(primary);
                        }
                    }
                    else
                    {
                        SyntaxError("Unknown character category: " + block);
                    }

                    break;
                case '0':
                    SyntaxError("Octal escapes not allowed");
                    break;
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    if (inSquareBrackets)
                    {
                        SyntaxError("Backreference not allowed within character class");
                    }
                    else if (isXPath)
                    {
                        int backRef = escapeChar - '0';
                        while (idx < len)
                        {
                            int c1 = (int)StringConstants.ZERO_TO_NINE.IndexOf(pattern.CodePointAt(idx));
                            if (c1 < 0)
                            {
                                break;
                            }
                            else
                            {
                                int backRef2 = backRef * 10 + c1;
                                if (backRef2 > (capturingOpenParenCount - 1))
                                {
                                    break;
                                }
                                else
                                {
                                    backRef = backRef2;
                                    idx++;
                                }
                            }
                        }

                        if (!captures.Contains(backRef))
                        {
                            string explanation = backRef > (capturingOpenParenCount - 1) ? "(no such group)" : "(group not yet closed)";
                            SyntaxError("invalid backreference \\" + backRef + " " + explanation);
                        }

                        hasBackReferences = true;
                        return new BackReference(backRef);
                    }
                    else
                    {
                        SyntaxError("digit not allowed after \\");
                    }

                    break;
                default:

                    // Other characters not allowed in XSD regexes
                    SyntaxError("Escape character '" + (char)escapeChar + "' not allowed");
                    break;
            }

            return null;
        }

#if NET
        // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
        // per-level recursion cycle, where that costs depth. Measured per site: the attribute
        // is not a blanket win, so it is applied only where it pays.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        protected virtual ICharacterClass ParseCharacterClass()
        {

            // Check for bad calling or empty class
            if (pattern.CodePointAt(idx) != '[')
            {
                InternalError();
            }


            // Check for unterminated or empty class
            int index = ++idx;
            if ((idx + 1) >= len || pattern.CodePointAt(index) == ']')
            {
                SyntaxError("Missing ']'");
            }


            // Parse class declaration
            int simpleChar;
            bool positive = true;
            bool definingRange = false;
            int rangeStart = -1;
            int rangeEnd;
            IntRangeSet range = new IntRangeSet();
            IList<ICharacterClass> addends = null;
            ICharacterClass subtrahend = null;
            if (ThereFollows('^'))
            {
                if (ThereFollows('^', '-', '['))
                {
                    SyntaxError("Nothing before subtraction operator");
                }
                else if (ThereFollows('^', ']'))
                {
                    SyntaxError("Empty negative character group");
                }
                else
                {
                    positive = false;
                    idx++;
                }
            }
            else if (ThereFollows('-', '['))
            {
                SyntaxError("Nothing before subtraction operator");
            }

            while (idx < len && pattern.CodePointAt(idx) != ']')
            {
                int ch = pattern.CodePointAt(idx);
                simpleChar = -1;
                switch (ch)
                {
                    case '[':
                        SyntaxError("Unescaped '[' within square brackets");
                        break;
                    case '\\':
                        {

                            // Escape always advances the stream
                            ICharacterClass cc = Escape(true);
                            if (cc is SingletonCharacterClass)
                            {
                                simpleChar = ((SingletonCharacterClass)cc).Codepoint;
                                break;
                            }
                            else
                            {
                                if (definingRange)
                                {
                                    SyntaxError("Multi-character escape cannot follow '-'");
                                }
                                else
                                {
                                    // collected, not folded pairwise: the n-ary union below
                                    // tests escapes without an extent by a loop, where the
                                    // pairwise fold nested a closure per escape
                                    if (addends == null)
                                    {
                                        addends = new List<ICharacterClass>();
                                    }

                                    addends.Add(cc);
                                }

                                continue;
                            }
                        }

                    case '-':
                        if (ThereFollows('-', '['))
                        {
                            idx++;
                            // One frame of class subtraction per '-[', and a dynamic pattern
                            // chooses its own nesting depth. ParseExpr's probe never sees this
                            // cycle: subtraction recurses here without leaving the class body.
                            StackGuard.Probe();
                            subtrahend = ParseCharacterClass();
                            if (!ThereFollows(']'))
                            {
                                SyntaxError("Expected closing ']' after subtraction");
                            }
                        }
                        else if (ThereFollows('-', ']'))
                        {
                            simpleChar = '-';
                            idx++;
                        }
                        else if (rangeStart >= 0)
                        {
                            definingRange = true;
                            idx++;
                            continue;
                        }
                        else if (definingRange)
                        {
                            SyntaxError("Bad range");
                        }
                        else if (ThereFollows('-', '-') && !ThereFollows('-', '-', '['))
                        {
                            SyntaxError("Unescaped hyphen as start of range");
                        }
                        else if (!isXSD11 && pattern.CodePointAt(idx - 1) != '[' && pattern.CodePointAt(idx - 1) != '^' && !ThereFollows(']') && !ThereFollows('-', '['))
                        {
                            SyntaxError("In XSD 1.0, hyphen is allowed only at the beginning or end of a positive character group");
                        }
                        else
                        {
                            simpleChar = '-';
                            idx++;
                        }

                        break;
                    default:
                        simpleChar = ch;
                        idx++;
                        break;
                }


                // Handle simple character simpleChar
                if (definingRange)
                {

                    // if we are defining a range make it now
                    rangeEnd = simpleChar;

                    // Actually create a range if the range is ok
                    if (rangeStart > rangeEnd)
                    {
                        SyntaxError("Bad character range: start > end"); // Technically this is not an error in XSD, merely a no-op; but it is so
                        // utterly pointless that it is almost certainly a mistake; and we have no
                        // way of indicating warnings.
                    }

                    range.AddRange(rangeStart, rangeEnd);
                    if (reFlags.IsCaseIndependent())
                    {

                        // Special-case A-Z and a-z
                        if (rangeStart == 'a' && rangeEnd == 'z')
                        {
                            range.AddRange('A', 'Z');
                            for (int v = 0; v < CaseVariants.ROMAN_VARIANTS.Length; v++)
                            {
                                range.Add(CaseVariants.ROMAN_VARIANTS[v]);
                            }
                        }
                        else if (rangeStart == 'A' && rangeEnd == 'Z')
                        {
                            range.AddRange('a', 'z');
                            for (int v = 0; v < CaseVariants.ROMAN_VARIANTS.Length; v++)
                            {
                                range.Add(CaseVariants.ROMAN_VARIANTS[v]);
                            }
                        }
                        else
                        {
                            for (int k = rangeStart; k <= rangeEnd; k++)
                            {
                                int[] variants = CaseVariants.GetCaseVariants(k);
                                foreach (int variant in variants)
                                {
                                    range.Add(variant);
                                }
                            }
                        }
                    }


                    // We are done defining the range
                    definingRange = false;
                    rangeStart = -1;
                }
                else
                {

                    // If simple character and not start of range, include it (see XSD 1.1 rules)
                    if (ThereFollows('-'))
                    {
                        if (ThereFollows('-', '['))
                        {
                            range.Add(simpleChar);
                        }
                        else if (ThereFollows('-', ']'))
                        {
                            range.Add(simpleChar);
                        }
                        else if (ThereFollows('-', '-', '['))
                        {
                            range.Add(simpleChar);
                        }
                        else if (ThereFollows('-', '-'))
                        {
                            SyntaxError("Unescaped hyphen cannot act as end of range");
                        }
                        else
                        {
                            rangeStart = simpleChar;
                        }
                    }
                    else
                    {
                        range.Add(simpleChar);
                        if (reFlags.IsCaseIndependent())
                        {
                            int[] variants = CaseVariants.GetCaseVariants(simpleChar);
                            foreach (int variant in variants)
                            {
                                range.Add(variant);
                            }
                        }
                    }
                }
            }


            // Shouldn't be out of input
            if (idx == len)
            {
                SyntaxError("Unterminated character class");
            }


            // Absorb the ']' end of class marker
            idx++;
            ICharacterClass result;
            if (addends == null)
            {
                result = new IntSetCharacterClass(range);
            }
            else
            {
                addends.Insert(0, new IntSetCharacterClass(range));
                result = MakeUnion(addends);
            }

            if (!positive)
            {
                result = MakeComplement(result);
            }

            if (subtrahend != null)
            {
                result = MakeDifference(result, subtrahend);
            }

            return result;
        }

        private bool ThereFollows(params int[] chars)
        {
            if (idx + chars.Length > len)
            {
                return false;
            }

            for (int i = 0; i < chars.Length; i++)
            {
                if (pattern.CodePointAt(idx + i) != chars[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// n-ary union. Members with a known extent merge into one set; the rest are tested by
        /// a loop in UnionCharacterClass. Accumulation loops (a class body of escapes, the
        /// initial character classes of an alternation) must combine through here: folding
        /// them through the binary overload nested one class per member, and the member count
        /// is the pattern's - which for a dynamic pattern means the input's.
        /// </summary>
        public static ICharacterClass MakeUnion(IList<ICharacterClass> members)
        {
            // a lone survivor is returned as-is, exactly as the binary fold left it
            ICharacterClass single = null;
            int survivors = 0;
            foreach (ICharacterClass cc in members)
            {
                if (cc != EmptyCharacterClass.GetInstance())
                {
                    single = cc;
                    survivors++;
                }
            }

            if (survivors <= 1)
            {
                return survivors == 0 ? EmptyCharacterClass.GetInstance() : single;
            }

            IntSet extent = null;
            List<ICharacterClass> opaque = null;
            foreach (ICharacterClass cc in members)
            {
                if (cc == EmptyCharacterClass.GetInstance())
                {
                    continue;
                }

                IntSet s = cc.GetIntSet();
                if (s != null)
                {
                    extent = extent == null ? s : extent.Union(s);
                }
                else
                {
                    if (opaque == null)
                    {
                        opaque = new List<ICharacterClass>();
                    }

                    opaque.Add(cc);
                }
            }

            if (opaque == null)
            {
                return extent == null ? (ICharacterClass)EmptyCharacterClass.GetInstance() : new IntSetCharacterClass(extent);
            }

            if (extent != null && !extent.IsEmpty())
            {
                opaque.Insert(0, new IntSetCharacterClass(extent));
            }

            return opaque.Count == 1 ? opaque[0] : new UnionCharacterClass(opaque.ToArray());
        }

        public static ICharacterClass MakeDifference(ICharacterClass p1, ICharacterClass p2)
        {
            if (p1 == EmptyCharacterClass.GetInstance())
            {
                return p1;
            }

            if (p2 == EmptyCharacterClass.GetInstance())
            {
                return p1;
            }

            IntSet is1 = p1.GetIntSet();
            IntSet is2 = p2.GetIntSet();
            if (is1 == null || is2 == null)
            {
                return new DifferenceCharacterClass(p1, p2);
            }
            else
            {
                return new IntSetCharacterClass(is1.Except(is2));
            }
        }

        public static ICharacterClass MakeComplement(ICharacterClass p1)
        {
            if (p1 is InverseCharacterClass)
            {
                return ((InverseCharacterClass)p1).Complement;
            }
            else
            {
                return new InverseCharacterClass(p1);
            }
        }

        protected virtual Operation ParseAtom()
        {

            // Length of atom
            int lenAtom = 0;

            // Loop while we've got input
            UnicodeBuilder ub = new UnicodeBuilder();

            // Avoid "break loop" construct to allow conversion to C#
            bool breakAtomLoop = false;
            while (idx < len)
            {

                // Is there a next char?
                if ((idx + 1) < len)
                {
                    int c = pattern.CodePointAt(idx + 1);

                    // If the next 'char' is an escape, look past the whole escape
                    if (pattern.CodePointAt(idx) == '\\')
                    {
                        int idxEscape = idx;
                        Escape(false);
                        if (idx < len)
                        {
                            c = pattern.CodePointAt(idx);
                        }

                        idx = idxEscape;
                    }


                    // Switch on next char
                    switch (c)
                    {
                        case '{':
                        case '?':
                        case '*':
                        case '+':

                            // If the next character is a quantifier operator and our atom is non-empty, the
                            // current character should bind to the quantifier operator rather than the atom
                            if (lenAtom != 0)
                            {
                                breakAtomLoop = true;
                            }

                            break;
                    }
                }

                if (breakAtomLoop)
                {
                    break;
                }


                // Switch on current char
                switch (pattern.CodePointAt(idx))
                {
                    case ']':
                    case '.':
                    case '[':
                    case '(':
                    case ')':
                    case '|':
                        breakAtomLoop = true;
                        break;
                    case '{':
                    case '?':
                    case '*':
                    case '+':

                        // We should have an atom by now
                        if (lenAtom == 0)
                        {

                            // No atom before quantifier
                            SyntaxError("No expression before quantifier");
                        }

                        breakAtomLoop = true;
                        break;
                    case '}':
                        SyntaxError("Unescaped right curly brace");
                        breakAtomLoop = true;
                        break;
                    case '\\':
                        {

                            // Get the escaped character (advances input automatically)
                            int idxBeforeEscape = idx;
                            ICharacterClass charClass = Escape(false);

                            // Check if it's a simple escape (as opposed to, say, a backreference)
                            if (!(charClass is IntValuePredicate))
                            {

                                // Not a simple escape, so backup to where we were before the escape.
                                idx = idxBeforeEscape;
                                breakAtomLoop = true;
                                break;
                            }


                            // Add escaped char to atom
                            ub.Append(((IntValuePredicate)charClass).GetTarget());
                            lenAtom++;
                            break;
                        }

                    case '^':
                    case '$':
                        if (isXPath)
                        {
                            breakAtomLoop = true;
                            break;
                        }


                        // else fall through ($ is not a metacharacter in XSD)
                        goto default;
                    default:

                        // Add normal character to atom
                        int index = idx++;
                        ub.Append(pattern.CodePointAt(index));
                        lenAtom++;
                        break;
                }

                if (breakAtomLoop)
                {
                    break;
                }
            }


            // This shouldn't happen
            if (ub.IsEmpty())
            {
                InternalError();
            }


            // Return the instruction
            return Trace(new OpAtom(ub.ToUnicodeString()));
        }

        protected virtual Operation ParseTerminal(int[] flags)
        {
            switch (pattern.CodePointAt(idx))
            {
                case '$':
                    if (isXPath)
                    {
                        idx++;
                        return Trace(new OpEOL());
                    }

                    break;
                case '^':
                    if (isXPath)
                    {
                        idx++;
                        return Trace(new OpBOL());
                    }

                    break;
                case '.':
                    idx++;
                    IIntPredicateProxy predicate;
                    if (reFlags.IsSingleLine())
                    {

                        // in XPath with the 's' flag, '.' matches everything
                        predicate = IntSetPredicate.ALWAYS_TRUE;
                    }
                    else
                    {

                        // in XSD, "." matches everything except \n and \r. See also bug 15594.
                        predicate = IntPredicateLambda.Of((value) => value != '\n' && value != '\r');
                    }

                    return Trace(new OpCharClass(predicate));
                case '[':
                    ICharacterClass range = ParseCharacterClass();
                    return Trace(new OpCharClass(range));
                case '(':
                    return ParseExpr(flags);
                case ')':
                    SyntaxError("Unexpected closing ')'");
                    break;
                case '|':
                    InternalError();
                    break;
                case ']':
                    SyntaxError("Unexpected closing ']'");
                    break;
                case 0:
                    SyntaxError("Unexpected end of input");
                    break;
                case '?':
                case '+':
                case '{':
                case '*':
                    SyntaxError("No expression before quantifier");
                    break;
                case '\\':
                    {

                        // Don't forget, escape() advances the input stream!
                        int idxBeforeEscape = idx;
                        ICharacterClass esc = Escape(false);
                        if (esc is BackReference)
                        {

                            // this is a total kludge
                            int backreference = ((BackReference)esc).Codepoint;
                            if (capturingOpenParenCount <= backreference)
                            {
                                SyntaxError("Bad backreference");
                            }

                            return Trace(new OpBackReference(backreference));
                        }
                        else if (esc is IntSingletonSet)
                        {

                            // We had a simple escape and we want to have it end up in
                            // an atom, so we back up and fall though to the default handling
                            idx = idxBeforeEscape;
                        }
                        else
                        {
                            return Trace(new OpCharClass(esc));
                        }

                        break;
                    }
            }


            // Everything above either fails or returns.
            // If it wasn't one of the above, it must be the start of an atom.
            return ParseAtom();
        }

        protected virtual Operation Piece(int[] flags)
        {

            // Values to pass by reference to terminal()
            int[] terminalFlags = new[]
            {
                NODE_NORMAL
            };

            // Get terminal symbol
            Operation ret = ParseTerminal(terminalFlags);

            // Or in flags from terminal symbol
            flags[0] |= terminalFlags[0];

            // Advance input, set NODE_NULLABLE flag and do sanity checks
            if (idx >= len)
            {
                return ret;
            }

            bool greedy = true;
            int quantifierType = pattern.CodePointAt(idx);
            switch (quantifierType)
            {
                case '?':
                case '*':
                case '+':

                    // Eat quantifier character
                    idx++;

                    // Drop through
                    goto case '{';
                case '{':
                    if (quantifierType == '{')
                    {
                        Bracket();
                    }

                    if (ret is OpBOL || ret is OpEOL)
                    {

                        // Pretty meaningless, but legal. If the quantifier allows zero occurrences, ignore the instruction.
                        // Otherwise, ignore the quantifier
                        if (quantifierType == '?' || quantifierType == '*' || (quantifierType == '{' && bracketMin == 0))
                        {
                            return new OpNothing();
                        }
                        else
                        {
                            quantifierType = 0;
                        }
                    }

                    if (ret.MatchesEmptyString() == Operation.MATCHES_ZLS_ANYWHERE)
                    {
                        if (quantifierType == '?')
                        {

                            // can ignore the quantifier
                            quantifierType = 0;
                        }
                        else if (quantifierType == '+')
                        {

                            // '*' and '+' are equivalent
                            quantifierType = '*';
                        }
                        else if (quantifierType == '{')
                        {

                            // bounds are meaningless
                            quantifierType = '*';
                        }
                    }

                    break;
            }


            // If the next character is a '?', make the quantifier non-greedy (reluctant)
            if (idx < len && pattern.CodePointAt(idx) == '?')
            {
                if (!isXPath)
                {
                    SyntaxError("Reluctant quantifiers are not allowed in XSD");
                }

                idx++;
                greedy = false;
            }

            int min = 1;
            int max = 1;
            switch (quantifierType)
            {
                case '{':
                    min = this.bracketMin;
                    max = this.bracketMax;
                    break;
                case '?':
                    min = 0;
                    max = 1;
                    break;
                case '+':
                    min = 1;
                    max = int.MaxValue;
                    break;
                case '*':
                    min = 0;
                    max = int.MaxValue;
                    break;
            }

            Operation result;
            if (max == 0)
            {
                result = new OpNothing();
            }
            else if (min == 1 && max == 1)
            {
                return ret;
            }
            else if (greedy)
            {

                // Actually do the quantifier now
                if (ret.MatchLength == -1)
                {
                    result = Trace(new OpRepeat(ret, min, max, true));
                }
                else
                {
                    result = new OpGreedyFixed(ret, min, max, ret.MatchLength);
                }
            }
            else
            {
                if (ret.MatchLength == -1)
                {
                    result = new OpRepeat(ret, min, max, false);
                }
                else
                {
                    result = new OpReluctantFixed(ret, min, max, ret.MatchLength);
                }
            }

            return Trace(result);
        }

        protected virtual Operation ParseBranch()
        {

            // Get each possibly qnatified piece and concat
            Operation current = null;
            int[] quantifierFlags = new int[1];
            while (idx < len && pattern.CodePointAt(idx) != '|' && pattern.CodePointAt(idx) != ')')
            {

                // Get new node
                quantifierFlags[0] = NODE_NORMAL;
                Operation op = Piece(quantifierFlags);
                if (current == null)
                {
                    current = op;
                }
                else
                {
                    current = MakeSequence(current, op);
                }
            }


            // If we don't run loop, make a nothing node
            if (current == null)
            {
                return new OpNothing();
            }

            return current;
        }

        private Operation ParseExpr(int[] compilerFlags)
        {
            // Recursive descent: every nested group re-enters here (ParseTerminal -> ParseExpr), so
            // one stack-adaptive probe bounds pattern-nesting depth. Java has no guard on this path —
            // a deep dynamic pattern throws raw StackOverflowError from RECompiler.parseExpr; on .NET
            // that would kill the process, so Compile converts the probe's signal to a syntax error.
            StackGuard.Probe();

            // Create open paren node unless we were called from the top level (which has no parens)
            int paren = -1;
            int group = 0;
            IList<Operation> branches = new List<Operation>();
            int closeParens = capturingOpenParenCount;
            bool capturing = true;
            if ((compilerFlags[0] & NODE_TOPLEVEL) == 0 && pattern.CodePointAt(idx) == '(')
            {

                // if its a cluster ( rather than a proper subexpression ie with backrefs )
                if (idx + 2 < len && pattern.CodePointAt(idx + 1) == '?' && pattern.CodePointAt(idx + 2) == ':')
                {
                    if (!isXPath30)
                    {
                        SyntaxError("Non-capturing groups allowed only in XPath3.0");
                    }

                    paren = 2;
                    idx += 3;
                    capturing = false;
                }
                else
                {
                    paren = 1;
                    idx++;
                    group = capturingOpenParenCount++;
                }
            }

            compilerFlags[0] &= ~NODE_TOPLEVEL;

            // Process contents of first branch node
            branches.Add(ParseBranch());

            // Loop through branches
            while (idx < len && pattern.CodePointAt(idx) == '|')
            {
                idx++;
                branches.Add(ParseBranch());
            }

            Operation op;
            if (branches.Count == 1)
            {
                op = branches[0];
            }
            else
            {
                op = new OpChoice(branches);
            }


            // Create an ending node (either a close paren or an OP_END)
            if (paren > 0)
            {
                if (idx < len && pattern.CodePointAt(idx) == ')')
                {
                    idx++;
                }
                else
                {
                    SyntaxError("Missing close paren");
                }

                if (capturing)
                {
                    op = new OpCapture(op, group);
                    captures.Add(closeParens);
                }
            }
            else
            {
                op = MakeSequence(op, new OpEndProgram());
            }


            // Return the node list
            return op;
        }

        private static Operation MakeSequence(Operation o1, Operation o2)
        {
            if (o1 is OpSequence)
            {
                if (o2 is OpSequence)
                {
                    IList<Operation> list1 = ((OpSequence)o1).Operations;
                    IList<Operation> list2 = ((OpSequence)o2).Operations;
                    list1.AddRange(list2);
                    return o1;
                }

                IList<Operation> l1 = ((OpSequence)o1).Operations;
                l1.Add(o2);
                return o1;
            }
            else if (o2 is OpSequence)
            {
                IList<Operation> l2 = ((OpSequence)o2).Operations;
                l2.Insert(0,o1);
                return o2;
            }
            else
            {
                IList<Operation> list = new List<Operation>(4);
                list.Add(o1);
                list.Add(o2);
                return Trace(new OpSequence(list));
            }
        }

        public virtual REProgram Compile(UnicodeString pattern)
        {

            // Initialize variables for compilation
            this.pattern = pattern; // Save pattern in instance variable
            len = this.pattern.Length32(); // Precompute pattern length for speed
            idx = 0; // Set parsing index to the first character
            capturingOpenParenCount = 1; // Set paren level to 1 (the implicit outer parens)
            if (reFlags.IsLiteral())
            {

                // 'q' flag is set
                // Create a string node
                Operation ret = new OpAtom(this.pattern);
                Operation endNode = new OpEndProgram();
                Operation seq = MakeSequence(ret, endNode);
                return new REProgram(seq, capturingOpenParenCount, reFlags);
            }
            else
            {
                if (reFlags.IsAllowWhitespace())
                {

                    // 'x' flag is set. Preprocess the expression to strip whitespace, other than between
                    // square brackets
                    UnicodeBuilder sb = new UnicodeBuilder();
                    int nesting = 0;
                    bool escaped = false;
                    IIntIterator iter = pattern.CodePoints();
                    while (iter.MoveNext())
                    {
                        int ch = iter.Current;
                        if (ch == '\\' && !escaped)
                        {
                            escaped = true;
                            sb.Append(ch);
                        }
                        else if (ch == '[' && !escaped)
                        {
                            nesting++;
                            escaped = false;
                            sb.Append(ch);
                        }
                        else if (ch == ']' && !escaped)
                        {
                            nesting--;
                            escaped = false;
                            sb.Append(ch);
                        }
                        else if (nesting == 0 && Whitespace.IsWhite(ch))
                        {
                        }
                        else
                        {
                            escaped = false;
                            sb.Append(ch);
                        }
                    }

                    this.pattern = sb.ToUnicodeString();
                    this.len = this.pattern.Length32();
                }


                // Initialize pass by reference flags value
                int[] compilerFlags = new[]
                {
                    NODE_TOPLEVEL
                };

                // Parse expression
                Operation exp;
                try
                {
                    exp = ParseExpr(compilerFlags);
                }
                catch (RecursionDepthError e) when (!e.Described)
                {
                    // Described, not turned into an RESyntaxException: a dynamic pattern is compiled
                    // at evaluation time, so this can run with the whole engine stack above it.
                    throw e.Describe("Regular expression is too deeply nested", "FORX0002", null);
                }

                // Should be at end of input
                if (idx != len)
                {
                    if (pattern.CodePointAt(idx) == ')')
                    {
                        SyntaxError("Unmatched close paren");
                    }

                    SyntaxError("Unexpected input remains");
                }

                REProgram program = new REProgram(exp, capturingOpenParenCount, reFlags);
                if (hasBackReferences)
                {
                    program.optimizationFlags |= REProgram.OPT_HASBACKREFS;
                }

                return program;
            }
        }

        public static bool NoAmbiguity(Operation op0, Operation op1, bool caseBlind, bool reluctant)
        {
            if (op1 is OpEndProgram)
            {
                return !reluctant;
            }

            if (op1 is OpBOL || op1 is OpEOL)
            {
                return true;
            }

            if (op1 is OpRepeat && ((OpRepeat)op1).min == 0)
            {
                return false; //Bug 3429
            }

            ICharacterClass c0 = op0.GetInitialCharacterClass(caseBlind);
            ICharacterClass c1 = op1.GetInitialCharacterClass(caseBlind);
            return c0.IsDisjoint(c1);
        }

        /// <summary>
        /// For convenience a back-reference is treated as an ICharacterClass, although this a fiction
        /// </summary>
        class BackReference : SingletonCharacterClass
        {
            public BackReference(int number) : base(number)
            {
            }
        }
    }
}