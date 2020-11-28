using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using AutoMapper.Internal;

namespace AutoMapper.Execution
{
    using static Expression;
    using static Internal.ExpressionFactory;
    using static ElementTypeHelper;
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class ObjectFactory
    {
        private static readonly LockingConcurrentDictionary<Type, Func<object>> CtorCache = new LockingConcurrentDictionary<Type, Func<object>>(GenerateConstructor);
        public static object CreateInstance(Type type) => CtorCache.GetOrAdd(type)();
        private static Func<object> GenerateConstructor(Type type)
        {
            var constructor = GenerateConstructorExpression(type);
            if (type.IsValueType)
            {
                constructor = Convert(constructor, typeof(object));
            }
            return Lambda<Func<object>>(constructor).Compile();
        }
        public static Expression GenerateConstructorExpression(Type type) => type switch
        {
            { IsValueType: true } => Default(type),
            Type stringType when stringType == typeof(string) => Constant(string.Empty),
            { IsInterface: true } => CreateInterface(type),
            { IsAbstract: true } => InvalidType(type, $"Cannot create an instance of abstract type {type}."),
            _ => CallConstructor(type)
        };
        private static Expression CallConstructor(Type type)
        {
            var defaultCtor = type.GetConstructor(TypeExtensions.InstanceFlags, null, Type.EmptyTypes, null);
            if (defaultCtor != null)
            {
                return New(defaultCtor);
            }
            //find a ctor with only optional args
            var ctorWithOptionalArgs = type.GetDeclaredConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional));
            if (ctorWithOptionalArgs == null)
            {
                return InvalidType(type, $"{type} needs to have a constructor with 0 args or only optional args.");
            }
            //get all optional default values
            var args = ctorWithOptionalArgs.GetParameters().Select(ReflectionHelper.GetDefaultValue);
            //create the ctor expression
            return New(ctorWithOptionalArgs, args);
        }
        private static Expression CreateInterface(Type type) =>
            type.IsGenericType(typeof(IDictionary<,>)) ? CreateCollection(type, typeof(Dictionary<,>)) : 
            type.IsGenericType(typeof(IReadOnlyDictionary<,>)) ? CreateReadOnlyCollection(type, typeof(ReadOnlyDictionary<,>)) : 
            type.IsGenericType(typeof(ISet<>)) ? CreateCollection(type, typeof(HashSet<>)) : 
            type.IsEnumerableType() ? CreateCollection(type, typeof(List<>), GetElementType(type)) : 
            InvalidInterfaceType(type);
        private static Expression CreateCollection(Type type, Type collectionType, Type genericArgument = null) => ToType(New(MakeGenericType(type, collectionType, genericArgument)), type);
        private static Type MakeGenericType(Type type, Type collectionType, Type genericArgument = null) => genericArgument == null ?
            collectionType.MakeGenericType(type.GenericTypeArguments) :
            collectionType.MakeGenericType(genericArgument);
        private static Expression CreateReadOnlyCollection(Type type, Type collectionType)
        {
            var listType = MakeGenericType(type, collectionType);
            var ctor = listType.GetConstructors()[0];
            var innerType = ctor.GetParameters()[0].ParameterType;
            return ToType(New(ctor, GenerateConstructorExpression(innerType)), type);
        }
        private static Expression InvalidInterfaceType(Type type) => InvalidType(type, $"Cannot create an instance of interface type {type}.");
        private static Expression InvalidType(Type type, string message) => Throw(Constant(new ArgumentException(message, "type")), type);
    }
}