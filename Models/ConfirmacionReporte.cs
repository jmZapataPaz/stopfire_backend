namespace StopFire.Api.Models;

public class ConfirmacionReporte
{
    public int Id { get; set; }
    public int IdReporte { get; set; }
    public int IdUsuario { get; set; }
    public DateTime FechaConfirmacion { get; set; } = DateTime.UtcNow;
    
    public Reporte? Reporte { get; set; }
    public Usuario? Usuario { get; set; }
}