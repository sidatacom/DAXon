////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Expressions.Compatibility;
using OutSmart.DAXon.Expressions.Elaboration;
using OutSmart.DAXon.Expressions;
using OutSmart.DAXon.Model;
using OutSmart.DAXon.Tracing;
using OutSmart.DAXon.Transformation;
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Expressions.Instructions;
using OutSmart.DAXon.Expressions.Parsing;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Types;
using OutSmart.DAXon.Values;
using OutSmart.DAXon.Internal;
using OutSmart.DAXon.Internal.Collections;
namespace OutSmart.DAXon.Expressions
{
    internal class ArithmeticExpression : BinaryExpression
    {
        protected Calculator calculator;
        private IPlainType itemType;

        public override string ExpressionName => "arithmetic";

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override IntegerValue[] IntegerBounds
        {
            get
            {
                IntegerValue[] bounds0 = GetLhsExpression().IntegerBounds;
                IntegerValue[] bounds1 = GetRhsExpression().IntegerBounds;
                if (bounds0 == null || bounds1 == null)
                {
                    return null;
                }
                else
                {
                    switch (@operator)
                    {
                        case Token.PLUS:
                            return new IntegerValue[]
                            {
                            bounds0[0].Plus(bounds1[0]),
                            bounds0[1].Plus(bounds1[1])
                            };
                        case Token.MINUS:
                            return new IntegerValue[]
                            {
                            bounds0[0].Minus(bounds1[1]),
                            bounds0[1].Minus(bounds1[0])
                            };
                        case Token.MULT:
                            if (GetRhsExpression() is Literal)
                            {
                                IntegerValue val1 = bounds1[0];
                                if (val1.Signum() > 0)
                                {
                                    return new IntegerValue[]
                                    {
                                    bounds0[0].Times(val1),
                                    bounds0[1].Times(val1)
                                    };
                                }
                                else
                                {
                                    return null;
                                }
                            }
                            else if (GetLhsExpression() is Literal)
                            {
                                IntegerValue val0 = bounds1[0];
                                if (val0.Signum() > 0)
                                {
                                    return new IntegerValue[]
                                    {
                                    bounds1[0].Times(val0),
                                    bounds1[1].Times(val0)
                                    };
                                }
                                else
                                {
                                    return null;
                                }
                            }

                            return null;
                        case Token.DIV:
                        case Token.IDIV:
                            if (GetRhsExpression() is Literal)
                            {
                                IntegerValue val1 = bounds1[0];
                                if (val1.Signum() > 0)
                                {
                                    try
                                    {
                                        return new IntegerValue[]
                                        {
                                        bounds0[0].Idiv(val1),
                                        bounds0[1].Idiv(val1)
                                        };
                                    }
                                    catch (XPathException e)
                                    {
                                        return null;
                                    }
                                }
                            }

                            return null;
                        default:
                            return null;
                    }
                }
            }
        }
        public ArithmeticExpression(Expression p0, int @operator, Expression p1) : base(p0, @operator, p1)
        {
        }

        protected override int ComputeSpecialProperties()
        {
            int p = base.ComputeSpecialProperties();
            return p | StaticProperty.NOT_UNTYPED_ATOMIC;
        }

        public virtual void SetCalculator(Calculator calculator)
        {
            this.calculator = calculator;
        }

        public virtual Calculator GetCalculator()
        {
            return calculator;
        }

