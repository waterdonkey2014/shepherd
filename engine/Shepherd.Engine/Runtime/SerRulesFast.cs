using System.Reflection;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #5 B4 (track boot, vita/notes/kit5-boot.md): SerRules.Fields -- which fields Unity serializes, asked for every
/// script / serializable type the boot instantiates or fills (602 types to the Main Menu, vlx) -- read each field's custom attributes up to
/// three times (NonSerialized, SerializeReference, SerializeField: one GetCustomAttributesData decode each) and asked an assembly's name
/// (Assembly.GetName: a new AssemblyName every call) for every candidate field. The fast scan reads the attributes once per field and
/// remembers IsBcl per assembly and IsSerializableType per type: the same answers, so the same fields in the same order.
/// SHEPHERD_SERRULES_FAST = unset/1 (fast) | 0 (the kit #4 scan) | check (both, every type compared; the end line counts them).</summary>
[ProfileOnly]
public static class SerRulesFast
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_SERRULES_FAST") ?? "").Trim().ToLowerInvariant();
    public static readonly bool On = !(Setting is "0" or "off");
    public static readonly bool Check = Setting == "check";
    static int _compared, _differ;
    public static void Compare(Type t, FieldInfo[] fast, FieldInfo[] slow)
    {
        _compared++;
        bool same = fast.Length == slow.Length;
        for (int i = 0; same && i < fast.Length; i++) same = fast[i] == slow[i];
        if (same) return;
        _differ++;
        Log.Warn("[serrules] check: " + t.FullName + ": fast scan " + fast.Length.ToString() + " fields (" + string.Join(",", fast.Select(f => f.Name)) + "), kit #4 scan " + slow.Length.ToString() + " (" + string.Join(",", slow.Select(f => f.Name)) + ")");
    }
    public static string Summary() => Check ? "[serrules] check: " + _compared.ToString() + " types scanned both ways, " + _differ.ToString() + " differ" : null;
}
