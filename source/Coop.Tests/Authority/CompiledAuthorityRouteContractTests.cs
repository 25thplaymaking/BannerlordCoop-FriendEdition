using Common.Messaging;
using Coop.Core.Client;
using GameInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Coop.Tests.Authority;

public sealed class CompiledAuthorityRouteContractTests
{
    [Fact]
    public void ProductionRouteRequests_AreUniqueTypedCommandsAcrossEveryOwningAssembly()
    {
        Assembly[] productionAssemblies =
        {
            typeof(GameInterfaceModule).Assembly,
            typeof(ClientModule).Assembly,
        };

        var routes = productionAssemblies
            .SelectMany(assembly => assembly.GetTypes().Select(type => new
            {
                Assembly = assembly.GetName().Name,
                Request = type,
                Route = type.GetCustomAttribute<AuthorityRouteAttribute>(),
            }))
            .Where(entry => entry.Route != null)
            .ToArray();

        Assert.All(productionAssemblies, assembly =>
            Assert.Contains(routes, route => route.Request.Assembly == assembly));
        Assert.All(routes, route => Assert.True(
            typeof(ICommand).IsAssignableFrom(route.Request),
            $"{route.Assembly}/{route.Request.FullName}/{route.Route.RouteId} is not an ICommand."));

        AssertUnique(routes, route => route.Route.RouteId, "route ID");
        AssertUnique(routes, route => route.Request.FullName, "request type");
    }

    private static void AssertUnique<T>(IEnumerable<T> routes, Func<T, string> key, string label)
    {
        string[] duplicates = routes
            .GroupBy(key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(duplicates.Length == 0,
            $"Duplicate authority {label}(s): {string.Join(", ", duplicates)}");
    }
}
