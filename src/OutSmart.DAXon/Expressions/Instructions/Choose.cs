////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Expressions;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Tracing;
using OutSmart.DAXon.Transformation;
using OutSmart.DAXon.Trees.Iterators;
using OutSmart.DAXon.Values;
using OutSmart.DAXon.Internal.Collections;
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Events;
using OutSmart.DAXon.Expressions.Elaboration;
using OutSmart.DAXon.Expressions.Parsing;
using OutSmart.DAXon.Model;
using OutSmart.DAXon.Types;
using OutSmart.DAXon.Internal;
namespace OutSmart.DAXon.Expressions.Instructions
{
    public class Choose : Instruction
    {
        public static readonly OperandRole CHOICE_ACTION = new OperandRole(OperandRole.IN_CHOICE_GROUP, OperandUsage.TRANSMISSION, SequenceType.ANY_SEQUENCE);
        private readonly Operand[] conditionOps;
        private readonly Operand[] actionOps;
        private bool _isInstruction;

        public int Count { get { return Size(); } }

        public override int InstructionNameCode => Size() == 1 ? StandardNames.XSL_IF : StandardNames.XSL_CHOOSE;

        public override int ImplementationMethod
        {
            get
            {
                int m = Expression.PROCESS_METHOD | Expression.ITERATE_METHOD | Expression.WATCH_METHOD;
                if (!Cardinality.AllowsMany(GetCardinality()))
                {
                    m |= Expression.EVALUATE_METHOD;
                }

                return m;
            }
        }

        public override string ExpressionName => "choose";

        public override string StreamerName => "Choose";

        public virtual IBooleanEvaluator[] ConditionEvaluators => ((ChooseExprElaborator)MakeElaborator()).MakeConditionEvaluators(this);
        public Choose(Expression[] conditions, Expression[] actions)
        {
            conditionOps = new Operand[conditions.Length];
            for (int i = 0; i < conditions.Length; i++)
            {
                conditionOps[i] = new Operand(this, conditions[i], OperandRole.INSPECT);
            }

            actionOps = new Operand[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                actionOps[i] = new Operand(this, actions[i], CHOICE_ACTION);
            }
        }

        public static Expression MakeConditional(Expression condition, Expression thenExp, Expression elseExp)
        {
            if (Literal.IsEmptySequence(elseExp))
            {
                Expression[] conditions = new Expression[]
                {
                    condition
                };
                Expression[] actions = new Expression[]
                {
                    thenExp
                };
                return new Choose(conditions, actions);
            }
            else
            {
                Expression[] conditions = new Expression[]
                {
                    condition,
                    Literal.MakeLiteral(BooleanValue.TRUE, condition)
                };
                Expression[] actions = new Expression[]
                {
                    thenExp,
                    elseExp
                };
                return new Choose(conditions, actions);
            }
        }

        public static Expression MakeConditional(Expression condition, Expression thenExp)
        {
            Expression[] conditions = new Expression[]
            {
                condition
            };
            Expression[] actions = new Expression[]
            {
                thenExp
            };
            return new Choose(conditions, actions);
        }

        public virtual void SetInstruction(bool inst)
        {
            _isInstruction = inst;
        }

        public override bool IsInstruction()
        {
            return _isInstruction;
        }
        public virtual int Size()
        {
            return conditionOps.Length;
        }

        public static bool IsSingleBranchChoice(Expression exp)
        {
            return exp is Choose && ((Choose)exp).Count == 1;
        }

        public virtual bool IsActionOperand(Expression child)
        {
            foreach (Operand actionOp in actionOps)
            {
                if (actionOp.GetChildExpression() == child)
                {
                    return true;
                }
            }

            return false;
        }

        public virtual Expression GetCondition(int i)
        {
            return conditionOps[i].GetChildExpression();
        }

        public virtual void SetCondition(int i, Expression condition)
        {
            conditionOps[i].SetChildExpression(condition);
        }

        public virtual IEnumerable<Operand> Conditions()
        {
            return conditionOps.ToList();
        }

        public virtual Operand GetActionOperand(int i)
        {
            return actionOps[i];
        }

