
// Modelo de solicitud unificada (sin cambios)
public class DteUnificadoRequest
{
    public string Usuario { get; set; }
    public string Password { get; set; }
    public string Ambiente { get; set; } // "00" o "01"
    public string DteJson { get; set; }
    public string Nit { get; set; }
    public string PasswordPrivado { get; set; }
    public string TipoDte { get; set; }
    public string CodigoGeneracion { get; set; }
    public string NumControl { get; set; }
    public int VersionDte { get; set; }
}


