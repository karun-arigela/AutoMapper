﻿using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace AutoMapper.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class PrimitiveHelper
    {
        public static TValue GetOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            dictionary.TryGetValue(key, out TValue value);
            return value;
        }
        public static bool IsEnumToEnum(this in TypePair context) => context.SourceType.IsEnum && context.DestinationType.IsEnum;
        public static bool IsUnderlyingTypeToEnum(this in TypePair context) =>
            context.DestinationType.IsEnum && context.SourceType.IsAssignableFrom(Enum.GetUnderlyingType(context.DestinationType));
        public static bool IsEnumToUnderlyingType(this in TypePair context) =>
            context.SourceType.IsEnum && context.DestinationType.IsAssignableFrom(Enum.GetUnderlyingType(context.SourceType));
    }
}