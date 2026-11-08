using OpenIddict.EntityFrameworkCore.Models;

namespace k6labs.Anubis.Data.Entities;

public class Authorization : OpenIddictEntityFrameworkCoreAuthorization<string, Client, Token>
{
}
