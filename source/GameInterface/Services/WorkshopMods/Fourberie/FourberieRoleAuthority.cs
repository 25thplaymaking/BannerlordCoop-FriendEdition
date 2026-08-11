using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieRoleSnapshot
{
    private readonly bool hadPaymaster;
    private readonly string paymaster;
    private readonly bool hadEnforcer;
    private readonly string enforcer;

    public FourberieRoleSnapshot(IDictionary roles)
    {
        hadPaymaster = roles?.Contains("paymaster") == true;
        paymaster = hadPaymaster ? roles["paymaster"] as string : null;
        hadEnforcer = roles?.Contains("enforcer") == true;
        enforcer = hadEnforcer ? roles["enforcer"] as string : null;
    }

    public void Restore(IDictionary roles)
    {
        if (roles == null) return;
        if (hadPaymaster) roles["paymaster"] = paymaster;
        else roles.Remove("paymaster");
        if (hadEnforcer) roles["enforcer"] = enforcer;
        else roles.Remove("enforcer");
    }
}

internal static class FourberieRoleAuthority
{
    public static FourberieRoleSnapshot Capture(IDictionary roles) => new FourberieRoleSnapshot(roles);

    public static int RoleCode(string role) => role switch
    {
        "paymaster" => 1,
        "enforcer" => 2,
        _ => 0,
    };

    public static string RoleName(int roleCode) => roleCode switch
    {
        1 => "paymaster",
        2 => "enforcer",
        _ => null,
    };

    public static bool TryAssign(IDictionary roles, int roleCode, string heroId, out string failure)
    {
        string role = RoleName(roleCode);
        if (roles == null || role == null || string.IsNullOrEmpty(heroId))
            return Fail("invalid Fourberie role assignment", out failure);
        roles[role] = heroId;
        failure = null;
        return true;
    }

    public static bool TryRemove(IDictionary roles, int roleCode, out string failure)
    {
        string role = RoleName(roleCode);
        if (roles == null || role == null)
            return Fail("invalid Fourberie role removal", out failure);
        roles.Remove(role);
        failure = null;
        return true;
    }

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }
}
