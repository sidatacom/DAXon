////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Events;
using OutSmart.DAXon.Expressions.Elaboration;
using OutSmart.DAXon.Expressions.Instructions;
using OutSmart.DAXon.Expressions;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Lib;
using OutSmart.DAXon.Patterns;
using OutSmart.DAXon.Api;
using OutSmart.DAXon.Text;
using OutSmart.DAXon.Tracing;
using OutSmart.DAXon.Transformation;
using OutSmart.DAXon.Types;
using OutSmart.DAXon.Values;
using OutSmart.DAXon.Collections;
using OutSmart.DAXon.Internal.Net;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Expressions.Parsing;
using OutSmart.DAXon.Collections.Trie;
using OutSmart.DAXon.Model;
using OutSmart.DAXon.Internal;
using OutSmart.DAXon.Internal.Collections;
using System.Runtime.CompilerServices;
namespace OutSmart.DAXon.Expressions
{
    public abstract class Expression : IIdentityComparable, IExportAgent, ILocatable, ITraceable
    {
        protected internal readonly object syncLock = new object();
        public const int EVALUATE_METHOD = 1;
        public const int ITERATE_METHOD = 2;
        public const int PROCESS_METHOD = 4;
        public const int WATCH_METHOD = 8;
        public const int ITEM_FEED_METHOD = 16;
        public const int EFFECTIVE_BOOLEAN_VALUE = 32;
        public const int UPDATE_METHOD = 64;

        public const double MAX_COST = 1000000000;

        public static readonly IntegerValue UNBOUNDED_LOWER = (IntegerValue)IntegerValue.FromDouble(-1E+100);
        public static readonly IntegerValue UNBOUNDED_UPPER = (IntegerValue)IntegerValue.FromDouble(+1E+100);
        public static readonly IntegerValue MAX_STRING_LENGTH = Int64Value.MakeIntegerValue(int.MaxValue);
        public static readonly IntegerValue MAX_SEQUENCE_LENGTH = Int64Value.MakeIntegerValue(int.MaxValue);
        protected int staticProperties = -1;
        private ILocation location = Loc.NONE;
        private Expression parentExpression;
        private RetainedStaticContext retainedStaticContext;
        private volatile int[] slotsUsed; // volatile: lock-free fast read in SlotsUsed once computed
        private int evaluationMethod;
        private Dictionary<string, object> extraProperties;
        private double cost = -1;
        private int cachedHashCode = -1;
        // Memoized IsUpdatingExpression() for the subtree (-1 unknown, 0 false, 1 true). Without it the
        // base recursion is O(n^2): CheckForUpdatingSubexpressions visits every node and re-walks each
        // node's whole subtree via IsUpdatingExpression, so a deep chain (1+1+..., a[.][.]...) hangs at
        // compile time. Updating-ness is invariant post-parse (optimization never adds/removes update
        // primitives), so caching with the same -1 sentinel + ResetLocalStaticProperties reset as
        // staticProperties/cachedHashCode is safe. Only the base override consults it.
        private int cachedIsUpdating = -1;
        private Elaborator elaborator;

        public virtual string ExpressionName => GetType().Name;

        public virtual Expression ParentExpression
        {
            get => parentExpression; set
            {

                parentExpression = value;
            }
        }

        public abstract int ImplementationMethod { get; }

        public virtual Expression ScopingExpression
        {
            get
            {
                int d = IntrinsicDependencies & StaticProperty.DEPENDS_ON_FOCUS;
                if (d != 0)
                {
                    if (d == StaticProperty.DEPENDS_ON_CONTEXT_DOCUMENT)
                    {
                        return ExpressionTool.GetContextDocumentSettingContainer(this);
                    }
                    else
                    {
                        return ExpressionTool.GetFocusSettingContainer(this);
                    }
                }
                else
                {
                    return null;
                }
            }
        }

        public virtual RetainedStaticContext LocalRetainedStaticContext => retainedStaticContext;

        public virtual string StaticBaseURIString => GetRetainedStaticContext().StaticBaseUriString;

        public virtual URI StaticBaseURI => GetRetainedStaticContext().GetStaticBaseUri();

