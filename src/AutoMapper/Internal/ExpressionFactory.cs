using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AutoMapper.Internal
{
    using static Expression;
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class ExpressionFactory
    {
        public static readonly MethodInfo ObjectToString = typeof(object).GetMethod("ToString");
        private static readonly MethodInfo DisposeMethod = typeof(IDisposable).GetMethod("Dispose");
        public static readonly Expression False = Constant(false);
        public static readonly Expression True = Constant(true);
        public static readonly Expression Null = Constant(null);
        public static readonly Expression Empty = Empty();
        public static bool IsQuery(this Expression expression) => expression is MethodCallExpression { Method: { IsStatic: true } method } && method.DeclaringType == typeof(Enumerable);
        public static Expression Chain(this IEnumerable<Expression> expressions, Expression parameter) => expressions.Aggregate(parameter,
            (left, right) => right is LambdaExpression lambda ? lambda.ReplaceParameters(left) : right.Replace(right.GetChain().FirstOrDefault().Target, left));
        public static LambdaExpression Lambda(this MemberInfo member) => new[] { member }.Lambda();
        public static LambdaExpression Lambda(this IReadOnlyCollection<MemberInfo> members)
        {
            var source = Parameter(members.First().DeclaringType, "source");
            return Expression.Lambda(members.Chain(source), source);
        }
        public static Expression Chain(this IEnumerable<MemberInfo> members, Expression target)
        {
            foreach (var member in members)
            {
                target = member switch
                {
                    PropertyInfo property => Property(target, property),
                    MethodInfo { IsStatic: true } getter => Call(getter, target),
                    FieldInfo field => Field(target, field),
                    MethodInfo getter => Call(target, getter),
                    _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unexpected member.")
                };
            }
            return target;
        }
        public static IEnumerable<MemberInfo> GetMembersChain(this LambdaExpression lambda) => lambda.Body.GetMembersChain();
        public static MemberInfo GetMember(this LambdaExpression lambda) =>
            (lambda?.Body is MemberExpression memberExpression && memberExpression.Expression == lambda.Parameters[0]) ? memberExpression.Member : null;
        public static IEnumerable<MemberInfo> GetMembersChain(this Expression expression) => expression.GetChain().Select(m => m.MemberInfo);
        public static Stack<Member> GetChain(this Expression expression)
        {
            var stack = new Stack<Member>();
            while (expression != null)
            {
                var member = expression switch
                {
                    MemberExpression{ Expression: Expression target, Member: MemberInfo propertyOrField } => 
                        new Member(expression, propertyOrField, target),
                    MethodCallExpression { Method: var instanceMethod, Object: Expression target } =>
                        new Member(expression, instanceMethod, target),
                    MethodCallExpression { Method: var extensionMethod, Arguments: { Count: >0 } arguments } when extensionMethod.Has<ExtensionAttribute>() => 
                        new Member(expression, extensionMethod, arguments[0]),
                    _ => default
                };
                if (member.Expression == null)
                {
                    break;
                }
                stack.Push(member);
                expression = member.Target;
            }
            return stack;
        }
        public readonly struct Member
        {
            public Member(Expression expression, MemberInfo memberInfo, Expression target)
            {
                Expression = expression;
                MemberInfo = memberInfo;
                Target = target;
            }
            public Expression Expression { get; }
            public MemberInfo MemberInfo { get; }
            public Expression Target { get; }
        }
        public static IEnumerable<MemberExpression> GetMemberExpressions(this Expression expression)
        {
            if (expression is not MemberExpression memberExpression)
            {
                return Array.Empty<MemberExpression>();
            }
            return expression.GetChain().Select(m => m.Expression as MemberExpression).TakeWhile(m => m != null);
        }
        public static void EnsureMemberPath(this LambdaExpression exp, string name)
        {
            if (!exp.IsMemberPath())
            {
                throw new ArgumentOutOfRangeException(name, "Only member accesses are allowed. " + exp);
            }
        }
        public static bool IsMemberPath(this LambdaExpression lambda)
        {
            Expression currentExpression = null;
            foreach (var member in lambda.Body.GetChain())
            {
                currentExpression = member.Expression;
                if (!(currentExpression is MemberExpression))
                {
                    return false;
                }
            }
            return currentExpression == lambda.Body;
        }
        public static LambdaExpression MemberAccessLambda(Type type, string memberPath) =>
            ReflectionHelper.GetMemberPath(type, memberPath).Lambda();
        public static MethodInfo Method<T>(Expression<Func<T>> expression) => ((MethodCallExpression)expression.Body).Method;
        public static Expression ForEach(Expression collection, ParameterExpression loopVar, Expression loopContent)
        {
            if (collection.Type.IsArray)
            {
                return ForEachArrayItem(collection, arrayItem => Block(new[] { loopVar }, Assign(loopVar, arrayItem), loopContent));
            }
            var getEnumerator = collection.Type.GetInheritedMethod("GetEnumerator");
            var getEnumeratorCall = Call(collection, getEnumerator);
            var enumeratorType = getEnumeratorCall.Type;
            var enumeratorVar = Variable(enumeratorType, "enumerator");
            var enumeratorAssign = Assign(enumeratorVar, getEnumeratorCall);

            var moveNext = enumeratorType.GetInheritedMethod("MoveNext");
            var moveNextCall = Call(enumeratorVar, moveNext);

            var breakLabel = Label("LoopBreak");

            var loop = Block(new[] { enumeratorVar },
                enumeratorAssign,
                Using(enumeratorVar,
                    Loop(
                        IfThenElse(
                            Equal(moveNextCall, True),
                            Block(new[] { loopVar },
                                Assign(loopVar, ToType(Property(enumeratorVar, "Current"), loopVar.Type)),
                                loopContent
                            ),
                            Break(breakLabel)
                        ),
                    breakLabel))
            );

            return loop;
        }
        public static Expression ForEachArrayItem(Expression array, Func<Expression, Expression> body)
        {
            var length = Property(array, "Length");
            return For(length, index => body(ArrayAccess(array, index)));
        }
        public static Expression For(Expression count, Func<Expression, Expression> body)
        {
            var breakLabel = Label("LoopBreak");
            var index = Variable(typeof(int), "sourceArrayIndex");
            var initialize = Assign(index, Constant(0, typeof(int)));
            var loop = Block(new[] { index },
                initialize,
                Loop(
                    IfThenElse(
                        LessThan(index, count),
                        Block(body(index), PostIncrementAssign(index)),
                        Break(breakLabel)
                    ),
                breakLabel)
            );
            return loop;
        }
        public static Expression ToObject(this Expression expression) => ToType(expression, typeof(object));
        public static Expression ToType(Expression expression, Type type) => expression.Type == type ? expression : Convert(expression, type);
        public static Expression ReplaceParameters(this LambdaExpression initialLambda, params Expression[] newParameters) =>
            new ParameterReplaceVisitor().Replace(initialLambda, newParameters);
        public static Expression ConvertReplaceParameters(this LambdaExpression initialLambda, params Expression[] newParameters) =>
            new ConvertParameterReplaceVisitor().Replace(initialLambda, newParameters);
        private static Expression Replace(this ParameterReplaceVisitor visitor, LambdaExpression initialLambda, params Expression[] newParameters)
        {
            var newLambda = initialLambda.Body;
            for (var i = 0; i < Math.Min(newParameters.Length, initialLambda.Parameters.Count); i++)
            {
                visitor.Replace(initialLambda.Parameters[i], newParameters[i]);
                newLambda = visitor.Visit(newLambda);
            }
            return newLambda;
        }
        public static Expression Replace(this Expression exp, Expression old, Expression replace) => new ReplaceVisitor(old, replace).Visit(exp);
        public static Expression NullCheck(this Expression expression, Type destinationType = null)
        {
            destinationType ??= expression.Type;
            var chain = expression.GetChain();
            if (chain.Count == 0 || chain.Peek().Target is not ParameterExpression parameter)
            {
                return expression;
            }
            var variables = new ParameterExpression[chain.Count];
            var nullCheck = False;
            var name = parameter.Name;
            int index = 0;
            foreach (var member in chain)
            {
                var variable = Variable(member.Target.Type, name);
                name += member.MemberInfo.Name;
                var target = index == 0 ? parameter : variables[index-1];
                var assignment = Assign(variable, UpdateTarget(member.Target, target));
                variables[index++] = variable;
                var nullCheckVariable = variable.Type.IsValueType ? (Expression)Block(assignment, False) : Equal(assignment, Null);
                nullCheck = OrElse(nullCheck, nullCheckVariable);
            }
            var returnType = Nullable.GetUnderlyingType(destinationType) == expression.Type ? destinationType : expression.Type;
            var nonNullExpression = UpdateTarget(expression, variables[variables.Length - 1]);
            return Block(variables, Condition(nullCheck, Default(returnType), ToType(nonNullExpression, returnType)));
            static Expression UpdateTarget(Expression sourceExpression, Expression newTarget) =>
                sourceExpression switch
                {
                    MemberExpression memberExpression => memberExpression.Update(newTarget),
                    MethodCallExpression { Method: { IsStatic: true } } methodCall => methodCall.Update(null, new[] { newTarget }.Concat(methodCall.Arguments.Skip(1))),
                    MethodCallExpression methodCall => methodCall.Update(newTarget, methodCall.Arguments),
                    _ => sourceExpression,
                };
        }
        public static Expression Using(Expression disposable, Expression body)
        {
            Expression disposeCall;
            if (typeof(IDisposable).IsAssignableFrom(disposable.Type))
            {
                disposeCall = Call(disposable, DisposeMethod);
            }
            else
            {
                if (disposable.Type.IsValueType)
                {
                    return body;
                }
                var disposableVariable = Variable(typeof(IDisposable), "disposableVariable");
                var assignDisposable = Assign(disposableVariable, TypeAs(disposable, typeof(IDisposable)));
                disposeCall = Block(new[] { disposableVariable }, assignDisposable, IfNullElse(disposableVariable, Empty, Call(disposableVariable, DisposeMethod)));
            }
            return TryFinally(body, disposeCall);
        }
        public static Expression IfNullElse(this Expression expression, Expression then, Expression @else = null)
        {
            var nonNullElse = ToType(@else ?? Default(then.Type), then.Type);
            if(expression.Type.IsValueType && !expression.Type.IsNullableType())
            {
                return nonNullElse;
            }
            return Condition(Equal(expression, Null), then, nonNullElse);
        }
        class ReplaceVisitorBase : ExpressionVisitor
        {
            protected Expression _oldNode;
            protected Expression _newNode;
            public virtual void Replace(Expression oldNode, Expression newNode)
            {
                _oldNode = oldNode;
                _newNode = newNode;
            }
        }
        class ReplaceVisitor : ReplaceVisitorBase
        {
            public ReplaceVisitor(Expression oldNode, Expression newNode) => Replace(oldNode, newNode);
            public override Expression Visit(Expression node) => _oldNode == node ? _newNode : base.Visit(node);
        }
        class ParameterReplaceVisitor : ReplaceVisitorBase
        {
            protected override Expression VisitParameter(ParameterExpression node) => _oldNode == node ? _newNode : base.VisitParameter(node);
        }
        class ConvertParameterReplaceVisitor : ParameterReplaceVisitor
        {
            public override void Replace(Expression oldNode, Expression newNode) => base.Replace(oldNode, ToType(newNode, oldNode.Type));
        }
    }
}