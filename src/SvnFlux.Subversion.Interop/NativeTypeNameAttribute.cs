namespace SvnFlux.Subversion.Interop;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class NativeTypeNameAttribute(string name) : Attribute {
    public string Name { get; } = name;
}