        public virtual double Cost
        {
            get
            {
                if (cost < 0)
                {
                    double i = NetCost;
                    foreach (Operand o in Operands())
                    {
                        i += o.GetChildExpression().Cost;
                        if (i > MAX_COST)
                        {
                            break;
                        }
                    }

                    cost = i;
                }

                return cost;
            }
        }
        public virtual int NetCost => 1;
        public virtual Values.SequenceType StaticType => Values.SequenceType.MakeSequenceType(GetItemType(), GetCardinality());

        public virtual int Dependencies
        {
            get
            {

                // Implemented as a memo function: we only compute the dependencies
                // for each expression once
                if (staticProperties == -1)
                {
                    ComputeStaticProperties();
                }

                return staticProperties & StaticProperty.DEPENDENCY_MASK;
            }
        }

        public virtual IntegerValue[] IntegerBounds => null;

        //
        public virtual int IntrinsicDependencies => 0;

        //
        public int[] SlotsUsed
        {
            get
            {
                // Double-checked: once computed, hot callers (Closure.SaveContext per xsl:iterate
                // step) read the volatile field without taking the monitor Java's synchronized
                // makes near-free but net472 does not.
                int[] cached = slotsUsed;
                if (cached != null)
                {
                    return cached;
                }

                lock (syncLock)
                {

                    // synchronized because it's calculated lazily at run-time the first time it's needed
                    if (slotsUsed != null)
                    {
                        return slotsUsed;
                    }

                    IntHashSet slots = new IntHashSet(10);
                    GatherSlotsUsed(this, slots);
                    int[] computed = new int[slots.Count];
                    int i = 0;
                    IIntIterator iter = slots.IIterator();
                    while (iter.MoveNext())
                    {
                        computed[i++] = iter.Current;
                    }

                    Array.Sort(computed);
                    slotsUsed = computed;
                    return computed;
                }
            }
        }

        //
        public virtual string TracingTag => ExpressionName;

        //
        public virtual string StreamerName => null;
        public Expression()
        {
        }

        public virtual IEnumerable<Operand> Operands()
        {
            return new List<Operand>();
        }

        public IEnumerable<Operand> CheckedOperands()
        {
            IEnumerable<Operand> ops = Operands();
            foreach (Operand o in ops)
            {
                Expression child = o.GetChildExpression();
                bool badOperand = o.ParentExpression != this;
                bool badExpression = child.ParentExpression != this;
                if (badOperand || badExpression)
                {
                    string message = "*** Bad parent pointer found in " + (badOperand ? "operand " : "expression ") + child.ToShortString() + " at " + child.GetLocation().GetSystemId() + "#" + child.GetLocation().GetLineNumber() + " ***";
                    try
                    {
                        Configuration config = GetConfiguration();
                        Logger logger = config == null ? null : config.Logger;
                        if (logger != null)
                        {
                            logger.Warning(message);
                        }
                        else
                        {
                            throw new InvalidOperationException(message);
                        }
                    }
                    catch (Exception err)
                    {
                        throw new InvalidOperationException(message);
                    }

                    child.ParentExpression = this;
                }

                if (child.GetRetainedStaticContext() == null)
                {
                    child.SetRetainedStaticContext(GetRetainedStaticContext());
                }
            }

            return ops;
        }

        protected virtual IList<Operand> OperandList(params Operand[] a)
        {
            return a.ToList();
        }

        protected virtual IList<Operand> OperandSparseList(params Operand[] a)
        {
            IList<Operand> operanda = new List<Operand>();
            foreach (Operand o in a)
            {
                if (o != null)
                {
                    operanda.Add(o);
                }
            }

            return operanda;
        }

        public virtual Expression VerifyParentPointers()
        {
            foreach (Operand o in Operands())
            {
                Expression parent = o.GetChildExpression().ParentExpression;
                if (parent != this)
                {
                    throw new InvalidOperationException("Invalid parent pointer in " + parent.ToShortString() + " subexpression " + o.GetChildExpression().ToShortString());
                }

                if (o.ParentExpression != this)
                {
                    throw new InvalidOperationException("Invalid parent pointer in operand object " + parent.ToShortString() + " subexpression " + o.GetChildExpression().ToShortString());
                }

                if (ExpressionTool.FindOperand(parent, o.GetChildExpression()) == null)
                {
                    throw new InvalidOperationException("Incorrect parent pointer in " + parent.ToShortString() + " subexpression " + o.GetChildExpression().ToShortString());
                }

                o.GetChildExpression().VerifyParentPointers();
            }

            return this;
        }

