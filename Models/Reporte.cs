namespace StopFire.Api.Models;

public class Reporte
{
    public int Id { get; set; }
    public int IdUsuario { get; set; }                  // FK explícita
    public string? Descripcion { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public string? FotoUrl { get; set; }                // Unificamos nombre (antes ImagenUrl / object FotoUrl)
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public string Estado { get; set; } = "PENDIENTE";
    public Usuario? Usuario { get; set; }               // Navegación
}