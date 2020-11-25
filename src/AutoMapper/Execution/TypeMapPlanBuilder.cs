namespace AutoMapper.Execution
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using static System.Linq.Expressions.Expression;
    using static Internal.ExpressionFactory;
    using static ExpressionBuilder;
    using System.Diagnostics;
    using Internal;

    public class TypeMapPlanBuilder
    {
        private static readonly MethodInfo MappingError = typeof(TypeMapPlanBuilder).GetStaticMethod(nameof(MemberMappingError));
        private readonly IGlobalConfiguration _configurationProvider;
        private readonly ParameterExpression _destination;
        private readonly ParameterExpression _initialDestination;
        private readonly TypeMap _typeMap;
        public TypeMapPlanBuilder(IGlobalConfiguration configurationProvider, TypeMap typeMap)
        {
            _configurationProvider = configurationProvider;
            _typeMap = typeMap;
            Source = Parameter(typeMap.SourceType, "src");
            _initialDestination = Parameter(typeMap.DestinationTypeToUse, "dest");
            Context = Parameter(typeof(ResolutionContext), "ctxt");
            _destination = Variable(_initialDestination.Type, "typeMapDestination");
        }
        public ParameterExpression Source { get; }
        public ParameterExpression Context { get; }
        private static AutoMapperMappingException MemberMappingError(Exception innerException, IMemberMap memberMap) => 
            new AutoMapperMappingException("Error mapping types.", innerException, memberMap);
        public LambdaExpression CreateMapperLambda(HashSet<TypeMap> typeMapsPath)
        {
            var customExpression = TypeConverterMapper() ?? _typeMap.CustomMapFunction ?? _typeMap.CustomMapExpression;
            if(customExpression != null)
            {
                return Lambda(customExpression.ReplaceParameters(Source, _initialDestination, Context), Source, _initialDestination, Context);
            }
            CheckForCycles(typeMapsPath);
            if(typeMapsPath != null)
            {
                return null;
            }
            var createDestinationFunc = CreateDestinationFunc();
            var assignmentFunc = CreateAssignmentFunc(createDestinationFunc);
            var mapperFunc = CreateMapperFunc(assignmentFunc);
            var checkContext = CheckContext(_typeMap, Context);
            var lambaBody = checkContext != null ? new[] {checkContext, mapperFunc} : new[] {mapperFunc};
            return Lambda(Block(new[] {_destination}, lambaBody), Source, _initialDestination, Context);
        }
        private void CheckForCycles(HashSet<TypeMap> typeMapsPath)
        {
            typeMapsPath ??= new HashSet<TypeMap>();
            var inlineWasChecked = _typeMap.WasInlineChecked;
            _typeMap.WasInlineChecked = true;
            typeMapsPath.Add(_typeMap);
            foreach (var memberMap in MemberMaps())
            {
                var memberTypeMap = ResolveMemberTypeMap(memberMap);
                if (memberTypeMap == null || memberTypeMap.PreserveReferences || memberTypeMap.MapExpression != null)
                {
                    continue;
                }
                if (!inlineWasChecked && typeMapsPath.Count % _configurationProvider.MaxExecutionPlanDepth == 0)
                {
                    memberMap.Inline = false;
                    Debug.WriteLine($"Resetting Inline: {memberMap.DestinationName} in {_typeMap.SourceType} - {_typeMap.DestinationType}");
                }
                if (typeMapsPath.Contains(memberTypeMap))
                {
                    if (memberTypeMap.SourceType.IsValueType)
                    {
                        if (memberTypeMap.MaxDepth == 0)
                        {
                            memberTypeMap.MaxDepth = 10;
                        }
                        typeMapsPath.Remove(_typeMap);
                        return;
                    }
                    memberTypeMap.PreserveReferences = true;
                    Debug.WriteLine($"Setting PreserveReferences: {_typeMap.SourceType} - {_typeMap.DestinationType} => {memberTypeMap.SourceType} - {memberTypeMap.DestinationType}");
                    foreach (var derivedTypeMap in _configurationProvider.GetIncludedTypeMaps(memberTypeMap))
                    {
                        derivedTypeMap.PreserveReferences = true;
                        Debug.WriteLine($"Setting PreserveReferences: {_typeMap.SourceType} - {_typeMap.DestinationType} => {derivedTypeMap.SourceType} - {derivedTypeMap.DestinationType}");
                    }
                }
                memberTypeMap.CreateMapperLambda(_configurationProvider, typeMapsPath);
            }
            typeMapsPath.Remove(_typeMap);
            IEnumerable<IMemberMap> MemberMaps() =>
                _typeMap.MemberMaps.Concat(_configurationProvider.GetIncludedTypeMaps(_typeMap).SelectMany(tm => tm.MemberMaps));
            TypeMap ResolveMemberTypeMap(IMemberMap memberMap)
            {
                if (!memberMap.CanResolveValue)
                {
                    return null;
                }
                var types = memberMap.Types;
                if (types.ContainsGenericParameters)
                {
                    return null;
                }
                var typeMap = _configurationProvider.ResolveTypeMap(types);
                if (typeMap == null && _configurationProvider.FindMapper(types) is IObjectMapperInfo mapper)
                {
                    typeMap = _configurationProvider.ResolveTypeMap(mapper.GetAssociatedTypes(types));
                }
                return typeMap;
            }
        }
        private LambdaExpression TypeConverterMapper()
        {
            if (_typeMap.TypeConverterType == null)
            {
                return null;
            }
            // (src, dest, ctxt) => ((ITypeConverter<TSource, TDest>)ctxt.Options.CreateInstance<TypeConverterType>()).ToType(src, ctxt);
            var converterInterfaceType = typeof(ITypeConverter<,>).MakeGenericType(_typeMap.SourceType, _typeMap.DestinationTypeToUse);
            return Lambda(
                Call(
                    ToType(CreateInstance(_typeMap.TypeConverterType), converterInterfaceType),
                    converterInterfaceType.GetMethod("Convert"),
                    Source, _initialDestination, Context
                ),
                Source, _initialDestination, Context);
        }
        private Expression CreateDestinationFunc()
        {
            var newDestFunc = ToType(CreateNewDestinationFunc(), _typeMap.DestinationTypeToUse);
            var getDest = _typeMap.DestinationTypeToUse.IsValueType ? newDestFunc : Coalesce(_initialDestination, newDestFunc);
            Expression destinationFunc = Assign(_destination, getDest);
            if (_typeMap.PreserveReferences)
            {
                var dest = Variable(typeof(object), "cachedDestination");
                var set = Call(Context, CacheDestinationMethod, Source, Constant(_destination.Type), _destination);
                var setCache = IfThen(NotEqual(Source, Null), set);
                destinationFunc = Block(new[] {dest}, Assign(dest, destinationFunc), setCache, dest);
            }
            return destinationFunc;
        }
        private Expression CreateAssignmentFunc(Expression createDestination)
        {
            var actions = new List<Expression> { createDestination };
            Expression typesExpression = null;
            if (_typeMap.MaxDepth > 0)
            {
                typesExpression = Constant(_typeMap.Types);
                actions.Add(Call(Context, IncTypeDepthInfo, typesExpression));
            }
            foreach (var beforeMapAction in _typeMap.BeforeMapActions)
            {
                actions.Add(beforeMapAction.ReplaceParameters(Source, _destination, Context));
            }
            var includedMembersVariables = _typeMap.IncludedMembersTypeMaps.Count == 0 ? Array.Empty<ParameterExpression>() : IncludedMembers(actions);
            var isConstructorMapping = _typeMap.IsConstructorMapping;
            foreach (var propertyMap in _typeMap.PropertyMaps)
            {
                if (propertyMap.CanResolveValue)
                {
                    var property = TryPropertyMap(propertyMap);
                    if (isConstructorMapping && _typeMap.ConstructorParameterMatches(propertyMap.DestinationName))
                    {
                        property = _initialDestination.IfNullElse(Default(property.Type), property);
                    }
                    actions.Add(property);
                }
            }
            foreach (var pathMap in _typeMap.PathMaps)
            {
                if (!pathMap.Ignored)
                {
                    actions.Add(TryPathMap(pathMap));
                }
            }
            foreach (var afterMapAction in _typeMap.AfterMapActions)
            {
                actions.Add(afterMapAction.ReplaceParameters(Source, _destination, Context));
            }
            if (_typeMap.MaxDepth > 0)
            {
                actions.Add(Call(Context, DecTypeDepthInfo, typesExpression));
            }
            actions.Add(_destination);
            return Block(includedMembersVariables, actions);
            IEnumerable<ParameterExpression> IncludedMembers(List<Expression> actions)
            {
                var includedMembersVariables = _typeMap.IncludedMembersTypeMaps.Select(i => i.Variable);
                var assignIncludedMembers = includedMembersVariables.Zip(_typeMap.IncludedMembersTypeMaps, (v, i) => Assign(v, i.MemberExpression.ReplaceParameters(Source).NullCheck()));
                actions.AddRange(assignIncludedMembers);
                return includedMembersVariables;
            }
        }
        private Expression TryPathMap(PathMap pathMap)
        {
            var destination = ((MemberExpression) pathMap.DestinationExpression.ConvertReplaceParameters(_destination)).Expression;
            var createInnerObjects = CreateInnerObjects(destination);
            var setFinalValue = CreatePropertyMapFunc(pathMap, destination, pathMap.MemberPath.Last);
            var pathMapExpression = Block(createInnerObjects, setFinalValue);
            return TryMemberMap(pathMap, pathMapExpression);
            static Expression CreateInnerObjects(Expression destination)
            {
                return Block(destination.GetMemberExpressions().Select(NullCheck).Concat(new[] { ExpressionFactory.Empty }));
                static Expression NullCheck(MemberExpression memberExpression)
                {
                    var setter = GetSetter(memberExpression);
                    var ifNull = setter == null
                        ? Throw(Constant(new NullReferenceException($"{memberExpression} cannot be null because it's used by ForPath.")))
                        : (Expression)Assign(setter, ObjectFactory.GenerateConstructorExpression(memberExpression.Type));
                    return memberExpression.IfNullElse(ifNull);
                }
                static Expression GetSetter(MemberExpression memberExpression) => memberExpression.Member switch
                {
                    PropertyInfo { CanWrite: true } property => Property(memberExpression.Expression, property),
                    FieldInfo { IsInitOnly: false } field => Field(memberExpression.Expression, field),
                    _ => null,
                };
            }
        }
        private Expression CreateMapperFunc(Expression assignmentFunc)
        {
            var mapperFunc = assignmentFunc;
            var overMaxDepth = Context.OverMaxDepth(_typeMap);
            if (overMaxDepth != null)
            {
                mapperFunc = Condition(overMaxDepth, Default(_typeMap.DestinationTypeToUse), mapperFunc);
            }
            if (_typeMap.Profile.AllowNullDestinationValues)
            {
                mapperFunc = Source.IfNullElse(Default(_typeMap.DestinationTypeToUse), mapperFunc);
            }
            return CheckReferencesCache(mapperFunc);
        }
        private Expression CheckReferencesCache(Expression valueBuilder)
        {
            if(!_typeMap.PreserveReferences)
            {
                return valueBuilder;
            }
            var cache = Variable(_typeMap.DestinationTypeToUse, "cachedDestination");
            var assignCache =
                Assign(cache,
                    ToType(Call(Context, GetDestinationMethod, Source, Constant(_destination.Type)), _destination.Type));
            var condition = Condition(
                AndAlso(NotEqual(Source, Null), NotEqual(assignCache, Null)),
                cache,
                valueBuilder);
            return Block(new[] { cache }, condition);
        }
        private Expression CreateNewDestinationFunc()
        {
            if(_typeMap.CustomCtorExpression != null)
            {
                return _typeMap.CustomCtorExpression.ReplaceParameters(Source);
            }
            if(_typeMap.CustomCtorFunction != null)
            {
                return _typeMap.CustomCtorFunction.ReplaceParameters(Source, Context);
            }
            if(_typeMap.ConstructDestinationUsingServiceLocator)
            {
                return CreateInstance(_typeMap.DestinationTypeToUse);
            }
            if(_typeMap.ConstructorMap?.CanResolve == true)
            {
                return CreateNewDestinationExpression(_typeMap.ConstructorMap);
            }
            if(_typeMap.DestinationTypeToUse.IsInterface)
            {
                var proxyType = Call(typeof(ProxyGenerator), nameof(ProxyGenerator.GetProxyType), null, Constant(_typeMap.DestinationTypeToUse));
                return Call(typeof(ObjectFactory), nameof(ObjectFactory.CreateInstance), null, proxyType);
            }
            return ObjectFactory.GenerateConstructorExpression(_typeMap.DestinationTypeToUse);
        }
        private Expression CreateNewDestinationExpression(ConstructorMap constructorMap)
        {
            var ctorArgs = constructorMap.CtorParams.Select(CreateConstructorParameterExpression);
            var variables = constructorMap.Ctor.GetParameters().Select(parameter => Variable(parameter.ParameterType, parameter.Name)).ToArray();
            var body = variables.Zip(ctorArgs,
                                                (variable, expression) => (Expression) Assign(variable, ToType(expression, variable.Type)))
                                                .Concat(new[] { CheckReferencesCache(New(constructorMap.Ctor, variables)) })
                                                .ToArray();
            return Block(variables, body);
        }
        private Expression CreateConstructorParameterExpression(ConstructorParameterMap ctorParamMap)
        {
            var defaultValue = ctorParamMap.Parameter.IsOptional ? ctorParamMap.DefaultValue() : Default(ctorParamMap.DestinationType);
            var resolvedExpression = BuildValueResolverFunc(ctorParamMap, defaultValue);
            var resolvedValue = Variable(resolvedExpression.Type, "resolvedValue");
            var tryMap = Block(new[] {resolvedValue},
                Assign(resolvedValue, resolvedExpression),
                MapExpression(_configurationProvider, _typeMap.Profile, new TypePair(resolvedExpression.Type, ctorParamMap.DestinationType), resolvedValue, Context));
            return TryMemberMap(ctorParamMap, tryMap);
        }
        private Expression TryPropertyMap(PropertyMap propertyMap)
        {
            var propertyMapExpression = CreatePropertyMapFunc(propertyMap, _destination, propertyMap.DestinationMember);
            return propertyMapExpression == null ? null : TryMemberMap(propertyMap, propertyMapExpression);
        }
        private static Expression TryMemberMap(IMemberMap memberMap, Expression memberMapExpression)
        {
            var exception = Parameter(typeof(Exception), "ex");
            var newException = Call(MappingError, exception, Constant(memberMap));
            return TryCatch(memberMapExpression, Catch(exception, Throw(newException, memberMapExpression.Type)));
        }
        private Expression CreatePropertyMapFunc(IMemberMap memberMap, Expression destination, MemberInfo destinationMember)
        {
            Expression destinationMemberAccess, destinationMemberGetter;
            bool destinationMemberReadOnly;
            if (destinationMember is PropertyInfo destinationProperty)
            {
                destinationMemberAccess = Property(destination, destinationProperty);
                destinationMemberReadOnly = !destinationProperty.CanWrite;
                destinationMemberGetter = destinationProperty.CanRead ? destinationMemberAccess : Default(memberMap.DestinationType);
            }
            else
            {
                var destinationField = (FieldInfo)destinationMember;
                destinationMemberAccess = Field(destination, destinationField);
                destinationMemberReadOnly = destinationField.IsInitOnly;
                destinationMemberGetter = destinationMemberAccess;
            }
            var valueResolver = BuildValueResolverFunc(memberMap, destinationMemberGetter);
            var resolvedValueVariable = Variable(valueResolver.Type, "resolvedValue");
            var setResolvedValue = Assign(resolvedValueVariable, valueResolver);
            var mappedMember = MapMember(memberMap, destinationMemberGetter, destinationMemberReadOnly, resolvedValueVariable);
            ParameterExpression mappedMemberVariable;
            ParameterExpression[] finalVariables;
            var mapperExpressions = new List<Expression>(capacity: 3) { setResolvedValue };
            if (mappedMember == resolvedValueVariable)
            {
                mappedMemberVariable = resolvedValueVariable;
                finalVariables = new[] { resolvedValueVariable };
            }
            else
            {
                mappedMemberVariable = Variable(mappedMember.Type, "mappedValue");
                finalVariables = new[] { resolvedValueVariable, mappedMemberVariable };
                mapperExpressions.Add(Assign(mappedMemberVariable, mappedMember));
            }
            var mapperExpr = destinationMemberReadOnly ? (Expression)mappedMemberVariable : Assign(destinationMemberAccess, mappedMemberVariable);
            if (memberMap.Condition != null)
            {
                mapperExpressions.Add(IfThen(Condition(memberMap, destinationMemberGetter, mappedMemberVariable), mapperExpr));
            }
            else if (!destinationMemberReadOnly)
            {
                mapperExpressions.Add(mapperExpr);
            }
            if (memberMap.PreCondition != null)
            {
                mapperExpressions = Precondition(memberMap, mapperExpressions);
            }
            return Block(finalVariables, mapperExpressions);
            Expression DestinationMemberValue(IMemberMap memberMap, Expression destinationMemberGetter, bool destinationMemberReadOnly)
            {
                if (memberMap.UseDestinationValue == true || (memberMap.UseDestinationValue == null && destinationMemberReadOnly))
                {
                    return destinationMemberGetter;
                }
                else if (_initialDestination.Type.IsValueType)
                {
                    return Default(memberMap.DestinationType);
                }
                else
                {
                    return Expression.Condition(Equal(_initialDestination, Null), Default(memberMap.DestinationType), destinationMemberGetter);
                }
            }
            List<Expression> Precondition(IMemberMap memberMap, List<Expression> mapperExpressions)
            {
                var preCondition = memberMap.PreCondition.ConvertReplaceParameters(GetCustomSource(memberMap), _destination, Context);
                return new() { IfThen(preCondition, Block(mapperExpressions)) };
            }
            Expression MapMember(IMemberMap memberMap, Expression destinationMemberGetter, bool destinationMemberReadOnly, ParameterExpression resolvedValue)
            {
                var typePair = new TypePair(resolvedValue.Type, memberMap.DestinationType);
                var destinationMemberValue = DestinationMemberValue(memberMap, destinationMemberGetter, destinationMemberReadOnly);
                var mapMember = memberMap.Inline ?
                    MapExpression(_configurationProvider, _typeMap.Profile, typePair, resolvedValue, Context, memberMap, destinationMemberValue) :
                    ContextMap(typePair, resolvedValue, Context, destinationMemberValue, memberMap);
                mapMember = memberMap.ApplyTransformers(mapMember);
                return mapMember;
            }
            Expression Condition(IMemberMap memberMap, Expression destinationMemberGetter, ParameterExpression mappedMemberVariable)
            {
                var memberType = memberMap.Condition.Parameters[2].Type;
                return memberMap.Condition.ConvertReplaceParameters(
                    GetCustomSource(memberMap), _destination, ToType(mappedMemberVariable, memberType), ToType(destinationMemberGetter, memberType), Context);
            }
        }
        private Expression BuildValueResolverFunc(IMemberMap memberMap, Expression destValueExpr)
        {
            var destinationPropertyType = memberMap.DestinationType;
            var valueResolverFunc = memberMap switch
            {
                { ValueConverterConfig: { } } => ToType(BuildConvertCall(memberMap), destinationPropertyType),
                { ValueResolverConfig: { } } => BuildResolveCall(destValueExpr, memberMap),
                { CustomMapFunction: LambdaExpression function } => function.ConvertReplaceParameters(GetCustomSource(memberMap), _destination, destValueExpr, Context),
                { CustomMapExpression: { } } => CustomMapExpression(memberMap, destinationPropertyType),
                { SourceMembers: { Count: > 0 } } => Chain(memberMap, destinationPropertyType),
                _ => destValueExpr
            };
            if (memberMap.NullSubstitute != null)
            {
                valueResolverFunc = memberMap.NullSubstitute(valueResolverFunc);
            }
            else if (!memberMap.AllowsNullDestinationValues())
            {
                var toCreate = memberMap.SourceType;
                if (!toCreate.IsAbstract && toCreate.IsClass && !toCreate.IsArray)
                {
                    valueResolverFunc = Coalesce(valueResolverFunc, ToType(ObjectFactory.GenerateConstructorExpression(toCreate), valueResolverFunc.Type));
                }
            }
            return valueResolverFunc;
            Expression CustomMapExpression(IMemberMap memberMap, Type destinationPropertyType)
            {
                var nullCheckedExpression = memberMap.CustomMapExpression.ReplaceParameters(GetCustomSource(memberMap)).NullCheck(destinationPropertyType);
                var defaultExpression = Default(nullCheckedExpression.Type);
                return TryCatch(nullCheckedExpression, Catch(typeof(NullReferenceException), defaultExpression), Catch(typeof(ArgumentNullException), defaultExpression));
            }
        }
        private Expression GetCustomSource(IMemberMap memberMap) => memberMap.IncludedMember?.Variable ?? Source;
        private Expression Chain(IMemberMap memberMap, Type destinationType) => memberMap.SourceMembers.Chain(GetCustomSource(memberMap)).NullCheck(destinationType);
        private Expression CreateInstance(Type type) => Call(Context, ContextCreate, Constant(type));
        private Expression BuildResolveCall(Expression destValueExpr, IMemberMap memberMap)
        {
            var typeMap = memberMap.TypeMap;
            var valueResolverConfig = memberMap.ValueResolverConfig;
            var resolverInstance = valueResolverConfig.Instance != null
                ? Constant(valueResolverConfig.Instance)
                : CreateInstance(typeMap.MakeGenericType(valueResolverConfig.ConcreteType));
            var source = GetCustomSource(memberMap);
            var sourceMember = valueResolverConfig.SourceMember?.ReplaceParameters(source) ??
                               (valueResolverConfig.SourceMemberName != null
                                   ? PropertyOrField(source, valueResolverConfig.SourceMemberName)
                                   : null);

            var iResolverType = valueResolverConfig.InterfaceType;
            if (iResolverType.ContainsGenericParameters)
            {
                iResolverType = iResolverType.GetGenericTypeDefinition().MakeGenericType(new[] { typeMap.SourceType, typeMap.DestinationType }.Concat(iResolverType.GenericTypeArguments.Skip(2)).ToArray());
            }
            var parameters = 
                new[] { source, _destination, sourceMember, destValueExpr }.Where(p => p != null)
                .Zip(iResolverType.GetGenericArguments(), ToType)
                .Concat(new[] {Context});
            return Call(ToType(resolverInstance, iResolverType), iResolverType.GetMethod("Resolve"), parameters);
        }
        private Expression BuildConvertCall(IMemberMap memberMap)
        {
            var valueConverterConfig = memberMap.ValueConverterConfig;
            var iResolverType = valueConverterConfig.InterfaceType;
            var iResolverTypeArgs = iResolverType.GetGenericArguments();
            var resolverInstance = valueConverterConfig.Instance != null
                ? Constant(valueConverterConfig.Instance)
                : CreateInstance(valueConverterConfig.ConcreteType);
            var source = GetCustomSource(memberMap);
            var sourceMember = valueConverterConfig.SourceMember?.ReplaceParameters(source) ??
                               (valueConverterConfig.SourceMemberName != null
                                   ? PropertyOrField(source, valueConverterConfig.SourceMemberName)
                                   : memberMap.SourceMembers.Any()
                                       ? Chain(memberMap, iResolverTypeArgs[1])
                                       : Throw(Constant(BuildExceptionMessage()), iResolverTypeArgs[0])
                               );
            return Call(ToType(resolverInstance, iResolverType), iResolverType.GetMethod("Convert"),
                ToType(sourceMember, iResolverTypeArgs[0]), Context);
            AutoMapperConfigurationException BuildExceptionMessage() 
                => new AutoMapperConfigurationException($"Cannot find a source member to pass to the value converter of type {valueConverterConfig.ConcreteType.FullName}. Configure a source member to map from.");
        }
    }
}