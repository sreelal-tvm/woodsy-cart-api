using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WoodsyCart.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected int? OptionalUserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    protected string UserName => User.FindFirstValue(ClaimTypes.Name) ?? "Admin";
}

[Authorize(Policy = "Admin")]
[Route("api/admin/[controller]")]
public abstract class AdminControllerBase : ApiControllerBase;
