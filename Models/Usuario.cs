namespace StopFire.Api.Models;

public class Usuario
{
    public int Id { get; set; }

    public string Nombre { get; set; } = string.Empty;
    public string Apellido { get; set; } = string.Empty;

    public string Ci { get; set; } = string.Empty;

    public string Correo { get; set; } = string.Empty;

    public string? Celular { get; set; }

    public string Contrasena { get; set; } = string.Empty;

    public int RolId { get; set; } = 3;
    public Rol? Rol { get; set; }
}