namespace StopFire.Api.Dtos.bombero;

public sealed class BomberoReporteAceptadoHistorialDto
{
    public int IdReporte { get; set; }
    public int IdAsignacion { get; set; }
    public int IdEstacion { get; set; }
    public string? Descripcion { get; set; }
    public string? NombreCompleto { get; set; }
    public string? Ci { get; set; }
    public string? Celular { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public string? FotoUrl { get; set; }

    public DateTime? FechaCreacion { get; set; }
}