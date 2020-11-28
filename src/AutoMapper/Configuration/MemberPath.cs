﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapper.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct MemberPath : IEquatable<MemberPath>
    {
        private readonly MemberInfo[] _members;
        public IReadOnlyCollection<MemberInfo> Members => _members;

        public static readonly MemberPath Empty = new MemberPath(Array.Empty<MemberInfo>());

        public MemberPath(Expression destinationExpression) : this(MemberVisitor.GetMemberPath(destinationExpression))
        {
        }

        public MemberPath(IEnumerable<MemberInfo> members)
        {
            _members = members.ToArray();
        }

        public MemberInfo Last => _members[_members.Length - 1];

        public MemberInfo First => _members[0];

        public int Length => _members.Length;

        public bool Equals(MemberPath other) => Members.SequenceEqual(other.Members);

        public override bool Equals(object obj)
        {
            if(obj is null) return false;
            return obj is MemberPath path && Equals(path);
        }

        public override int GetHashCode()
        {
            var hashCode = 0;
            foreach(var member in Members)
            {
                hashCode = HashCodeCombiner.CombineCodes(hashCode, member.GetHashCode());
            }
            return hashCode;
        }

        public override string ToString()
            => string.Join(".", Members.Select(mi => mi.Name));

        public static bool operator==(MemberPath left, MemberPath right) => left.Equals(right);

        public static bool operator!=(MemberPath left, MemberPath right) => !left.Equals(right);

        public bool StartsWith(MemberPath path)
        {
            if (path.Length > Length)
            {
                return false;
            }
            for (int index = 0; index < path.Length; index++)
            {
                if (_members[index] != path._members[index])
                {
                    return false;
                }
            }
            return true;
        }

        public MemberPath Concat(IEnumerable<MemberInfo> memberInfos) => new MemberPath(_members.Concat(memberInfos));
    }
}