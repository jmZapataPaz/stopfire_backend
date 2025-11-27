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
                FechaCreacion = r.FechaCreacion,
                Direccion = r.Direccion, // NUEVO
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

    [HttpGet("estaciones/{idEstacion:int}/metricas/heatmap")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetHeatmapByEstacion(int idEstacion, [FromQuery] int? month, [FromQuery] int? year, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.AsNoTracking().FirstOrDefaultAsync(e => e.Id == idEstacion, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });
        if (estacion.IdUsuario != uid.Value) return Forbid("La estación no pertenece al usuario autenticado.");
        var now = DateTime.UtcNow;
        var applyMonth = month ?? now.Month;
        var applyYear = year ?? now.Year;
        DateTime start;
        try
        {
            start = new DateTime(applyYear, applyMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
        var end = start.AddMonths(1);
 
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
        const double radiusMeters = 2000.0;
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
        var result = clusters
            .Select(c =>
            {
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

    [HttpGet("estaciones/{idEstacion:int}/metricas/response-time")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetResponseTimeByEstacion(int idEstacion, [FromQuery] int? month, [FromQuery] int? year, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var estacion = await _db.Estaciones.AsNoTracking().FirstOrDefaultAsync(e => e.Id == idEstacion, ct);
        if (estacion == null) return NotFound(new { mensaje = "Estación no encontrada." });
        if (estacion.IdUsuario != uid.Value) return Forbid("La estación no pertenece al usuario autenticado.");

        var now = DateTime.UtcNow;
        var applyMonth = month ?? now.Month;
        var applyYear = year ?? now.Year;
        DateTime start;
        try { start = new DateTime(applyYear, applyMonth, 1, 0, 0, 0, DateTimeKind.Utc); }
        catch { start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); }
        var end = start.AddMonths(1);

        // CORRECCIÓN: filtrar por RespuestaUtc (fecha de aceptación) en lugar de FechaCreacion
        var items = await (
            from a in _db.Asignaciones.AsNoTracking()
            join r in _db.Reportes.AsNoTracking() on a.IdReporte equals r.Id
            where a.IdEstacion == idEstacion
                  && a.RespuestaUtc != null
                  && a.RespuestaUtc >= start && a.RespuestaUtc < end  // CAMBIO AQUÍ
            select new { AsignacionId = a.Id, ReporteId = r.Id, FechaCreacion = r.FechaCreacion, Respuesta = a.RespuestaUtc }
        ).ToListAsync(ct);

        if (items.Count == 0)
        {
            return Ok(new
            {
                averageMinutes = 0.0,
                distribution = new { green = 0, yellow = 0, orange = 0, red = 0 },
                month = start.Month,
                year = start.Year,
                message = "No hay datos para el periodo."
            });
        }

        DateTimeOffset? ToDto(object? v)
        {
            if (v == null) return null;
            if (v is DateTimeOffset dto) return dto.ToUniversalTime();
            if (v is DateTime dt)
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                {
                    if (DateTimeOffset.TryParse(dt.ToString("o"), out var p)) return p.ToUniversalTime();
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUniversalTime();
                }
                return new DateTimeOffset(dt).ToUniversalTime();
            }
            if (DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.ToUniversalTime();
            return null;
        }

        var secsList = new List<double>();
        foreach (var it in items)
        {
            var f = ToDto(it.FechaCreacion);
            var r = ToDto(it.Respuesta);
            if (!f.HasValue || !r.HasValue) continue;

            // truncar a segundos para eliminar microsegundos
            var fTr = new DateTimeOffset(f.Value.UtcDateTime.AddTicks(-(f.Value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);
            var rTr = new DateTimeOffset(r.Value.UtcDateTime.AddTicks(-(r.Value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);

            var diffSec = (rTr - fTr).TotalSeconds;
            if (diffSec < 0) diffSec = 0;  // evitar negativos
            secsList.Add(diffSec);
        }

        if (secsList.Count == 0)
        {
            return Ok(new
            {
                averageMinutes = 0.0,
                distribution = new { green = 0, yellow = 0, orange = 0, red = 0 },
                month = start.Month,
                year = start.Year,
                message = "No hay datos válidos para el periodo."
            });
        }

        // convertir a minutos con dos decimales
        var minutesList = secsList.Select(s => Math.Round(s / 60.0, 2)).ToList();
        var avg = Math.Round(minutesList.Average(), 2);

        // distribución por colores
        var green = minutesList.Count(m => m < 1.0);
        var yellow = minutesList.Count(m => m >= 1.0 && m < 10.0);
        var orange = minutesList.Count(m => m >= 10.0 && m < 21.0);
        var red = minutesList.Count(m => m >= 21.0);

        return Ok(new
        {
            averageMinutes = avg,
            distribution = new { green, yellow, orange, red },
            month = start.Month,
            year = start.Year
        });
    }

    [HttpGet("hidrantes")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetHidrantes(CancellationToken ct = default)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var writer = new GeoJsonWriter();

        var raw = await _db.Hidrantes
            .AsNoTracking()
            .Where(h => h.Estado) // SOLO ACTIVOS
            .Select(h => new { h.Id, h.Latitud, h.Longitud, h.Geom })
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var lista = raw.Select(h => new BomberoHidranteDto
        {
            Id = h.Id,
            Latitud = h.Latitud ?? h.Geom?.Y,
            Longitud = h.Longitud ?? h.Geom?.X,
            GeomWkt = h.Geom?.AsText(),
            GeomGeoJson = h.Geom != null ? writer.Write(h.Geom) : null,
            GeomInternal = h.Geom
        }).ToList();

        return Ok(lista);
    }

    [HttpGet("hidrantes/{id:int}")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> GetHidrantePorId(int id, CancellationToken ct = default)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var writer = new GeoJsonWriter();

        var h = await _db.Hidrantes
            .AsNoTracking()
            .Where(x => x.Id == id && x.Estado) // SOLO SI ACTIVO
            .Select(x => new { x.Id, x.Latitud, x.Longitud, x.Geom })
            .FirstOrDefaultAsync(ct);

        if (h == null) return NotFound(new { mensaje = "Hidrante no encontrado o inactivo." });

        var dto = new BomberoHidranteDto
        {
            Id = h.Id,
            Latitud = h.Latitud ?? h.Geom?.Y,
            Longitud = h.Longitud ?? h.Geom?.X,
            GeomWkt = h.Geom?.AsText(),
            GeomGeoJson = h.Geom != null ? writer.Write(h.Geom) : null,
            GeomInternal = h.Geom
        };

        return Ok(dto);
    }

    [HttpGet("reportes/mitigados")]
    [Authorize(Policy = "BomberoOnly")]
    public async Task<IActionResult> ListarReportesMitigados(
        [FromQuery] int? month,
        [FromQuery] int? year,
        CancellationToken ct = default)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized();

        var q = _db.Reportes.AsNoTracking()
            .Where(r => r.Estado.ToUpper() == "MITIGADO");

        if (year.HasValue)
            q = q.Where(r => r.FechaCreacion.Year == year.Value);
        if (month.HasValue)
            q = q.Where(r => r.FechaCreacion.Month == month.Value);

        var list = await q
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new {
                id = r.Id,
                descripcion = r.Descripcion,
                latitud = r.Latitud,
                longitud = r.Longitud,
                fotoUrl = r.FotoUrl,
                estado = r.Estado,
                fechaCreacion = r.FechaCreacion
            })
            .ToListAsync(ct);

        return Ok(list);
    }
}