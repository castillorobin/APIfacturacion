using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient(); // Registrando IHttpClientFactory

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Endpoint de autenticación
//app.MapPost("/api/auth", async (HttpContext context, IHttpClientFactory clientFactory) =>
//{
//    // Definir el modelo de la solicitud para la autenticación
//    var authRequest = await context.Request.ReadFromJsonAsync<AuthRequest>();

//    // Validar que los datos necesarios estén presentes
//    if (authRequest == null || string.IsNullOrEmpty(authRequest.Usuario) || string.IsNullOrEmpty(authRequest.Password) || string.IsNullOrEmpty(authRequest.Ambiente))
//    {
//        return Results.BadRequest("Faltan datos de autenticación.");
//    }

//    // Definir la URL de la API según el ambiente
//    var url = authRequest.Ambiente == "01"
//        ? "https://api.dtes.mh.gob.sv/seguridad/auth"   // Producción
//        : "https://apitest.dtes.mh.gob.sv/seguridad/auth"; // Pruebas

//    // Usar IHttpClientFactory para crear el cliente HTTP
//    using (HttpClient client = clientFactory.CreateClient())
//    {
//        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
//        client.DefaultRequestHeaders.Add("Accept", "application/json");

//        // Crear el contenido del formulario
//        var formData = new FormUrlEncodedContent(new[]
//        {
//            new KeyValuePair<string, string>("user", authRequest.Usuario),
//            new KeyValuePair<string, string>("pwd", authRequest.Password)
//        });

//        // Enviar la solicitud POST a la API de Hacienda
//        HttpResponseMessage response = await client.PostAsync(url, formData);
//        var responseContent = await response.Content.ReadAsStringAsync();

//        // Si la respuesta no es exitosa, devolver un error
//        if (!response.IsSuccessStatusCode)
//            return Results.BadRequest($"Error de autenticación: {responseContent}");

//        // Deserializar la respuesta JSON y obtener el token
//        var authResponse = JsonConvert.DeserializeObject<AuthResponse>(responseContent);

//        // Verificar si el token fue recibido correctamente
//        if (authResponse?.Body?.Token == null)
//            return Results.BadRequest("No se recibió un token válido");

//        // Devolver el token de autenticación en la respuesta
//        return Results.Ok(new { Token = authResponse.Body.Token });
//    }
//}); // fin autenticar. 


//// Endpoint para firmar el DTE
//app.MapPost("/api/firmar", async (HttpContext context) =>
//{
//    var firmaRequest = await context.Request.ReadFromJsonAsync<FirmaRequest>();

//    if (firmaRequest == null || string.IsNullOrEmpty(firmaRequest.DteJson) || string.IsNullOrEmpty(firmaRequest.Nit) || string.IsNullOrEmpty(firmaRequest.PasswordPrivado))
//    {
//        return Results.BadRequest("Faltan datos para la firma.");
//    }

//    var firmaService = new FirmaElectronicaService();
//    try
//    {
//        string dteFirmado = await firmaService.FirmarDocumento(firmaRequest.DteJson, firmaRequest.Nit, firmaRequest.PasswordPrivado);

//        // Deserializar la respuesta de firma
//        var jsonResponse = JsonConvert.DeserializeObject<dynamic>(dteFirmado);
//        dteFirmado = jsonResponse.body.ToString();

//        return Results.Ok(new { DteFirmado = dteFirmado });
//    }
//    catch (Exception ex)
//    {
//        return Results.BadRequest($"Error al firmar el documento: {ex.Message}");
//    }
//});
//// fin firma


//app.MapPost("/api/enviar", (HttpContext context) =>
//{
//    var envioRequest = context.Request.ReadFromJsonAsync<EnvioRequest>().Result;

//    if (envioRequest == null || string.IsNullOrEmpty(envioRequest.DteFirmado) || string.IsNullOrEmpty(envioRequest.Token) || string.IsNullOrEmpty(envioRequest.TipoDte) || string.IsNullOrEmpty(envioRequest.CodigoGeneracion) || envioRequest.VersionDte == 0)
//    {
//        return Results.BadRequest("Faltan datos para el envío del DTE.");
//    }

//    var recepcionService = new RecepcionDTEService();
//    try
//    {
//        string respuesta = recepcionService.EnviarDTE(envioRequest.DteFirmado, envioRequest.Token, envioRequest.TipoDte, envioRequest.CodigoGeneracion, envioRequest.AmbienteDTE, envioRequest.VersionDte).Result;

