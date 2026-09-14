////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Expressions;using OutSmart.DAXon.Functions;

using OutSmart.DAXon.Expressions.Elaboration;
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Transformation;
using OutSmart.DAXon.Trees.Iterators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Model;
using OutSmart.DAXon.Internal;
using OutSmart.DAXon.Internal.Collections;
namespace OutSmart.DAXon.Values
{
    internal class SingletonClosure : Closure, ISequence
    {
        private bool built = false;
        private IItem value = null;
        public SingletonClosure(Expression exp, IPullEvaluator inputEvaluator, IXPathContext context)
        {
            SetInputEvaluator(inputEvaluator);
            savedXPathContext = context.NewContext();
            savedXPathContext.Origin = this;
            SaveContext(exp, context); //Instrumentation.count("SingletonClosure.new()");
        }

        public override ISequenceIterator Iterate()
        {
            try
            {
                IItem item = AsItem();
                if (item == null)
                {
                    return EmptyIterator.GetInstance();
                }
                else if (learningEvaluator != null)
                {
                    return new ReportingSingletonIterator(item, learningEvaluator, serialNumber);
                }
                else
                {
                    return new SingletonIterator(item);
                }
            }
            catch (XPathException e)
            {
                throw new UncheckedXPathException(e);
            }
        }

#if NET
        // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
        // per-level recursion cycle, where that costs depth. Measured per site: the attribute
        // is not a blanket win, so it is applied only where it pays.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
        public override IItem Head()
        {
            try
            {
                return AsItem();
            }
            catch (UncheckedXPathException e)
            {
                throw e.GetXPathException();
            }
        }

#if NET
        // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
        // per-level recursion cycle, where that costs depth. Measured per site: the attribute
        // is not a blanket win, so it is applied only where it pays.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
        public virtual IItem AsItem()
        {
            lock (this)
            {

                // bug 6161
                if (!built)
                {
                    value = inputEvaluator.Iterate(savedXPathContext).Next();
                    built = true;
                    savedXPathContext = null; // release variables saved in the context to the garbage collector
                    if (learningEvaluator != null)
                    {
                        learningEvaluator.ReportCompletion(serialNumber);

                        learningEvaluator = null;
                    }
                }

                return value;
            }
        }

        public override IGroundedValue Materialize()
        {
            try
            {
                return SequenceTool.ItemOrEmpty(AsItem());
            }
            catch (UncheckedXPathException e)
            {
                throw e.GetXPathException();
            }
        }

        public override ISequence MakeRepeatable()
        {
            return this;
        }

        public virtual bool IsBuilt()
        {
            return built;
        }
    }
}
