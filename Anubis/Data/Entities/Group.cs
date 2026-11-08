using Microsoft.AspNetCore.Identity;

namespace k6labs.Anubis.Data.Entities;

/// <summary>An Anubis group (e.g. "admin").</summary>
public class Group : IdentityRole
{
    public Group()
    {
    }

    public Group(string groupName)
        : base(groupName)
    {
    }
}
