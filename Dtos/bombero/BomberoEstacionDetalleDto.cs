namespace StopFire.Api.Dtos.bombero;

public sealed class BomberoEstacionDetalleDto
{
    public int Id { get; set; }
    public int IdUsuario { get; set; }
    public string? Nombre { get; set; }
    public string? DescripcionDireccion { get; set; }
    public string? Celular { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public bool Estado { get; set; }
    public string? CoberturaWkt { get; set; } // opcional: WKT del polígono
}