        public virtual void RestoreParentPointers()
        {
            foreach (Operand o in Operands())
            {
                Expression child = o.GetChildExpression();
                child.ParentExpression = this;
                child.RestoreParentPointers();
            }
        }
        public virtual bool ImplementsStaticTypeCheck()
        {
            return false;
        }

        public virtual bool HasVariableBinding(IBinding binding)
        {
            return false;
        }

        public virtual bool IsLiftable(bool forStreaming)
        {
            int p = GetSpecialProperties();
            int d = Dependencies;
            return (p & StaticProperty.NO_NODES_NEWLY_CREATED) != 0 && (p & StaticProperty.HAS_SIDE_EFFECTS) == 0 && ((d & StaticProperty.DEPENDS_ON_ASSIGNABLE_GLOBALS) == 0) && ((d & StaticProperty.DEPENDS_ON_POSITION) == 0) && ((d & StaticProperty.DEPENDS_ON_LAST) == 0);
        }

        public virtual bool SupportsLazyEvaluation()
        {
            return (Dependencies & (StaticProperty.DEPENDS_ON_POSITION | StaticProperty.DEPENDS_ON_LAST | StaticProperty.DEPENDS_ON_CURRENT_ITEM | StaticProperty.DEPENDS_ON_CURRENT_GROUP | StaticProperty.DEPENDS_ON_REGEX_GROUP)) == 0; // we can't save these values in a closure, so we evaluate
            // the expression eagerly
        }

        public virtual bool IsMultiThreaded(Configuration config)
        {
            return false;
        }

        public virtual bool AllowExtractingCommonSubexpressions()
        {
            return true;
        }

        public virtual Expression Simplify()
        {
            SimplifyChildren();
            return this;
        }

        protected void SimplifyChildren()
        {
            foreach (Operand o in Operands())
            {
                if (o != null)
                {
                    Expression e = o.GetChildExpression();
                    if (e != null)
                    {
                        Expression f = e.Simplify();
                        o.SetChildExpression(f);
                    }
                }
            }
        }

        public virtual void SetRetainedStaticContext(RetainedStaticContext rsc)
        {
            if (rsc != null)
            {
                retainedStaticContext = rsc;
                foreach (Operand o in Operands())
                {
                    if (o != null)
                    {
                        Expression child = o.GetChildExpression();
                        if (child != null && child.retainedStaticContext == null)
                        {
                            child.SetRetainedStaticContext(rsc);
                        }
                    }
                }
            }
        }

        // Explicit worklist, not recursion: this runs once per tree level on the compile path,
        // after the parser's own guarded descent, and a deeply nested expression used to take the
        // process down with a real StackOverflowException (visible first on .NET 10, whose tier-0
        // frames are ~2x Framework's). Per-node behaviour is unchanged, including the deliberate
        // carry-over of a child's local context to its later siblings.
        public virtual void SetRetainedStaticContextThoroughly(RetainedStaticContext rsc)
        {
            if (rsc == null)
            {
                return;
            }

            var work = new Stack<PendingRsc>();
            work.Push(new PendingRsc(this, rsc));
            while (work.Count > 0)
            {
                PendingRsc item = work.Pop();
                Expression node = item.Node;
                RetainedStaticContext ctx = item.Context;
                if (ctx == null)
                {
                    continue;
                }

                node.retainedStaticContext = ctx;
                foreach (Operand o in node.Operands())
                {
                    if (o == null)
                    {
                        continue;
                    }

                    Expression child = o.GetChildExpression();
                    if (child == null)
                    {
                        continue;
                    }

                    if (child.LocalRetainedStaticContext == null)
                    {
                        work.Push(new PendingRsc(child, ctx));
                    }
                    else
                    {
                        ctx = child.LocalRetainedStaticContext;
                        foreach (Operand p in child.Operands())
                        {
                            Expression grandchild = p.GetChildExpression();
                            if (grandchild != null)
                            {
                                work.Push(new PendingRsc(grandchild, ctx));
                            }
                        }
                    }
                }
            }
        }

        private readonly struct PendingRsc
        {
            public PendingRsc(Expression node, RetainedStaticContext context)
            {
                Node = node;
                Context = context;
            }

            public Expression Node { get; }

            public RetainedStaticContext Context { get; }
        }

        public virtual void SetRetainedStaticContextLocally(RetainedStaticContext rsc)
        {
            if (rsc != null)
            {
                retainedStaticContext = rsc;
            }
        }

