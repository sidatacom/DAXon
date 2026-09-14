////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Expressions;using OutSmart.DAXon.Functions;

using OutSmart.DAXon.Model;
using OutSmart.DAXon.Transformation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Internal;
using OutSmart.DAXon.Internal.Collections;
namespace OutSmart.DAXon.Expressions.Elaboration
{
    internal class SingleItemEvaluator : ISequenceEvaluator
    {
        readonly IItemEvaluator evaluator;
        public SingleItemEvaluator(IItemEvaluator eval)
        {
            this.evaluator = eval;
        }

#if NET
        // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
        // per-level recursion cycle, where that costs depth. Measured per site: the attribute
        // is not a blanket win, so it is applied only where it pays.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
        public virtual ISequence Evaluate(IXPathContext context)
        {
            try
            {
                return evaluator.Eval(context);
            }
            catch (UncheckedXPathException e)
            {
                throw e.GetXPathException();
            }
        }
    }
}