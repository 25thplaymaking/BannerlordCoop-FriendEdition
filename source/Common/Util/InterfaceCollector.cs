using Common.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Common.Util;

public static class InterfaceCollector
{
    public static IEnumerable<Type> GetInterfaces<T>(string @namespace)
    {
        return AppDomain.CurrentDomain.GetDomainTypes(@namespace)
            .Where(t => typeof(T).IsAssignableFrom(t) &&
                        t.IsConcrete());
    }

    /// <summary>
    /// Discovers implementations from one exact assembly. Runtime DI modules must use this overload:
    /// namespace prefixes are not ownership boundaries, and a test/plugin assembly can legitimately
    /// contain types under a production namespace such as GameInterface.Tests.
    /// </summary>
    public static IEnumerable<Type> GetInterfaces<T>(string @namespace, Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));

        IEnumerable<Type> types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(type => type != null);
        }

        string prefix = string.IsNullOrEmpty(@namespace) ? string.Empty : @namespace;
        return types.Where(type =>
            type.Namespace != null &&
            type.Namespace.StartsWith(prefix, StringComparison.Ordinal) &&
            typeof(T).IsAssignableFrom(type) &&
            type.IsConcrete());
    }

    public static bool IsConcrete(this Type type)
    {
        return type.IsClass &&
               type.IsGenericType == false &&
               type.IsAbstract == false;
    }
}