        public RetainedStaticContext GetRetainedStaticContext()
        {
            if (retainedStaticContext == null)
            {
                Expression parent = ParentExpression;
                try
                {
                    retainedStaticContext = parent.GetRetainedStaticContext();
                }
                catch (NullReferenceException npe)
                {
                    ILocation location = GetLocation();
                    string loc = location.GetSystemId() + " - " + location.GetLineNumber() + ":" + location.GetColumnNumber();
                    throw new NullReferenceException(npe.Message + " At " + ToShortString() + ": " + loc);
                }
            }

            return retainedStaticContext;
        }

        public virtual bool IsCallOn(System.Type function)
        {
            return false;
        }

        public virtual Expression TypeCheck(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            TypeCheckChildren(visitor, contextInfo);
            return this;
        }

        protected void TypeCheckChildren(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            foreach (Operand o in Operands())
            {
                o.TypeCheck(visitor, contextInfo);
            }
        }

        public virtual Expression StaticTypeCheck(Values.SequenceType req, bool backwardsCompatible, Func<RoleDiagnostic> roleSupplier, ExpressionVisitor visitor)
        {
            return visitor.GetConfiguration().GetTypeChecker(backwardsCompatible).StaticTypeCheck(this, req, roleSupplier, visitor);
        }

        public virtual Expression Optimize(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            if (visitor.IncrementAndTestDepth())
            {

                // protect against infinite recursion
                OptimizeChildren(visitor, contextInfo);
                visitor.DecrementDepth();
            }

            return this;
        }

        protected void OptimizeChildren(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            foreach (Operand o in Operands())
            {
                o.Optimize(visitor, contextInfo);
            }
        }

        public virtual void PrepareForStreaming()
        {
        }

        public virtual Expression Unordered(bool retainAllNodes, bool forStreaming)
        {
            return this;
        }

        public int GetSpecialProperties()
        {
            if (staticProperties == -1)
            {
                ComputeStaticProperties();
            }

            return staticProperties & StaticProperty.SPECIAL_PROPERTY_MASK;
        }

        public virtual bool HasSpecialProperty(int property)
        {
            return (GetSpecialProperties() & property) != 0;
        }

        public virtual int GetCardinality()
        {
            if (staticProperties == -1)
            {
                ComputeStaticProperties();
            }

            return staticProperties & StaticProperty.CARDINALITY_MASK;
        }

        public abstract Types.ItemType GetItemType();

        public virtual UType GetStaticUType(UType contextItemType)
        {
            return UType.ANY;
        }
        public virtual void SetFlattened(bool flattened)
        {
        }

        public virtual void SetFiltered(bool filtered)
        {
        }

        public virtual IItem EvaluateItem(IXPathContext context)
        {
            return Iterate(context).Next();
        }

        public virtual ISequenceIterator Iterate(IXPathContext context)
        {
            IItem value = EvaluateItem(context);
            return SequenceTool.ItemOrEmpty(value).Iterate();
        }

        public virtual bool EffectiveBooleanValue(IXPathContext context)
        {
            try
            {
                return ExpressionTool.EffectiveBooleanValue(Iterate(context));
            }
            catch (XPathException e)
            {
                throw e.MaybeWithFailingExpression(this).MaybeWithContext(context);
            }
        }

        public virtual UnicodeString EvaluateAsString(IXPathContext context)
        {
            IItem o = EvaluateItem(context);
            if (o == null)
            {
                return EmptyUnicodeString.GetInstance();
            }

            return o.UnicodeStringValue;
        }

        public virtual void Process(Outputter output, IXPathContext context)
        {
            int m = ImplementationMethod;
            bool hasEvaluateMethod = (m & EVALUATE_METHOD) != 0;
            bool hasIterateMethod = (m & ITERATE_METHOD) != 0;
            try
            {
                if (hasEvaluateMethod && (!hasIterateMethod || !Cardinality.AllowsMany(GetCardinality())))
                {
                    IItem item = EvaluateItem(context);
                    if (item != null)
                    {
                        output.Append(item, GetLocation(), ReceiverOption.ALL_NAMESPACES);
                    }
                }
                else if (hasIterateMethod)
                {
                    SequenceTool.Supply(Iterate(context), (it) => output.Append(it, GetLocation(), ReceiverOption.ALL_NAMESPACES));
                }
                else
                {
                    throw new InvalidOperationException("process() is not implemented in the subclass " + GetType());
                }
            }
            catch (UncheckedXPathException unxe)
            {
                throw unxe.GetXPathException().MaybeWithLocation(GetLocation()).MaybeWithContext(context);
            }
            catch (XPathException e)
            {
                throw e.MaybeWithLocation(GetLocation()).MaybeWithContext(context);
            }
        }

