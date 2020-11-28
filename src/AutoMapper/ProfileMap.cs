using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using AutoMapper.Configuration;
using AutoMapper.Configuration.Conventions;
using AutoMapper.Internal;

namespace AutoMapper
{
    using static Expression;
    [DebuggerDisplay("{Name}")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class ProfileMap
    {
        private ITypeMapConfiguration[] _typeMapConfigs;
        private Dictionary<TypePair, ITypeMapConfiguration> _openTypeMapConfigs;
        private ConcurrentDictionaryWrapper<Type, TypeDetails> _typeDetails;
        private readonly IMemberConfiguration[] _memberConfigurations;

        public ProfileMap(IProfileConfiguration profile)
            : this(profile, null)
        {
        }

        public ProfileMap(IProfileConfiguration profile, IGlobalConfigurationExpression configuration)
        {
            var globalProfile = (IProfileConfiguration)configuration;
            Name = profile.ProfileName;
            AllowNullCollections = profile.AllowNullCollections ?? configuration?.AllowNullCollections ?? false;
            AllowNullDestinationValues = profile.AllowNullDestinationValues ?? configuration?.AllowNullDestinationValues ?? true;
            EnableNullPropagationForQueryMapping = profile.EnableNullPropagationForQueryMapping ?? configuration?.EnableNullPropagationForQueryMapping ?? false;
            ConstructorMappingEnabled = profile.ConstructorMappingEnabled ?? globalProfile?.ConstructorMappingEnabled ?? true;
            MethodMappingEnabled = profile.MethodMappingEnabled ?? globalProfile?.MethodMappingEnabled ?? true;
            FieldMappingEnabled = profile.FieldMappingEnabled ?? globalProfile?.FieldMappingEnabled ?? true;
            ShouldMapField = profile.ShouldMapField ?? configuration?.ShouldMapField ?? (p => p.IsPublic);
            ShouldMapProperty = profile.ShouldMapProperty ?? configuration?.ShouldMapProperty ?? (p => p.IsPublic());
            ShouldMapMethod = profile.ShouldMapMethod ?? configuration?.ShouldMapMethod ?? (p => !p.IsSpecialName);
            ShouldUseConstructor = profile.ShouldUseConstructor ?? configuration?.ShouldUseConstructor ?? (c => true);

            ValueTransformers = profile.ValueTransformers.Concat(configuration?.ValueTransformers ?? Enumerable.Empty<ValueTransformerConfiguration>()).ToArray();

            _memberConfigurations = profile.MemberConfigurations.Concat(globalProfile?.MemberConfigurations ?? Enumerable.Empty<IMemberConfiguration>()).ToArray();

            var nameSplitMember = _memberConfigurations[0].MemberMappers.OfType<NameSplitMember>().FirstOrDefault();
            if (nameSplitMember != null)
            {
                nameSplitMember.SourceMemberNamingConvention = profile.SourceMemberNamingConvention ?? PascalCaseNamingConvention.Instance;
                nameSplitMember.DestinationMemberNamingConvention = profile.DestinationMemberNamingConvention ?? PascalCaseNamingConvention.Instance;
            }

            GlobalIgnores = profile.GlobalIgnores.Concat(globalProfile?.GlobalIgnores ?? Enumerable.Empty<string>()).ToArray();
            SourceExtensionMethods = profile.SourceExtensionMethods.Concat(globalProfile?.SourceExtensionMethods ?? Enumerable.Empty<MethodInfo>()).ToArray();
            AllPropertyMapActions = profile.AllPropertyMapActions.Concat(globalProfile?.AllPropertyMapActions ?? Enumerable.Empty<Action<PropertyMap, IMemberConfigurationExpression>>()).ToArray();
            AllTypeMapActions = profile.AllTypeMapActions.Concat(globalProfile?.AllTypeMapActions ?? Enumerable.Empty<Action<TypeMap, IMappingExpression>>()).ToArray();

            var prePostFixes = profile.MemberConfigurations.Concat(globalProfile?.MemberConfigurations ?? Enumerable.Empty<IMemberConfiguration>())
                                        .Select(m => m.NameMapper)
                                        .SelectMany(m => m.NamedMappers)
                                        .OfType<PrePostfixName>()
                                        .ToArray();
            Prefixes = prePostFixes.SelectMany(m => m.Prefixes).Distinct().ToList();
            Postfixes = prePostFixes.SelectMany(m => m.Postfixes).Distinct().ToList();

            SetTypeMapConfigs();
            SetOpenTypeMapConfigs();
            _typeDetails = new(TypeDetailsFactory, 2*_typeMapConfigs.Length);
            return;
            void SetTypeMapConfigs()
            {
                _typeMapConfigs = new ITypeMapConfiguration[profile.TypeMapConfigs.Count];
                var index = 0;
                var reverseMapsCount = 0;
                foreach (var typeMapConfig in profile.TypeMapConfigs)
                {
                    _typeMapConfigs[index++] = typeMapConfig;
                    if (typeMapConfig.ReverseTypeMap != null)
                    {
                        reverseMapsCount++;
                    }
                }
                TypeMapsCount = index + reverseMapsCount;
            }
            void SetOpenTypeMapConfigs()
            {
                _openTypeMapConfigs = new Dictionary<TypePair, ITypeMapConfiguration>(profile.OpenTypeMapConfigs.Count);
                foreach (var openTypeMapConfig in profile.OpenTypeMapConfigs)
                {
                    _openTypeMapConfigs.Add(openTypeMapConfig.Types, openTypeMapConfig);
                    var reverseMap = openTypeMapConfig.ReverseTypeMap;
                    if (reverseMap != null)
                    {
                        _openTypeMapConfigs.Add(reverseMap.Types, reverseMap);
                    }
                }
            }
        }
        public int TypeMapsCount { get; private set; }
        internal void Clear()
        {
            _typeDetails = new(TypeDetailsFactory, 2 * _openTypeMapConfigs.Count);
            _typeMapConfigs = null;
        }
        public bool AllowNullCollections { get; }
        public bool AllowNullDestinationValues { get; }
        public bool ConstructorMappingEnabled { get; }
        public bool EnableNullPropagationForQueryMapping { get; }
        public bool MethodMappingEnabled { get; }
        public bool FieldMappingEnabled { get; }
        public string Name { get; }
        public Func<FieldInfo, bool> ShouldMapField { get; }
        public Func<PropertyInfo, bool> ShouldMapProperty { get; }
        public Func<MethodInfo, bool> ShouldMapMethod { get; }
        public Func<ConstructorInfo, bool> ShouldUseConstructor { get; }

        public IEnumerable<Action<PropertyMap, IMemberConfigurationExpression>> AllPropertyMapActions { get; }
        public IEnumerable<Action<TypeMap, IMappingExpression>> AllTypeMapActions { get; }
        public IReadOnlyCollection<string> GlobalIgnores { get; }
        public IEnumerable<IMemberConfiguration> MemberConfigurations => _memberConfigurations;
        public IEnumerable<MethodInfo> SourceExtensionMethods { get; }
        public List<string> Prefixes { get; }
        public List<string> Postfixes { get; }
        public IReadOnlyCollection<ValueTransformerConfiguration> ValueTransformers { get; }
        public TypeDetails GetTypeDetails(Type type) => _typeDetails.GetOrDefault(type);
        public TypeDetails CreateTypeDetails(Type type) => _typeDetails.GetOrAdd(type);

        private TypeDetails TypeDetailsFactory(Type type) => new TypeDetails(type, this);

        public void Register(IGlobalConfiguration configurationProvider)
        {
            foreach (var config in _typeMapConfigs)
            {
                BuildTypeMap(configurationProvider, config);

                if (config.ReverseTypeMap != null)
                {
                    BuildTypeMap(configurationProvider, config.ReverseTypeMap);
                }
            }
        }

        public void Configure(IGlobalConfiguration configurationProvider)
        {
            foreach (var typeMapConfiguration in _typeMapConfigs)
            {
                Configure(typeMapConfiguration, configurationProvider);
                if (typeMapConfiguration.ReverseTypeMap != null)
                {
                    Configure(typeMapConfiguration.ReverseTypeMap, configurationProvider);
                }
            }
        }

        private void BuildTypeMap(IGlobalConfiguration configurationProvider, ITypeMapConfiguration config)
        {
            var typeMap = TypeMapFactory.CreateTypeMap(config.SourceType, config.DestinationType, this, config.IsReverseMap);

            config.Configure(typeMap);

            configurationProvider.RegisterTypeMap(typeMap);
        }

        private void Configure(ITypeMapConfiguration typeMapConfiguration, IGlobalConfiguration configurationProvider)
        {
            var typeMap = configurationProvider.FindTypeMapFor(typeMapConfiguration.Types);
            if (typeMap.IncludeAllDerivedTypes)
            {
                IncludeAllDerived(configurationProvider, typeMap);
            }
            Configure(typeMap, configurationProvider);
        }

        private static void IncludeAllDerived(IGlobalConfiguration configurationProvider, TypeMap typeMap)
        {
            foreach (var derivedMap in configurationProvider.GetAllTypeMaps().Where(tm =>
                    typeMap != tm &&
                    typeMap.SourceType.IsAssignableFrom(tm.SourceType) &&
                    typeMap.DestinationType.IsAssignableFrom(tm.DestinationType)))
            {
                typeMap.IncludeDerivedTypes(derivedMap.Types);
            }
        }

        private void Configure(TypeMap typeMap, IGlobalConfiguration configurationProvider)
        {
            foreach (var action in AllTypeMapActions)
            {
                var expression = new MappingExpression(typeMap.Types, typeMap.ConfiguredMemberList);

                action(typeMap, expression);

                expression.Configure(typeMap);
            }

            foreach (var action in AllPropertyMapActions)
            {
                foreach (var propertyMap in typeMap.PropertyMaps)
                {
                    var memberExpression = new MappingExpression.MemberConfigurationExpression(propertyMap.DestinationMember, typeMap.SourceType);

                    action(propertyMap, memberExpression);

                    memberExpression.Configure(typeMap);
                }
            }

            ApplyBaseMaps(typeMap, typeMap, configurationProvider);
            ApplyDerivedMaps(typeMap, typeMap, configurationProvider);
            ApplyMemberMaps(typeMap, configurationProvider);
        }

        public TypeMap CreateClosedGenericTypeMap(ITypeMapConfiguration openMapConfig, in TypePair closedTypes, IGlobalConfiguration configurationProvider)
        {
            TypeMap closedMap;
            lock (configurationProvider)
            {
                closedMap = TypeMapFactory.CreateTypeMap(closedTypes.SourceType, closedTypes.DestinationType, this);
            }
            openMapConfig.Configure(closedMap);

            Configure(closedMap, configurationProvider);

            if (closedMap.TypeConverterType != null)
            {
                var typeParams =
                    (openMapConfig.SourceType.IsGenericTypeDefinition ? closedTypes.SourceType.GetGenericArguments() : Type.EmptyTypes)
                        .Concat
                    (openMapConfig.DestinationType.IsGenericTypeDefinition ? closedTypes.DestinationType.GetGenericArguments() : Type.EmptyTypes);

                var neededParameters = closedMap.TypeConverterType.GetGenericParameters().Length;
                closedMap.TypeConverterType = closedMap.TypeConverterType.MakeGenericType(typeParams.Take(neededParameters).ToArray());
            }
            if (closedMap.DestinationTypeOverride?.IsGenericTypeDefinition == true)
            {
                var neededParameters = closedMap.DestinationTypeOverride.GetGenericParameters().Length;
                closedMap.DestinationTypeOverride = closedMap.DestinationTypeOverride.MakeGenericType(closedTypes.DestinationType.GetGenericArguments().Take(neededParameters).ToArray());
            }
            return closedMap;
        }

        public ITypeMapConfiguration GetGenericMap(in TypePair genericPair) => _openTypeMapConfigs.GetOrDefault(genericPair);

        private void ApplyBaseMaps(TypeMap derivedMap, TypeMap currentMap, IGlobalConfiguration configurationProvider)
        {
            foreach (var baseMap in configurationProvider.GetIncludedTypeMaps(currentMap.IncludedBaseTypes))
            {
                baseMap.IncludeDerivedTypes(currentMap.Types);
                derivedMap.AddInheritedMap(baseMap);
                ApplyBaseMaps(derivedMap, baseMap, configurationProvider);
            }
        }

        private void ApplyMemberMaps(TypeMap currentMap, IGlobalConfiguration configurationProvider)
        {
            if (!currentMap.HasIncludedMembers)
            {
                return;
            }
            foreach (var includedMemberExpression in currentMap.GetAllIncludedMembers())
            {
                var includedMap = configurationProvider.GetIncludedTypeMap(includedMemberExpression.Body.Type, currentMap.DestinationType);
                var includedMember = new IncludedMember(includedMap, includedMemberExpression);
                if (currentMap.AddMemberMap(includedMember))
                {
                    ApplyMemberMaps(includedMap, configurationProvider);
                    foreach (var inheritedIncludedMember in includedMap.IncludedMembersTypeMaps)
                    {
                        currentMap.AddMemberMap(includedMember.Chain(inheritedIncludedMember));
                    }
                }
            }
        }

        private void ApplyDerivedMaps(TypeMap baseMap, TypeMap typeMap, IGlobalConfiguration configurationProvider)
        {
            foreach (var derivedMap in configurationProvider.GetIncludedTypeMaps(typeMap))
            {
                derivedMap.IncludeBaseTypes(typeMap.Types);
                derivedMap.AddInheritedMap(baseMap);
                ApplyDerivedMaps(baseMap, derivedMap, configurationProvider);
            }
        }

        public bool MapDestinationPropertyToSource(TypeDetails sourceTypeDetails, Type destType, Type destMemberType, string destMemberInfo, LinkedList<MemberInfo> members, bool reverseNamingConventions)
        {
            if (string.IsNullOrEmpty(destMemberInfo))
            {
                return false;
            }
            foreach (var memberConfiguration in _memberConfigurations)
            {
                if (memberConfiguration.MapDestinationPropertyToSource(this, sourceTypeDetails, destType, destMemberType, destMemberInfo, members, reverseNamingConventions))
                {
                    return true;
                }
            }
            return false;
        }
    }
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DebuggerDisplay("{MemberExpression}, {TypeMap}")]
    public class IncludedMember : IEquatable<IncludedMember>
    {
        public IncludedMember(TypeMap typeMap, LambdaExpression memberExpression) : this(typeMap, memberExpression,
            Variable(memberExpression.Body.Type, string.Join("", memberExpression.GetMembersChain().Select(m => m.Name))), memberExpression)
        {
        }
        private IncludedMember(TypeMap typeMap, LambdaExpression memberExpression, ParameterExpression variable, LambdaExpression projectToCustomSource)
        {
            TypeMap = typeMap;
            MemberExpression = memberExpression;
            Variable = variable;
            ProjectToCustomSource = projectToCustomSource;
        }
        public IncludedMember Chain(IncludedMember other)
        {
            if (other == null)
            {
                return this;
            }
            return new IncludedMember(other.TypeMap, Chain(other.MemberExpression), other.Variable, Chain(MemberExpression, other.MemberExpression));
        }
        public static LambdaExpression Chain(LambdaExpression customSource, LambdaExpression lambda) => 
            Lambda(lambda.ReplaceParameters(customSource.Body), customSource.Parameters);
        public TypeMap TypeMap { get; }
        public LambdaExpression MemberExpression { get; }
        public ParameterExpression Variable { get; }
        public LambdaExpression ProjectToCustomSource { get; }
        public LambdaExpression Chain(LambdaExpression lambda) => Lambda(lambda.ReplaceParameters(Variable), lambda.Parameters);
        public bool Equals(IncludedMember other) => TypeMap == other?.TypeMap;
        public override int GetHashCode() => TypeMap.GetHashCode();
    }
}