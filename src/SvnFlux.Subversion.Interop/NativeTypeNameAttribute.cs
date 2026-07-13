namespace SvnFlux.Subversion.Interop;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
internal sealed class NativeTypeNameAttribute(string name) : Attribute {
    public string Name { get; } = name;
}