        public static void DispatchTailCall(ITailCall tc)
        {
            while (tc != null)
            {
                tc = tc.ProcessLeavingTail();
            }
        }

        public override string ToString()
        {

            // fallback implementation
            StringBuilder buff = new StringBuilder(64);
            string className = GetType().FullName;
            while (true)
            {
                int dot = className.IndexOf('.');
                if (dot >= 0)
                {
                    className = className.Substring(dot + 1);
                }
                else
                {
                    break;
                }
            }

            buff.Append(className);
            bool first = true;
            foreach (Operand o in Operands())
            {
                buff.Append(first ? "(" : ", ");
                buff.Append(o.GetChildExpression().ToString());
                first = false;
            }

            if (!first)
            {
                buff.Append(')');
            }

            return buff.ToString();
        }

        public virtual string ToShortString()
        {

            // fallback implementation
            return ExpressionName;
        }

        public abstract void Export(ExpressionPresenter @out);
        public void Explain(Logger @out)
        {
            ExpressionPresenter ep = new ExpressionPresenter(GetConfiguration(), @out);
            ExpressionPresenter.ExportOptions options = new ExpressionPresenter.ExportOptions();
            options.explaining = true;
            ep.SetOptions(options);
            try
            {
                Export(ep);
            }
            catch (XPathException e)
            {
                ep.StartElement("failure");
                ep.EmitAttribute("message", e.Message);
                ep.EndElement();
            }

            ep.Dispose();
        }

        public virtual void CheckPermittedContents(ISchemaType parentType, bool whole)
        {
        }

        //
        public virtual void AdoptChildExpression(Expression child)
        {
            if (child == null)
            {
                return;
            }


            //                    o.detachChild();
            //                }
            child.ParentExpression = this;
            if (child.retainedStaticContext == null)
            {
                child.retainedStaticContext = retainedStaticContext;
            }

            if (GetLocation() == null || GetLocation() == Loc.NONE)
            {
                ExpressionTool.CopyLocationInfo(child, this);
            }
            else if (child.GetLocation() == null || child.GetLocation() == Loc.NONE)
            {
                ExpressionTool.CopyLocationInfo(this, child);
            }

            ResetLocalStaticProperties();
        }

        //
        public virtual void SetLocation(ILocation id)
        {
            location = id;
        }

        //
        public virtual Expression WithLocation(ILocation id)
        {
            SetLocation(id);
            return this;
        }

        //
        public ILocation GetLocation()
        {
            int limit = 0;
            Expression exp = this;
            while (limit < 10)
            {
                if ((exp.location == null || exp.location == Loc.NONE) && exp.ParentExpression != null)
                {

                    // avoid infinite recursion: bug 3703#1
                    exp = exp.ParentExpression;
                    limit++;
                }
                else
                {
                    break;
                }
            }

            return exp.location == null ? Loc.NONE : exp.location;
        }

        //
        public virtual Configuration GetConfiguration()
        {
            try
            {
                return GetRetainedStaticContext().GetConfiguration();
            }
            catch (NullReferenceException e)
            {
                throw new NullReferenceException("Internal error: expression " + ToShortString() + " has no retained static context");
            }
        }

        //
        public virtual PackageData GetPackageData()
        {
            try
            {
                return GetRetainedStaticContext().GetPackageData();
            }
            catch (NullReferenceException e)
            {
                throw new NullReferenceException("Internal error: expression " + ToShortString() + " has no retained static context");
            }
        }

        //
        public virtual bool IsInstruction()
        {
            return false;
        }

        //
        public void ComputeStaticProperties()
        {
            staticProperties = ComputeDependencies() | ComputeCardinality() | ComputeSpecialProperties();
        }

        //
        public virtual void ResetLocalStaticProperties()
        {
            staticProperties = -1;
            cachedHashCode = -1;
            cachedIsUpdating = -1;
        }

        //
        public virtual bool IsStaticPropertiesKnown()
        {
            return staticProperties != -1;
        }

        //
        protected abstract int ComputeCardinality();
        //
        protected virtual int ComputeSpecialProperties()
        {
            return 0;
        }

