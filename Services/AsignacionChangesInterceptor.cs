using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using StopFire.Api.Hubs;
using StopFire.Api.Models;

public class AsignacionChangesInterceptor : SaveChangesInterceptor
{
    private readonly IHubContext<NotificacionesHub> _hub;
    private readonly ConcurrentDictionary<DbContext, List<PendingAsignacionEvent>> _buffer = new();

    public AsignacionChangesInterceptor(IHubContext<NotificacionesHub> hub)
    {
        _hub = hub;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var ctx = eventData.Context;
        if (ctx == null) return await base.SavingChangesAsync(eventData, result, cancellationToken);

        var changes = ctx.ChangeTracker.Entries<Asignacion>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => new PendingAsignacionEvent
            {
                Kind = e.State,
                Snapshot = TakeSnapshot(e)
            })
            .ToList();

        if (changes.Count > 0)
            _buffer[ctx] = changes;

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var ctx = eventData.Context;
        if (ctx != null && _buffer.TryRemove(ctx, out var list) && list.Count > 0)
        {
            foreach (var ev in list)
            {
                var payload = new
                {
                    Id = ev.Snapshot.Id,
                    IdReporte = ev.Snapshot.IdReporte,
                    IdEstacion = ev.Snapshot.IdEstacion,
                    CronometroMinutos = ev.Snapshot.CronometroMinutos,
                    RespuestaUtc = ev.Snapshot.RespuestaUtc
                };

                var evtName = ev.Kind switch
                {
                    EntityState.Added => "AsignacionCreada",
                    EntityState.Modified => "AsignacionActualizada",
                    EntityState.Deleted => "AsignacionEliminada",
                    _ => "AsignacionActualizada"
                };

                await _hub.Clients.All.SendAsync(evtName, payload, cancellationToken);
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
            _buffer.TryRemove(eventData.Context, out _);
        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static AsignacionSnapshot TakeSnapshot(EntityEntry<Asignacion> e)
    {
        var a = e.Entity;
        return new AsignacionSnapshot
        {
            Id = a.Id,
            IdReporte = a.IdReporte,
            IdEstacion = a.IdEstacion,
            CronometroMinutos = a.CronometroMinutos,
            RespuestaUtc = a.RespuestaUtc
        };
    }

    private sealed class PendingAsignacionEvent
    {
        public EntityState Kind { get; set; }
        public AsignacionSnapshot Snapshot { get; set; } = default!;
    }

    private sealed class AsignacionSnapshot
    {
        public int Id { get; set; }
        public int IdReporte { get; set; }
        public int IdEstacion { get; set; }
        public int? CronometroMinutos { get; set; }
        public DateTime? RespuestaUtc { get; set; }
    }
}