using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Data.Entities;

/// <summary>An Anubis role (e.g. "admin").</summary>
public class Role : IdentityRole
{
    public Role()
    {
    }

    public Role(string roleName)
        : base(roleName)
    {
    }
}
