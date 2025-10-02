using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StopFire.Api.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StopFire.Api.Hubs;

[Authorize]
public class NotificacionesHub : Hub
{
    private readonly StopFire.Api.Data.StopFireDbContext _db;
    public NotificacionesHub(StopFire.Api.Data.StopFireDbContext db) => _db = db;

    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"[Hub] Conectado {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public async Task JoinEstacion(int idEstacion)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId))
            throw new HubException("Usuario no válido.");

        var owns = await _db.Estaciones
            .AsNoTracking()
            .AnyAsync(e => e.Id == idEstacion && e.IdUsuario == userId);

        if (!owns)
            throw new HubException("No autorizado para esa estación.");

        var groupName = $"estacion_{idEstacion}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        Console.WriteLine($"[Hub] {Context.ConnectionId} unido a {groupName}");
    }

    public Task LeaveEstacion(int idEstacion)
    {
        var groupName = $"estacion_{idEstacion}";
        Console.WriteLine($"[Hub] {Context.ConnectionId} sale de {groupName}");
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}