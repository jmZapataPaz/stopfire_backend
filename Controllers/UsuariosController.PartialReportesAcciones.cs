using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StopFire.Api.Models;
using System.Security.Claims;
using NetTopologySuite.Geometries;
using System.Globalization;
using System.Collections.Concurrent;

namespace StopFire.Api.Controllers;

public partial class UsuariosController
{
    private static readonly ConcurrentDictionary<int, HashSet<int>> _rechazosPorReporte = new();

    private async Task<List<Estacion>> ObtenerEstacionesOrdenadasAsync(Reporte reporte, CancellationToken ct)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(reporte.Longitud!.Value, reporte.Latitud!.Value));

        var contenedoras = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null && e.Cobertura.Contains(point))
            .ToListAsync(ct);

        List<Estacion> orden;
        if (contenedoras.Count > 0)
        {
            orden = contenedoras
                .OrderBy(e => e.Cobertura!.Centroid.Distance(point))
                .ToList();
        }
        else
        {
            orden = await _db.Estaciones
                .AsNoTracking()
                .Where(e => e.Estado && e.Cobertura != null)
                .OrderBy(e => e.Cobertura!.Distance(point))
                .ToListAsync(ct);
        }
        return orden;
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
    [Authorize]
    public async Task<IActionResult> AceptarReporte(int id, CancellationToken ct)
    {
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(uidStr, out var uid)) return Unauthorized();
        var reporte = await _db.Reportes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reporte == null) return NotFound();
        if (reporte.Estado == "ACEPTADO") return Ok(new { mensaje = "Ya aceptado." });
        var siguiente = await ObtenerSiguienteCandidataAsync(reporte, ct);
        if (siguiente == null) return BadRequest(new { mensaje = "No hay estación candidata." });
        if (siguiente.IdUsuario != uid)
            return Forbid("La estación candidata no pertenece al usuario.");

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

        await _hub.Clients.Group($"estacion_{siguiente.Id}")
            .SendCoreAsync("ReporteAsignado", new object[] {
                new {
                    ReporteId = reporte.Id,
                    EstacionId = siguiente.Id,
                    Estado = reporte.Estado,
                    asignacion = new {
                        asign.Id,
                        asign.IdEstacion,
                        asign.IdReporte,
                        asign.CronometroMinutos,
                        asign.RespuestaUtc
                    }
                }
            }, ct);

        await _hub.Clients.All.SendCoreAsync("ReporteEstado", new object[] {
            new { Id = reporte.Id, Estado = reporte.Estado, EstacionId = siguiente.Id }
        }, ct);

        return Ok(new { mensaje = "Aceptado", IdReporte = reporte.Id, IdEstacion = siguiente.Id });
    }

    [HttpPost("reportes/{id:int}/rechazar")]
    [Authorize]
    public async Task<IActionResult> RechazarReporte(int id, CancellationToken ct)
    {
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(uidStr, out var uid)) return Unauthorized();

        var reporte = await _db.Reportes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reporte == null) return NotFound();
        if (reporte.Estado == "ACEPTADO")
            return BadRequest(new { mensaje = "El reporte ya fue aceptado." });

        var candidata = await ObtenerSiguienteCandidataAsync(reporte, ct);
        if (candidata == null)
            return BadRequest(new { mensaje = "No hay estación candidata para rechazar." });
        if (candidata.IdUsuario != uid)
            return Forbid("La estación candidata no pertenece al usuario.");

        var rechazadas = _rechazosPorReporte.GetOrAdd(reporte.Id, _ => new HashSet<int>());
        lock (rechazadas) { rechazadas.Add(candidata.Id); }

        var siguiente = await ObtenerSiguienteCandidataAsync(reporte, ct);

        await _hub.Clients.All.SendCoreAsync("ReporteRechazado", new object[] {
            new {
                ReporteId = reporte.Id,
                EstacionRechazo = candidata.Id,
                NuevaCandidata = siguiente?.Id
            }
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
}