        public virtual Expression GetAction(int i)
        {
            return actionOps[i].GetChildExpression();
        }

        public virtual void SetAction(int i, Expression action)
        {
            actionOps[i].SetChildExpression(action);
        }

        public virtual IEnumerable<Operand> Actions()
        {
            return actionOps.ToList();
        }

        public override IEnumerable<Operand> Operands()
        {
            return conditionOps.ToList().Concat(actionOps.ToList());
        }

        public override bool AllowExtractingCommonSubexpressions()
        {
            return false;
        }

        public virtual void AtomizeActions()
        {
            for (int i = 0; i < Size(); i++)
            {
                SetAction(i, Atomizer.MakeAtomizer(GetAction(i), null));
            }
        }

        public override Expression Simplify()
        {
            for (int i = 0; i < Size(); i++)
            {
                SetCondition(i, GetCondition(i).Simplify());
                try
                {
                    SetAction(i, GetAction(i).Simplify());
                }
                catch (XPathException err)
                {

                    // mustn't throw the error unless the branch is actually selected, unless its a type error
                    if (err.IsTypeError())
                    {
                        throw err;
                    }
                    else
                    {
                        SetAction(i, new ErrorExpression(new XmlProcessingException(err)));
                    }
                }
            }

            return this;
        }

        private Expression RemoveRedundantBranches(ExpressionVisitor visitor)
        {
            Expression result = RemoveRedundantBranches0(visitor);
            if (result != this)
            {
                ExpressionTool.CopyLocationInfo(this, result);
            }

            return result;
        }

        private Expression RemoveRedundantBranches0(ExpressionVisitor visitor)
        {

            // Eliminate a redundant if (false)
            bool compress = false;
            for (int i = 0; i < Size(); i++)
            {
                Expression condition = GetCondition(i);
                if (condition is Literal)
                {
                    compress = true;
                    break;
                }
            }

            int count = Size();
            if (compress)
            {
                IList<Expression> conditions = new List<Expression>(count);
                IList<Expression> actions = new List<Expression>(count);
                for (int i = 0; i < count; i++)
                {
                    Expression condition = GetCondition(i);
                    if (!Literal.HasEffectiveBooleanValue(condition, false))
                    {
                        conditions.Add(condition);
                        actions.Add(GetAction(i));
                    }

                    if (Literal.HasEffectiveBooleanValue(condition, true))
                    {
                        break;
                    }
                }

                if (conditions.Count == 0)
                {
                    Literal lit = Literal.MakeEmptySequence();
                    ExpressionTool.CopyLocationInfo(this, lit);
                    return lit;
                }
                else if (conditions.Count == 1 && Literal.HasEffectiveBooleanValue(conditions[0], true))
                {
                    return actions[0];
                }
                else if (conditions.Count != count)
                {
                    Expression[] c = conditions.ToArray();
                    Expression[] a = actions.ToArray();
                    Choose result = new Choose(c, a);
                    result.SetRetainedStaticContext(GetRetainedStaticContext());
                    return result;
                }
            }


            // See if only condition left is: if (true) then x else ()
            if (Size() == 1 && Literal.HasEffectiveBooleanValue(GetCondition(0), true))
            {
                return GetAction(0);
            }


            // Eliminate a redundant <xsl:otherwise/> or "when (test) then ()"
            if (Literal.IsEmptySequence(GetAction(Size() - 1)))
            {
                if (Size() == 1)
                {
                    Literal lit = Literal.MakeEmptySequence();
                    ExpressionTool.CopyLocationInfo(this, lit);
                    return lit;
                }
                else
                {
                    Expression[] conditions = new Expression[count - 1];
                    Expression[] actions = new Expression[count - 1];
                    for (int i = 0; i < count - 1; i++)
                    {
                        conditions[i] = GetCondition(i);
                        actions[i] = GetAction(i);
                    }

                    return new Choose(conditions, actions);
                }
            }


            // Flatten an "else if"
            if (Literal.HasEffectiveBooleanValue(GetCondition(count - 1), true) && GetAction(count - 1) is Choose)
            {
                Choose choose2 = (Choose)GetAction(count - 1);
                int newLen = count + choose2.Count - 1;
                Expression[] c2 = new Expression[newLen];
                Expression[] a2 = new Expression[newLen];
                for (int i = 0; i < count - 1; i++)
                {
                    c2[i] = GetCondition(i);
                    a2[i] = GetAction(i);
                }

                for (int i = 0; i < choose2.Count; i++)
                {
                    c2[i + count - 1] = choose2.GetCondition(i);
                    a2[i + count - 1] = choose2.GetAction(i);
                }

                return new Choose(c2, a2);
            }


            // Rewrite "if (EXP) then true() else false()" as boolean(EXP)
            if (count == 2 && Literal.IsConstantBoolean(GetAction(0), true) && Literal.IsConstantBoolean(GetAction(1), false) && Literal.HasEffectiveBooleanValue(GetCondition(1), true))
            {
                TypeHierarchy th = visitor.GetConfiguration().GetTypeHierarchy();
                if (th.IsSubType(GetCondition(0).GetItemType(), BuiltInAtomicType.BOOLEAN) && GetCondition(0).GetCardinality() == StaticProperty.EXACTLY_ONE)
                {
                    return GetCondition(0);
                }
                else
                {
                    return SystemFunction.MakeCall("boolean", GetRetainedStaticContext(), GetCondition(0));
                }
            }

            return this;
        }