        //
        public virtual int ComputeDependencies()
        {
            int dependencies = IntrinsicDependencies;
            foreach (Operand o in Operands())
            {
                if (o.HasSameFocus())
                {
                    dependencies |= o.GetChildExpression().Dependencies;
                }
                else
                {
                    dependencies |= o.GetChildExpression().Dependencies & ~StaticProperty.DEPENDS_ON_FOCUS;
                }
            }

            return dependencies;
        }

        //
        public virtual void SetStaticProperty(int prop)
        {
            if (staticProperties == -1)
            {
                ComputeStaticProperties();
            }

            staticProperties |= prop;
        }

        //
        public virtual void CheckForUpdatingSubexpressions()
        {
            foreach (Operand o in Operands())
            {
                Expression sub = o.GetChildExpression();
                if (sub == null)
                {
                    throw new NullReferenceException();
                }

                sub.CheckForUpdatingSubexpressions();
                if (sub.IsUpdatingExpression())
                {
                    throw new XPathException("Updating expression appears in a context where it is not permitted", "XUST0001").WithLocation(sub.GetLocation());
                }
            }
        }

        //
        public virtual bool IsUpdatingExpression()
        {
            if (cachedIsUpdating == -1)
            {
                cachedIsUpdating = 0;
                foreach (Operand o in Operands())
                {
                    if (o.GetChildExpression().IsUpdatingExpression())
                    {
                        cachedIsUpdating = 1;
                        break;
                    }
                }
            }

            return cachedIsUpdating == 1;
        }

        //
        public virtual bool IsVacuousExpression()
        {
            return false;
        }

        //
        public abstract Expression Copy(RebindingMap rebindings);
        //
        public virtual void SuppressValidation(int parentValidationMode)
        {
        }

        //
        public virtual int MarkTailFunctionCalls(StructuredQName qName, int arity)
        {
            return UserFunctionCall.NOT_TAIL_CALL;
        }

        //
        public virtual Patterns.Pattern ToPattern(Configuration config)
        {
            Types.ItemType type = GetItemType();
            if (((Dependencies & StaticProperty.DEPENDS_ON_NON_DOCUMENT_FOCUS) == 0) && (type is NodeTest || this is VariableReference))
            {
                return new NodeSetPattern(this);
            }

            if (IsCallOn(typeof(KeyFn)) || IsCallOn(typeof(SuperId)))
            {
                return new NodeSetPattern(this);
            }

            throw new XPathException("Cannot convert the expression {" + this + "} to a pattern");
        }

        //
        private static void GatherSlotsUsed(Expression exp, IntHashSet slots)
        {
            if (exp is LocalVariableReference)
            {
                slots.Add(((LocalVariableReference)exp).SlotNumber);
            }
            else if (exp is SuppliedParameterReference)
            {
                int slot = ((SuppliedParameterReference)exp).SlotNumber;
                slots.Add(slot);
            }
            else
            {
                foreach (Operand o in exp.Operands())
                {
                    GatherSlotsUsed(o.GetChildExpression(), slots);
                }
            }
        }

        //
        protected virtual void DynamicError(string message, string code, IXPathContext context)
        {
            throw new XPathException(message, code, GetLocation()).WithXPathContext(context).WithFailingExpression(this);
        }

        //
        protected virtual void TypeError(string message, string errorCode, IXPathContext context)
        {
            throw new XPathException(message, errorCode, GetLocation()).AsTypeError().WithXPathContext(context).WithFailingExpression(this);
        }

        //
        public virtual StructuredQName GetObjectName()
        {
            return null;
        }

        //
        public virtual object GetProperty(string name)
        {
            if (name.Equals("expression"))
            {
                return GetLocation();
            }
            else
            {
                return null;
            }
        }

        //
        public virtual IEnumerator<string> GetProperties()
        {
            yield return "expression";
        }

