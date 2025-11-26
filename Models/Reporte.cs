namespace StopFire.Api.Models;

public class Reporte
{
    public int Id { get; set; }
    public int IdUsuario { get; set; }                 
    public string? Descripcion { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public string? Direccion { get; set; }
    public string? FotoUrl { get; set; }              
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public string Estado { get; set; } = "PENDIENTE";
    public int? Confirmaciones { get; set; } = 0;
    public Usuario? Usuario { get; set; }               
}