        public override Expression TypeCheck(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            TypeHierarchy th = visitor.GetConfiguration().GetTypeHierarchy();
            for (int i = 0; i < Size(); i++)
            {
                conditionOps[i].TypeCheck(visitor, contextInfo);
                XPathException err = TypeChecker.EbvError(GetCondition(i), th);
                if (err != null)
                {
                    throw err.WithLocation(GetCondition(i).GetLocation()).MaybeWithFailingExpression(GetCondition(i));
                }
            }


            // Check that each of the action branches satisfies the expected type. This is a stronger check than checking the
            // type of the top-level expression. It's important with tail recursion not to wrap a tail call in a type checking
            // expression just because a dynamic type check is needed on a different branch of the choice.
            for (int i = 0; i < Size(); i++)
            {
                if (Literal.HasEffectiveBooleanValue(GetCondition(i), false))
                {

                    // Don't do any checking if we know statically the condition will be false, because it could
                    // result in spurious warnings: bug 4537
                    continue;
                }

                try
                {
                    actionOps[i].TypeCheck(visitor, contextInfo);
                }
                catch (XPathException err)
                {
                    XPathException e2 = err.MaybeWithLocation(GetLocation()).MaybeWithFailingExpression(GetAction(i));

                    // mustn't throw the error unless the branch is actually selected, unless its a static or type error
                    if (e2.IsStaticError())
                    {
                        throw e2;
                    }
                    else if (e2.IsTypeError())
                    {

                        // if this is an "empty" else branch, don't be draconian about the error handling. It might be
                        // the user knows the otherwise branch isn't needed because one of the when branches will always
                        // be satisfied.
                        // Also, don't throw a type error if the branch will never be executed; this can happen with
                        // a typeswitch where the purpose of the condition is to test the type.
                        if (Literal.IsEmptySequence(GetAction(i)) || Literal.HasEffectiveBooleanValue(GetCondition(i), false))
                        {
                            SetAction(i, new ErrorExpression(new XmlProcessingException(e2)));
                        }
                        else
                        {
                            throw e2;
                        }
                    }
                    else
                    {
                        SetAction(i, new ErrorExpression(new XmlProcessingException(e2)));
                    }
                }

                if (Literal.HasEffectiveBooleanValue(GetCondition(i), true))
                {
                    break;
                }
            }

            Optimizer opt = visitor.ObtainOptimizer();
            if (opt.IsOptionSet(OptimizerOptions.CONSTANT_FOLDING))
            {
                Expression reduced = RemoveRedundantBranches(visitor);
                if (reduced != this)
                {
                    return reduced.TypeCheck(visitor, contextInfo);
                }

                return reduced;
            }

            return this;
        }

        public override bool ImplementsStaticTypeCheck()
        {
            return true;
        }

