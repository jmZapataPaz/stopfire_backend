using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using StopFire.Api.Data;
using StopFire.Api.Dtos.bombero;
using StopFire.Api.Hubs;
using StopFire.Api.Models;
using System.Security.Claims;
using System.Collections.Concurrent;

namespace StopFire.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BomberoController : ControllerBase
{
    private readonly StopFireDbContext _db;
    private readonly IHubContext<NotificacionesHub> _hub;
    private readonly GeometryFactory _geometryFactory;
    private static readonly ConcurrentDictionary<int, HashSet<int>> _rechazosPorReporte = new();

    public BomberoController(StopFireDbContext db, IHubContext<NotificacionesHub> hub)
    {
        _db = db;
        _hub = hub;
        _geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    }

    private static int? GetUserId(ClaimsPrincipal user)
    {
        var s = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return int.TryParse(s, out var id) ? id : null;
    }

    private async Task<List<Estacion>> ObtenerEstacionesOrdenadasAsync(Reporte reporte, CancellationToken ct)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(reporte.Longitud!.Value, reporte.Latitud!.Value));

        var contenedoras = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null && e.Cobertura.Contains(point))
            .ToListAsync(ct);

        if (contenedoras.Count > 0)
        {
            return contenedoras
                .OrderBy(e => e.Cobertura!.Centroid.Distance(point))
                .ToList();
        }

        return await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null)
            .OrderBy(e => e.Cobertura!.Distance(point))
            .ToListAsync(ct);
    }

    private async Task<Estacion?> ObtenerSiguienteCandidataAsync(Reporte reporte, CancellationToken ct)
    {
        var rechazadas = _rechazosPorReporte.GetOrAdd(reporte.Id, _ => new HashSet<int>());
        var ordenPrimario = await ObtenerEstacionesOrdenadasAsync(reporte, ct);
        var candidata = ordenPrimario.FirstOrDefault(e => !rechazadas.Contains(e.Id));
        if (candidata != null) return candidata;

        var point = _geometryFactory.CreatePoint(new Coordinate(reporte.Longitud!.Value, reporte.Latitud!.Value));
        var fallback = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null)
            .OrderBy(e => e.Cobertura!.Distance(point))
            .ToListAsync(ct);

        return fallback.FirstOrDefault(e => !rechazadas.Contains(e.Id));
    }

    [HttpPost("reportes/{id:int}/aceptar")]
    public async Task<IActionResult> AceptarReporte(int id, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();
        var reporte = await _db.Reportes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reporte == null) return NotFound();
        if (reporte.Estado == "ACEPTADO") return Ok(new { mensaje = "Ya aceptado." });
        var siguiente = await ObtenerSiguienteCandidataAsync(reporte, ct);
        if (siguiente == null) return BadRequest(new { mensaje = "No hay estaci�n candidata." });
        if (siguiente.IdUsuario != uid.Value) return Forbid("La estaci�n candidata no pertenece al usuario.");
        var asign = new Asignacion
        {
            IdEstacion = siguiente.Id,
            IdReporte = reporte.Id,
            RespuestaUtc = DateTime.UtcNow,
            CronometroMinutos = 0
        };
        _db.Asignaciones.Add(asign);
        reporte.Estado = "ACEPTADO";
        await _db.SaveChangesAsync(ct);

        var asignPayload = new {
            asign.Id,
            asign.IdReporte,
            asign.IdEstacion,
            asign.CronometroMinutos,
            asign.RespuestaUtc
        };
        await _hub.Clients.Group($"estacion_{siguiente.Id}")
            .SendCoreAsync("AsignacionCreada", new object[] { asignPayload }, ct);
        await _hub.Clients.All
            .SendCoreAsync("AsignacionCreada", new object[] { asignPayload }, ct);
        await _hub.Clients.Group($"estacion_{siguiente.Id}")
            .SendCoreAsync("ReporteAsignado", new object[] {
                new {
                    ReporteId = reporte.Id,
                    EstacionId = siguiente.Id,
                    Estado = reporte.Estado,
                    asignacion = asignPayload
                }
            }, ct);

        await _hub.Clients.All.SendCoreAsync("ReporteEstado", new object[] {
            new { Id = reporte.Id, Estado = reporte.Estado, EstacionId = siguiente.Id }
        }, ct);

        return Ok(new { mensaje = "Aceptado", IdReporte = reporte.Id, IdEstacion = siguiente.Id });
    }

    [HttpPost("reportes/{id:int}/rechazar")]
    public async Task<IActionResult> RechazarReporte(int id, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var reporte = await _db.Reportes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reporte == null) return NotFound();
        if (reporte.Estado == "ACEPTADO") return BadRequest(new { mensaje = "El reporte ya fue aceptado." });

        var candidata = await ObtenerSiguienteCandidataAsync(reporte, ct);
        if (candidata == null) return BadRequest(new { mensaje = "No hay estaci�n candidata para rechazar." });
        if (candidata.IdUsuario != uid.Value) return Forbid("La estaci�n candidata no pertenece al usuario.");

        var rechazadas = _rechazosPorReporte.GetOrAdd(reporte.Id, _ => new HashSet<int>());
        lock (rechazadas) { rechazadas.Add(candidata.Id); }

        var siguiente = await ObtenerSiguienteCandidataAsync(reporte, ct);

        await _hub.Clients.All.SendCoreAsync("ReporteRechazado", new object[] {
            new { ReporteId = reporte.Id, EstacionRechazo = candidata.Id, NuevaCandidata = siguiente?.Id }
        }, ct);

        if (siguiente != null)
        {
            await _hub.Clients.Group($"estacion_{siguiente.Id}")
                .SendCoreAsync("ReportePendiente", new object[] {
                    new {
                        ReporteId = reporte.Id,
                        Candidata = siguiente.Id,
                        Descripcion = reporte.Descripcion,
                        reporte.Latitud,
                        reporte.Longitud,
                        Estado = reporte.Estado
                    }
                }, ct);
        }

        return Ok(new { mensaje = "Rechazado", IdReporte = reporte.Id, IdEstacion = candidata.Id, siguiente = siguiente?.Id });
    }

    [HttpPost("reportes/{id:int}/mitigar")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> MitigarReporte(int id, CancellationToken ct)
    {
        var reporte = await _db.Reportes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reporte == null) return NotFound(new { mensaje = "Reporte no encontrado." });
        if (string.Equals(reporte.Estado, "MITIGADO", StringComparison.OrdinalIgnoreCase))
            return Ok(new { mensaje = "Ya estaba mitigado.", Id = reporte.Id, Estado = reporte.Estado });

        if (!string.Equals(reporte.Estado, "ACEPTADO", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { mensaje = "Solo se puede mitigar un reporte ACEPTADO.", EstadoActual = reporte.Estado });

        reporte.Estado = "MITIGADO";
        await _db.SaveChangesAsync(ct);
        await _hub.Clients.All.SendAsync("ReporteEstado", new { Id = reporte.Id, Estado = reporte.Estado }, ct);
        await _hub.Clients.All.SendAsync("ReporteMitigado", new { Id = reporte.Id }, ct);

        return Ok(new { Id = reporte.Id, Estado = reporte.Estado });
    }

    [HttpGet("estaciones/{idEstacion:int}/historial-aceptados")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetHistorialAceptadosPorEstacion(int idEstacion, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();
        var estacion = await _db.Estaciones.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == idEstacion, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });
        if (estacion.IdUsuario != uid.Value)
            return Forbid("La estación no pertenece al usuario autenticado.");
        var datos = await (
            from a in _db.Asignaciones.AsNoTracking()
            join r in _db.Reportes.AsNoTracking() on a.IdReporte equals r.Id
            join u in _db.Usuarios.AsNoTracking() on r.IdUsuario equals u.Id
            where a.IdEstacion == idEstacion && r.Estado == "MITIGADO" 
            orderby r.FechaCreacion descending, a.Id descending
            select new BomberoReporteAceptadoHistorialDto
            {
                IdReporte = r.Id,
                IdAsignacion = a.Id,
                IdEstacion = a.IdEstacion,
                Descripcion = r.Descripcion,
                NombreCompleto = ((u.Nombre ?? "") + " " + (u.Apellido ?? "")).Trim(),
                Ci = u.Ci,
                Celular = u.Celular,
                Latitud = r.Latitud,
                Longitud = r.Longitud,
                FotoUrl = r.FotoUrl,
                FechaCreacion = r.FechaCreacion
            }
        ).ToListAsync(ct);

        return Ok(datos);
    }

    
}