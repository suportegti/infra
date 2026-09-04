using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public interface IUserService
{
    Task<AppUser?> ValidarCredenciaisAsync(string username, string password);

    Task RegistrarLoginAsync(int userId);
}
