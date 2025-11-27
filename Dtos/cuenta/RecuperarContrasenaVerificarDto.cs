namespace stopfire_backend.Dtos.cuenta;

public sealed class RecuperarContrasenaVerificarDto
{
    public string Correo { get; set; } = string.Empty;
    public string Codigo { get; set; } = string.Empty;
    public string NuevaContrasena { get; set; } = string.Empty;
}