        //
        public virtual PathMap.PathMapNodeSet AddToPathMap(PathMap pathMap, PathMap.PathMapNodeSet pathMapNodeSet)
        {
            bool dependsOnFocus = ExpressionTool.DependsOnFocus(this);
            PathMap.PathMapNodeSet attachmentPoint;
            if (pathMapNodeSet == null)
            {
                if (dependsOnFocus)
                {
                    ContextItemExpression cie = new ContextItemExpression();
                    ExpressionTool.CopyLocationInfo(this, cie);
                    pathMapNodeSet = new PathMap.PathMapNodeSet(pathMap.MakeNewRoot(cie));
                }

                attachmentPoint = pathMapNodeSet;
            }
            else
            {
                attachmentPoint = dependsOnFocus ? pathMapNodeSet : null;
            }

            PathMap.PathMapNodeSet result = new PathMap.PathMapNodeSet();
            foreach (Operand o in Operands())
            {
                OperandUsage usage = o.Usage;
                Expression child = o.GetChildExpression();
                PathMap.PathMapNodeSet target = child.AddToPathMap(pathMap, attachmentPoint);
                if (usage == OperandUsage.NAVIGATION)
                {

                    // indicate that the function navigates to all elements in the document
                    target = target.CreateArc(AxisInfo.ANCESTOR_OR_SELF, NodeKindTest.ELEMENT);
                    target = target.CreateArc(AxisInfo.DESCENDANT, NodeKindTest.ELEMENT);
                }

                result.AddNodeSet(target);
            }

            if (GetItemType() is IAtomicType)
            {

                // if expression returns an atomic value then any nodes accessed don't contribute to the result
                return null;
            }
            else
            {
                return result;
            }
        }

        //
        public virtual bool IsSubtreeExpression()
        {
            if (ExpressionTool.DependsOnFocus(this))
            {
                if ((IntrinsicDependencies & StaticProperty.DEPENDS_ON_FOCUS) != 0)
                {
                    return false;
                }
                else
                {
                    foreach (Operand o in Operands())
                    {
                        if (!o.GetChildExpression().IsSubtreeExpression())
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            else
            {
                return true;
            }
        }

        //
        public virtual void SetEvaluationMethod(int method)
        {
            this.evaluationMethod = method;
        }

        //
        public virtual int GetEvaluationMethod()
        {
            return evaluationMethod;
        }

        //
        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        //
        public bool IsEqual(Expression other)
        {
            return this == other || (GetHashCode() == other.GetHashCode() && Equals(other));
        }

        //
        public override int GetHashCode()
        {
            if (cachedHashCode == -1)
            {
                cachedHashCode = ComputeHashCode();
            }

            return cachedHashCode;
        }

        //
        protected virtual bool HasCompatibleStaticContext(Expression other)
        {
            bool d1 = (IntrinsicDependencies & StaticProperty.DEPENDS_ON_STATIC_CONTEXT) != 0;
            bool d2 = (other.IntrinsicDependencies & StaticProperty.DEPENDS_ON_STATIC_CONTEXT) != 0;
            if (d1 != d2)
            {
                return false;
            }

            if (d1)
            {
                return GetRetainedStaticContext().Equals(other.GetRetainedStaticContext());
            }

            return true;
        }

        //
        protected virtual int ComputeHashCode()
        {
            return base.GetHashCode();
        }

        //
        public virtual bool IsIdentical(IIdentityComparable other)
        {
            return this == other;
        }

        //
        public virtual int IdentityHashCode()
        {
            return RuntimeHelpers.GetHashCode(GetLocation());
        }

        //
        public virtual void SetExtraProperty(string name, object value)
        {
            if (extraProperties == null)
            {
                if (value == null)
                {
                    return;
                }

                extraProperties = new Dictionary<string, object>(4);
            }

            if (value == null)
            {
                extraProperties.Remove(name);
            }
            else
            {
                extraProperties[name] = value;
            }
        }

        //
        public virtual object GetExtraProperty(string name)
        {
            if (extraProperties == null)
            {
                return null;
            }
            else
            {
                return extraProperties.GetOrDefault(name);
            }
        }

        //
        public virtual Elaborator GetElaborator()
        {
            return new FallbackElaborator();
        }

        //
        public Elaborator MakeElaborator()
        {
            lock (syncLock)
            {
                if (elaborator == null)
                {
                    Elaborator elab = GetElaborator();
                    elab.SetExpression(this);
                    return elaborator = elab;
                }
                else
                {
                    return elaborator;
                }
            }
        }

        // === Auto-generated stubs (StubGenerator Phase 3.1f) ===
        // Upstream Traceable.gatherProperties default is a no-op; TraceExpression calls this on EVERY
        // traced child, so a throwing base made compile-with-tracing crash on any expression without
        // an override.
        public virtual void GatherProperties(Action<string, object> consumer) { }
    }
}