//        // Procesar respuesta (extraer el selloRecibido)
//        string selloRecibido = "";
//        using (JsonDocument document = JsonDocument.Parse(respuesta))
//        {
//            if (document.RootElement.TryGetProperty("selloRecibido", out JsonElement selloRecibidoElement))
//            {
//                selloRecibido = selloRecibidoElement.GetString();
//            }
//        }

//        return Results.Ok(new { SelloRecibido = selloRecibido });
//    }
//    catch (Exception ex)
//    {
//        return Results.BadRequest($"Error al enviar el DTE: {ex.Message}");
//    }
//}); //ENVIAR DTE 

// Nuevo endpoint unificado
app.MapPost("/api/procesar-dte", async (HttpContext context, IHttpClientFactory clientFactory) =>
{
    var request = await context.Request.ReadFromJsonAsync<DteUnificadoRequest>();

    if (request == null)
        return Results.BadRequest("Solicitud inválida");

    // 1. Autenticación
    var authUrl = request.Ambiente == "01"
        ? "https://api.dtes.mh.gob.sv/seguridad/auth"
        : "https://apitest.dtes.mh.gob.sv/seguridad/auth";

    using var client = clientFactory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    // Autenticación
    var authResponse = await client.PostAsync(authUrl, new FormUrlEncodedContent(new[]
    {
        new KeyValuePair<string, string>("user", request.Usuario),
        new KeyValuePair<string, string>("pwd", request.Password)
    }));

    if (!authResponse.IsSuccessStatusCode)
        return Results.BadRequest("Error en autenticación");

    var authContent = await authResponse.Content.ReadAsStringAsync();
    var token = JsonConvert.DeserializeObject<AuthResponse>(authContent)?.Body?.Token;

    if (string.IsNullOrEmpty(token))
        return Results.BadRequest("Token no recibido");

    // 2. Firma
    var firmaService = new FirmaElectronicaService();
    var dteFirmado = await firmaService.FirmarDocumento(request.DteJson, request.Nit, request.PasswordPrivado);
    var jsonFirmado = JsonConvert.DeserializeObject<dynamic>(dteFirmado)?.body.ToString();

    // 3. Envío
    var recepcionService = new RecepcionDTEService();
    var respuestaEnvio = await recepcionService.EnviarDTE(jsonFirmado, token, request.TipoDte,
        request.CodigoGeneracion, request.Ambiente == "01" ? 1 : 0, request.VersionDte);

    // Procesar respuesta
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
});

app.Run("http://*:7122");

// Modelos para la autenticación
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


// Modelos para la firma
public class FirmaRequest
{
    public string DteJson { get; set; }
    public string Nit { get; set; }
    public string PasswordPrivado { get; set; }
}

// Servicio para firmar el DTE
public class FirmaElectronicaService
{
    private static readonly HttpClient client = new HttpClient();

    public async Task<string> FirmarDocumento(string dteJson, string nit, string passwordPri)
    {
        var url = "http://207.58.175.220:8113/firmardocumento/";

        var requestBody = new
        {
            nit = nit,
            activo = true,
            passwordPri = passwordPri,
            dteJson = JsonConvert.DeserializeObject<dynamic>(dteJson)
        };

        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            var responseData = await response.Content.ReadAsStringAsync();
            return responseData; // Retorna el DTE firmado
        }
        else
        {
            throw new Exception("Error en la firma electrónica: " + response.StatusCode);
        }
    }
}

public class EnvioRequest
{
    public string DteFirmado { get; set; }
    public string Token { get; set; }
    public string TipoDte { get; set; }
    public string CodigoGeneracion { get; set; }
    public int AmbienteDTE { get; set; }
    public int VersionDte { get; set; }
}

// Servicio para enviar el DTE
public class RecepcionDTEService
{
    private static readonly HttpClient client = new HttpClient();

    public Task<string> EnviarDTE(string dteFirmado, string token, string tipodte, string codigogeneracion, int ambienteDTE, int versiondte)
    {
        string ambiente = "00";
        var url = "https://apitest.dtes.mh.gob.sv/fesv/recepciondte";
        if (ambienteDTE == 1)
        {
            url = "https://api.dtes.mh.gob.sv/fesv/recepciondte";
            ambiente = "01";
        }

        var requestBody = new
        {
            ambiente = ambiente,
            idEnvio = 1,
            version = versiondte,
            tipoDte = tipodte,
            documento = dteFirmado,
            codigoGeneracion = codigogeneracion
        };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", $"{token}");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        try
        {
            var response = client.PostAsync(url, content).Result;
            var responseData = response.Content.ReadAsStringAsync().Result;

            if (response.IsSuccessStatusCode)
            {
                return Task.FromResult(responseData); // Retorna la respuesta del DTE
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