////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Expressions;using OutSmart.DAXon.Functions;

using OutSmart.DAXon.Model;
using OutSmart.DAXon.Transformation;
using OutSmart.DAXon.Values;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Internal;
using OutSmart.DAXon.Internal.Collections;
namespace OutSmart.DAXon.Expressions.Elaboration
{
    internal class LearningEvaluator : ISequenceEvaluator
    {
        private const int EVAL_LIMIT = 20;
        private readonly Expression expression;
        private ISequenceEvaluator evaluator;
        private int completed;
        private int count;
        public LearningEvaluator(Expression expr, ISequenceEvaluator lazy)
        {

            //        monitoring = expr.toShortString().contains("$return-val");
            this.expression = expr;
            this.evaluator = lazy;
            this.completed = 0;
            this.count = 0;
        }

#if NET
        // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
        // per-level recursion cycle, where that costs depth. Measured per site: the attribute
        // is not a blanket win, so it is applied only where it pays.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
        public virtual ISequence Evaluate(IXPathContext context)
        {
            // Counter updates are deliberately race-tolerant (as in Java): a stale value only
            // delays or repeats the strategy switch, and both evaluators are equivalent. This
            // runs on every function-argument evaluation, so no interlocked/barrier here.
            if (count > EVAL_LIMIT)
            {
                return evaluator.Evaluate(context);
            }
            else
            {
                ISequence result = evaluator.Evaluate(context);
                if (result is Closure)
                {
                    ((Closure)result).SetLearningEvaluator(this, count);
                }

                count++;
                return result;
            }
        }

        public virtual void ReportCompletion(int serialNumber)
        {

            // Note, does thread-unsafe updates to the statistics
            // Note: three things might happen to a MemoClosure: it might be read to completion, it might
            // be partially read, and it might never be accessed at all. In the final case we will get no
            // feedback. The condition we want to test for is that of the first N MemoClosures created,
            // each one was read to completion
            if (completed++ >= EVAL_LIMIT && completed == count)
            {
                evaluator = (expression.MakeElaborator().Eagerly());
                count = int.MaxValue; // learning over: pin Evaluate to its fast branch for good
            }
        }
    }
}