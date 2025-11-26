namespace StopFire.Api.Models;

public class RegistroCapitanEstacion
{
    public int Id { get; set; }
    public int EstacionId { get; set; }
    public string EstacionNombre { get; set; } = string.Empty;

    public int ResponsableId { get; set; }
    public string ResponsableNombre { get; set; } = string.Empty;

    public DateTime Fecha { get; set; } 
}