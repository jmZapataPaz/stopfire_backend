namespace StopFire.Api.Models;

public class Rol
{
    public int Id { get; set; }
    public string Descripcion { get; set; } = string.Empty;

    public ICollection<Usuario> Usuarios { get; set; } = new List<Usuario>();
}