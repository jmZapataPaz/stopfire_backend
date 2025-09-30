namespace stopfire_backend.Models
{
    internal class PendingRegistration
    {
        public string Nombre { get; set; }
        public string Apellido { get; set; }
        public string Ci { get; set; }
        public string Correo { get; set; }
        public string Celular { get; set; }
        public string PasswordHash { get; set; }
        public int RolId { get; set; }
        public string Otp { get; set; }
    }
}