        public override Expression TypeCheck(ExpressionVisitor visitor, ContextItemStaticInfo contextInfo)
        {
            ResetLocalStaticProperties();
            Lhs.TypeCheck(visitor, contextInfo);
            Rhs.TypeCheck(visitor, contextInfo);
            Configuration config = visitor.GetConfiguration();
            TypeHierarchy th = config.GetTypeHierarchy();
            TypeChecker tc = config.GetTypeChecker(false);
            Expression oldOp0 = GetLhsExpression();
            Expression oldOp1 = GetRhsExpression();
            SequenceType atomicType = SequenceType.OPTIONAL_ATOMIC;
            Func<RoleDiagnostic> role0 = () => new RoleDiagnostic(RoleDiagnostic.BINARY_EXPR, Token.tokens[@operator], 0);

            SetLhsExpression(tc.StaticTypeCheck(GetLhsExpression(), atomicType, role0, visitor));
            ItemType itemType0 = GetLhsExpression().GetItemType();
            if (itemType0 is ErrorType)
            {
                return Literal.MakeEmptySequence();
            }

            IAtomicType type0 = itemType0.GetPrimitiveItemType() as IAtomicType;
            if (type0 == null)
            {
                throw new XPathException("Arithmetic operator is not defined for a non-atomic operand of type " + itemType0.ToString(), "XPTY0004");
            }
            if (type0.Fingerprint == StandardNames.XS_UNTYPED_ATOMIC)
            {
                SetLhsExpression(UntypedSequenceConverter.MakeUntypedSequenceConverter(config, GetLhsExpression(), BuiltInAtomicType.DOUBLE));
                type0 = BuiltInAtomicType.DOUBLE;
            } /*!(operand0 instanceof UntypedAtomicConverter)*/
            else if ((GetLhsExpression().GetSpecialProperties() & StaticProperty.NOT_UNTYPED_ATOMIC) == 0 && th.Relationship(type0, BuiltInAtomicType.UNTYPED_ATOMIC) != Affinity.DISJOINT)
            {
                SetLhsExpression(UntypedSequenceConverter.MakeUntypedSequenceConverter(config, GetLhsExpression(), BuiltInAtomicType.DOUBLE));
                type0 = (IAtomicType)GetLhsExpression().GetItemType().GetPrimitiveItemType();
            }


            Func<RoleDiagnostic> role1 = () => new RoleDiagnostic(RoleDiagnostic.BINARY_EXPR, Token.tokens[@operator], 1);
            SetRhsExpression(tc.StaticTypeCheck(GetRhsExpression(), atomicType, role1, visitor));
            ItemType itemType1 = GetRhsExpression().GetItemType();
            if (itemType1 is ErrorType)
            {
                return Literal.MakeEmptySequence();
            }

            IAtomicType type1 = itemType1.GetPrimitiveItemType() as IAtomicType;
            if (type1 == null)
            {
                throw new XPathException("Arithmetic operator is not defined for a non-atomic operand of type " + itemType1.ToString(), "XPTY0004");
            }
            if (type1.Fingerprint == StandardNames.XS_UNTYPED_ATOMIC)
            {
                SetRhsExpression(UntypedSequenceConverter.MakeUntypedSequenceConverter(config, GetRhsExpression(), BuiltInAtomicType.DOUBLE));
                type1 = BuiltInAtomicType.DOUBLE;
            } /*!(operand1 instanceof UntypedAtomicConverter) &&*/
            else if ((GetRhsExpression().GetSpecialProperties() & StaticProperty.NOT_UNTYPED_ATOMIC) == 0 && th.Relationship(type1, BuiltInAtomicType.UNTYPED_ATOMIC) != Affinity.DISJOINT)
            {
                SetRhsExpression(UntypedSequenceConverter.MakeUntypedSequenceConverter(config, GetRhsExpression(), BuiltInAtomicType.DOUBLE));
                type1 = (IAtomicType)GetRhsExpression().GetItemType().GetPrimitiveItemType();
            }

            if (itemType0.GetUType().Union(itemType1.GetUType()).Overlaps(UType.EXTENSION))
            {
                throw new XPathException("Arithmetic operators are not defined for external objects").WithLocation(GetLocation()).WithErrorCode("XPTY0004");
            }

            if (GetLhsExpression() != oldOp0)
            {
                AdoptChildExpression(GetLhsExpression());
            }

            if (GetRhsExpression() != oldOp1)
            {
                AdoptChildExpression(GetRhsExpression());
            }

            if (Literal.IsEmptySequence(GetLhsExpression()) || Literal.IsEmptySequence(GetRhsExpression()))
            {
                return Literal.MakeEmptySequence();
            }

            if (@operator == Token.NEGATE)
            {
                if (GetRhsExpression() is Literal && ((Literal)GetRhsExpression()).GroundedValue is NumericValue)
                {
                    NumericValue nv = (NumericValue)((Literal)GetRhsExpression()).GroundedValue;
                    return Literal.MakeLiteral(nv.Negate(), this);
                }
                else
                {
                    NegateExpression ne = new NegateExpression(GetRhsExpression());
                    ne.SetBackwardsCompatible(false);
                    return ne.TypeCheck(visitor, contextInfo);
                }
            }


            // Get a calculator to implement the arithmetic operation. If the types are not yet specifically known,
            // we allow this to return an "ANY" calculator which defers the decision. However, we only allow this if
            // at least one of the operand types is AnyAtomicType or (otherwise unspecified) numeric.
            bool mustResolve = !(type0.Equals(BuiltInAtomicType.ANY_ATOMIC) || type1.Equals(BuiltInAtomicType.ANY_ATOMIC) || type0.Equals(NumericType.GetInstance()) || type1.Equals(NumericType.GetInstance()));
            calculator = Calculator.GetCalculator(type0.Fingerprint, type1.Fingerprint, MapOpCode(@operator), mustResolve);
            if (calculator == null)
            {
                throw new XPathException("Arithmetic operator is not defined for arguments of types (" + type0.Description + ", " + type1.Description + ")").WithLocation(GetLocation()).AsTypeError().WithErrorCode("XPTY0004");
            }


            // If the calculator is going to promote arguments to xs:double, then promote any literal arguments now.
            // (Could generalize this, but this is the common case)
            if (calculator is Calculator.IDoubleOpDouble)
            {
                if (GetLhsExpression() is Literal && !type0.Equals(BuiltInAtomicType.DOUBLE))
                {
                    IGroundedValue value = ((Literal)GetLhsExpression()).GroundedValue;
                    if (value is NumericValue)
                    {
                        SetLhsExpression(Literal.MakeLiteral(new DoubleValue(((NumericValue)value).GetDoubleValue()), this));
                    }
                }

                if (GetRhsExpression() is Literal && !type1.Equals(BuiltInAtomicType.DOUBLE))
                {
                    IGroundedValue value = ((Literal)GetRhsExpression()).GroundedValue;
                    if (value is NumericValue)
                    {
                        SetRhsExpression(Literal.MakeLiteral(new DoubleValue(((NumericValue)value).GetDoubleValue()), this));
                    }
                }
            }

            try
            {
                if ((GetLhsExpression() is Literal) && (GetRhsExpression() is Literal))
                {
                    return Literal.MakeLiteral(EvaluateItem(visitor.StaticContext.MakeEarlyEvaluationContext()).Materialize(), this);
                }
            }
            catch (XPathException err)
            {
            }

            return this;
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override Expression Copy(RebindingMap rebindings)
        {
            ArithmeticExpression ae = new ArithmeticExpression(GetLhsExpression().Copy(rebindings), @operator, GetRhsExpression().Copy(rebindings));
            ExpressionTool.CopyLocationInfo(this, ae);
            ae.calculator = calculator;
            return ae;
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public static AtomicValue Compute(AtomicValue value0, int @operator, AtomicValue value1, IXPathContext context)
        {
            int p0 = value0.PrimitiveType.Fingerprint;
            int p1 = value1.PrimitiveType.Fingerprint;
            Calculator calculator = Calculator.GetCalculator(p0, p1, @operator, false);
            return calculator.Compute(value0, value1, context);
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public static int MapOpCode(int op)
        {
            switch (op)
            {
                case Token.PLUS:
                    return Calculator.PLUS;
                case Token.MINUS:
                case Token.NEGATE:
                    return Calculator.MINUS;
                case Token.MULT:
                    return Calculator.TIMES;
                case Token.DIV:
                    return Calculator.DIV;
                case Token.IDIV:
                    return Calculator.IDIV;
                case Token.MOD:
                    return Calculator.MOD;
                default:
                    throw new ArgumentException();
            }
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override ItemType GetItemType()
        {
            if (itemType != null)
            {
                return itemType;
            }

            if (calculator == null)
            {
                return BuiltInAtomicType.ANY_ATOMIC; // type is not known statically
            }
            else
            {
                ItemType t1 = GetLhsExpression().GetItemType();
                if (!(t1 is IAtomicType))
                {
                    t1 = t1.GetAtomizedItemType();
                }

                ItemType t2 = GetRhsExpression().GetItemType();
                if (!(t2 is IAtomicType))
                {
                    t2 = t2.GetAtomizedItemType();
                }

                IPlainType resultType = calculator.GetResultType((IAtomicType)t1.GetPrimitiveItemType(), (IAtomicType)t2.GetPrimitiveItemType());
                if (resultType.Equals(BuiltInAtomicType.ANY_ATOMIC))
                {

                    // there are a few special cases where we can do better. For example, given X+1, where the type of X
                    // is unknown, we can still infer that the result is numeric. (Not so for X*2, however, where it could
                    // be a duration)
                    TypeHierarchy th = GetConfiguration().GetTypeHierarchy();
                    if ((@operator == Token.PLUS || @operator == Token.MINUS) && (NumericType.IsNumericType(t2) || NumericType.IsNumericType(t1)))
                    {
                        resultType = NumericType.GetInstance();
                    }
                }

                return itemType = resultType;
            }
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override UType GetStaticUType(UType contextItemType)
        {

            // The rationale for this @is in the XSLT 3.0 spec
            if (ParentExpression is FilterExpression && ((FilterExpression)ParentExpression).GetRhsExpression() == this)
            {
                return UType.NUMERIC;
            }
            else if (@operator == Token.NEGATE)
            {
                return UType.NUMERIC;
            }
            else
            {
                return UType.ANY_ATOMIC;
            }
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override void ResetLocalStaticProperties()
        {
            base.ResetLocalStaticProperties();
            itemType = null;
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override IItem EvaluateItem(IXPathContext context)
        {
            return (AtomicValue)MakeElaborator().ElaborateForItem().Eval(context);
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        protected override string Tag()
        {
            return "arith";
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        protected override void ExplainExtraAttributes(ExpressionPresenter @out)
        {
            if (calculator != null)
            {

                // May be null during optimizer tracing
                @out.EmitAttribute("calc", calculator.Code());
            }
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        public override Elaborator GetElaborator()
        {
            return new ArithmeticElaborator();
        }

        /*!(operand0 instanceof UntypedAtomicConverter)*/
        /*!(operand1 instanceof UntypedAtomicConverter) &&*/
        /// <summary>
        /// Elaborator for an ArithmeticExpression (for example P + Q)
        /// </summary>
        internal class ArithmeticElaborator : ItemElaborator
        {
            public override IItemEvaluator ElaborateForItem()
            {
                ArithmeticExpression exp = (ArithmeticExpression)GetExpression();
                IItemEvaluator arg0Eval = exp.GetLhsExpression().MakeElaborator().ElaborateForItem();
                IItemEvaluator arg1Eval = exp.GetRhsExpression().MakeElaborator().ElaborateForItem();

                // Allow the null checks to be skipped if not needed
                bool nullable0 = Cardinality.AllowsZero(exp.GetLhsExpression().GetCardinality());
                bool nullable1 = Cardinality.AllowsZero(exp.GetRhsExpression().GetCardinality());
                Calculator calc = exp.GetCalculator();
                if (nullable0 || nullable1)
                {
                    return (context) =>
                    {
                        AtomicValue v0 = (AtomicValue)arg0Eval.Eval(context);
                        if (v0 == null)
                        {
                            return null;
                        }

                        AtomicValue v1 = (AtomicValue)arg1Eval.Eval(context);
                        if (v1 == null)
                        {
                            return null;
                        }

                        try
                        {
                            return calc.Compute(v0, v1, context);
                        }
                        catch (XPathException e)
                        {
                            throw e.MaybeWithLocation(exp.GetLocation()).MaybeWithContext(context);
                        }
                    };
                }
                else if (calc is Calculator.DoublePlusDouble && exp.GetRhsExpression() is Literal)
                {

                    // Fast path for common case such as $x + 1
                    double addend = ((NumericValue)((Literal)exp.GetRhsExpression()).GroundedValue).GetDoubleValue();
                    return (context) => new DoubleValue(((NumericValue)arg0Eval.Eval(context)).GetDoubleValue() + addend);
                }
                else
                {
                    return new TotalArith(arg0Eval, arg1Eval, calc, exp).Eval;
                }
            }

            // Named rather than a lambda so the attribute below can be attached: this frame sits
            // in the per-level cycle of user-function recursion, where a tier-0 frame costs
            // recursion depth on .NET 10. The class itself is frame-neutral; the attribute is not.
            private sealed class TotalArith
            {
                private readonly IItemEvaluator arg0Eval;
                private readonly IItemEvaluator arg1Eval;
                private readonly Calculator calc;
                private readonly ArithmeticExpression exp;

                public TotalArith(IItemEvaluator arg0Eval, IItemEvaluator arg1Eval, Calculator calc, ArithmeticExpression exp)
                {
                    this.arg0Eval = arg0Eval;
                    this.arg1Eval = arg1Eval;
                    this.calc = calc;
                    this.exp = exp;
                }

#if NET
                [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
                public IItem Eval(IXPathContext context)
                {
                    AtomicValue v0 = (AtomicValue)arg0Eval.Eval(context);
                    AtomicValue v1 = (AtomicValue)arg1Eval.Eval(context);
                    try
                    {
                        return calc.Compute(v0, v1, context);
                    }
                    catch (XPathException e)
                    {
                        throw e.MaybeWithLocation(exp.GetLocation()).MaybeWithContext(context);
                    }
                }
            }
        }
    }
}
