using System.Collections.Concurrent;
using System.Reflection;

namespace ClientPrefs_GoldKingZ;

internal sealed class PrefsTypeInfo
{
    private static readonly ConcurrentDictionary<Type, PrefsTypeInfo> _cache = new();

    public const string NameColumn = "PlayerName";
    public const string PkColumn = "PlayerSteamID";
    public const string DateColumn = "DateAndTime";

    private static readonly string[] ReservedNames = { NameColumn, PkColumn, DateColumn };

    public Type Type { get; }
    public PropertyInfo[] Props { get; }
    public IReadOnlyDictionary<string, PropertyInfo> ByName { get; }

    private PrefsTypeInfo(Type t)
    {
        Type  = t;
        Props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var p in Props)
        {
            foreach (var reserved in ReservedNames)
            {
                if (string.Equals(p.Name, reserved, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"ClientPrefs: Type {t.Name} Declares A Property Named '{p.Name}', But " +
                        $"'{reserved}' Is Reserved And Injected Automatically. Remove It From your POCO.");
            }
        }

        ByName = Props.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
    }

    public static PrefsTypeInfo Of(Type t) => _cache.GetOrAdd(t, x => new PrefsTypeInfo(x));
    public static PrefsTypeInfo Of<T>() => Of(typeof(T));

    public bool DiffersFromBaseline(object payload, object baseline)
    {
        foreach (var p in Props)
        {
            var cur = p.GetValue(payload);
            var baseVal = p.GetValue(baseline);
            if (!object.Equals(cur, baseVal)) return true;
        }
        return false;
    }

    public object Clone(object src)
    {
        var dst = Activator.CreateInstance(Type)!;
        foreach (var p in Props)
            p.SetValue(dst, p.GetValue(src));
        return dst;
    }

    public static object DefaultValueOf(Type type)
    {
        if (type == typeof(string))   return string.Empty;
        if (type == typeof(DateTime)) return DateTime.MinValue;
        return type.IsValueType ? Activator.CreateInstance(type)! : string.Empty;
    }

    public void FillNullStrings(object record)
    {
        foreach (var p in Props)
            if (p.PropertyType == typeof(string) && p.GetValue(record) == null)
                p.SetValue(record, string.Empty);
    }
}