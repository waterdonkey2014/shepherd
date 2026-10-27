// kit #6 fps (vita/gameaot/profadd.py build): every method of the given assemblies as "image|ns.Class[/Nested]|name|paramcount|ret(params)" in
// Mono's signature naming (mono_signature_full_name), for building .aotprofile records. usage: sigdump <dll>...
// Its output names the owner's game methods when run on the game's DLLs: keep it owner-local (never in git).
// Build: dotnet build -c Release -o <dir> (System.Reflection.Metadata, in the box); run with DOTNET_ROOT=$HOME/.dotnet.
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    using var fs = File.OpenRead(path); using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader(); var img = md.GetString(md.GetAssemblyDefinition().Name);
    var prov = new Prov(md);
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th); var cls = Prov.TypeDefName(md, th);
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh); var sig = m.DecodeSignature(prov, null);
            Console.WriteLine($"{img}|{cls}|{md.GetString(m.Name)}|{sig.ParameterTypes.Length}|{sig.ReturnType}({string.Join(",", sig.ParameterTypes)})|{sig.GenericParameterCount}");
        }
    }
}

class Prov : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader md; public Prov(MetadataReader m) { md = m; }
    public static string TypeDefName(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h); var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeDefName(md, decl) + "/" + name;
        var ns = md.GetString(td.Namespace); return ns.Length > 0 ? ns + "." + name : name;
    }
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.Char => "char", PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.Int16 => "int16", PrimitiveTypeCode.UInt16 => "uint16", PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint", PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Single => "single",
        PrimitiveTypeCode.Double => "double", PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object", PrimitiveTypeCode.IntPtr => "intptr",
        PrimitiveTypeCode.UIntPtr => "uintptr", PrimitiveTypeCode.TypedReference => "typedbyref", _ => c.ToString()
    };
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => TypeDefName(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
    {
        var tr = r.GetTypeReference(h); var name = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(r, (TypeReferenceHandle)tr.ResolutionScope, k) + "/" + name;
        var ns = r.GetString(tr.Namespace); return ns.Length > 0 ? ns + "." + name : name;
    }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(", ", a) + ">";
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
}
