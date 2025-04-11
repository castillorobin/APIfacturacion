using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient(); // Registrando IHttpClientFactory
builder.Services.AddMemoryCache(); // Agregar servicio de caché
builder.Services.AddSingleton<TokenCacheService>(); // Registrar servicio de caché de tokens
builder.Services.AddSingleton<FirmaElectronicaService>(); // Registrar como singleton
builder.Services.AddSingleton<RecepcionDTEService>(); // Registrar como singleton
builder.Services.AddSingleton<AnulacionDTEService>(); // Registrar como singleton

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Endpoint optimizado
app.MapPost("/api/procesar-dte", async (
    HttpContext context,
    TokenCacheService tokenService,
    FirmaElectronicaService firmaService,
    RecepcionDTEService recepcionService) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<DteUnificadoRequest>();
        if (request == null)
            return Results.BadRequest("Solicitud inválida");
        // 1. Obtener token (usando el servicio de caché)
        string token = await tokenService.GetTokenAsync(request.Usuario, request.Password, request.Ambiente);
        if (string.IsNullOrEmpty(token))
            return Results.BadRequest("Error en autenticación: token no recibido");
        // 2. Firma
        string jsonFirmado;
        try
        {
            var dteFirmado = await firmaService.FirmarDocumento(request.DteJson, request.Nit, request.PasswordPrivado);
            jsonFirmado = JsonConvert.DeserializeObject<dynamic>(dteFirmado)?.body.ToString();
            if (string.IsNullOrEmpty(jsonFirmado))
                return Results.BadRequest("Error en firma: documento firmado inválido");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en firma de documento: {ex.Message}");
        }
        // 3. Envío
        string respuestaEnvio;
        try
        {
            respuestaEnvio = await recepcionService.EnviarDTE(
                jsonFirmado,
                token,
                request.TipoDte,
                request.CodigoGeneracion,
                request.Ambiente == "01" ? 1 : 0,
                request.VersionDte);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en envío de DTE: {ex.Message}");
        }
        // Procesar respuesta
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(respuestaEnvio))
            {
                if (doc.RootElement.TryGetProperty("selloRecibido", out var sello))
                {
                    return Results.Ok(new
                    {
                        Token = token,
                        DteFirmado = jsonFirmado,
                        SelloRecibido = sello.GetString(),
                        CodigoGeneracion = request.CodigoGeneracion,
                        NumControl = request.NumControl
                    });
                }
            }
            return Results.BadRequest("No se recibió sello de Hacienda");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error procesando respuesta: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Error general: {ex.Message}");
    }
});

app.MapPost("/api/anular-dte", async (
    HttpContext context,
    TokenCacheService tokenService,
    FirmaElectronicaService firmaService,
    AnulacionDTEService anulacionService) =>
{
    try
    {

        var request = await context.Request.ReadFromJsonAsync<DteAnulacionRequest>();
        if (request == null)
            return Results.BadRequest("Solicitud inválida");

        // 1. Obtener token (usando el servicio de caché)
        string token = await tokenService.GetTokenAsync(request.Usuario, request.Password, request.Ambiente);
        if (string.IsNullOrEmpty(token))
            return Results.BadRequest("Error en autenticación: token no recibido");

        // 2. Firma
        string jsonFirmado;
        try
        {
            var dteFirmado = await firmaService.FirmarDocumento(request.DteJson, request.Nit, request.PasswordPrivado);
            jsonFirmado = JsonConvert.DeserializeObject<dynamic>(dteFirmado)?.body.ToString();
            if (string.IsNullOrEmpty(jsonFirmado))
                return Results.BadRequest("Error en firma: documento firmado inválido");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en firma de documento: {ex.Message}");
        }

        // 3. Envío de anulación
        string respuestaEnvio;
        try
        {
            int ambienteInt = request.Ambiente == "01" ? 1 : 0;
            respuestaEnvio = await anulacionService.EnviarDTE(jsonFirmado, token, ambienteInt);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en envío de DTE (anulación): {ex.Message}");
        }

        // Procesar respuesta
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(respuestaEnvio))
            {
                if (doc.RootElement.TryGetProperty("selloRecibido", out var sello))
                {
                    return Results.Ok(new
                    {
                        Token = token,
                        DteFirmado = jsonFirmado,
                        SelloRecibido = sello.GetString()
                       
                    });
                }
            }
            return Results.BadRequest("No se recibió sello de Hacienda");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error procesando respuesta: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Error general: {ex.Message}");
    }
});


app.Run("https://*:7122");


public class AnulacionDTEService
{
    private static readonly HttpClient client = new HttpClient();

