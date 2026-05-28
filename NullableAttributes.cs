using System;

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
internal sealed class NullableAttribute : Attribute
{
    public NullableAttribute(byte flag) { }
    public NullableAttribute(byte[] flags) { }
}

[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
internal sealed class NullableContextAttribute : Attribute
{
    public NullableContextAttribute(byte flag) { }
}