        public override Expression StaticTypeCheck(SequenceType req, bool backwardsCompatible, Func<RoleDiagnostic> roleSupplier, ExpressionVisitor visitor)
        {
            int count = Size();
            TypeChecker tc = GetConfiguration().GetTypeChecker(backwardsCompatible);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    SetAction(i, tc.StaticTypeCheck(GetAction(i), req, roleSupplier, visitor));
                }
                catch (XPathException err)
                {
                    if (err.IsStaticError())
                    {
                        throw err;
                    }

                    ErrorExpression ee = new ErrorExpression(new XmlProcessingException(err));
                    ExpressionTool.CopyLocationInfo(GetAction(i), ee);
                    SetAction(i, ee);
                }
            }


            // If the last condition isn't true(), then we need to consider the fall-through case, which returns
            // an empty sequence
            if (!Literal.HasEffectiveBooleanValue(GetCondition(count - 1), true) && !Cardinality.AllowsZero(req.GetCardinality()))
            {
                Expression[] c = new Expression[count + 1];
                Expression[] a = new Expression[count + 1];
                for (int i = 0; i < count; i++)
                {
                    c[i] = GetCondition(i);
                    a[i] = GetAction(i);
                }

                c[count] = Literal.MakeLiteral(BooleanValue.TRUE, this);
                string cond = count == 1 ? "The condition is not" : "None of the conditions is";
                RoleDiagnostic role = roleSupplier();
                string message = "Conditional expression: " + cond + " satisfied, so an empty sequence is returned, " + "but this is not allowed as the " + role.GetMessage();
                ErrorExpression errExp = new ErrorExpression(message, role.ErrorCode, true);
                ExpressionTool.CopyLocationInfo(this, errExp);
                a[count] = errExp;
                return new Choose(c, a);
            }