    public Task<string> EnviarDTE(string dteFirmado, string token, int ambiente)
    {
        var url = "https://apitest.dtes.mh.gob.sv/fesv/anulardte";
        string ambientet = "00";
        if (ambiente == 1)
        {
            ambientet = "01";
            url = "https://api.dtes.mh.gob.sv/fesv/anulardte";
        }

        // Cuerpo de la solicitud según el manual
        var requestBody = new
        {
            ambiente = ambientet, // Pruebas
            idEnvio = 1, // Correlativo simple, ajusta si necesitas
            version = 2, // Ajusta según tu DTE
            documento = dteFirmado, // DTE firmado como string
            codigoGeneracion = Guid.NewGuid().ToString().ToUpper() // UUID en mayúsculas
        };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json"); // Content-Type se define aquí

        // Depuración: Imprimir el token y el cuerpo
        // Console.WriteLine($"Token enviado: Bearer {token}");
        //Console.WriteLine($"Cuerpo JSON: {json}");

        // Configurar encabezados del cliente (solo los que no son de contenido)
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", $"{token}"); // Token en el encabezado
        client.DefaultRequestHeaders.Add("Accept", "application/json"); // Indica que esperamos JSON como respuesta
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"); // Opcional, pero recomendado

        try
        {
            var response = client.PostAsync(url, content).Result;  // AQUI SE DETIENE
            var responseData = response.Content.ReadAsStringAsync();

            // Depuración: Imprimir respuesta
            //  Console.WriteLine($"Status Code: {response.StatusCode}");
            // Console.WriteLine($"Respuesta: {responseData}");

            if (response.IsSuccessStatusCode)
            {
                return responseData;
            }
            else
            {
                throw new Exception($"Error en el envío del DTE: {response.StatusCode}. Detalles: {responseData}");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al enviar el DTE: {ex.Message}", ex);
        }
    }
} // fin


// Servicio de caché de tokens
// Servicio de caché de tokens mejorado con verificación de expiración JWT
// Servicio de caché de tokens con requisito estricto de validez
public class TokenCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new ConcurrentDictionary<string, SemaphoreSlim>();

    public TokenCacheService(IMemoryCache cache, IHttpClientFactory clientFactory)
    {
        _cache = cache;
        _clientFactory = clientFactory;
    }

    public async Task<string> GetTokenAsync(string usuario, string password, string ambiente)
    {
        string cacheKey = $"AuthToken_{usuario}_{ambiente}";

        // Intentar obtener del caché
        if (_cache.TryGetValue(cacheKey, out string cachedToken))
        {
            // Verificar si el token JWT es válido
            if (IsTokenValid(cachedToken))
            {
                return cachedToken;
            }
            // Si no es válido, eliminarlo del caché
            _cache.Remove(cacheKey);
        }

        // Obtener o crear un semáforo para este usuario específico
        var semaphore = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try
        {
            // Verificar el caché nuevamente después de obtener el semáforo
            if (_cache.TryGetValue(cacheKey, out string token) && IsTokenValid(token))
            {
                return token;
            }

            // Si no está en caché o no es válido, autenticar siempre
            var authUrl = ambiente == "01"
                ? "https://api.dtes.mh.gob.sv/seguridad/auth"
                : "https://apitest.dtes.mh.gob.sv/seguridad/auth";

            using var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            var authResponse = await client.PostAsync(authUrl, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("user", usuario),
                new KeyValuePair<string, string>("pwd", password)
            }));

            if (!authResponse.IsSuccessStatusCode)
                throw new Exception($"Error en autenticación: {authResponse.StatusCode}");

            var authContent = await authResponse.Content.ReadAsStringAsync();
            token = JsonConvert.DeserializeObject<AuthResponse>(authContent)?.Body?.Token;

            if (string.IsNullOrEmpty(token))
                throw new Exception("Token no recibido");

            // Verificamos que el token recibido sea válido
            if (!IsTokenValid(token))
                throw new Exception("El token recibido no es válido o no se puede verificar");

            // Determinar tiempo de expiración del token JWT y establecer caché con ese tiempo
            // menos un margen de seguridad (por ejemplo, 5 minutos antes)
            TimeSpan cacheTime = GetTokenLifetime(token).Subtract(TimeSpan.FromMinutes(5));

            // Asegurarnos de que el tiempo de caché sea positivo
            if (cacheTime.TotalMinutes <= 0)
                cacheTime = TimeSpan.FromMinutes(1); // Mínimo 1 minuto si ya está por expirar

            _cache.Set(cacheKey, token, cacheTime);

