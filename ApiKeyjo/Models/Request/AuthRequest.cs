
// Modelos para la autenticación (sin cambios)

public class AuthRequest
{
    public string Usuario { get; set; }
    public string Password { get; set; }
    public string Ambiente { get; set; }  // 00 para pruebas, 01 para producción
}


