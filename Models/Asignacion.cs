using System;

namespace StopFire.Api.Models;

public class Asignacion
{
    public int Id { get; set; }
    public int IdReporte { get; set; }
    public int IdEstacion { get; set; }

    public DateTime? RespuestaUtc { get; set; }

    public int CronometroMinutos { get; set; } = 2;

    public Reporte? Reporte { get; set; }
    public Estacion? Estacion { get; set; }
}