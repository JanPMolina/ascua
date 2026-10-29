namespace Ascua.Application.Users;

/// <summary>Rol que viaja en el claim <c>role</c> del JWT (en minúsculas: <c>player</c> o <c>admin</c>).</summary>
public enum UserRole
{
    Player,
    Admin,
}

public static class UserRoleNames
{
    public const string Player = "player";
    public const string Admin = "admin";

    public static string ToClaimValue(this UserRole role) => role switch
    {
        UserRole.Admin => Admin,
        _ => Player,
    };
}
