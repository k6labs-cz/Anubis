using OpenIddict.EntityFrameworkCore.Models;

namespace k6labs.Anubis.Data.Entities;

public class Token : OpenIddictEntityFrameworkCoreToken<string, Client, Authorization>
{
}