            return token;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private bool IsTokenValid(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        try
        {
            // Dividir el token en sus partes (header, payload, signature)
            var parts = token.Split('.');
            if (parts.Length != 3)
                return false; // No es un JWT válido

            // Decodificar el payload
            var payload = parts[1];
            var paddedPayload = payload.PadRight(4 * ((payload.Length + 3) / 4), '=').Replace('-', '+').Replace('_', '/');
            var decodedBytes = Convert.FromBase64String(paddedPayload);
            var jsonPayload = Encoding.UTF8.GetString(decodedBytes);

            // Parsear el payload como JSON
            using (JsonDocument doc = JsonDocument.Parse(jsonPayload))
            {
                // Buscar el claim "exp" (timestamp de expiración)
                if (doc.RootElement.TryGetProperty("exp", out var expClaim))
                {
                    // El exp es un timestamp Unix (segundos desde 1/1/1970)
                    var expDateTime = DateTimeOffset.FromUnixTimeSeconds(expClaim.GetInt64()).UtcDateTime;

                    // Verificar si ha expirado (con un margen de seguridad de 30 segundos)
                    return DateTime.UtcNow.AddSeconds(30) < expDateTime;
                }

                // Si no encuentra el claim "exp", considerarlo inválido
                return false;
            }
        }
        catch
        {
            // Cualquier error al procesar el token, considerarlo inválido
            return false;
        }
    }

    private TimeSpan GetTokenLifetime(string token)
    {
        try
        {
            // Dividir el token en sus partes
            var parts = token.Split('.');
            if (parts.Length != 3)
                return TimeSpan.FromMinutes(5); // Valor mínimo si no es un JWT válido

            // Decodificar el payload
            var payload = parts[1];
            var paddedPayload = payload.PadRight(4 * ((payload.Length + 3) / 4), '=').Replace('-', '+').Replace('_', '/');
            var decodedBytes = Convert.FromBase64String(paddedPayload);
            var jsonPayload = Encoding.UTF8.GetString(decodedBytes);

            // Parsear el payload
            using (JsonDocument doc = JsonDocument.Parse(jsonPayload))
            {
                // Si tiene exp, calcular tiempo restante hasta expiración
                if (doc.RootElement.TryGetProperty("exp", out var expClaim))
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(expClaim.GetInt64()).UtcDateTime;
                    var remaining = expTime - DateTime.UtcNow;

                    // Asegurarnos de que el resultado sea positivo
                    return remaining.TotalMinutes > 0 ? remaining : TimeSpan.FromMinutes(1);
                }

                // Si no se puede determinar, usar un valor mínimo
                return TimeSpan.FromMinutes(5);
            }
        }
        catch
        {
            // En caso de error, usar un valor mínimo
            return TimeSpan.FromMinutes(5);
        }
    }
}


// Modelos para la autenticación (sin cambios)


public class AuthRequest
{
    public string Usuario { get; set; }
    public string Password { get; set; }
    public string Ambiente { get; set; }  // 00 para pruebas, 01 para producción
}

public class AuthResponse
{
    public AuthResponseBody Body { get; set; }
}

public class AuthResponseBody
{
    public string Token { get; set; }
}

// Servicio para firmar el DTE (optimizado)
public class FirmaElectronicaService
{
    private readonly HttpClient _client;

    public FirmaElectronicaService(IHttpClientFactory clientFactory)
    {
        _client = clientFactory.CreateClient("FirmaService");
        _client.BaseAddress = new Uri("http://207.58.175.220:8113/");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> FirmarDocumento(string dteJson, string nit, string passwordPri)
    {
        var requestBody = new
        {
            nit = nit,
            activo = true,
            passwordPri = passwordPri,
            dteJson = JsonConvert.DeserializeObject<dynamic>(dteJson)
        };

        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("firmardocumento/", content);

        if (response.IsSuccessStatusCode)
        {
            var responseData = await response.Content.ReadAsStringAsync();
            return responseData; // Retorna el DTE firmado
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"Error en la firma electrónica: {response.StatusCode}. Detalles: {errorContent}");
        }
    }
}

// Servicio para enviar el DTE (optimizado)
public class RecepcionDTEService
{
    private readonly IHttpClientFactory _clientFactory;

    public RecepcionDTEService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<string> EnviarDTE(string dteFirmado, string token, string tipodte, string codigogeneracion, int ambienteDTE, int versiondte)
    {
        string ambiente = ambienteDTE == 1 ? "01" : "00";
        string baseUrl = ambienteDTE == 1
            ? "https://api.dtes.mh.gob.sv"
            : "https://apitest.dtes.mh.gob.sv";

        var client = _clientFactory.CreateClient("RecepcionService");
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", token);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var requestBody = new
        {
            ambiente = ambiente,
            idEnvio = 1,
            version = versiondte,
            tipoDte = tipodte,
            documento = dteFirmado,
            codigoGeneracion = codigogeneracion
        };

        var json = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await client.PostAsync("/fesv/recepciondte", content);
            var responseData = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return responseData;
            }
            else
            {
                throw new Exception($"Error en el envío del DTE: {response.StatusCode}. Detalles: {responseData}");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al enviar el DTE: {ex.Message}", ex);
        }
    }
}

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

public class DteAnulacionRequest
{
    public string Usuario { get; set; }
    public string Password { get; set; }
    public string Ambiente { get; set; } // "00" o "01"
    public string DteJson { get; set; }
    public string Nit { get; set; }
    public string PasswordPrivado { get; set; }
    
}