            return this;
        }

        public override Expression Optimize(ExpressionVisitor visitor, ContextItemStaticInfo contextItemType)
        {
            int count = Size();
            for (int i = 0; i < count; i++)
            {
                conditionOps[i].Optimize(visitor, contextItemType);
                Expression ebv = BooleanFn.RewriteEffectiveBooleanValue(GetCondition(i), visitor, contextItemType);
                if (ebv != null && ebv != GetCondition(i))
                {
                    SetCondition(i, ebv);
                }

                if (GetCondition(i) is Literal && !(((Literal)GetCondition(i)).GroundedValue is BooleanValue))
                {
                    bool b;
                    try
                    {
                        b = ((Literal)GetCondition(i)).GroundedValue.EffectiveBooleanValue();
                    }
                    catch (XPathException err)
                    {
                        throw err.WithLocation(GetLocation());
                    }

                    SetCondition(i, Literal.MakeLiteral(BooleanValue.Get(b), this));
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (Literal.HasEffectiveBooleanValue(GetCondition(i), false))
                {

                    // Don't bother with optimisation if the code won't be executed: bug 4537
                    continue;
                }

                try
                {
                    actionOps[i].Optimize(visitor, contextItemType);
                }
                catch (XPathException err)
                {

                    // mustn't throw the error unless the branch is actually selected, unless its a type error
                    if (err.IsTypeError() && !visitor.IsInliningFunctions())
                    {
                        throw err;
                    }
                    else
                    {
                        ErrorExpression ee = new ErrorExpression(new XmlProcessingException(err));
                        ExpressionTool.CopyLocationInfo(actionOps[i].GetChildExpression(), ee);
                        SetAction(i, ee);
                    }
                }

                if (GetAction(i) is ErrorExpression && ((ErrorExpression)GetAction(i)).IsTypeError() && !Literal.IsConstantBoolean(GetCondition(i), false) && !Literal.IsConstantBoolean(GetCondition(i), true))
                {

                    // Bug 3933: avoid the warning for an implicit xsl:otherwise branch (constant condition = true)
                    visitor.IssueWarning("Branch " + (i + 1) + " of conditional will fail with a type error if executed. " + ((ErrorExpression)GetAction(i)).GetMessage(), DAXonErrorCode.SXWN9027, GetAction(i).GetLocation());
                }

                if (Literal.HasEffectiveBooleanValue(GetCondition(i), true))
                {

                    // Don't bother with optimisation of subsequent branches if the code won't be executed: bug 4537
                    break;
                }
            }

            if (count == 0)
            {
                return Literal.MakeEmptySequence();
            }

            Optimizer opt = visitor.ObtainOptimizer();
            if (opt.IsOptionSet(OptimizerOptions.CONSTANT_FOLDING))
            {
                Expression e = RemoveRedundantBranches(visitor);
                if (e is Choose)
                {
                    return visitor.ObtainOptimizer().TrySwitch((Choose)e, visitor);
                }
                else
                {
                    return e;
                }
            }

            return this;
        }

        public override Expression Copy(RebindingMap rebindings)
        {
            int count = Size();
            Expression[] c2 = new Expression[count];
            Expression[] a2 = new Expression[count];
            for (int c = 0; c < count; c++)
            {
                c2[c] = GetCondition(c).Copy(rebindings);
                a2[c] = GetAction(c).Copy(rebindings);
            }

            Choose ch2 = new Choose(c2, a2);
            ExpressionTool.CopyLocationInfo(this, ch2);
            ch2.SetInstruction(IsInstruction());
            return ch2;
        }

        public override void CheckForUpdatingSubexpressions()
        {
            foreach (Operand o in Conditions())
            {
                Expression condition = o.GetChildExpression();
                condition.CheckForUpdatingSubexpressions();
                if (condition.IsUpdatingExpression())
                {
                    XPathException err = new XPathException("Updating expression appears in a context where it is not permitted", "XUST0001");
                    err.SetLocator(condition.GetLocation());
                    throw err;
                }
            }

            bool updating = false;
            bool nonUpdating = false;
            foreach (Operand o in Actions())
            {
                Expression act = o.GetChildExpression();
                act.CheckForUpdatingSubexpressions();
                if (ExpressionTool.IsNotAllowedInUpdatingContext(act))
                {
                    if (updating)
                    {
                        XPathException err = new XPathException("If any branch of a conditional is an updating expression, then all must be updating expressions (or vacuous)", "XUST0001");
                        err.SetLocator(act.GetLocation());
                        throw err;
                    }

                    nonUpdating = true;
                }

                if (act.IsUpdatingExpression())
                {
                    if (nonUpdating)
                    {
                        XPathException err = new XPathException("If any branch of a conditional is an updating expression, then all must be updating expressions (or vacuous)", "XUST0001");
                        err.SetLocator(act.GetLocation());
                        throw err;
                    }

                    updating = true;
                }
            }
        }

        public override bool IsUpdatingExpression()
        {
            foreach (Operand o in Actions())
            {
                if (o.GetChildExpression().IsUpdatingExpression())
                {
                    return true;
                }
            }

            return false;
        }

        public override bool IsVacuousExpression()
        {

            // The Choose is vacuous if all branches are vacuous
            foreach (Operand action in Actions())
            {
                if (!action.GetChildExpression().IsVacuousExpression())
                {
                    return false;
                }
            }

            return true;
        }

        public override int MarkTailFunctionCalls(StructuredQName qName, int arity)
        {
            int result = UserFunctionCall.NOT_TAIL_CALL;
            foreach (Operand action in Actions())
            {
                result = Math.Max(result, action.GetChildExpression().MarkTailFunctionCalls(qName, arity));
            }

            return result;
        }

        public override ItemType GetItemType()
        {
            TypeHierarchy th = GetConfiguration().GetTypeHierarchy();
            ItemType type = GetAction(0).GetItemType();
            for (int i = 1; i < Size(); i++)
            {
                type = Types.Type.GetCommonSuperType(type, GetAction(i).GetItemType(), th);
            }

            return type;
        }

        public override UType GetStaticUType(UType contextItemType)
        {
            if (IsInstruction())
            {
                return base.GetStaticUType(contextItemType);
            }
            else
            {
                UType type = GetAction(0).GetStaticUType(contextItemType);
                for (int i = 1; i < Size(); i++)
                {
                    type = type.Union(GetAction(i).GetStaticUType(contextItemType));
                }

                return type;
            }
        }

        protected override int ComputeCardinality()
        {
            int card = 0;
            bool includesTrue = false;
            for (int i = 0; i < Size(); i++)
            {
                card = Cardinality.Union(card, GetAction(i).GetCardinality());
                if (Literal.HasEffectiveBooleanValue(GetCondition(i), true))
                {
                    includesTrue = true;
                }
            }

            if (!includesTrue)
            {

                // we may drop off the end and return an empty sequence (typical for xsl:if)
                card = Cardinality.Union(card, StaticProperty.ALLOWS_ZERO);
            }

            return card;
        }

        protected override int ComputeSpecialProperties()
        {

            // The special properties of a conditional are those which are common to every branch of the conditional
            int props = GetAction(0).GetSpecialProperties();
            for (int i = 1; i < Size(); i++)
            {
                props &= GetAction(i).GetSpecialProperties();
            }

            return props;
        }

        public override bool MayCreateNewNodes()
        {
            foreach (Operand action in Actions())
            {
                int props = action.GetChildExpression().GetSpecialProperties();
                if ((props & StaticProperty.NO_NODES_NEWLY_CREATED) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public override Expression Unordered(bool retainAllNodes, bool forStreaming)
        {
            for (int i = 0; i < Size(); i++)
            {
                SetAction(i, GetAction(i).Unordered(retainAllNodes, forStreaming));
            }

            return this;
        }

        public override void CheckPermittedContents(ISchemaType parentType, bool whole)
        {
            foreach (Operand action in Actions())
            {
                action.GetChildExpression().CheckPermittedContents(parentType, whole);
            }
        }

        public override PathMap.PathMapNodeSet AddToPathMap(PathMap pathMap, PathMap.PathMapNodeSet pathMapNodeSet)
        {

            // expressions used in a condition contribute paths, but these do not contribute to the result
            foreach (Operand condition in Conditions())
            {
                condition.GetChildExpression().AddToPathMap(pathMap, pathMapNodeSet);
            }

            PathMap.PathMapNodeSet result = new PathMap.PathMapNodeSet();
            foreach (Operand action in Actions())
            {
                PathMap.PathMapNodeSet temp = action.GetChildExpression().AddToPathMap(pathMap, pathMapNodeSet);
                result.AddNodeSet(temp);
            }

            return result;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("if (");
            for (int i = 0; i < Size(); i++)
            {
                sb.Append(GetCondition(i).ToString());
                sb.Append(") then (");
                sb.Append(GetAction(i).ToString());
                if (i == Size() - 1)
                {
                    sb.Append(')');
                }
                else
                {
                    sb.Append(") else if (");
                }
            }

            return sb.ToString();
        }

        public override string ToShortString()
        {
            return "if(" + GetCondition(0).ToShortString() + ") then ... else ...";
        }

        public override void Export(ExpressionPresenter @out)
        {
            @out.StartElement("choose", this);
            for (int i = 0; i < Size(); i++)
            {
                GetCondition(i).Export(@out);
                GetAction(i).Export(@out);
            }

            @out.EndElement();
        }

        public override IItem EvaluateItem(IXPathContext context)
        {
            return MakeElaborator().ElaborateForItem().Eval(context);
        }

        public override ISequenceIterator Iterate(IXPathContext context)
        {
            return MakeElaborator().ElaborateForPull().Iterate(context);
        }

        public override Elaborator GetElaborator()
        {
            return new ChooseExprElaborator();
        }

        internal class ChooseExprElaborator : PullElaborator
        {
            private IBooleanEvaluator[] conditions;
            private readonly object conditionsLock = new object();

            public virtual IBooleanEvaluator[] MakeConditionEvaluators(Choose expr)
            {
                lock (conditionsLock)
                {
                    if (conditions == null)
                    {
                        conditions = new IBooleanEvaluator[expr.Count];
                        for (int i = 0; i < expr.Count; i++)
                        {
                            conditions[i] = expr.GetCondition(i).MakeElaborator().ElaborateForBoolean();
                        }
                    }

                    return conditions;
                }
            }

            public override ISequenceEvaluator Eagerly()
            {
                Choose expr = (Choose)GetExpression();
                int count = expr.Count;
                MakeConditionEvaluators(expr);
                ISequenceEvaluator[] actions = new ISequenceEvaluator[count];
                for (int i = 0; i < count; i++)
                {
                    actions[i] = expr.GetAction(i).MakeElaborator().Eagerly();
                }

                return new EagerChooseEvaluator(conditions, actions);
            }

            public override IPullEvaluator ElaborateForPull()
            {
                Choose expr = (Choose)GetExpression();
                int count = expr.Count;
                IPullEvaluator[] actions = new IPullEvaluator[count];
                MakeConditionEvaluators(expr);
                for (int i = 0; i < count; i++)
                {
                    actions[i] = expr.GetAction(i).MakeElaborator().ElaborateForPull();
                }

                ChoosePull pull = new ChoosePull(conditions, actions);
                switch (count)
                {
                    case 1: return pull.Eval1;
                    case 2: return pull.Eval2;
                    case 3: return pull.Eval3;
                    case 4: return pull.Eval4;
                    default: return pull.EvalN;
                }
            }

            // Named rather than lambdas so AggressiveOptimization can be attached: an if/then/else
            // is a Choose, so this frame sits in the per-level cycle of user-function recursion,
            // where a tier-0 frame costs recursion depth on .NET 10. The unrolled arities are kept
            // exactly as they were - the class is frame-neutral on its own, the attribute is not.
            private sealed class ChoosePull
            {
                private readonly IBooleanEvaluator[] conditions;
                private readonly IPullEvaluator[] actions;

                public ChoosePull(IBooleanEvaluator[] conditions, IPullEvaluator[] actions)
                {
                    this.conditions = conditions;
                    this.actions = actions;
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public ISequenceIterator Eval1(IXPathContext context)
                {
                    if (conditions[0].Eval(context))
                        return actions[0].Iterate(context);
                    return EmptyIterator.GetInstance();
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public ISequenceIterator Eval2(IXPathContext context)
                {
                    if (conditions[0].Eval(context))
                        return actions[0].Iterate(context);
                    if (conditions[1].Eval(context))
                        return actions[1].Iterate(context);
                    return EmptyIterator.GetInstance();
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public ISequenceIterator Eval3(IXPathContext context)
                {
                    if (conditions[0].Eval(context))
                        return actions[0].Iterate(context);
                    if (conditions[1].Eval(context))
                        return actions[1].Iterate(context);
                    if (conditions[2].Eval(context))
                        return actions[2].Iterate(context);
                    return EmptyIterator.GetInstance();
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public ISequenceIterator Eval4(IXPathContext context)
                {
                    if (conditions[0].Eval(context))
                        return actions[0].Iterate(context);
                    if (conditions[1].Eval(context))
                        return actions[1].Iterate(context);
                    if (conditions[2].Eval(context))
                        return actions[2].Iterate(context);
                    if (conditions[3].Eval(context))
                        return actions[3].Iterate(context);
                    return EmptyIterator.GetInstance();
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public ISequenceIterator EvalN(IXPathContext context)
                {
                    for (int i = 0; i < conditions.Length; i++)
                    {
                        if (conditions[i].Eval(context))
                        {
                            return actions[i].Iterate(context);
                        }
                    }

                    return EmptyIterator.GetInstance();
                }
            }

            public override IItemEvaluator ElaborateForItem()
            {
                Choose expr = (Choose)GetExpression();
                int count = expr.Count;
                IItemEvaluator[] actions = new IItemEvaluator[count];
                MakeConditionEvaluators(expr);
                for (int i = 0; i < count; i++)
                {
                    actions[i] = expr.GetAction(i).MakeElaborator().ElaborateForItem();
                }

                switch (count)
                {
                    case 1:
                        return (context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].Eval(context);
                            return null;
                        };
                    case 2:
                        return (context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].Eval(context);
                            if (conditions[1].Eval(context))
                                return actions[1].Eval(context);
                            return null;
                        };
                    case 3:
                        return (context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].Eval(context);
                            if (conditions[1].Eval(context))
                                return actions[1].Eval(context);
                            if (conditions[2].Eval(context))
                                return actions[2].Eval(context);
                            return null;
                        };
                    case 4:
                        return (context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].Eval(context);
                            if (conditions[1].Eval(context))
                                return actions[1].Eval(context);
                            if (conditions[2].Eval(context))
                                return actions[2].Eval(context);
                            if (conditions[3].Eval(context))
                                return actions[3].Eval(context);
                            return null;
                        };
                    default:
                        return (context) =>
                        {
                            for (int i = 0; i < count; i++)
                            {
                                if (conditions[i].Eval(context))
                                {
                                    return actions[i].Eval(context);
                                }
                            }

                            return null;
                        };
                }
            }

            public override IPushEvaluator ElaborateForPush()
            {
                Choose expr = (Choose)GetExpression();
                int count = expr.Count;
                MakeConditionEvaluators(expr);
                IPushEvaluator[] actions = new IPushEvaluator[count];
                for (int i = 0; i < count; i++)
                {
                    actions[i] = expr.GetAction(i).MakeElaborator().ElaborateForPush();
                }

                switch (count)
                {
                    case 1:
                        return (output, context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].ProcessLeavingTail(output, context);
                            return null;
                        };
                    case 2:
                        return (output, context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].ProcessLeavingTail(output, context);
                            if (conditions[1].Eval(context))
                                return actions[1].ProcessLeavingTail(output, context);
                            return null;
                        };
                    case 3:
                        return (output, context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].ProcessLeavingTail(output, context);
                            if (conditions[1].Eval(context))
                                return actions[1].ProcessLeavingTail(output, context);
                            if (conditions[2].Eval(context))
                                return actions[2].ProcessLeavingTail(output, context);
                            return null;
                        };
                    case 4:
                        return (output, context) =>
                        {
                            if (conditions[0].Eval(context))
                                return actions[0].ProcessLeavingTail(output, context);
                            if (conditions[1].Eval(context))
                                return actions[1].ProcessLeavingTail(output, context);
                            if (conditions[2].Eval(context))
                                return actions[2].ProcessLeavingTail(output, context);
                            if (conditions[3].Eval(context))
                                return actions[3].ProcessLeavingTail(output, context);
                            return null;
                        };
                    default:
                        return (output, context) =>
                        {
                            for (int i = 0; i < count; i++)
                            {
                                if (conditions[i].Eval(context))
                                {
                                    return actions[i].ProcessLeavingTail(output, context);
                                }
                            }

                            return null;
                        };
                }
            }

            public override IUpdateEvaluator ElaborateForUpdate()
            {
                Choose expr = (Choose)GetExpression();
                int count = expr.Count;
                MakeConditionEvaluators(expr);
                IUpdateEvaluator[] actions = new IUpdateEvaluator[count];
                for (int i = 0; i < count; i++)
                {
                    actions[i] = expr.GetAction(i).MakeElaborator().ElaborateForUpdate();
                }

                return (context, updates) =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (conditions[i].Eval(context))
                        {
                            actions[i].RegisterUpdates(context, updates);
                            break;
                        }
                    }
                };
            }
        }

        private class EagerChooseEvaluator : ISequenceEvaluator
        {
            private readonly IBooleanEvaluator[] conditions;
            private readonly ISequenceEvaluator[] actions;
            private readonly int count;
            public EagerChooseEvaluator(IBooleanEvaluator[] conditions, ISequenceEvaluator[] actions)
            {
                this.conditions = conditions;
                this.actions = actions;
                this.count = conditions.Length;
            }

#if NET
            // .NET 10 tier-0 frames are ~2.25x the optimised size and this method sits in a
            // per-level recursion cycle, where that costs depth. Measured per site: the attribute
            // is not a blanket win, so it is applied only where it pays.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
            public virtual ISequence Evaluate(IXPathContext context)
            {
                for (int i = 0; i < count; i++)
                {
                    if (conditions[i].Eval(context))
                    {
                        return actions[i].Evaluate(context);
                    }
                }

                return EmptySequence.GetInstance();
            }
        }
    }
}