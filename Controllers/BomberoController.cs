using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using StopFire.Api.Data;
using StopFire.Api.Dtos.bombero;
using StopFire.Api.Hubs;
using StopFire.Api.Models;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Globalization;

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
        if (candidata == null) return BadRequest(new { mensaje = "No hay estación candidata para rechazar." });
        if (candidata.IdUsuario != uid.Value) return Forbid("La estación candidata no pertenece al usuario.");

        var rechazadas = _rechazosPorReporte.GetOrAdd(reporte.Id, _ => new HashSet<int>());
        lock (rechazadas) { rechazadas.Add(candidata.Id); }

        var siguiente = await ObtenerSiguienteCandidataAsync(reporte, ct);

        // Cargar datos del usuario para incluirlos en el payload (mismo shape que ReporteCreado)
        var u = await _db.Usuarios
            .AsNoTracking()
            .Where(x => x.Id == reporte.IdUsuario)
            .Select(x => new { x.Nombre, x.Apellido, x.Ci, x.Correo, x.Celular })
            .FirstAsync(ct);

        var rechazadoPayload = new
        {
            Id = reporte.Id,
            IdUsuario = reporte.IdUsuario,
            Descripcion = reporte.Descripcion,
            FotoUrl = reporte.FotoUrl,
            Latitud = reporte.Latitud,
            Longitud = reporte.Longitud,
            Estado = reporte.Estado,
            PrimeraCandidata = siguiente?.Id,
            usuarioNombre = $"{u.Nombre} {u.Apellido}".Trim(),
            usuarioCi = u.Ci,
            usuarioCelular = u.Celular,
            usuarioEmail = u.Correo
        };

        await _hub.Clients.All.SendCoreAsync("ReporteRechazado", new object[] { rechazadoPayload }, ct);

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

    [HttpPatch("estaciones/{id:int}")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> ActualizarEstacion(int id, [FromBody] 
    BomberoActualizarEstacionDto dto, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });

        if (estacion.IdUsuario != uid.Value)
            return Forbid("La estación no pertenece al usuario autenticado.");
        if (dto.Nombre is not null) estacion.Nombre = dto.Nombre.Trim();
        if (dto.DescripcionDireccion is not null) estacion.DescripcionDireccion 
        = dto.DescripcionDireccion.Trim();
        if (dto.Celular is not null) estacion.Celular = dto.Celular.Trim();
        await _db.SaveChangesAsync(ct);
        await _hub.Clients.Group($"estacion_{estacion.Id}")
            .SendCoreAsync("EstacionActualizada", new object[] {
                new {
                    Id = estacion.Id,
                    Nombre = estacion.Nombre,
                    DescripcionDireccion = estacion.DescripcionDireccion,
                    Celular = estacion.Celular
                }
            }, ct);

        return Ok(new
        {
            estacion.Id,
            estacion.Nombre,
            estacion.DescripcionDireccion,
            estacion.Celular
        });
    }

    [HttpGet("estaciones/{id:int}")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetEstacionPorId(int id, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });
        if (estacion.IdUsuario != uid.Value)
            return Forbid("La estación no pertenece al usuario autenticado.");

        var dto = new BomberoEstacionDetalleDto
        {
            Id = estacion.Id,
            IdUsuario = estacion.IdUsuario,
            Nombre = estacion.Nombre,
            DescripcionDireccion = estacion.DescripcionDireccion,
            Celular = estacion.Celular,
            Latitud = double.TryParse(estacion.Latitud, out var lat) ? lat : (double?)null,
            Longitud = double.TryParse(estacion.Longitud, out var lon) ? lon : (double?)null,
            Estado = estacion.Estado,
            CoberturaWkt = estacion.Cobertura != null ? new WKTWriter().Write(estacion.Cobertura) : null
        };
        return Ok(dto);
    }

    [HttpGet("mi-estacion")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetMiEstacion(CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdUsuario == uid.Value, ct);
        if (estacion == null) return NotFound(new { mensaje = "No tiene estación asignada." });

        var dto = new BomberoEstacionDetalleDto
        {
            Id = estacion.Id,
            IdUsuario = estacion.IdUsuario,
            Nombre = estacion.Nombre,
            DescripcionDireccion = estacion.DescripcionDireccion,
            Celular = estacion.Celular,
            Latitud = double.TryParse(estacion.Latitud, out var lat) ? lat : (double?)null,
            Longitud = double.TryParse(estacion.Longitud, out var lon) ? lon : (double?)null,
            Estado = estacion.Estado,
            CoberturaWkt = estacion.Cobertura != null ? new WKTWriter().Write(estacion.Cobertura) : null
        };
        return Ok(dto);
    }

    // AGREGADO: métricas heatmap por estación (solo mitigados) -> clustering por radio (2km)
    [HttpGet("estaciones/{idEstacion:int}/metricas/heatmap")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetHeatmapByEstacion(int idEstacion, [FromQuery] int? month, [FromQuery] int? year, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.AsNoTracking().FirstOrDefaultAsync(e => e.Id == idEstacion, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });
        if (estacion.IdUsuario != uid.Value) return Forbid("La estación no pertenece al usuario autenticado.");

        // Determinar mes/año aplicados (por defecto: fecha del sistema UTC)
        var now = DateTime.UtcNow;
        var applyMonth = month ?? now.Month;
        var applyYear = year ?? now.Year;
        // rango [inicio, fin) para filtrar FechaCreacion
        DateTime start;
        try
        {
            start = new DateTime(applyYear, applyMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            // en caso de parámetros inválidos, usar mes/año actual
            start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        var end = start.AddMonths(1);
 
        // Traer reportes mitigados asignados a esta estación (usando Asignaciones para la relación)
        var items = await (
            from a in _db.Asignaciones.AsNoTracking()
            join r in _db.Reportes.AsNoTracking() on a.IdReporte equals r.Id
            where a.IdEstacion == idEstacion
                  && r.Estado == "MITIGADO"
                  && r.Latitud != null && r.Longitud != null
                  && r.FechaCreacion >= start && r.FechaCreacion < end
            select new { r.Id, Lat = r.Latitud, Lon = r.Longitud, r.FechaCreacion }
        ).ToListAsync(ct);
 
        if (items.Count == 0)
        {
            return Ok(new { total = 0, points = Array.Empty<object>(), message = "No hay datos para hacer la métrica.", month = start.Month, year = start.Year });
        }
 
        // Clustering por radio (metros)
        const double radiusMeters = 2000.0;
 
        // helper: Haversine distance in meters
        static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000; // Earth radius in meters
            double toRad(double deg) => deg * Math.PI / 180.0;
            var dLat = toRad(lat2 - lat1);
            var dLon = toRad(lon2 - lon1);
            var a = Math.Sin(dLat/2) * Math.Sin(dLat/2) +
                    Math.Cos(toRad(lat1)) * Math.Cos(toRad(lat2)) *
                    Math.Sin(dLon/2) * Math.Sin(dLon/2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
            return R * c;
        }
 
        // clusters: centroid + members (to compute radius)
        var clusters = new List<(double Lat, double Lon, List<(int Id, double Lat, double Lon)> Members, DateTime FirstFecha)>();
 
        foreach (var it in items)
        {
            var lat = it.Lat!.Value;
            var lon = it.Lon!.Value;
            bool added = false;
 
            for (int i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                var d = DistanceMeters(lat, lon, c.Lat, c.Lon);
                if (d <= radiusMeters)
                {
                    c.Members.Add((it.Id, lat, lon));
                    if (it.FechaCreacion < c.FirstFecha) c.FirstFecha = it.FechaCreacion;
                    // update centroid as average of member coordinates
                    var n = c.Members.Count;
                    c.Lat = (c.Lat * (n - 1) + lat) / n;
                    c.Lon = (c.Lon * (n - 1) + lon) / n;
                    clusters[i] = c;
                    added = true;
                    break;
                }
            }
 
            if (!added)
            {
                clusters.Add((Lat: lat, Lon: lon, Members: new List<(int, double, double)> { (it.Id, lat, lon) }, FirstFecha: it.FechaCreacion));
            }
        }
 
        // proyectar resultado y calcular radiusMeters por cluster (máx distancia a centroid + buffer)
        var result = clusters
            .Select(c =>
            {
                // calcular radio en metros
                var maxDist = c.Members.Count == 0 ? 0.0 :
                    c.Members.Max(m => DistanceMeters(c.Lat, c.Lon, m.Lat, m.Lon));
                var radius = Math.Max(150.0, maxDist + 100.0); // al menos 150m, buffer 100m
 
                return new {
                    Lat = Math.Round(c.Lat, 6),
                    Lon = Math.Round(c.Lon, 6),
                    Count = c.Members.Count,
                    ReporteIds = c.Members.Select(m => m.Id).ToArray(),
                    FirstFecha = c.FirstFecha.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    RadiusMeters = Math.Round(radius, 1)
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList();
 
        return Ok(new { total = items.Count, points = result, month = start.Month, year = start.Year });
    }
}