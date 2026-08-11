using System.Collections;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal static class FourberieRoleAuthority
{
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
